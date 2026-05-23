using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// The stylized render pipelines — a port of MapPrinter's GenerateOldMap /
    /// GenerateChartMap / GenerateTopographicalMap / GenerateSatelliteImage and
    /// the image primitives they chain. Each primitive takes layer arrays and
    /// returns a new one (pure functions; no Unity object access), so the whole
    /// sequence is safe on the background thread <see cref="RunPipeline"/> uses.
    ///
    /// All methods operate on <c>_width * _height</c> Color32 arrays indexed
    /// <c>row * _width + col</c>, row 0 = south. The arrays cover exactly the
    /// captured viewport, so this is the final image — no further cropping.
    /// </summary>
    internal sealed partial class MapStyleContext
    {
        // Colour of the biome-boundary outline drawn over the chart styles.
        private static readonly Color32 BiomeEdge = new Color32(35, 25, 18, 235);

        // Coastal / land-biome water is tinted toward this — a shade lighter
        // than the open-ocean blue — so shorelines read distinctly.
        private static readonly Color32 ShallowWater = new Color32(155, 215, 230, 255);

        // ════════════════════════════════════════════════════════════════════
        //  Style pipelines
        // ════════════════════════════════════════════════════════════════════

        // Aged-parchment chart: biome wash, Perlin grain, contour lines, fog.
        private Color32[] GenerateOldMap()
        {
            Color32[] ocean = GenerateOceanTexture(_heightLayer, clear: false);
            Color32[] outtex = ReplaceColour(_biomeLayer, new Color32(0, 0, 0, 255), Parchment);
            outtex = OverlayTexture(outtex, ocean);

            Color32[] solid = GetSolidColour(Parchment);
            outtex = LerpTextures(outtex, solid);
            outtex = LerpTextures(outtex, solid);
            outtex = LerpTextures(outtex, solid);

            outtex = AddPerlinNoise(outtex, GrainTightness, 16f);

            Color32[] contours = GenerateContourMap(_heightLayer, 8, 128);
            outtex = OverlayTexture(outtex, contours);

            Color32[] fog = StylizeFog(_fogLayer);
            outtex = OverlayTexture(outtex, fog);

            return SmoothImage(outtex);
        }

        // Flat topographic chart — a single parchment wash and no final
        // smoothing, so biome tints stay crisp under the contours.
        private Color32[] GenerateChartMap()
        {
            Color32[] ocean = GenerateOceanTexture(_heightLayer, clear: false);
            Color32[] outtex = ReplaceColour(_biomeLayer, new Color32(0, 0, 0, 255), Parchment);
            outtex = OverlayTexture(outtex, ocean);

            Color32[] solid = GetSolidColour(Parchment);
            outtex = LerpTextures(outtex, solid);

            outtex = AddPerlinNoise(outtex, GrainTightness, 16f);

            Color32[] contours = GenerateContourMap(_heightLayer, 10, 128);
            outtex = OverlayTexture(outtex, contours);

            Color32[] fog = StylizeFog(_fogLayer);
            return OverlayTexture(outtex, fog);
        }

        // Shaded-relief terrain: naturalistic biome colours, hillshading and
        // contour lines.
        private Color32[] GenerateTopographicalMap()
        {
            Color32[] ocean = GenerateOceanTexture(_heightLayer, clear: true);
            ocean = AddPerlinNoise(ocean, GrainTightness / 16f, 64f);

            Color32[] sand = CreateSand();
            sand = ReplaceColour(sand, new Color32(51, 51, 51, 255), new Color32(65, 75, 70, 255));
            Color32[] outtex = OverlayTexture(sand, ocean);

            Color32[] shadow = CreateShadowMap(_heightLayer, 23);
            outtex = DarkenTextureLinear(outtex, 20);

            Color32[] contours = GenerateContourMap(_heightLayer, 8, 128);
            outtex = OverlayTexture(outtex, shadow);
            outtex = OverlayTexture(outtex, contours);

            Color32[] fog = StylizeFog(_fogLayer);
            outtex = OverlayTexture(outtex, fog);

            return SmoothImage(outtex);
        }

        // Naturalistic shaded terrain with no contour lines.
        private Color32[] GenerateSatelliteImage()
        {
            Color32[] ocean = GenerateOceanTexture(_heightLayer, clear: true);
            ocean = AddPerlinNoise(ocean, GrainTightness / 16f, 64f);

            Color32[] sand = CreateSand();
            sand = ReplaceColour(sand, new Color32(51, 51, 51, 255), new Color32(65, 75, 70, 255));
            Color32[] outtex = OverlayTexture(sand, ocean);

            Color32[] shadow = CreateShadowMap(_heightLayer, 23);
            outtex = DarkenTextureLinear(outtex, 20);
            outtex = OverlayTexture(outtex, shadow);

            Color32[] fog = StylizeFog(_fogLayer);
            outtex = OverlayTexture(outtex, fog);

            return SmoothImage(outtex);
        }

        // Perlin grain frequency — keyed to the output size, not the world, so
        // the paper grain looks the same in every capture regardless of zoom.
        private float GrainTightness => Mathf.Max(_width, _height) / 16f;

        // ════════════════════════════════════════════════════════════════════
        //  Primitives
        // ════════════════════════════════════════════════════════════════════

        // ── Ocean ───────────────────────────────────────────────────────────
        // Underwater pixels get a position-based blue gradient. The gradient is
        // computed from the pixel's WORLD uv (so it stays continuous across
        // captures); land stays clear (clear=false) or the alpha encodes depth
        // (clear=true, relief styles).
        private Color32[] GenerateOceanTexture(Color32[] height, bool clear)
        {
            var output = new Color32[_width * _height];
            for (int row = 0; row < _height; row++)
            {
                float worldV = _v0 + (row + 0.5f) / _height * (_v1 - _v0);
                int num = Mathf.RoundToInt(worldV * 256f) - 128;
                int rowBase = row * _width;
                for (int col = 0; col < _width; col++)
                {
                    int i = rowBase + col;
                    if (height[i].b <= 0)
                        continue; // land: leave Color.clear

                    float worldU = _u0 + (col + 0.5f) / _width * (_u1 - _u0);
                    int num2 = Mathf.RoundToInt(worldU * 256f) - 128;
                    int num3 = num * num / 128 + num2 * num2 / 512;

                    Color32 ocean = num < 0
                        ? new Color32((byte)(10 + num3), (byte)(136 - num3 / 4), 193, 255)
                        : new Color32((byte)(10 + num3 / 2), 136, (byte)(193 - num3 / 2), 255);

                    // Water that belongs to a land biome (coastal shallows,
                    // lakes, inlets) reads a shade lighter than the open-ocean
                    // biome, so shorelines stand out from the deep sea. The
                    // shallow field is a blurred mask, so the tint fades in
                    // smoothly rather than stepping along the biome grid.
                    float shallow = _shallowField[i];
                    if (shallow > 0f)
                        ocean = Color32.Lerp(ocean, ShallowWater, 0.5f * shallow);

                    if (clear)
                    {
                        int alpha = Mathf.Min(height[i].b * 16 + 128, 255);
                        output[i] = new Color32(ocean.r, ocean.g, ocean.b, (byte)alpha);
                    }
                    else
                    {
                        output[i] = new Color32(ocean.r, ocean.g, ocean.b, 255);
                    }
                }
            }
            return output;
        }

        // ── Sand / biome base (relief styles) ───────────────────────────────
        // Above water: the biome colour. Underwater: the heath colour where the
        // biome pixel isn't pure black, else left clear for the ocean overlay.
        private Color32[] CreateSand()
        {
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                if (_heightLayer[i].b > 0)
                {
                    Color32 bc = _biomeLayer[i];
                    if (bc.r != 0 && bc.g != 0 && bc.b != 0)
                        output[i] = _heathColor;
                }
                else
                {
                    output[i] = _biomeLayer[i];
                }
            }
            return output;
        }

        // ── Colour ops ──────────────────────────────────────────────────────

        private Color32[] ReplaceColour(Color32[] input, Color32 from, Color32 to)
        {
            var output = new Color32[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                Color32 c = input[i];
                output[i] = (c.r == from.r && c.g == from.g && c.b == from.b) ? to : c;
            }
            return output;
        }

        private Color32[] GetSolidColour(Color32 colour)
        {
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
                output[i] = colour;
            return output;
        }

        // Alpha-composite array2 over array1.
        private Color32[] OverlayTexture(Color32[] array1, Color32[] array2)
        {
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                Color bottom = array1[i];
                Color top = array2[i];
                float aTop = top.a;
                float aBottom = bottom.a;
                Color wc = Color.Lerp(bottom, top, aTop);
                wc.a = Mathf.Min(aTop + aBottom, 1f);
                output[i] = wc;
            }
            return output;
        }

        // Alpha-weighted blend — MapPrinter's "wash toward a flat colour" op.
        private Color32[] LerpTextures(Color32[] array1, Color32[] array2)
        {
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                int diff = array2[i].a - array1[i].a;
                int sum = Mathf.Min(array1[i].a + array2[i].a, 255);
                int hi = Mathf.Max(array1[i].a, array2[i].a) * 2;
                float t = hi == 0 ? 0.5f : (float)diff / hi + 0.5f;
                Color32 c = Color32.Lerp(array1[i], array2[i], t);
                c.a = (byte)sum;
                output[i] = c;
            }
            return output;
        }

        // Subtract a flat amount from every RGB channel (relief shading base).
        private Color32[] DarkenTextureLinear(Color32[] array, byte d)
        {
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                Color32 c = array[i];
                output[i] = new Color32(
                    (byte)Mathf.Max(c.r - d, 0),
                    (byte)Mathf.Max(c.g - d, 0),
                    (byte)Mathf.Max(c.b - d, 0),
                    c.a);
            }
            return output;
        }

        // ── Perlin grain ────────────────────────────────────────────────────

        private Color32[] AddPerlinNoise(Color32[] input, float tightness, float damping)
        {
            var output = new Color32[_width * _height];
            for (int row = 0; row < _height; row++)
            {
                for (int col = 0; col < _width; col++)
                {
                    int idx = row * _width + col;
                    Color val = input[idx];
                    float noise = Mathf.PerlinNoise(row / tightness, col / tightness);
                    noise = (noise - 0.5f) / damping;
                    output[idx] = new Color(val.r + noise, val.g + noise, val.b + noise, val.a);
                }
            }
            return output;
        }

        private Color32[] GetPerlin(float tightness, float damping)
        {
            var output = new Color32[_width * _height];
            for (int row = 0; row < _height; row++)
            {
                for (int col = 0; col < _width; col++)
                {
                    float noise = Mathf.PerlinNoise(row / tightness, col / tightness);
                    noise = (noise - 0.5f) / damping + 0.5f;
                    output[row * _width + col] = new Color(noise, noise, noise, 0.2f);
                }
            }
            return output;
        }

        // ── Fog ─────────────────────────────────────────────────────────────
        // Unexplored pixels become Perlin-textured parchment; explored pixels
        // stay clear so the detailed map shows through the overlay.
        private Color32[] StylizeFog(Color32[] fog)
        {
            Color32[] noise = GetPerlin(GrainTightness, 16f);
            int n = _width * _height;
            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                if (fog[i].a > 0)
                {
                    output[i] = new Color32(
                        (byte)(203 + (noise[i].r - 128)),
                        (byte)(155 + (noise[i].g - 128)),
                        (byte)(87 + (noise[i].b - 128)),
                        255);
                }
            }
            return output;
        }

        // ── Hillshading (relief styles) ─────────────────────────────────────

        private Color32[] CreateShadowMap(Color32[] heightmap, byte intensity)
        {
            Color32[] hard = CreateHardShadowMap(heightmap, intensity);
            Color32[] soft = CreateSoftShadowMap(heightmap);
            return LerpTextures(soft, hard);
        }

        // Per-pixel north-facing slope shade. "_shadowScale" stands in for
        // MapPrinter's supersampling factor (fixed off-by-one: the row guard is
        // "row < _height - 1", not the always-true "row < _height").
        private Color32[] CreateSoftShadowMap(Color32[] input)
        {
            var output = new Color32[_width * _height];
            for (int row = 0; row < _height; row++)
            {
                for (int col = 0; col < _width; col++)
                {
                    int num = 0;
                    if (row < _height - 1)
                    {
                        float slope = (input[row * _width + col].r
                            - input[(row + 1) * _width + col].r) * _shadowScale;
                        num = (int)(slope * 8f);
                    }
                    byte b = (byte)Mathf.Abs(num);
                    byte b2 = (byte)(num >= 0 ? 255 : 0);
                    output[row * _width + col] = new Color32(b2, b2, b2, b);
                }
            }
            return output;
        }

        // Cast hard drop-shadows downhill from the south, MapPrinter's algorithm.
        private Color32[] CreateHardShadowMap(Color32[] input, byte intensity)
        {
            int n = _width * _height;
            var output = new Color32[n];
            var shadowed = new bool[n];
            for (int row = _height - 1; row > -1; row--)
            {
                for (int col = 0; col < _width; col++)
                {
                    int idx = row * _width + col;
                    if (!shadowed[idx])
                    {
                        output[idx] = new Color32(255, 255, 255, 0);
                        for (int k = 1;
                             row - k > 0 &&
                             input[idx].r * _shadowScale > input[(row - k) * _width + col].r * _shadowScale + k * 2;
                             k++)
                        {
                            shadowed[(row - k) * _width + col] = true;
                        }
                    }
                    else
                    {
                        output[idx] = new Color32(0, 0, 0, intensity);
                    }
                }
            }
            return output;
        }

        // ── Contours ────────────────────────────────────────────────────────
        // Draws a line wherever a pixel's quantised elevation band is higher
        // than a neighbour's — MapPrinter's contour algorithm, unmodified.
        private Color32[] GenerateContourMap(Color32[] start, int graduations, byte alpha)
        {
            int n = _width * _height;
            var input = new Color32[n];
            var output = new Color32[n];

            for (int i = 0; i < n; i++)
            {
                int num = start[i].r + graduations;
                if (num > 255) num = 255;
                if (start[i].b > 0) num = 0; // water: flat
                input[i].r = (byte)num;
            }

            for (int row = 1; row < _height - 1; row++)
            {
                int rowBase = row * _width;
                for (int col = 1; col < _width - 1; col++)
                {
                    int idx = rowBase + col;
                    int level = input[idx].r / graduations;
                    for (int l = -1; l < 2; l++)
                    {
                        int lrow = l * _width;
                        for (int m = -1; m < 2; m++)
                        {
                            if (l == 0 && m == 0) continue;
                            int neighbour = input[idx + lrow + m].r / graduations;
                            if (neighbour < level)
                            {
                                byte b = alpha;
                                if ((level - 1) / 5 == (neighbour - 1) / 5) b = (byte)(b / 2);
                                if (neighbour == 0) b = alpha;
                                if (l == 0 || m == 0 || output[idx].a == b)
                                {
                                    output[idx] = new Color32(0, 0, 0, b);
                                    break;
                                }
                                output[idx] = new Color32(0, 0, 0, (byte)(b / 2));
                            }
                        }
                    }
                }
            }
            return output;
        }

        // ── Biome boundary outline ──────────────────────────────────────────
        // Draws an anti-aliased line wherever two DRY-LAND biomes meet. Water
        // pixels are skipped entirely, so there is no outline around coasts or
        // the ocean — only borders between land biomes.
        //
        // The raw boundary is a hard on/off mask that staircases along the
        // blocky biome grid; to smooth it we blur that mask into a coverage
        // field and re-slice it with a smoothstep, which turns the staircase
        // into a clean curve while keeping a crisp, defined line.
        private Color32[] GenerateBiomeEdges(Color32[] biome)
        {
            int n = _width * _height;

            var cover = new float[n];
            for (int row = 1; row < _height - 1; row++)
            {
                int rowBase = row * _width;
                for (int col = 1; col < _width - 1; col++)
                {
                    int idx = rowBase + col;
                    if (_heightLayer[idx].b > 0)
                        continue; // water: no coastline / ocean outline

                    Color32 c = biome[idx];
                    if (LandBiomeDiffers(c, biome, idx - 1) ||
                        LandBiomeDiffers(c, biome, idx + 1) ||
                        LandBiomeDiffers(c, biome, idx - _width) ||
                        LandBiomeDiffers(c, biome, idx + _width))
                    {
                        cover[idx] = 1f;
                    }
                }
            }

            cover = BoxBlur(cover, 2);
            cover = BoxBlur(cover, 2);

            // Renormalise so the line peaks at 1 regardless of blur spread,
            // then smoothstep into a clean anti-aliased line.
            float max = 0f;
            for (int i = 0; i < n; i++)
                if (cover[i] > max) max = cover[i];
            if (max <= 0f) return new Color32[n];
            float invMax = 1f / max;

            var output = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                if (cover[i] <= 0f) continue;
                float t = Mathf.Clamp01((cover[i] * invMax - 0.5f) / 0.4f);
                t = t * t * (3f - 2f * t); // smoothstep
                if (t <= 0f) continue;
                output[i] = new Color32(BiomeEdge.r, BiomeEdge.g, BiomeEdge.b,
                    (byte)(t * BiomeEdge.a));
            }
            return output;
        }

        // Separable box blur of a coverage field — edge-clamped at the borders.
        private float[] BoxBlur(float[] src, int radius)
        {
            int n = _width * _height;
            float inv = 1f / (2 * radius + 1);

            var tmp = new float[n];
            for (int row = 0; row < _height; row++)
            {
                int rowBase = row * _width;
                for (int col = 0; col < _width; col++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                        sum += src[rowBase + Mathf.Clamp(col + k, 0, _width - 1)];
                    tmp[rowBase + col] = sum * inv;
                }
            }

            var dst = new float[n];
            for (int row = 0; row < _height; row++)
            {
                for (int col = 0; col < _width; col++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                        sum += tmp[Mathf.Clamp(row + k, 0, _height - 1) * _width + col];
                    dst[row * _width + col] = sum * inv;
                }
            }
            return dst;
        }

        // True when the neighbour is dry land of a different biome — so the
        // outline is only ever drawn between two land biomes.
        private bool LandBiomeDiffers(Color32 c, Color32[] biome, int nIdx)
        {
            if (_heightLayer[nIdx].b > 0) return false; // neighbour is water
            Color32 nb = biome[nIdx];
            return c.r != nb.r || c.g != nb.g || c.b != nb.b;
        }

        // The ocean biome maps to white in Valheim's biome texture
        // (Minimap.GetPixelColor); every land biome has a non-white colour.
        private static bool IsOceanBiome(Color32 c)
            => c.r == 255 && c.g == 255 && c.b == 255;

        // ── Smoothing ───────────────────────────────────────────────────────
        // 5-tap blur of the interior; edge pixels keep their input value
        // (output starts as a copy) so the map has no transparent border.
        private Color32[] SmoothImage(Color32[] input)
        {
            var output = (Color32[])input.Clone();
            for (int row = 1; row < _height - 1; row++)
            {
                for (int col = 1; col < _width - 1; col++)
                {
                    int idx = row * _width + col;
                    Color32 up = input[idx - _width];
                    Color32 right = input[idx + 1];
                    Color32 down = input[idx + _width];
                    Color32 left = input[idx - 1];
                    Color32 horizontal = Color32.Lerp(right, left, 0.5f);
                    Color32 vertical = Color32.Lerp(up, down, 0.5f);
                    Color32 avg = Color32.Lerp(horizontal, vertical, 0.5f);
                    output[idx] = Color32.Lerp(input[idx], avg, 0.5f);
                }
            }
            return output;
        }
    }
}
