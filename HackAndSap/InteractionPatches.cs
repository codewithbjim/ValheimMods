using HarmonyLib;
using UnityEngine;

namespace HackAndSap
{
    internal static class InteractionInstaller
    {
        public static void Install(GameObject root)
        {
            CuttingInteraction target = root.GetComponent<CuttingInteraction>();
            if (target == null) target = root.AddComponent<CuttingInteraction>();

            int points = 0;
            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null) continue;
                TreeInteractionPoint point = collider.GetComponent<TreeInteractionPoint>();
                if (point == null) point = collider.gameObject.AddComponent<TreeInteractionPoint>();
                point.Target = target;
                points++;
            }

            if (points == 0)
                ModLog.Warn($"No collider interaction points found on '{root.name}'.");
        }
    }

    [HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Awake))]
    internal static class TreeBaseAwakePatch
    {
        private static void Postfix(TreeBase __instance)
        {
            if (__instance.m_logPrefab != null)
                InteractionInstaller.Install(__instance.gameObject);
        }
    }

    [HarmonyPatch(typeof(TreeLog), nameof(TreeLog.Awake))]
    internal static class TreeLogAwakePatch
    {
        private static void Postfix(TreeLog __instance)
        {
            InteractionInstaller.Install(__instance.gameObject);
            if (__instance.GetComponent<LogDecay>() == null)
                __instance.gameObject.AddComponent<LogDecay>();
        }
    }
}
