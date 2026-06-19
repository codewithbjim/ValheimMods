using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Snapshots every Minimap pin the live map would draw — respecting the
    /// player's icon-type toggle and the shared-map gate, but NOT the viewport
    /// check — so they can be stamped onto the finished composite. The
    /// composite spans every captured tile's world region; a pin in a
    /// previously-captured tile is off-screen at a later table but must still
    /// appear on the composite.
    ///
    /// Stamping once on the composite (vs. baking per tile) makes pins render
    /// uniformly at correct scale regardless of how many tiles cover their
    /// world position. Baked pins rode through MapCompositor.CompositeTile's
    /// chroma-pick and got inconsistently overwritten in tile-overlap regions.
    ///
    /// MUST be called on the main thread before the composite goes off-thread —
    /// it reads live Unity state (Minimap.m_pins + pin UI components).
    /// </summary>
    public static class MapCompilePinSnapshot
    {
        // Strips TMP rich-text tags (<color=...>, <size=...>, <b>, <style=...>,
        // etc.) from a localized pin name. Valheim's in-game pin TMP renders
        // boss / location names as plain text ("The Elder"), but the same
        // localized string viewed through our compose path picks up styling
        // (e.g. orange "THE ELDER" caps) — likely from a TMP_StyleSheet or
        // style tag that resolves differently in the live map vs the headless
        // stamp. Stripping tags up front gives us the in-game look (white face
        // + black outline) without depending on which stylesheet TMP picks.
        private static readonly Regex s_richText =
            new Regex("<[^>]+>", RegexOptions.Compiled);


        /// <summary>
        /// Capture every pin that would normally appear on the player's large
        /// map — using Valheim's own filter rules (icon-type toggle, shared-map
        /// gate) MINUS the viewport check. The composite spans every captured
        /// tile's world region, so a pin sitting in a previously-captured tile
        /// (currently off-screen at the new table) must still land on the
        /// composite. Returns the on-screen reference width so the stamp can
        /// convert per-pin screen sizes into composite px.
        /// </summary>
        public static List<MapCompositor.PinDraw> Capture(out float referenceScreenWidth)
        {
            referenceScreenWidth = 0f;

            var list = new List<MapCompositor.PinDraw>();
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_pins == null) return list;

            // Reference width: the on-screen large-map RawImage width. The
            // composite covers the same in-world content the player sees, so
            // (composite_W / referenceScreenWidth) gives the px-per-px scale
            // for an icon to appear roughly the same size on both. Falls back
            // to Screen.width when the map isn't drawn — keeps the stamp size
            // sane rather than zero.
            if (minimap.m_mapImageLarge != null)
            {
                var corners = new Vector3[4];
                minimap.m_mapImageLarge.rectTransform.GetWorldCorners(corners);
                float w = Mathf.Abs(corners[2].x - corners[0].x);
                if (w > 1f) referenceScreenWidth = w;
            }
            if (referenceScreenWidth <= 1f) referenceScreenWidth = Screen.width;

            // Mirror Valheim's UpdatePins gates (Minimap.cs:1359) MINUS the
            // viewport check — pins in already-captured-but-currently-off-screen
            // regions still belong on the composite. UpdatePins does NOT filter
            // by pin.m_save, so we don't either: location pins (bosses,
            // traders), spawn/bed markers, other-player markers and ZenMap's
            // auto table pins are all save:false but ARE drawn on the live map
            // and the player expects them on the composite.
            bool[] visibleTypes = minimap.m_visibleIconTypes;
            bool sharedFadeOn = minimap.m_sharedMapDataFade > 0f;
            float baseSize = minimap.m_pinSizeLarge;
            // Tint shared-map pins the same way UpdatePins does so a shared
            // pin doesn't look identical to one the local player placed.
            Color32 sharedTint = (Color32)new Color(0.7f, 0.7f, 0.7f,
                0.8f * minimap.m_sharedMapDataFade);

            var pins = minimap.m_pins;
            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                var pin = pins[i];
                if (pin == null) continue;

                // Skip live other-player position markers — they're ephemeral
                // (move with each player, despawn when the player leaves) and
                // baking a snapshot of someone's position onto a long-lived
                // composite is misleading.
                if (pin.m_type == Minimap.PinType.Player) continue;

                // Skip death/tombstone markers — same enum value for vanilla
                // and ZenMap (ZenMap reuses PinType.Death = 4; see ZenMap's
                // PinDeath patch). Tombstones are session-scoped and clutter
                // a long-lived composite that's meant to be a wayfinding
                // reference, not a history of mishaps.
                if (pin.m_type == Minimap.PinType.Death) continue;

                // Skip the bed/spawn pin (PinType.Bed, Minimap.m_spawnPointPin —
                // Minimap.cs:1109). It's always shown on the live map but marks
                // the player's spawn, not a wayfinding feature, so it's kept off
                // the composite. Matched by reference too in case a mod (e.g.
                // ZenMap) retypes it away from Bed.
                // NOTE: this is the bed pin, NOT the "start" StartTemple location
                // marker — that one stays on the composite (hidden only from the
                // PINS listing; see MapCompilePinFilter.TryGetDrawSprite).
                if (pin.m_type == Minimap.PinType.Bed
                    || (minimap.m_spawnPointPin != null && pin == minimap.m_spawnPointPin))
                    continue;

                // Icon-type filter the player toggled in the UI.
                int typeIdx = (int)pin.m_type;
                if (visibleTypes != null
                    && typeIdx >= 0 && typeIdx < visibleTypes.Length
                    && !visibleTypes[typeIdx])
                    continue;

                // Shared-map pin gate: skip when shared-map fade is off
                // (same condition UpdatePins uses to suppress them).
                if (pin.m_ownerID != 0L && !sharedFadeOn) continue;

                // Prefer the live UI element's Image.sprite over pin.m_icon —
                // some mods (and Valheim's own boss / discovered-location
                // logic) swap the displayed sprite at render time without
                // updating pin.m_icon, so reading pin.m_icon picks up the
                // ORIGINAL asset (e.g. orange filled boss head) instead of
                // what the player actually sees on the live map (e.g. white
                // outline head). Fall back to pin.m_icon when there's no
                // live element, then to GetSprite(type) as a last resort.
                Sprite sprite = MapCompilePinFilter.ResolveSprite(pin, minimap);
                if (sprite == null) continue;

                // Per-pin-kind include filter the player set in the PINS panel.
                // Grouped by the SAME sprite key the panel lists, so turning a
                // row off here drops every pin drawing that icon from the
                // composite — vanilla or mod-added alike. No-op (everything
                // included) until the player explicitly hides a kind.
                if (MapCompilePinFilter.IsExcluded(
                        MapCompilePinFilter.KeyFor(sprite, pin.m_type)))
                    continue;

                // Size from the STABLE vanilla formula — m_pinSizeLarge ×
                // (doubleSize ? 2 : 1) — for EVERY pin, NOT the live
                // ui.rect.size. The live rect is unreliable for a uniform
                // composite: only pins inside the current viewport have a live
                // m_uiElement (Valheim destroys the rest off-screen via
                // DestroyPinMarker), and mods like ZenMap rescale the on-screen
                // ones by the current zoom. Reading the rect therefore sized two
                // identical pins differently depending on which happened to be
                // on-screen when COMPILE ran (the reported "same pin, different
                // size" bug). This formula is the exact size vanilla sets per
                // UpdatePins (Minimap.cs:1371) and is position- and
                // zoom-independent, so same-type pins come out uniform. The
                // m_animate per-frame wobble is intentionally not baked for the
                // same reason.
                float size = pin.m_doubleSize ? baseSize * 2f : baseSize;
                float pxW = size, pxH = size;

                // Prefer the live Image.color over our heuristic — same
                // reason as the sprite swap above. Mods can recolour pin
                // icons (e.g. a "boss tracker" tinting boss pins differently
                // than vanilla) and we want the composite to mirror what the
                // player sees, not the unmodified white / shared-grey default.
                //
                // The catch: only pins currently on-screen have a live
                // m_iconElement. Valheim's UpdatePins destroys the UI marker
                // for every off-screen pin (DestroyPinMarker), and the
                // composite spans many tables — so the bulk of pins are
                // off-screen and m_iconElement reads null (Unity destroyed-
                // object equality). Those used to all fall back to white,
                // erasing ZenMap's boss-orange / private-peach coloring.
                //
                // Fix: when there's no live element, ask ZenMap's own
                // AdjustPinColor for the hue it would have assigned (it reads
                // the same PinColor config the live map uses), and only fall
                // back to the white / shared-grey vanilla default when ZenMap
                // is absent or its pin coloring is disabled.
                Color32 tint;
                if (pin.m_iconElement != null)
                {
                    tint = pin.m_iconElement.color;
                }
                else
                {
                    Color def = pin.m_ownerID != 0L
                        ? (Color)sharedTint
                        : Color.white;
                    tint = ZenMapInterop.TryGetPinColor(pin, def, out var zc)
                        ? (Color32)zc
                        : (Color32)def;
                }

                // Valheim stores location/boss pin names as localization
                // tokens (e.g. "$enemy_gdking"). The live map UI runs them
                // through Localization before rendering — without that, the
                // raw token leaks onto the composite. After localize, strip
                // any TMP rich-text tags so boss / location captions render
                // as plain text (white face + black outline from our stamp),
                // matching the in-game minimap which shows them plain.
                string displayName = TablePinName.Clean(pin.m_name);
                if (!string.IsNullOrEmpty(displayName))
                {
                    var loc = Localization.instance;
                    if (loc != null) displayName = loc.Localize(displayName);
                    if (!string.IsNullOrEmpty(displayName))
                        displayName = s_richText.Replace(displayName, string.Empty).Trim();
                    if (string.IsNullOrEmpty(displayName)) displayName = null;
                }

                list.Add(new MapCompositor.PinDraw
                {
                    WorldX = pin.m_pos.x,
                    WorldZ = pin.m_pos.z,
                    Icon = sprite,
                    Tint = tint,
                    ScreenPxW = pxW,
                    ScreenPxH = pxH,
                    Name = displayName,
                });
            }
            return list;
        }
    }
}
