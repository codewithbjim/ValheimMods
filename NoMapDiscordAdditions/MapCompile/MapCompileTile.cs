using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// One captured tile: a PNG of the visible large map plus the world-space
    /// rect it covers. PNG bytes are NOT held in memory — each tile is
    /// 1920x1080, so a session of many tiles would balloon the heap. The bytes
    /// live on disk at <see cref="PngPath"/> and are re-read only when the
    /// merge step runs.
    /// </summary>
    public class MapCompileTile
    {
        public int Index;
        public Vector2 WorldMin;       // (X, Z) of the world rect's lower-left
        public Vector2 WorldMax;       // (X, Z) of the world rect's upper-right
        public Vector3 TableWorldPos;  // table the player was at when this was captured
        // Name of the pin on that table at capture time, or null if unnamed.
        // Captured here (not re-derived at compose time) because a resumed
        // session composites tiles whose tables may be far away / unloaded,
        // with no pin in Minimap.m_pins to look up. Null for older sessions.
        public string TableName;
        public int PixelWidth;
        public int PixelHeight;
        public string PngPath;
        public long TimestampUnixMs;

        // False when captured over a not-fully-explored area (fog/void within
        // the world). The compositor demotes partial tiles so their fog never
        // paints over explored terrain from a complete tile.
        public bool FullyMapped = true;

        // Set only for tiles brought in from another player's share (via the
        // incoming folder). Local table captures leave both null. ImportKey is
        // a stable identity used to dedup re-imports of the same shared tile;
        // SourcePlayer is the original capturer's name (display only).
        public string ImportKey;
        public string SourcePlayer;

        public bool IsImported => ImportKey != null;

        public Vector2 WorldSize => WorldMax - WorldMin;

        /// <summary>
        /// Convert the minimap's uvRect at capture time to a world-space rect,
        /// using the inverse of Minimap.MapPointToWorld
        /// (Minimap.cs:1538 in the publicized assembly).
        /// uvRect uses normalized [0,1] coords; the texture maps symmetrically
        /// around world origin via m_pixelSize and m_textureSize.
        /// </summary>
        public static void ComputeWorldRect(
            Minimap minimap, Rect uvRect,
            out Vector2 worldMin, out Vector2 worldMax)
        {
            float uMin = Mathf.Clamp01(uvRect.xMin);
            float vMin = Mathf.Clamp01(uvRect.yMin);
            float uMax = Mathf.Clamp01(uvRect.xMax);
            float vMax = Mathf.Clamp01(uvRect.yMax);

            Vector3 wMin = minimap.MapPointToWorld(uMin, vMin); // returns (x, 0, z)
            Vector3 wMax = minimap.MapPointToWorld(uMax, vMax);

            worldMin = new Vector2(wMin.x, wMin.z);
            worldMax = new Vector2(wMax.x, wMax.z);
        }
    }
}
