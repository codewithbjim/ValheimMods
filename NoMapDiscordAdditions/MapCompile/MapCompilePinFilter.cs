using System.Collections.Generic;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Per-session selection of which pin kinds get stamped onto the composite.
    /// Pins are grouped by the sprite they actually draw with, so a "Longship"
    /// pin and a "House" pin are separate rows — and a custom pin shipped by
    /// another mod gets its own row too, since each mod brings its own sprite.
    /// The player toggles groups off in the PINS panel
    /// (<see cref="MapCompilePinFilterPanel"/>); excluded sprite keys are then
    /// skipped by <see cref="MapCompilePinSnapshot.Capture"/> at compile time.
    ///
    /// State is "everything included" by default — a key only lands in the
    /// excluded set once the player explicitly turns it off. So a pin kind that
    /// first appears AFTER the panel was last opened is included automatically,
    /// and a forgotten exclusion can never silently hide a brand-new pin type.
    ///
    /// Persisted with the compile session: the excluded keys are written into
    /// the session's index.json (see <see cref="MapCompileSession"/>) and
    /// restored on RESUME, so the choice survives a logoff. It's still reset on
    /// START / CLEAR / DISCARD (a fresh or wiped session starts all-included).
    /// A stored key only re-applies if that pin kind still exists on resume — a
    /// since-removed mod's key is simply dormant (harmless).
    /// </summary>
    public static class MapCompilePinFilter
    {
        // Sprite-group keys the player turned OFF. Empty ⇒ include everything.
        // Stored as keys (not Sprite refs) so the choice survives pins being
        // created/destroyed as the live map pans — Valheim destroys the
        // m_iconElement of every off-screen pin, so a Sprite ref would dangle.
        private static readonly HashSet<string> _excluded = new HashSet<string>();

        /// <summary>One distinct pin icon present in the captured tiles.</summary>
        public struct PinGroup
        {
            public string Key;         // stable grouping key (see KeyFor)
            public string DisplayName; // human-friendly label for the row
            public Sprite Icon;        // representative sprite for the thumbnail
            public int Count;          // how many in-tile pins share this icon
            public bool Included;      // current include state (snapshot)
        }

        /// <summary>Count of currently-excluded keys (cheap; no pin scan).</summary>
        public static int ExcludedCount => _excluded.Count;

        public static bool IsExcluded(string key) =>
            !string.IsNullOrEmpty(key) && _excluded.Contains(key);

        public static void SetIncluded(string key, bool included)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (included) _excluded.Remove(key);
            else _excluded.Add(key);
        }

        /// <summary>Clear all exclusions — everything back to included.</summary>
        public static void Reset() => _excluded.Clear();

        /// <summary>
        /// The excluded sprite-keys, for persisting with the session index.
        /// </summary>
        public static IReadOnlyCollection<string> ExcludedKeys => _excluded;

        /// <summary>
        /// Replace the excluded set wholesale (restoring a persisted selection
        /// on RESUME). A null/empty list clears it — everything included.
        /// </summary>
        public static void SetExcludedKeys(IEnumerable<string> keys)
        {
            _excluded.Clear();
            if (keys == null) return;
            foreach (var k in keys)
                if (!string.IsNullOrEmpty(k)) _excluded.Add(k);
        }

        /// <summary>
        /// Resolve the sprite a pin will actually draw with — same precedence
        /// <see cref="MapCompilePinSnapshot"/> uses for stamping: the live UI
        /// element's sprite (mods/Valheim swap it at render time without
        /// touching m_icon), then pin.m_icon, then the per-type default. Reading
        /// the same sprite the stamp uses keeps the panel's grouping in lockstep
        /// with what ends up on the composite.
        /// </summary>
        public static Sprite ResolveSprite(Minimap.PinData pin, Minimap minimap)
        {
            if (pin == null) return null;
            Sprite sprite = null;
            if (pin.m_iconElement != null && pin.m_iconElement.sprite != null)
                sprite = pin.m_iconElement.sprite;
            if (sprite == null) sprite = pin.m_icon;
            if (sprite == null && minimap != null) sprite = minimap.GetSprite(pin.m_type);
            return sprite;
        }

        /// <summary>
        /// Stable grouping/exclusion key for a pin's drawn sprite. Uses the
        /// sprite asset name (stable across sessions and shared by every pin of
        /// that kind, including mod-added ones), falling back to the PinType
        /// when a sprite has no name so unnamed sprites still group sanely.
        /// </summary>
        public static string KeyFor(Sprite sprite, Minimap.PinType type)
        {
            if (sprite != null && !string.IsNullOrEmpty(sprite.name))
                return sprite.name;
            return "type:" + (int)type;
        }

        /// <summary>
        /// The PINS-panel gate: which pins appear as a toggle. Mostly mirrors
        /// <see cref="MapCompilePinSnapshot.Capture"/>'s draw gates (skip
        /// Player/Death, honor the icon-type toggle + shared-map fade, MINUS the
        /// viewport check) so the panel lists the pin kinds the compile stamps.
        /// One deliberate divergence: the always-on "start" StartTemple location
        /// marker is hidden from the listing but still composited — it isn't a
        /// player choice. (The bed/spawn pin, by contrast, is excluded from BOTH
        /// the composite and the listing.) Returns false (and a null sprite) for
        /// any pin not offered as a toggle.
        /// </summary>
        public static bool TryGetDrawSprite(Minimap minimap, Minimap.PinData pin, out Sprite sprite)
        {
            sprite = null;
            if (minimap == null || pin == null) return false;

            // Player/Death are session-scoped; the composite skips them too.
            // The bed/spawn pin (PinType.Bed, Minimap.m_spawnPointPin) is fully
            // excluded — kept off both the composite (see Capture) and this
            // listing. Matched by reference too in case a mod (e.g. ZenMap)
            // retypes it away from Bed.
            if (pin.m_type == Minimap.PinType.Player) return false;
            if (pin.m_type == Minimap.PinType.Death) return false;
            if (pin.m_type == Minimap.PinType.Bed) return false;
            if (minimap.m_spawnPointPin != null && pin == minimap.m_spawnPointPin) return false;

            // Icon-type filter the player toggled in the vanilla map legend.
            bool[] visibleTypes = minimap.m_visibleIconTypes;
            int typeIdx = (int)pin.m_type;
            if (visibleTypes != null
                && typeIdx >= 0 && typeIdx < visibleTypes.Length
                && !visibleTypes[typeIdx])
                return false;

            // Shared-map pins only count when the shared-map fade is on.
            if (pin.m_ownerID != 0L && minimap.m_sharedMapDataFade <= 0f) return false;

            sprite = ResolveSprite(pin, minimap);
            if (sprite == null) return false;

            // DIVERGENCE from Capture: the "start" StartTemple location marker
            // (added as PinType.None with GetLocationIcon — Minimap.cs:1200)
            // STAYS on the composite but is hidden from this toggle listing —
            // it's an always-on landmark, not a player choice. Identify it by
            // its location-icon sprite so it's caught regardless of the (None)
            // pin type, by reference or by sprite-asset name.
            var game = Game.instance;
            string startLoc = game != null ? game.m_StartLocation : "StartTemple";
            Sprite startIcon = !string.IsNullOrEmpty(startLoc)
                ? minimap.GetLocationIcon(startLoc) : null;
            if (startIcon != null
                && (sprite == startIcon
                    || (!string.IsNullOrEmpty(sprite.name) && sprite.name == startIcon.name)))
                return false;

            return true;
        }

        /// <summary>
        /// Build the distinct-icon group list for the PINS panel: every pin that
        /// the composite would stamp AND that lands inside one of the captured
        /// <paramref name="tiles"/>, grouped by drawn sprite. Clipping to tiles
        /// is what makes the panel show "only the pins present in the composite"
        /// — a pin in an un-captured region never reaches the output, so it
        /// shouldn't appear as a toggle. Sorted most-common first, then by name.
        /// </summary>
        public static List<PinGroup> BuildGroups(IReadOnlyList<MapCompileTile> tiles)
        {
            var groups = new List<PinGroup>();
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_pins == null) return groups;

            var byKey = new Dictionary<string, int>();
            var pins = minimap.m_pins;
            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                var pin = pins[i];
                if (!TryGetDrawSprite(minimap, pin, out var sprite)) continue;
                if (!IsInsideAnyTile(pin.m_pos.x, pin.m_pos.z, tiles)) continue;

                string key = KeyFor(sprite, pin.m_type);
                if (byKey.TryGetValue(key, out int gi))
                {
                    var g = groups[gi];
                    g.Count++;
                    groups[gi] = g;
                }
                else
                {
                    byKey[key] = groups.Count;
                    groups.Add(new PinGroup
                    {
                        Key = key,
                        DisplayName = Humanize(sprite, pin),
                        Icon = sprite,
                        Count = 1,
                        Included = !IsExcluded(key),
                    });
                }
            }

            groups.Sort((a, b) =>
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0
                    ? c
                    : string.Compare(a.DisplayName, b.DisplayName,
                        System.StringComparison.OrdinalIgnoreCase);
            });
            return groups;
        }

        // Friendly label from the sprite asset name: strip common icon-asset
        // prefixes/suffixes and tidy separators so "MapIconShip" reads as "Ship".
        // Falls back to the PinType name for unnamed sprites.
        private static string Humanize(Sprite sprite, Minimap.PinData pin)
        {
            string n = sprite != null ? sprite.name : null;
            if (string.IsNullOrEmpty(n))
                return pin != null ? pin.m_type.ToString() : "Pin";

            n = n.Replace("(Clone)", "")
                 .Replace("MapIcon", "")
                 .Replace("mapicon", "")
                 .Replace("_pin", "")
                 .Replace("pin_", "");
            n = n.Replace('_', ' ').Replace('-', ' ').Trim();
            return string.IsNullOrEmpty(n)
                ? (pin != null ? pin.m_type.ToString() : "Pin")
                : n;
        }

        // Same per-tile world-rect test MapCompileLabelStamp uses to cull pins
        // sitting in the black gaps between non-adjacent tiles. Empty tile list
        // ⇒ permissive (count everything) so the panel isn't blank before the
        // first capture; the panel only opens with at least one tile anyway.
        private static bool IsInsideAnyTile(float wx, float wz,
            IReadOnlyList<MapCompileTile> tiles)
        {
            if (tiles == null || tiles.Count == 0) return true;
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (wx >= t.WorldMin.x && wx <= t.WorldMax.x
                    && wz >= t.WorldMin.y && wz <= t.WorldMax.y)
                    return true;
            }
            return false;
        }
    }
}
