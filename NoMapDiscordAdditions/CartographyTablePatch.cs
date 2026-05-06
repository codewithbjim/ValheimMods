using HarmonyLib;
using NoMapDiscordAdditions.MapCompile;

namespace NoMapDiscordAdditions
{
    [HarmonyPatch]
    public static class CartographyTablePatch
    {
        /// <summary>
        /// Records the cartography table position the player just read so
        /// <see cref="SpawnDirection"/> can describe its direction/distance from spawn,
        /// and so <see cref="MapCompileSession"/> knows the player is at a valid
        /// add-tile location (compile mode is "tables only").
        /// Runs after ZenMap, which may suppress the original method but still triggers
        /// our postfix because Harmony postfixes always run.
        /// </summary>
        [HarmonyPatch(typeof(MapTable), "OnRead",
            typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData))]
        [HarmonyPostfix]
        static void OnRead_Postfix(MapTable __instance)
        {
            if (__instance == null) return;
            var pos = __instance.transform.position;
            SpawnDirection.SetActiveTable(pos);
            MapCompileSession.SetActiveTable(pos);
        }
    }
}
