using UnityEngine;

namespace CarveAndPlunder
{
    // Drives a single in-progress skin/loot action. Static + single-slot: a
    // player can only work one corpse at a time. Begin() is called from the
    // corpse's Interact; Tick() is pumped every frame from Plugin.Update and
    // handles the hold timer, cancellation, and completion.
    internal static class CorpseWorkSession
    {
        private static bool _active;
        private static CorpseInteraction _corpse;
        private static Player _player;
        private static float _elapsed;
        private static float _duration;
        private static float _bonusExtra;

        // Press-once mode only: a session can be cancelled by pressing Use again,
        // but we must not treat the opening press as a cancel. _armedForPressCancel
        // turns true once that opening press is released; only then does a fresh
        // press cancel. _suppressRestart blocks Begin on the same press that
        // cancelled (Interact and our cancel both fire on that GetButtonDown, in an
        // undefined order), and is cleared on the next button-up.
        private static bool _armedForPressCancel;
        private static bool _suppressRestart;

        public static bool Active => _active;

        public static void Begin(CorpseInteraction corpse, Player player)
        {
            if (_active) return;
            if (_suppressRestart) return;
            if (corpse == null || player == null) return;

            ComputePlan(corpse, player, out _duration, out _bonusExtra);

            _corpse = corpse;
            _player = player;
            _elapsed = 0f;
            _armedForPressCancel = false;
            _active = true;

            // Freeze the corpse's rot timer so it can't expire mid-work.
            corpse.SuspendDecay();

            StartWorkPose(player);
            WorkProgressUI.Show(WorkLabel(corpse, player));
            ModLog.Info($"[CarveAndPlunder] Begin {corpse.Kind} on '{corpse.gameObject.name}' " +
                        $"dur={_duration:0.00}s extra={_bonusExtra:0.0}");
        }

        public static void Tick(float dt)
        {
            // Clear the restart guard once the cancelling press is released, so the
            // next genuine press can start a new session. Runs every frame.
            if (_suppressRestart && !ZInput.GetButton("Use")) _suppressRestart = false;

            if (!_active) return;

            if (!StillValid())
            {
                Cancel();
                return;
            }

            _elapsed += dt;
            WorkProgressUI.SetProgress(Mathf.Clamp01(_elapsed / _duration));

            if (_elapsed >= _duration)
                Complete();
        }

        private static bool StillValid()
        {
            if (_corpse == null || _player == null) return false;
            if (_corpse.Nview == null || !_corpse.Nview.IsValid()) return false;
            if (_player != Player.m_localPlayer || _player.IsDead()) return false;

            if (ModConfig.HoldToWork.Value)
            {
                // Hold mode: releasing Use cancels.
                if (!ZInput.GetButton("Use")) return false;
            }
            else
            {
                // Press-once mode: the work runs on its own. Once the opening press
                // is released we arm; a fresh press after that cancels (and blocks
                // the same press from restarting via Interact).
                if (!_armedForPressCancel)
                {
                    if (!ZInput.GetButton("Use")) _armedForPressCancel = true;
                }
                else if (ZInput.GetButtonDown("Use"))
                {
                    _suppressRestart = true;
                    return false;
                }
            }

            // Swinging a weapon/tool interrupts the work.
            if (_player.InAttack()) return false;

            // Walked too far from the carcass.
            float dist = Vector3.Distance(_player.transform.position,
                                          _corpse.Ragdoll.GetAverageBodyPosition());
            if (dist > ModConfig.MaxWorkDistance.Value) return false;

            return true;
        }

        private static void Complete()
        {
            CorpseInteraction corpse = _corpse;
            Player player = _player;
            float extra = _bonusExtra;
            CorpseKind kind = corpse.Kind;

            EndSession();

            // Owner-authoritative: take the corpse's ZDO before mutating/dropping
            // so the loot spawns on our machine like the vanilla path does.
            corpse.Nview.ClaimOwnership();
            AddStoredDrops(corpse, extra);

            corpse.AllowSpawnNow();
            Vector3 center = corpse.Ragdoll.GetAverageBodyPosition();
            corpse.Ragdoll.SpawnLoot(center);
            if (ZNetScene.instance != null)
                ZNetScene.instance.Destroy(corpse.gameObject);

            if (kind == CorpseKind.Humanoid)
            {
                LootingSkill.Raise(player, ModConfig.LootingXpPerLoot.Value);
                player.Message(MessageHud.MessageType.TopLeft, "Looted the corpse");
            }
            else
            {
                player.RaiseSkill(Skills.SkillType.Knives, ModConfig.KnivesXpPerSkin.Value);
                player.Message(MessageHud.MessageType.TopLeft, "Skinned the carcass");
            }
        }

        private static void Cancel()
        {
            // Player aborted but the corpse still exists — re-arm its rot timer so
            // it doesn't sit frozen forever, then it can decay (and drop partial
            // loot) normally if left alone.
            if (_corpse != null && _corpse.Nview != null && _corpse.Nview.IsValid())
                _corpse.ResumeDecay();
            EndSession();
        }

        private static void EndSession()
        {
            if (_player != null) StopWorkPose(_player);
            WorkProgressUI.Hide();
            _active = false;
            _corpse = null;
            _player = null;
            _elapsed = 0f;
            _armedForPressCancel = false;
        }

        // Matches the hover prompt: humanoids Loot, animals Skin with a knife or
        // Dismantle bare-handed.
        private static string WorkLabel(CorpseInteraction corpse, Player player)
        {
            if (corpse.Kind == CorpseKind.Humanoid) return "Looting";
            return KnifeUtil.HasKnife(player) ? "Skinning" : "Dismantling";
        }

        // ── Plan: how long, how much bonus ─────────────────────────────
        private static void ComputePlan(CorpseInteraction corpse, Player player,
            out float duration, out float bonusExtra)
        {
            if (corpse.Kind == CorpseKind.Humanoid)
            {
                float f = LootingSkill.GetFactor(player);
                duration = ModConfig.LootTimeBase.Value - f * ModConfig.LootTimeReduction.Value;
                bonusExtra = f * ModConfig.LootExtraMax.Value;
            }
            else // Animal
            {
                float sharpness = KnifeUtil.BestKnifeSharpness(player);
                if (sharpness > 0f)
                {
                    float knivesFactor = player.GetSkillFactor(Skills.SkillType.Knives);
                    duration = ModConfig.SkinTimeBase.Value - knivesFactor * ModConfig.SkinTimeReduction.Value;
                    float sharpFactor = Mathf.Clamp01(sharpness / ModConfig.KnifeBonusReferenceDamage.Value);
                    bonusExtra = sharpFactor * ModConfig.SkinExtraMax.Value;
                }
                else // bare-handed dismantle: slow, no bonus
                {
                    duration = ModConfig.DismantleTime.Value;
                    bonusExtra = 0f;
                }
            }
            duration = Mathf.Max(0.25f, duration);
        }

        // Bump the loot amounts the Ragdoll stashed in its ZDO at death time
        // (see Ragdoll.SaveLootList) so the bonus rides through the existing
        // SpawnLoot path without us re-rolling the drop table. Adds a flat
        // count to each stack the corpse was already going to drop.
        private static void AddStoredDrops(CorpseInteraction corpse, float extra)
        {
            int add = Mathf.RoundToInt(extra);
            if (add <= 0) return;
            ZDO zdo = corpse.Nview.GetZDO();
            if (zdo == null) return;

            int count = zdo.GetInt(ZDOVars.s_drops);
            for (int i = 0; i < count; i++)
            {
                int amount = zdo.GetInt("drop_amount" + i);
                if (amount <= 0) continue;
                zdo.Set("drop_amount" + i, amount + add);
            }
        }

        // ── Pose / animation ───────────────────────────────────────────
        private static void StartWorkPose(Player player)
        {
            // Kneel over the corpse. (The crafting animation can't overlay an
            // emote — both want the base body layer — so we use the emote alone.)
            player.StartEmote("kneel", oneshot: false);
        }

        private static void StopWorkPose(Player player)
        {
            if (player.InEmote())
                player.StopEmote();
        }
    }
}
