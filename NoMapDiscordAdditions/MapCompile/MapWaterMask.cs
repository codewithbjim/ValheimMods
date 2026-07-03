using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// World-space water mask for the compositor: which world positions are
    /// below the water level. Snapshotted from <c>Minimap.m_heightTexture</c>
    /// on the MAIN THREAD (Unity texture access) via <see cref="Capture"/>,
    /// then read freely from the compose worker thread (immutable arrays,
    /// no Unity API calls after capture).
    ///
    /// The compositor uses this to treat tile-overlap pixels on water
    /// differently from land: water carries a time-animated wave pattern that
    /// differs between tiles captured at different times, so hard per-pixel
    /// picks (chroma) produce visible straight-edged seams there — see the
    /// feathered merge in <see cref="MapCompositor"/>.
    /// </summary>
    public sealed class MapWaterMask
    {
        private readonly bool[] _water;    // size² — true = below water level
        private readonly int _size;        // Minimap.m_textureSize
        private readonly float _pixelSize; // world metres per map texel

        private MapWaterMask(bool[] water, int size, float pixelSize)
        {
            _water = water;
            _size = size;
            _pixelSize = pixelSize;
        }

        /// <summary>
        /// Snapshot the current water mask. MAIN THREAD ONLY. Returns null when
        /// the Minimap/ZoneSystem aren't ready — callers pass that straight
        /// through to the compositor, which then simply skips water feathering
        /// (previous behaviour) instead of failing the compose.
        /// </summary>
        public static MapWaterMask Capture()
        {
            var mm = Minimap.instance;
            var zs = ZoneSystem.instance;
            if (mm == null || zs == null) return null;

            Texture2D heightTex = mm.m_heightTexture;
            int size = mm.m_textureSize;
            if (heightTex == null || size <= 0) return null;

            // m_heightTexture is RHalf — GetPixels returns raw world height in
            // .r (same read MapStyleContext.CaptureLayers does).
            Color[] px;
            try { px = heightTex.GetPixels(); }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Water mask capture failed: {ex.Message}");
                return null;
            }
            if (px.Length < size * size) return null;

            float waterLevel = zs.m_waterLevel;
            var water = new bool[size * size];
            for (int i = 0; i < water.Length; i++)
                water[i] = px[i].r < waterLevel;

            return new MapWaterMask(water, size, mm.m_pixelSize);
        }

        /// <summary>
        /// World coordinate (x or z) → mask row/column index, or -1 when the
        /// position falls outside the map texture. Mirrors
        /// <c>Minimap.WorldToPixel</c> (world / pixelSize + size/2, rounded).
        /// Split out from <see cref="Cell"/> so the compositor can precompute
        /// per-row / per-column indices instead of converting per pixel.
        /// </summary>
        public int CoordToIndex(float worldCoord)
        {
            int c = Mathf.RoundToInt(worldCoord / _pixelSize + _size / 2);
            return (c < 0 || c >= _size) ? -1 : c;
        }

        /// <summary>Mask lookup by precomputed indices (row = world Z axis).</summary>
        public bool Cell(int rowZ, int colX) => _water[rowZ * _size + colX];
    }
}
