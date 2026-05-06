using HarmonyLib;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    [HarmonyPatch]
    public static class MinimapPatch
    {
        /// <summary>
        /// Captures non-table map opens (portable map items, Vegvisir reveals, etc.).
        /// ZenMap's MapLocation.Show() funnels every map-item read through
        /// Minimap.ShowPointOnMap, so this catches them all without needing a
        /// reference to ZenMap. SetActiveItem skips the assignment if a Table
        /// source is already set this frame, so cartography reads aren't clobbered.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "ShowPointOnMap")]
        [HarmonyPostfix]
        static void ShowPointOnMap_Postfix(Vector3 point)
        {
            SpawnDirection.SetActiveItem(point);
        }

        [HarmonyPatch(typeof(Minimap), "Start")]
        [HarmonyAfter("ZenDragon.ZenMap")]
        [HarmonyPostfix]
        static void Minimap_Start_Postfix()
        {
            CaptureButton.Create();
        }

        /// <summary>
        /// Show/hide the capture button when map mode changes.
        /// Also clears the cached cartography-table position when the map closes,
        /// so a stale "X of Spawn" label can't leak into a later capture opened
        /// by some other path (M key, map item, etc.).
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "SetMapMode")]
        [HarmonyPostfix]
        static void SetMapMode_Postfix(Minimap.MapMode mode)
        {
            bool large = mode == Minimap.MapMode.Large;
            CaptureButton.SetVisible(large);
            if (!large)
                SpawnDirection.Clear();
        }
    }
}
