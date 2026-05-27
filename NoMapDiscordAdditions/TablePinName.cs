using System.Collections.Generic;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Resolves a human-readable name for a cartography table by finding the
    /// closest named map pin sitting on it. ZenMap auto-pins tables as the
    /// vanilla "house" icon (<see cref="Minimap.PinType.Icon1"/>) and a player
    /// who renames that pin (or drops a Town pin on the table) is effectively
    /// naming the table — there is no per-instance name on <c>MapTable</c>
    /// itself, so the pin is the only carrier of an author-given label.
    /// </summary>
    public static class TablePinName
    {
        // Same icon ZenMap uses for table auto-pins. Accepts manual Town pins
        // dropped on the table too — those are the player's way of giving the
        // table a name without ZenMap involved.
        private const Minimap.PinType TablePinType = Minimap.PinType.Icon1;

        // ZenMap hides a pin-tracking GUID inside m_name as
        // "<label>\0#<32-hex-guid>" (ZenMap MapTrack.CreateTrackingLabel).
        // ZenMap strips it before display; we must too, or the GUID leaks into
        // the Discord message / baked label (the NUL is invisible, so it shows
        // as "Name#deadbeef..."). Matches ZenMap's RemoveTrackingFromLabel.
        private const string ZenMapTrackingDelimiter = "\0#";

        /// <summary>
        /// Pin display name with ZenMap's tracking suffix removed, trimmed.
        /// Returns null if the result is empty. Safe on pins ZenMap isn't
        /// tracking (no delimiter → returned as-is).
        /// </summary>
        public static string Clean(string rawPinName)
        {
            if (string.IsNullOrEmpty(rawPinName)) return null;

            int cut = rawPinName.IndexOf(ZenMapTrackingDelimiter, System.StringComparison.Ordinal);
            string name = (cut >= 0 ? rawPinName.Substring(0, cut) : rawPinName).Trim();
            return name.Length == 0 ? null : name;
        }

        // How close (in world metres, X/Z only) a named pin must sit to the
        // table to be considered "its" pin. Matches MapCompileSession's
        // DedupRadius — the codebase's existing notion of "the same table",
        // generous enough for a pin nudged slightly off the table footprint
        // without merging a neighbouring base's pin.
        private const float MatchRadius = 8f;

        /// <summary>
        /// Closest named <see cref="TablePinType"/> pin within
        /// <see cref="MatchRadius"/> of <paramref name="worldPos"/>, trimmed.
        /// Returns <c>null</c> when no pin qualifies or none has a name —
        /// callers treat that as "table has no name" (empty / omit).
        /// </summary>
        public static string Resolve(Vector3 worldPos)
        {
            Minimap mm = Minimap.instance;
            if (mm == null) return null;

            List<Minimap.PinData> pins = mm.m_pins;
            if (pins == null) return null;

            string best = null;
            float bestSq = MatchRadius * MatchRadius;

            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                Minimap.PinData pin = pins[i];
                if (pin == null || pin.m_type != TablePinType) continue;

                string name = Clean(pin.m_name);
                if (name == null) continue;

                float dx = pin.m_pos.x - worldPos.x;
                float dz = pin.m_pos.z - worldPos.z;
                float distSq = dx * dx + dz * dz;
                if (distSq > bestSq) continue;

                bestSq = distSq;
                best = name;
            }

            return best;
        }
    }
}
