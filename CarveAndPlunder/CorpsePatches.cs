using HarmonyLib;
using UnityEngine;

namespace CarveAndPlunder
{
    // Attaches our interaction component to every corpse ragdoll as it awakes.
    [HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Awake))]
    internal static class Ragdoll_Awake_Patch
    {
        private static void Postfix(Ragdoll __instance)
        {
            if (__instance.GetComponent<CorpseInteraction>() == null)
            {
                var ci = __instance.gameObject.AddComponent<CorpseInteraction>();
                ModLog.Diag($"Ragdoll.Awake -> attached to '{__instance.gameObject.name}' " +
                            $"kind={ci.Kind} layer={LayerMask.LayerToName(__instance.gameObject.layer)}");
            }
        }
    }

    // The despawn timer fired before anyone worked this corpse. Open the loot
    // gate (at a reduced fraction) right before the vanilla DestroyNow runs its
    // SpawnLoot, so a rotted corpse drops a partial haul instead of nothing.
    [HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.DestroyNow))]
    internal static class Ragdoll_DestroyNow_Patch
    {
        private static void Prefix(Ragdoll __instance)
        {
            CorpseInteraction ci = __instance.GetComponent<CorpseInteraction>();
            if (ci == null || ci.Kind == CorpseKind.Passthrough) return;
            if (ci.SpawnAllowed) return; // already worked/handled — don't touch it
            ci.ApplyExpiryLoot();
        }
    }

    // Gates the corpse's loot. Ragdoll.SpawnLoot is called from two places:
    //   1. The vanilla TTL timer (DestroyNow) — we BLOCK this so an un-worked
    //      corpse rots away with its loot.
    //   2. Our own CorpseWorkSession.Complete, after it sets SpawnAllowed — we
    //      ALLOW this so skinning/looting actually drops items.
    // Passthrough corpses (bosses, excluded, mod disabled) are never blocked.
    [HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.SpawnLoot))]
    internal static class Ragdoll_SpawnLoot_Patch
    {
        // Return false to skip the original method.
        private static bool Prefix(Ragdoll __instance)
        {
            CorpseInteraction ci = __instance.GetComponent<CorpseInteraction>();
            if (ci == null) { ModLog.Diag($"SpawnLoot '{__instance.gameObject.name}': no CI -> vanilla"); return true; }
            if (ci.Kind == CorpseKind.Passthrough) { ModLog.Diag($"SpawnLoot '{__instance.gameObject.name}': passthrough -> vanilla"); return true; }
            ModLog.Diag($"SpawnLoot '{__instance.gameObject.name}': kind={ci.Kind} allowed={ci.SpawnAllowed}");
            return ci.SpawnAllowed;                    // false until worked
        }
    }
}
