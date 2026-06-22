using UnityEngine;

namespace CarveAndPlunder
{
    // Attached to every Ragdoll (see CorpsePatches). Makes the corpse hoverable
    // and interactable, and owns the "has this corpse been worked?" gate the
    // SpawnLoot patch reads. The actual hold-to-work timing lives in
    // CorpseWorkSession; this component just describes the corpse and kicks a
    // session off when the player presses Use.
    public class CorpseInteraction : MonoBehaviour, Hoverable, Interactable
    {
        public Ragdoll Ragdoll { get; private set; }
        public ZNetView Nview { get; private set; }
        public CorpseKind Kind { get; private set; }

        // Set true the instant a work session completes, immediately before we
        // call SpawnLoot ourselves. The SpawnLoot patch blocks the vanilla
        // TTL-driven call while this is false (so ignored corpses rot with their
        // loot) and allows our deliberate call once it's true.
        public bool SpawnAllowed { get; private set; }

        // Collider proxy on an interact-mask layer (see CorpseInteractionProxy).
        private GameObject _proxy;

        private void Awake()
        {
            Ragdoll = GetComponent<Ragdoll>();
            Nview = GetComponent<ZNetView>();
            Kind = CorpseClassifier.Classify(gameObject.name);
            // Passthrough corpses never block vanilla drops and need no interaction.
            if (Kind == CorpseKind.Passthrough)
            {
                SpawnAllowed = true;
                return;
            }
            CreateProxy();
            ExtendLifetime();
        }

        // The vanilla ragdoll TTL is too short to walk over and work the corpse.
        // Cancel that despawn timer and reschedule it with our configured lifetime,
        // so the corpse lingers long enough to skin/loot but still rots if ignored.
        private void ExtendLifetime()
        {
            if (Ragdoll == null) return;
            float configured = Kind == CorpseKind.Humanoid
                ? ModConfig.HumanoidCorpseLifetime.Value
                : ModConfig.AnimalCorpseLifetime.Value;
            float ttl = Mathf.Max(1f, configured);
            Ragdoll.CancelInvoke("DestroyNow");
            Ragdoll.InvokeRepeating("DestroyNow", ttl, 1f);
            ModLog.Diag($"lifetime set to {ttl}s for '{gameObject.name}' (kind={Kind})");
        }

        // Freeze the decay timer while a work session is in progress, so a corpse
        // can't rot out from under the player mid-skin/loot (which would destroy it
        // and—via the SpawnLoot gate—lose the loot entirely).
        public void SuspendDecay()
        {
            if (Ragdoll != null) Ragdoll.CancelInvoke("DestroyNow");
        }

        // Re-arm the decay timer after an aborted session (corpse survived). Gives
        // it a fresh full lifetime so the player can try again.
        public void ResumeDecay() => ExtendLifetime();

        // Corpse rotted away before anyone worked it. Rather than losing everything
        // (the SpawnLoot gate blocks the vanilla TTL drop while SpawnAllowed is
        // false), drop a reduced share: scale the stashed amounts down and open the
        // gate so the vanilla DestroyNow path drops what's left.
        public void ApplyExpiryLoot()
        {
            if (Nview == null || !Nview.IsValid() || !Nview.IsOwner()) return;
            float frac = Mathf.Clamp01(ModConfig.ExpiryLootFraction.Value);

            // Fraction 0 -> drop nothing: leave the gate shut so the corpse rots
            // empty (the original "un-worked loot is lost" behaviour).
            if (frac <= 0f)
            {
                ModLog.Diag($"expiry loot for '{gameObject.name}': frac=0, dropping nothing");
                return;
            }

            ZDO zdo = Nview.GetZDO();
            if (zdo != null && frac < 1f)
            {
                int count = zdo.GetInt(ZDOVars.s_drops);
                for (int i = 0; i < count; i++)
                {
                    int amount = zdo.GetInt("drop_amount" + i);
                    if (amount <= 0) continue;
                    // Keep at least 1 of each item so the corpse still drops loot;
                    // stacks scale down by the fraction.
                    int scaled = Mathf.Max(1, Mathf.RoundToInt(amount * frac));
                    zdo.Set("drop_amount" + i, scaled);
                }
            }

            SpawnAllowed = true;
            ModLog.Diag($"expiry loot for '{gameObject.name}' kind={Kind} frac={frac:0.00}");
        }

        // A walk-through collider the interact raycast can actually hit, placed at
        // the corpse's body so the player can aim at the carcass.
        private void CreateProxy()
        {
            _proxy = new GameObject("CarvePlunderInteract");
            _proxy.layer = LayerMask.NameToLayer("piece_nonsolid");
            _proxy.transform.position = BodyPosition();
            _proxy.transform.SetParent(transform, worldPositionStays: true);

            var col = _proxy.AddComponent<SphereCollider>();
            col.radius = 0.8f;
            col.isTrigger = false; // raycast-hittable regardless of queriesHitTriggers

            _proxy.AddComponent<CorpseInteractionProxy>().Target = this;
            ModLog.Diag($"proxy created for '{gameObject.name}' kind={Kind} at {BodyPosition()}");
        }

        private void Update()
        {
            // Ragdolls flop after death; keep the interaction collider on the body.
            if (_proxy != null && !SpawnAllowed)
                _proxy.transform.position = BodyPosition();
        }

        private Vector3 BodyPosition()
            => Ragdoll != null ? Ragdoll.GetAverageBodyPosition() : transform.position;

        public void AllowSpawnNow() => SpawnAllowed = true;

        // ── Hoverable ──────────────────────────────────────────────────
        public string GetHoverName()
        {
            switch (Kind)
            {
                case CorpseKind.Humanoid: return "Corpse";
                case CorpseKind.Animal: return "Carcass";
                default: return "";
            }
        }

        public string GetHoverText()
        {
            if (Kind == CorpseKind.Passthrough || SpawnAllowed)
                return "";

            string verb;
            if (Kind == CorpseKind.Humanoid)
            {
                verb = "Loot";
            }
            else
            {
                verb = KnifeUtil.HasKnife(Player.m_localPlayer) ? "Skin" : "Dismantle";
            }

            // [<hotkey>] Verb, with a "(hold)" tag only in hold mode (press-once
            // needs no qualifier — a single press just works).
            string hint = ModConfig.HoldToWork.Value ? " <color=grey>(hold)</color>" : "";
            return Localization.instance.Localize(
                $"[<color=yellow><b>$KEY_Use</b></color>] {verb}{hint}");
        }

        // ── Interactable ───────────────────────────────────────────────
        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            // Only the initial press starts a session; the hold repeat is driven
            // by CorpseWorkSession reading the Use button each frame.
            if (hold) return false;
            if (Kind == CorpseKind.Passthrough || SpawnAllowed) return false;
            if (!(user is Player player) || player != Player.m_localPlayer) return false;

            ModLog.Diag($"Interact on '{gameObject.name}' kind={Kind} -> begin session");
            CorpseWorkSession.Begin(this, player);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) => false;
    }
}
