using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Wrapper around <see cref="MapCaptureTexture"/> that also records the
    /// world-space rect a tile covers. Snapshots <c>m_mapImageLarge.uvRect</c>
    /// up front so the rect always matches what was actually rendered, even
    /// if anything else mutates the uvRect mid-frame.
    /// </summary>
    public static class MapCompileCapture
    {
        public struct Result
        {
            public byte[] Png;
            public int Width;
            public int Height;
            public Vector2 WorldMin;
            public Vector2 WorldMax;
            // True when every in-world map pixel inside the captured viewport is
            // explored (per Valheim's own m_explored state). A partial tile is
            // demoted by the compositor so its fog can never overwrite explored
            // terrain from a complete tile.
            public bool FullyMapped;
        }

        public static bool TryCapture(out Result result)
        {
            result = default;

            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mapImageLarge == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: Minimap unavailable.");
                return false;
            }
            if (minimap.m_mode != Minimap.MapMode.Large)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: large map not active.");
                return false;
            }

            // Snapshot uvRect BEFORE the capture so the rect we record matches
            // exactly what gets rasterized — DrawMapBase reads the same uvRect
            // synchronously inside CaptureMap, so they stay consistent.
            Rect uv = minimap.m_mapImageLarge.uvRect;

            // Never bake captions into compile tiles — they'd be eaten by the
            // chroma-pick where tiles overlap. Compile mode stamps labels once
            // onto the finished composite instead (see MapCompileLabelStamp,
            // gated by Pin Label "Show on Compile Mode" + "Enabled").
            byte[] png = MapCaptureTexture.CaptureMap(includePinLabels: false);
            if (png == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: MapCaptureTexture returned null.");
                return false;
            }

            MapCompileTile.ComputeWorldRect(minimap, uv, out var wmin, out var wmax);

            result = new Result
            {
                Png = png,
                Width = MapCaptureTexture.OutputWidth,
                Height = MapCaptureTexture.OutputHeight,
                WorldMin = wmin,
                WorldMax = wmax,
                FullyMapped = IsRegionFullyMapped(minimap, uv),
            };
            return true;
        }

        // Endless-ocean radius. Map pixels whose world position is past this
        // (WorldGenerator.worldSize) can never be reached, so they stay
        // unexplored forever — excluding them keeps a genuinely complete tile
        // that merely shows some off-world void from being flagged partial.
        private const float WorldRadius = 10000f;

        /// <summary>
        /// True when every reachable (in-world) map pixel covered by the
        /// captured uvRect is explored, using Valheim's authoritative
        /// <see cref="Minimap.m_explored"/> state — mod-independent and
        /// unaffected by whatever fog colour ZenMap renders. Off-world pixels
        /// (endless ocean past <see cref="WorldRadius"/>) are ignored.
        /// </summary>
        private static bool IsRegionFullyMapped(Minimap minimap, Rect uv)
        {
            var explored = minimap.m_explored;
            int size = minimap.m_textureSize;
            if (explored == null || size <= 0 || explored.Length < size * size)
                return false; // unknown → treat as partial (safe: loses priority)

            float uMin = Mathf.Clamp01(uv.xMin);
            float vMin = Mathf.Clamp01(uv.yMin);
            float uMax = Mathf.Clamp01(uv.xMax);
            float vMax = Mathf.Clamp01(uv.yMax);

            int x0 = Mathf.Clamp(Mathf.FloorToInt(uMin * size), 0, size - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(uMax * size) - 1, 0, size - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(vMin * size), 0, size - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(vMax * size) - 1, 0, size - 1);

            float pixelSize = minimap.m_pixelSize;
            float half = size / 2f;
            float r2 = WorldRadius * WorldRadius;

            for (int y = y0; y <= y1; y++)
            {
                // World Z for this row (inverse of Minimap.MapPointToWorld,
                // skipping the per-pixel struct alloc).
                float wz = ((y + 0.5f) - half) * pixelSize;
                int row = y * size;
                for (int x = x0; x <= x1; x++)
                {
                    float wx = ((x + 0.5f) - half) * pixelSize;
                    if (wx * wx + wz * wz > r2) continue;   // off-world
                    if (!explored[row + x]) return false;   // a hole in coverage
                }
            }
            return true;
        }
    }
}
