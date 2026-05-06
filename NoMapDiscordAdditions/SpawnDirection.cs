using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Resolves a "{distance}m {direction}" label (e.g. "1240m NE") relative to
    /// world spawn for the position the player most recently opened the map at.
    /// The position can come from a cartography table (stamped from
    /// MapTable.OnRead) or from a portable map item (caught via
    /// Minimap.ShowPointOnMap, since ZenMap funnels both paths through there).
    /// The <see cref="ActiveSource"/> lets callers gate behavior per source —
    /// see config <c>SpawnLabelIncludeMapItemSources</c>.
    /// </summary>
    public static class SpawnDirection
    {
        public enum Source { None, CartographyTable, MapItem }

        // Below this radius from spawn the label is suppressed — there's no
        // useful direction info "5m N of Spawn" can give that the map view
        // doesn't already make obvious.
        private const float MinDistance = 200f;

        public static Vector3? ActivePos { get; private set; }
        public static Source ActiveSource { get; private set; } = Source.None;

        // Cartography table read. Always wins over a prior MapItem set in the same
        // frame — ShowPointOnMap (postfix runs first via ZenMap's Show()) marks it
        // as MapItem briefly, then this overrides to Table when OnRead's postfix runs.
        public static void SetActiveTable(Vector3 worldPos)
        {
            ActivePos = worldPos;
            ActiveSource = Source.CartographyTable;
            Debug.Log($"[NoMapDiscordAdditions] SpawnDirection: active TABLE at ({worldPos.x:F1}, {worldPos.z:F1}).");
        }

        // Portable map item (ZenMap parchment etc.). Only sets the source if it's
        // not already a Table — prevents overriding a fresh OnRead set in the same frame.
        public static void SetActiveItem(Vector3 worldPos)
        {
            if (ActiveSource == Source.CartographyTable) return;
            ActivePos = worldPos;
            ActiveSource = Source.MapItem;
            Debug.Log($"[NoMapDiscordAdditions] SpawnDirection: active ITEM at ({worldPos.x:F1}, {worldPos.z:F1}).");
        }

        public static void Clear()
        {
            if (ActiveSource != Source.None)
                Debug.Log("[NoMapDiscordAdditions] SpawnDirection: cleared.");
            ActivePos = null;
            ActiveSource = Source.None;
        }

        // Label for the currently-active source position. Honors source gating.
        // Falls back to the player's current position when no source has been
        // set — the M-key shortcut opens the map without going through any of
        // our patches (no MapTable.OnRead, no Minimap.ShowPointOnMap), so
        // ActivePos stays null. Player position is the right default since the
        // M-key map centers on the player anyway.
        // No logging here — Tick callers may invoke this every frame.
        public static string GetLabel()
        {
            // Master switch — when the cartography-table label feature is
            // disabled (server-authoritative when ServerSync is loaded), the
            // {spawnDir} placeholder in the Discord message must also be empty.
            // Treating this as the single source of truth keeps server policy
            // consistent across both the in-image labels and the chat message.
            if (!ModHelpers.EffectiveConfig.EnableCartographyTableLabels) return null;

            if (ActiveSource == Source.MapItem &&
                Plugin.SpawnLabelIncludeMapItemSources != null &&
                !Plugin.SpawnLabelIncludeMapItemSources.Value)
                return null;

            Vector3 pos;
            if (ActivePos != null)
            {
                pos = ActivePos.Value;
            }
            else
            {
                var player = Player.m_localPlayer;
                if (player == null) return null;
                pos = player.transform.position;
            }
            return GetLabelForPos(pos);
        }

        // Label for an arbitrary world position. Used by per-pin labelers that
        // need to compute distance/direction for many pins each frame, not just
        // the active source. Returns null when the point is within MinDistance
        // of spawn (no useful info to show that close).
        public static string GetLabelForPos(Vector3 pos)
        {
            Vector3 spawn = GetSpawnPos();

            float dx = pos.x - spawn.x;
            float dz = pos.z - spawn.z;
            float distSq = dx * dx + dz * dz;
            if (distSq <= MinDistance * MinDistance) return null;

            float dist = Mathf.Sqrt(distSq);
            float angle = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg; // N=0, E=90, S=180, W=270
            if (angle < 0f) angle += 360f;

            string dir = AngleToDirection(angle);
            // Compass bearing in parens — precise direction alongside the
            // human-readable cardinal. 360° normalizes to 0° so North reads
            // consistently.
            int bearing = Mathf.RoundToInt(angle) % 360;
            string dirWithBearing = $"{dir} ({bearing}°)";

            bool includeDist = Plugin.SpawnLabelIncludeDistance == null ||
                               Plugin.SpawnLabelIncludeDistance.Value;
            return includeDist
                ? $"{Mathf.RoundToInt(dist)}m {dirWithBearing}"
                : dirWithBearing;
        }

        private static Vector3 GetSpawnPos()
        {
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetLocationIcon("StartTemple", out Vector3 pos))
                return pos;
            return Vector3.zero;
        }

        private static string AngleToDirection(float a)
        {
            if (a < 22.5f || a >= 337.5f) return "North";
            if (a < 67.5f) return "NorthEast";
            if (a < 112.5f) return "East";
            if (a < 157.5f) return "SouthEast";
            if (a < 202.5f) return "South";
            if (a < 247.5f) return "SouthWest";
            if (a < 292.5f) return "West";
            return "NorthWest";
        }
    }
}
