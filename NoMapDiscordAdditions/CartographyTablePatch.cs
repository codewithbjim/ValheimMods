using HarmonyLib;

namespace NoMapDiscordAdditions
{
    [HarmonyPatch]
    public static class CartographyTablePatch
    {
        /// <summary>
        /// Records the cartography table position the player just read so
        /// <see cref="SpawnDirection"/> can describe its direction/distance from spawn.
        /// Runs after ZenMap, which may suppress the original method but still triggers
        /// our postfix because Harmony postfixes always run.
        /// </summary>
        [HarmonyPatch(typeof(MapTable), "OnRead",
            typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData))]
        [HarmonyPostfix]
        static void OnRead_Postfix(MapTable __instance)
        {
            if (__instance != null)
                SpawnDirection.SetActiveTable(__instance.transform.position);
        }
    }
}
