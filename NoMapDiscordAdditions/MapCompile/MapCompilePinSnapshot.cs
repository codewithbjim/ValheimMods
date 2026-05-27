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
                Sprite sprite = null;
                if (pin.m_iconElement != null && pin.m_iconElement.sprite != null)
                    sprite = pin.m_iconElement.sprite;
                if (sprite == null) sprite = pin.m_icon;
                if (sprite == null) sprite = minimap.GetSprite(pin.m_type);
                if (sprite == null) continue;

                // Size: prefer the live UI element's actual rect (so animated /
                // doubleSize / m_worldSize pins keep their custom dimensions),
                // and fall back to m_pinSizeLarge × (doubleSize ? 2 : 1) when
                // there's no live element — that's what Valheim would have set
                // it to per UpdatePins.
                float pxW, pxH;
                var ui = pin.m_uiElement;
                if (ui != null && ui.gameObject.activeInHierarchy)
                {
                    Vector2 sz = ui.rect.size;
                    pxW = sz.x > 0f ? sz.x : baseSize;
                    pxH = sz.y > 0f ? sz.y : baseSize;
                }
                else
                {
                    float size = pin.m_doubleSize ? baseSize * 2f : baseSize;
                    pxW = pxH = size;
                }

                // Prefer the live Image.color over our heuristic — same
                // reason as the sprite swap above. Mods can recolour pin
                // icons (e.g. a "boss tracker" tinting boss pins differently
                // than vanilla) and we want the composite to mirror what the
                // player sees, not the unmodified white / shared-grey default.
                Color32 tint;
                if (pin.m_iconElement != null)
                    tint = pin.m_iconElement.color;
                else
                    tint = pin.m_ownerID != 0L ? sharedTint : (Color32)Color.white;

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
