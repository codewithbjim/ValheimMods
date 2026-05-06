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

            byte[] png = MapCaptureTexture.CaptureMap();
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
            };
            return true;
        }
    }
}
