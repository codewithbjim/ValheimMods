using System.Collections;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Captures one compile tile — PNG bytes plus the world-space rect they
    /// cover. Snapshots <c>m_mapImageLarge.uvRect</c> up front so the rect
    /// always matches what was actually rendered.
    ///
    /// Honours the global <c>Capture Method</c> config: screen-capture by
    /// default (the simpler path — uses what the player sees, no shader
    /// re-render), texture-capture for the off-screen render variant. Both
    /// produce a PNG covering the clamped-uv span (matches
    /// <see cref="MapCompileTile.ComputeWorldRect"/>'s world rect) so the
    /// compositor can mix tiles from either path without alignment drift.
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
            // True when this tile represents a complete reveal of its area:
            // under ZenMap, the active table/item is at (essentially) full
            // reveal radius; otherwise (vanilla) every in-world pixel in the
            // captured viewport is explored per Valheim's own m_explored state.
            // A partial tile is demoted by the compositor so its fog can never
            // overwrite explored terrain from a complete tile. See IsFullyMapped.
            public bool FullyMapped;
        }

        /// <summary>
        /// Capture one tile, branching on the global Capture Method config.
        /// Coroutine because the screen-capture path needs <c>WaitForEndOfFrame</c>
        /// after hiding the UI. <paramref name="callback"/> receives the
        /// <see cref="Result"/> on success or <c>null</c> on any failure.
        /// </summary>
        public static IEnumerator Capture(System.Action<Result?> callback)
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mapImageLarge == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: Minimap unavailable.");
                callback?.Invoke(null);
                yield break;
            }
            if (minimap.m_mode != Minimap.MapMode.Large)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: large map not active.");
                callback?.Invoke(null);
                yield break;
            }

            // Snapshot uvRect BEFORE the capture so the rect we record matches
            // exactly what gets rasterized. World rect + FullyMapped are
            // derived here so both capture paths share identical metadata.
            Rect uv = minimap.m_mapImageLarge.uvRect;
            MapCompileTile.ComputeWorldRect(minimap, uv, out var wmin, out var wmax);
            bool fully = IsFullyMapped(minimap, uv);

            // When a Map Style is selected it overrides the Capture Method
            // config — the screen-capture path screenshots the unstyled live
            // map and can't substitute a styled base, so we always go through
            // an off-screen texture render with the stylized texture handed in
            // as the base layer (same pattern as Plugin.CaptureTextureWithStyle).
            if (MapStyleRender.IsStyleActive())
            {
                Result? styledResult = null;
                yield return CaptureStyledTile(uv, wmin, wmax, fully, r => styledResult = r);
                callback?.Invoke(styledResult);
                yield break;
            }

            if (ModHelpers.EffectiveConfig.UseTextureCapture)
            {
                yield return new WaitForEndOfFrame();

                // Re-verify mode after the yield — the player can close the
                // map during the frame and Minimap can tear down references.
                if (Minimap.instance == null
                    || Minimap.instance.m_mode != Minimap.MapMode.Large)
                {
                    callback?.Invoke(null);
                    yield break;
                }

                callback?.Invoke(CaptureTexture(uv, wmin, wmax, fully));
            }
            else
            {
                byte[] png = null;
                int w = 0, h = 0;
                // Screen capture handles its own UI hide + WaitForEndOfFrame
                // (see MapCapture.CaptureVisibleMap). cropToClampedUv keeps the
                // PNG aligned with the world rect ComputeWorldRect produced;
                // includePinLabels=false matches the texture path — labels
                // stamp once on the composite. Lighting normalization is read
                // from the config inside CaptureVisibleMap itself.
                yield return MapCapture.CaptureVisibleMap(
                    data => png = data,
                    cropToClampedUv: true,
                    includePinLabels: false,
                    sizeCallback: (cw, ch) => { w = cw; h = ch; });

                if (png == null || w <= 0 || h <= 0)
                {
                    ModLog.Warn("[NoMapDiscordAdditions] Compile capture: screen capture returned no data.");
                    callback?.Invoke(null);
                    yield break;
                }

                callback?.Invoke(new Result
                {
                    Png = png,
                    Width = w,
                    Height = h,
                    WorldMin = wmin,
                    WorldMax = wmax,
                    FullyMapped = fully,
                });
            }
        }

        // Texture-capture branch. Renders the map shader off-screen at a
        // 4K-class longest edge sized to the clamped-uv aspect so the
        // composite has isotropic per-tile density. Kept available as the
        // alternative to screen capture (config: Capture Method = Texture).
        private static Result? CaptureTexture(Rect uv, Vector2 wmin, Vector2 wmax, bool fully)
        {
            // Size the off-screen target to the clamped-uv aspect so DrawMapBase
            // (which rasterizes the CLAMPED uv across the whole buffer) doesn't
            // waste pixels on the long axis — see the long-form rationale that
            // used to live here and now lives in the class comment.
            float cuW = Mathf.Clamp01(uv.xMax) - Mathf.Clamp01(uv.xMin);
            float cuH = Mathf.Clamp01(uv.yMax) - Mathf.Clamp01(uv.yMin);
            int capW, capH;
            if (cuW > 1e-6f && cuH > 1e-6f)
            {
                float aspect = cuW / cuH;
                if (aspect >= 1f)
                {
                    capW = TileMaxDim;
                    capH = Mathf.Max(64, Mathf.RoundToInt(TileMaxDim / aspect));
                }
                else
                {
                    capH = TileMaxDim;
                    capW = Mathf.Max(64, Mathf.RoundToInt(TileMaxDim * aspect));
                }
            }
            else
            {
                capW = MapCaptureTexture.OutputWidth;
                capH = MapCaptureTexture.OutputHeight;
            }

            byte[] png = MapCaptureTexture.CaptureMap(capW, capH, includePinLabels: false);
            if (png == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture: MapCaptureTexture returned null.");
                return null;
            }

            return new Result
            {
                Png = png,
                Width = capW,
                Height = capH,
                WorldMin = wmin,
                WorldMax = wmax,
                FullyMapped = fully,
            };
        }

        // Styled-tile branch. Renders the active Map Style off the main thread
        // for this tile's viewport, then hands the texture to CaptureMap as the
        // base layer — so the PNG is the stylized terrain with the normal pin
        // overlay on top, exactly like Plugin.CaptureTextureWithStyle but tile-
        // sized. Tile dimensions follow the same clamped-uv aspect rule as the
        // unstyled texture path so the composite stitches cleanly.
        private static IEnumerator CaptureStyledTile(Rect uv,
            Vector2 wmin, Vector2 wmax, bool fully,
            System.Action<Result?> callback)
        {
            float cuW = Mathf.Clamp01(uv.xMax) - Mathf.Clamp01(uv.xMin);
            float cuH = Mathf.Clamp01(uv.yMax) - Mathf.Clamp01(uv.yMin);
            int capW, capH;
            if (cuW > 1e-6f && cuH > 1e-6f)
            {
                float aspect = cuW / cuH;
                if (aspect >= 1f)
                {
                    capW = TileMaxDim;
                    capH = Mathf.Max(64, Mathf.RoundToInt(TileMaxDim / aspect));
                }
                else
                {
                    capH = TileMaxDim;
                    capW = Mathf.Max(64, Mathf.RoundToInt(TileMaxDim * aspect));
                }
            }
            else
            {
                capW = MapCaptureTexture.OutputWidth;
                capH = MapCaptureTexture.OutputHeight;
            }

            // Render the styled base at a moderate internal resolution scaled
            // down from the tile size; the GPU blit in DrawStyledBase upsizes
            // it to capW × capH effectively for free. See StyledRenderMaxDim
            // for the cost/quality rationale.
            int styledW = capW, styledH = capH;
            int longest = Mathf.Max(capW, capH);
            if (longest > StyledRenderMaxDim)
            {
                float k = (float)StyledRenderMaxDim / longest;
                styledW = Mathf.Max(64, Mathf.RoundToInt(capW * k));
                styledH = Mathf.Max(64, Mathf.RoundToInt(capH * k));
            }

            Texture2D styled = null;
            yield return MapStyleRender.BuildAsync(uv, styledW, styledH, t => styled = t);

            // The Map may have torn down during the render (player closes it,
            // or scene change) — bail before touching Minimap again.
            if (Minimap.instance == null
                || Minimap.instance.m_mode != Minimap.MapMode.Large)
            {
                if (styled != null) Object.Destroy(styled);
                callback?.Invoke(null);
                yield break;
            }

            // CaptureMap tolerates a null styled base (falls back to a default
            // texture render) — that keeps the composite usable even if the
            // style pipeline failed for one tile.
            byte[] png = MapCaptureTexture.CaptureMap(capW, capH, includePinLabels: false, styled);
            if (styled != null) Object.Destroy(styled);

            if (png == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile capture (styled): MapCaptureTexture returned null.");
                callback?.Invoke(null);
                yield break;
            }

            callback?.Invoke(new Result
            {
                Png = png,
                Width = capW,
                Height = capH,
                WorldMin = wmin,
                WorldMax = wmax,
                FullyMapped = fully,
            });
        }

        // 4K-class longest edge per texture-capture tile, matching the
        // CTRL+COPY full-res cap in Plugin.cs. Downstream composite ceilings
        // still apply (Discord/COPY cap, native SAVE clamps at 8192) so this
        // only sets how much real detail each tile carries IN — not the final
        // output size. Screen-capture tile size is driven by the player's
        // screen × CaptureSuperSize instead, which gives equivalent density on
        // typical 1440p/4K setups without needing its own constant.
        private const int TileMaxDim = 4096;

        // Per-tile cap on the internal resolution the Map Style pipeline runs
        // at. The styled output is intrinsically low-frequency content
        // (the biome blur, shallow-water field, and final SmoothImage all
        // wash out fine detail anyway) so DrawStyledBase's GPU bilinear blit
        // up to TileMaxDim is visually indistinguishable from a native render
        // — but the pipeline cost (8-10 Color32[w*h] arrays plus a stack of
        // O(n) passes) drops ~7x at 1536² vs 4096², which is the difference
        // between an ADD TILE that feels instant and one that stalls for
        // close to a second.
        private const int StyledRenderMaxDim = 1536;

        /// <summary>
        /// Decides the tile's <see cref="Result.FullyMapped"/> flag.
        ///
        /// In a ZenMap nomap world the reveal is a single circular disc per
        /// table read, so the per-pixel rectangle test below can never pass
        /// (the rect's corners always fall outside the disc) and would flag
        /// even a maxed-out table as partial. When ZenMap is driving the
        /// reveal we instead trust its own completeness ratio
        /// (<c>MapLocation.Percent</c>): full radius ⇒ fully mapped.
        ///
        /// When ZenMap isn't the source (no ZenMap, or god-mode ExploreAll
        /// with no active location), we fall back to Valheim's authoritative
        /// per-pixel <see cref="Minimap.m_explored"/> test — correct for
        /// vanilla walking exploration, which genuinely fills arbitrary shapes.
        /// </summary>
        private static bool IsFullyMapped(Minimap minimap, Rect uv)
        {
            if (ZenMapInterop.TryGetActiveRevealPercent(out float percent))
                return percent >= ZenMapInterop.FullRevealThreshold;

            return IsRegionFullyMapped(minimap, uv);
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
