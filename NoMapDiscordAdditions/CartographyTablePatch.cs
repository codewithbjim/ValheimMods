using HarmonyLib;
using NoMapDiscordAdditions.MapCompile;

namespace NoMapDiscordAdditions
{
    [HarmonyPatch]
    public static class CartographyTablePatch
    {
        /// <summary>
        /// Records the cartography table position the player just read so
        /// <see cref="MapCompileSession"/> knows the player is at a valid
        /// add-tile location (compile mode is "tables only") and resolves the
        /// table's name (for the Discord {table} placeholder).
        /// Runs after ZenMap, which may suppress the original method but still triggers
        /// our postfix because Harmony postfixes always run.
        /// </summary>
        [HarmonyPatch(typeof(MapTable), "OnRead",
            typeof(Switch), typeof(Humanoid), typeof(ItemDrop.ItemData))]
        [HarmonyPostfix]
        static void OnRead_Postfix(MapTable __instance)
        {
            if (__instance == null) return;
            MapCompileSession.SetActiveTable(__instance.transform.position);
        }
    }
}
