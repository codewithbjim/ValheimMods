using System;
using HarmonyLib;

namespace HackAndSap
{
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
    [HarmonyPriority(Priority.Last)]
    [HarmonyAfter(Jotunn.Main.ModGuid)]
    internal static class ZNetSceneAwakePatch
    {
        private static void Postfix(ZNetScene __instance)
        {
            RenewableRegistry.Initialize(__instance);
        }
    }

    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.SpawnLog))]
    internal static class TreeBaseSpawnLogPatch
    {
        private static void Prefix(TreeBase __instance) => SpawnContext.Enter(__instance);

        private static Exception Finalizer(Exception __exception)
        {
            SpawnContext.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.RPC_Damage))]
    internal static class TreeLogDamagePatch
    {
        private static void Prefix(TreeLog __instance, out float __state)
        {
            __state = float.NaN;
            ZNetView nview = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            __state = nview.GetZDO().GetFloat(ZDOVars.s_health, __instance.m_health);
        }

        private static void Postfix(TreeLog __instance, HitData hit, float __state)
        {
            if (float.IsNaN(__state) || hit == null || hit.m_damage.m_chop <= 0f) return;
            ZNetView nview = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            float remaining = nview.GetZDO().GetFloat(ZDOVars.s_health, __instance.m_health);
            if (remaining < __state)
                __instance.GetComponent<LogDecay>()?.MarkWorked();
        }
    }
}
