using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// One stylized-map render job. <see cref="CaptureLayers"/> snapshots the
    /// Minimap data layers on the main thread; <see cref="RunPipeline"/> does
    /// the per-pixel work and is safe on a background thread (plain arrays +
    /// stateless math — Color/Color32/Mathf, the calls MapPrinter runs off-thread).
    ///
    /// Crucially the render covers ONLY the captured viewport (the clamped
    /// uvRect of the large map), sampled directly to the capture's pixel size.
    /// A zoomed-in capture is therefore rendered at full output resolution for
    /// the visible region — not cut from a whole-world render and upscaled,
    /// which was what made earlier styled captures look blurry.
    /// </summary>
    internal sealed partial class MapStyleContext
    {
        // MapPrinter's parchment base colour.
        private static readonly Color32 Parchment = new Color32(203, 155, 87, 255);

        // ── Result (read by MapStyleRender after the thread joins) ──────────
        public int OutWidth;
        public int OutHeight;
        public bool Failed;
        public string Error;
        public Color32[] Result;

        // ── Source layers + scalars (captured on the main thread) ───────────
        private Plugin.MapStyleMode _style;
        private int _srcSize;
        private float _waterLevel;
        private Color32 _heathColor;
        private Color32[] _srcBiome;     // _srcSize², biome colours
        private float[] _srcHeight;      // _srcSize², raw world height
        private bool[] _srcExplored;     // _srcSize², explored | exploredOthers
        private float _u0, _v0, _u1, _v1; // clamped uv span of the captured viewport

        // ── Working layers at OutWidth × OutHeight (built in RunPipeline) ───
        private int _width;
        private int _height;
        private float _shadowScale;
        private Color32[] _biomeLayer;
        private Color32[] _biomeNearest;   // hard biome colours, for ocean classification
        private Color32[] _heightLayer;
        private Color32[] _fogLayer;
        private float[] _shallowField;     // 0..1 smooth deep-ocean -> shallow-water mask

        // ════════════════════════════════════════════════════════════════════
        //  Layer capture — MAIN THREAD ONLY (Unity texture access)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Snapshots the Minimap data layers and the viewport to render, or
        /// returns null if the map isn't ready / the viewport is degenerate.
        /// <paramref name="uvRect"/> is the large map's live uvRect; it is
        /// clamped to the in-world [0,1] span (the same clamp DrawMapBase uses).
        /// </summary>
        public static MapStyleContext CaptureLayers(Rect uvRect, int outWidth, int outHeight)
        {
            if (outWidth < 64 || outHeight < 64) return null;

            var mm = Minimap.instance;
            if (mm == null) return null;
            var zs = ZoneSystem.instance;
            if (zs == null) return null;

            int srcSize = mm.m_textureSize;
            if (srcSize <= 0) return null;
            int count = srcSize * srcSize;

            // The in-world part of the viewport — matches DrawMapBase's clamp,
            // so the styled base frames identically and pins still line up.
            float u0 = Mathf.Clamp01(uvRect.xMin);
            float v0 = Mathf.Clamp01(uvRect.yMin);
            float u1 = Mathf.Clamp01(uvRect.xMax);
            float v1 = Mathf.Clamp01(uvRect.yMax);
            if (u1 - u0 < 1e-5f || v1 - v0 < 1e-5f) return null;

            Texture2D biomeTex = mm.m_mapTexture;
            Texture2D heightTex = mm.m_heightTexture;
            bool[] explored = mm.m_explored;
            if (biomeTex == null || heightTex == null || explored == null) return null;
            if (explored.Length < count) return null;

            Color32[] biome = biomeTex.GetPixels32();
            if (biome.Length < count) return null;

            // m_heightTexture is RHalf — GetPixels gives the height in .r
            // (GenerateWorldMap stores the raw biome height there).
            Color[] heightPx = heightTex.GetPixels();
            if (heightPx.Length < count) return null;
            var height = new float[count];
            for (int i = 0; i < count; i++)
                height[i] = heightPx[i].r;

            // Fold shared-map exploration in, and copy off the live array.
            bool[] others = mm.m_exploredOthers;
            bool hasOthers = others != null && others.Length >= count;
            var exploredCopy = new bool[count];
            for (int i = 0; i < count; i++)
                exploredCopy[i] = explored[i] || (hasOthers && others[i]);

            ModLog.Info($"[NoMapDiscordAdditions] Map style: source {srcSize}px, viewport "
                + $"{(u1 - u0) * 100f:F0}%x{(v1 - v0) * 100f:F0}% -> {outWidth}x{outHeight} output.");

            return new MapStyleContext
            {
                _style = Plugin.MapStyle.Value,
                _srcSize = srcSize,
                _srcBiome = biome,
                _srcHeight = height,
                _srcExplored = exploredCopy,
                _waterLevel = zs.m_waterLevel,
                _heathColor = mm.m_heathColor,
                _u0 = u0, _v0 = v0, _u1 = u1, _v1 = v1,
                OutWidth = outWidth,
                OutHeight = outHeight,
            };
        }

        // ════════════════════════════════════════════════════════════════════
        //  Pipeline entry — BACKGROUND THREAD
        // ════════════════════════════════════════════════════════════════════

        public void RunPipeline()
        {
            try
            {
                _width = OutWidth;
                _height = OutHeight;
                _shadowScale = Mathf.Max(_width, _height) / 2048f;

                _biomeLayer = SampleColour(_srcBiome, _srcSize);
                _biomeNearest = SampleColourNearest(_srcBiome, _srcSize);
                _heightLayer = SampleHeight(_srcHeight, _srcSize, _waterLevel);
                _fogLayer = SampleFog(_srcExplored, _srcSize);

                // Soften the low-resolution biome grid into smooth colour
                // regions. Bilinear magnification leaves cell-sized facets
                // (the "boxy" look); a two-pass masked blur of about half a
                // source cell rounds the facet creases off without averaging
                // whole biomes together. The sliding-window blur makes the
                // radius free. Only the colour fill is blurred — contour and
                // hillshade line work is drawn from other layers and stays
                // crisp.
                float mag = _width / Mathf.Max(1f, (_u1 - _u0) * _srcSize);
                int biomeBlur = Mathf.Clamp(Mathf.RoundToInt(mag * 0.35f), 1, 28);
                _biomeLayer = BlurBiomeColours(_biomeLayer, biomeBlur);
                _biomeLayer = BlurBiomeColours(_biomeLayer, biomeBlur);

                // Soften the deep-ocean -> shallow-water tint boundary the
                // same way. The shallow mask is derived from the blocky
                // nearest biome layer, so blur it into a smooth gradient
                // before it drives the ocean tint downstream.
                int shallowBlur = Mathf.Clamp(Mathf.RoundToInt(mag * 0.8f), 2, 48);
                _shallowField = BuildShallowField(shallowBlur);

                switch (_style)
                {
                    case Plugin.MapStyleMode.Chart:
                        Result = GenerateChartMap();
                        break;
                    case Plugin.MapStyleMode.Topographical:
                        Result = GenerateTopographicalMap();
                        break;
                    case Plugin.MapStyleMode.Satellite:
                        Result = GenerateSatelliteImage();
                        break;
                    default:
                        Result = GenerateOldMap();
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Failed = true;
                Error = ex.Message;
                Result = null;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Viewport sampling — clamped uv span -> OutWidth × OutHeight
        // ════════════════════════════════════════════════════════════════════

        // Bilinear-sample the biome layer's viewport sub-rectangle. Contours,
        // grain and lines are drawn AFTER this at output resolution, so they
        // stay crisp even where the source data is magnified.
        private Color32[] SampleColour(Color32[] src, int srcSize)
        {
            var dst = new Color32[_width * _height];
            for (int dy = 0; dy < _height; dy++)
            {
                float v = _v0 + (dy + 0.5f) / _height * (_v1 - _v0);
                float sy = v * srcSize - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, srcSize - 1);
                int y1 = Mathf.Min(y0 + 1, srcSize - 1);
                float ty = Mathf.Clamp01(sy - y0);
                int drow = dy * _width;
                for (int dx = 0; dx < _width; dx++)
                {
                    float u = _u0 + (dx + 0.5f) / _width * (_u1 - _u0);
                    float sx = u * srcSize - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, srcSize - 1);
                    int x1 = Mathf.Min(x0 + 1, srcSize - 1);
                    float tx = Mathf.Clamp01(sx - x0);
                    Color c00 = src[y0 * srcSize + x0], c10 = src[y0 * srcSize + x1];
                    Color c01 = src[y1 * srcSize + x0], c11 = src[y1 * srcSize + x1];
                    dst[drow + dx] = Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
                }
            }
            return dst;
        }

        // Nearest-sample the biome layer — keeps hard biome colours (no
        // bilinear blend) so biome boundaries can be detected crisply.
        private Color32[] SampleColourNearest(Color32[] src, int srcSize)
        {
            var dst = new Color32[_width * _height];
            for (int dy = 0; dy < _height; dy++)
            {
                float v = _v0 + (dy + 0.5f) / _height * (_v1 - _v0);
                int sy = Mathf.Clamp((int)(v * srcSize), 0, srcSize - 1);
                int srow = sy * srcSize;
                int drow = dy * _width;
                for (int dx = 0; dx < _width; dx++)
                {
                    float u = _u0 + (dx + 0.5f) / _width * (_u1 - _u0);
                    int sx = Mathf.Clamp((int)(u * srcSize), 0, srcSize - 1);
                    dst[drow + dx] = src[srow + sx];
                }
            }
            return dst;
        }

        // Bilinear-sample the raw float heights for the viewport, then encode
        // into MapPrinter's Color32 height convention: above water -> red
        // (height / 512), below water -> blue (depth / 256).
        private Color32[] SampleHeight(float[] src, int srcSize, float waterLevel)
        {
            var dst = new Color32[_width * _height];
            for (int dy = 0; dy < _height; dy++)
            {
                float v = _v0 + (dy + 0.5f) / _height * (_v1 - _v0);
                float sy = v * srcSize - 0.5f;
                int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, srcSize - 1);
                int y1 = Mathf.Min(y0 + 1, srcSize - 1);
                float ty = Mathf.Clamp01(sy - y0);
                int drow = dy * _width;
                for (int dx = 0; dx < _width; dx++)
                {
                    float u = _u0 + (dx + 0.5f) / _width * (_u1 - _u0);
                    float sx = u * srcSize - 0.5f;
                    int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, srcSize - 1);
                    int x1 = Mathf.Min(x0 + 1, srcSize - 1);
                    float tx = Mathf.Clamp01(sx - x0);
                    float h0 = Mathf.Lerp(src[y0 * srcSize + x0], src[y0 * srcSize + x1], tx);
                    float h1 = Mathf.Lerp(src[y1 * srcSize + x0], src[y1 * srcSize + x1], tx);
                    float d = Mathf.Lerp(h0, h1, ty) - waterLevel;
                    if (d > 0f)
                        dst[drow + dx] = new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(d / 512f * 255f), 0, 255), 0, 0, 255);
                    else
                        dst[drow + dx] = new Color32(
                            0, 0, (byte)Mathf.Clamp(Mathf.RoundToInt(-d / 256f * 255f), 0, 255), 255);
                }
            }
            return dst;
        }

        // Nearest-sample the exploration mask for the viewport. Explored pixels
        // stay the default Color32 (0,0,0,0) == Color.clear; unexplored become
        // opaque grey, matching MapPrinter's fog map.
        private Color32[] SampleFog(bool[] explored, int srcSize)
        {
            var dst = new Color32[_width * _height];
            Color32 fogged = new Color32(128, 128, 128, 255);
            for (int dy = 0; dy < _height; dy++)
            {
                float v = _v0 + (dy + 0.5f) / _height * (_v1 - _v0);
                int sy = Mathf.Clamp((int)(v * srcSize), 0, srcSize - 1);
                int srow = sy * srcSize;
                int drow = dy * _width;
                for (int dx = 0; dx < _width; dx++)
                {
                    float u = _u0 + (dx + 0.5f) / _width * (_u1 - _u0);
                    int sx = Mathf.Clamp((int)(u * srcSize), 0, srcSize - 1);
                    if (!explored[srow + sx])
                        dst[drow + dx] = fogged;
                }
            }
            return dst;
        }

        // Masked box blur of the biome colour fill. Only land pixels
        // contribute to the average, so the ocean's white never bleeds into
        // coastal land; water pixels are left untouched (they are overpainted
        // by the ocean texture downstream). Dissolves the boxy look of the
        // low-resolution biome grid into smooth organic colour regions.
        //
        // Both passes use a sliding-window accumulator: stepping one pixel
        // adds the entering sample and drops the leaving one, so the cost is
        // O(1) per pixel no matter how large the radius — the radius can scale
        // with magnification without the blur ever getting slow.
        private Color32[] BlurBiomeColours(Color32[] src, int radius)
        {
            if (radius < 1) return src;
            int n = _width * _height;
            var hr = new float[n];
            var hg = new float[n];
            var hb = new float[n];
            var hw = new float[n];

            // Horizontal pass — sliding-window masked sums + land sample count.
            for (int row = 0; row < _height; row++)
            {
                int rb = row * _width;
                float sr = 0f, sg = 0f, sb = 0f, w = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int s = rb + Mathf.Clamp(k, 0, _width - 1);
                    if (_heightLayer[s].b > 0) continue; // skip water
                    Color32 p = src[s];
                    sr += p.r; sg += p.g; sb += p.b; w += 1f;
                }
                hr[rb] = sr; hg[rb] = sg; hb[rb] = sb; hw[rb] = w;
                for (int col = 1; col < _width; col++)
                {
                    int add = rb + Mathf.Clamp(col + radius, 0, _width - 1);
                    int sub = rb + Mathf.Clamp(col - radius - 1, 0, _width - 1);
                    if (_heightLayer[add].b <= 0)
                    {
                        Color32 p = src[add];
                        sr += p.r; sg += p.g; sb += p.b; w += 1f;
                    }
                    if (_heightLayer[sub].b <= 0)
                    {
                        Color32 p = src[sub];
                        sr -= p.r; sg -= p.g; sb -= p.b; w -= 1f;
                    }
                    int idx = rb + col;
                    hr[idx] = sr; hg[idx] = sg; hb[idx] = sb; hw[idx] = w;
                }
            }

            // Vertical pass — slide the same window over the horizontal sums,
            // then divide by the total land sample count for a true masked
            // box average.
            var dst = (Color32[])src.Clone();
            for (int col = 0; col < _width; col++)
            {
                float sr = 0f, sg = 0f, sb = 0f, w = 0f;
                for (int k = -radius; k <= radius; k++)
                {
                    int s = Mathf.Clamp(k, 0, _height - 1) * _width + col;
                    sr += hr[s]; sg += hg[s]; sb += hb[s]; w += hw[s];
                }
                if (_heightLayer[col].b <= 0 && w > 0f)
                    dst[col] = new Color32((byte)(sr / w), (byte)(sg / w), (byte)(sb / w), 255);
                for (int row = 1; row < _height; row++)
                {
                    int add = Mathf.Clamp(row + radius, 0, _height - 1) * _width + col;
                    int sub = Mathf.Clamp(row - radius - 1, 0, _height - 1) * _width + col;
                    sr += hr[add] - hr[sub];
                    sg += hg[add] - hg[sub];
                    sb += hb[add] - hb[sub];
                    w += hw[add] - hw[sub];
                    int idx = row * _width + col;
                    if (_heightLayer[idx].b > 0) continue; // water: leave as-is
                    if (w > 0f)
                        dst[idx] = new Color32(
                            (byte)(sr / w), (byte)(sg / w), (byte)(sb / w), 255);
                }
            }
            return dst;
        }

        // Smooth 0..1 field separating water that belongs to a land biome
        // (coastal shallows, lakes, inlets -> 1) from open ocean (-> 0).
        // It starts as a hard mask off the blocky nearest biome layer, then a
        // two-pass box blur turns the biome-grid step into a soft gradient so
        // the shallow-water tint fades into the deep sea instead of stepping.
        private float[] BuildShallowField(int radius)
        {
            int n = _width * _height;
            var field = new float[n];
            for (int i = 0; i < n; i++)
                field[i] = IsOceanBiome(_biomeNearest[i]) ? 0f : 1f;
            field = BoxBlur(field, radius);
            field = BoxBlur(field, radius);
            return field;
        }
    }
}
