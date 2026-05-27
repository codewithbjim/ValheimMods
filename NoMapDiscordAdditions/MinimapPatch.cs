using HarmonyLib;
using NoMapDiscordAdditions.MapCompile;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    [HarmonyPatch]
    public static class MinimapPatch
    {
        [HarmonyPatch(typeof(Minimap), "Start")]
        [HarmonyAfter("ZenDragon.ZenMap")]
        [HarmonyPostfix]
        static void Minimap_Start_Postfix()
        {
            CaptureButton.Create();
            MapCompileButtons.Create();
        }

        /// <summary>
        /// Map opened from a portable map item (ZenMap parchment, Vegvisir
        /// reveal, etc. — ZenMap funnels these through Minimap.ShowPointOnMap).
        /// When the "Allow From Map Items" config is on, treat the read point
        /// as an active source so compile mode's "ADD TILE" enables.
        /// CartographyTablePatch.OnRead_Postfix overrides this in the same
        /// frame for a real table read (it fires after ShowPointOnMap), so the
        /// table position always wins when both apply.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "ShowPointOnMap")]
        [HarmonyPostfix]
        static void ShowPointOnMap_Postfix(Vector3 point)
        {
            if (!ModHelpers.EffectiveConfig.AllowCompileFromMapItems) return;
            MapCompileSession.SetActiveTable(point);
        }

        /// <summary>
        /// During a SEND / COPY capture, re-activate every named pin's caption
        /// GameObject AFTER ZenMap's UpdateDynamicPins postfix runs. ZenMap
        /// hides every caption that isn't hovered or LeftShift-held
        /// (ZenMap.cs:3173, via ShowLabel(IsCursorOver || IsLabelTogglePressed))
        /// EVERY frame, so a one-shot SetActive(true) in our coroutine survives
        /// only the frame it ran on — subsequent captures see ZenMap's hide
        /// state and our SetActive happens too late. This postfix runs after
        /// ZenMap's via HarmonyAfter and undoes the hide for the duration of
        /// the capture (gated by <see cref="PinCaptureFilter.s_forceShowLabels"/>).
        /// Skips pins with no name (their caption is legitimately empty) and
        /// pins that PinCaptureFilter disabled at component level (Player /
        /// Death) — those are already invisible regardless of GO active state.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "UpdateDynamicPins")]
        [HarmonyAfter("ZenDragon.ZenMap")]
        [HarmonyPostfix]
        static void UpdateDynamicPins_ForceCaptionsDuringCapture(Minimap __instance)
        {
            if (!PinCaptureFilter.s_forceShowLabels) return;
            var pins = __instance?.m_pins;
            if (pins == null) return;
            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                var pin = pins[i];
                if (pin == null) continue;
                var captionGo = pin.m_NamePinData?.PinNameGameObject;
                if (captionGo == null) continue;
                if (string.IsNullOrEmpty(pin.m_name)) continue;
                var tmp = pin.m_NamePinData?.PinNameText;
                if (tmp != null && !tmp.enabled) continue;
                if (!captionGo.activeSelf) captionGo.SetActive(true);
            }
        }

        /// <summary>
        /// Show/hide overlay UI when map mode changes. Also clears the active
        /// cartography-table ref when the map closes so "ADD TILE" disables
        /// until the player re-opens the map at a table.
        /// </summary>
        [HarmonyPatch(typeof(Minimap), "SetMapMode")]
        [HarmonyPostfix]
        static void SetMapMode_Postfix(Minimap.MapMode mode)
        {
            bool large = mode == Minimap.MapMode.Large;
            CaptureButton.SetVisible(large);
            MapCompileButtons.SetVisible(large);
            if (!large)
            {
                MapCompileSession.ClearActiveTable();
                // Keep the compiled result so reopening the map restores the
                // panel — a plain Hide() here drops _result and leaves the
                // session stuck in Reviewing (no panel, no START/RESUME).
                if (MapCompileResultPanel.IsVisible)
                    MapCompileResultPanel.HideKeepingResult();
            }
            else if (MapCompileSession.CurrentState == MapCompileSession.State.Reviewing
                     && !MapCompileResultPanel.IsVisible
                     && !MapCompileResultPanel.HasPendingResult)
            {
                // Reviewing but the compiled result was somehow lost: drop back
                // to compile mode (tiles intact) so the player gets the
                // ADD TILE / COMPILE / CANCEL panel instead of a dead screen —
                // no progress is discarded. When the result IS preserved,
                // MapCompileButtons shows a RESUME COMPILE button (set up by
                // SetVisible above) and the panel stays hidden until recalled.
                MapCompileSession.ReturnToCompiling();
                MapCompileButtons.SetVisible(true);
            }
        }
    }
}
