using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Merges captured tiles into a single PNG by projecting each tile's
    /// world-rect onto a shared canvas. Overlap is resolved in two stages:
    ///
    ///   1. Explored priority. A tile flagged not fully mapped
    ///      (<see cref="MapCompileTile.FullyMapped"/> == false) never overwrites
    ///      a pixel already painted by a fully-mapped tile, and a fully-mapped
    ///      tile always overwrites a pixel painted by a partial one. This is the
    ///      reliable fix for a mostly-unexplored capture being placed over an
    ///      already-complete region — the flag comes from Valheim's own
    ///      m_explored state, independent of whatever fog colour is rendered.
    ///   2. Same-tier ties (both complete, or both partial) fall back to
    ///      chromatic preference: ZenMap's fog overlay is low-saturation grey,
    ///      so the higher-chroma candidate (max(R,G,B) - min(R,G,B)) wins.
    ///      Real terrain beats grey fog without any tile-ordering bias.
    ///
    /// Built on System.Drawing — already a dependency for clipboard PNG-to-DIB
    /// conversion, so no new package is required.
    /// </summary>
    public static class MapCompositor
    {
        /// <summary>
        /// Pixel-format / container choice for the finalized image. Map captures
        /// are opaque RGB, so all three options round-trip the same content;
        /// they trade visual fidelity for file size:
        ///   Png        — 24bpp PNG, default zlib compression. Lossless. Big.
        ///   Jpeg       — 24bpp JPEG at the configured quality. Lossy but tiny
        ///                — fastest size win for cases where pin-label edges
        ///                don't need pixel-perfect crispness.
        ///   IndexedPng — 8bpp palette PNG, palette built via median-cut on a
        ///                15-bit histogram with Floyd-Steinberg dither at encode.
        ///                Lossy in colour count only; keeps label edges crisp
        ///                because there's no DCT ringing. Maps quantize very
        ///                well (~6 dominant colours) so this often beats JPEG
        ///                on size while keeping labels sharp.
        /// </summary>
        public enum EncodeFormat { Png, Jpeg, IndexedPng }

        /// <summary>
        /// Encoder parameters threaded through the finalize step. Use
        /// <see cref="Default"/> for the lossless PNG path the preview/COPY/SEND
        /// flows already expect; SAVE constructs its own from the Plugin config.
        /// </summary>
        public struct EncodeOptions
        {
            public EncodeFormat Format;
            // JPEG quality 1..100 (System.Drawing's encoder accepts this as a
            // long parameter). Only consulted when Format == Jpeg.
            public int JpegQuality;
            // Palette size for indexed PNG, 2..256. Only consulted when
            // Format == IndexedPng. Smaller = smaller file + more banding.
            public int IndexedPngColors;

            public static EncodeOptions Default => new EncodeOptions
            {
                Format = EncodeFormat.Png,
                JpegQuality = 88,
                IndexedPngColors = 64,
            };

            // File extension (with leading dot) for the chosen format. Used by
            // the SAVE path so the on-disk filename matches the actual bytes.
            public string Extension =>
                Format == EncodeFormat.Jpeg ? ".jpg" : ".png";
        }

        public class CompiledMap
        {
            // Final encoded image bytes (PNG or JPEG depending on the
            // EncodeOptions used at finalize). Null until the finalize step
            // (Unity TMP label stamp + single encode) runs — Compose/ComposeNative
            // now only produce the pixel buffer; encoding moved out so captions
            // can be rendered with Valheim's real TMP font on the main thread.
            public byte[] PngBytes;
            // File extension (".png" or ".jpg") matching the bytes in PngBytes.
            // Set by the finalize step from the EncodeOptions in effect; SAVE
            // reads this so the on-disk filename matches the actual format.
            // Defaults to ".png" so the preview/COPY/SEND paths (which never
            // override the format) keep their previous behaviour.
            public string Extension = ".png";
            // Raw composite, BGRA, top-down, tightly packed (stride = Width*4),
            // opaque. Kept so the finalize step can either encode it directly
            // (no labels) or hand it to the label stamp without re-decoding.
            internal byte[] Bgra;
            public Vector2 WorldMin;
            public Vector2 WorldMax;
            public int Width;
            public int Height;
            public int TileCount;
            // True only for ComposeNative output that hit the 8192 ceiling and
            // had to be scaled below true per-tile resolution. The SAVE status
            // line reports this so the player knows whether they got native px.
            public bool WasClamped;
        }

        // Hard ceiling for the native-resolution SAVE path. At 8192² a
        // 4 bytes/px buffer is ~268MB. Worst case is the label stamp, which
        // briefly holds the raw composite AND the labelled copy (~536MB) while
        // it converts. The raw composite is then released before the 24bpp
        // encode bitmap allocates (see MapCompileLabelStamp.Finalize), so the
        // encode peak is labelled + bitmap (~470MB), NOT three buffers at once
        // — which is what kept 8192² within what System.Drawing handles.
        // Going higher risks GDI+ refusing the bitmap mid-save.
        private const int NativeMaxDim = 8192;

        /// <summary>
        /// One Minimap pin to draw on the finished composite (icon + name).
        /// Snapshotted on the main thread from <see cref="MapCompile.MapCompilePinSnapshot"/>;
        /// the stamp pass turns this into a sprite blit + TMP caption so pins
        /// land at a consistent size in tile-overlap regions (per-tile baked
        /// pins were getting eaten by the chroma-pick).
        /// </summary>
        public struct PinDraw
        {
            public float WorldX;
            public float WorldZ;
            public UnityEngine.Sprite Icon;
            public UnityEngine.Color32 Tint;
            // Pin size on the player's reference screen, in screen px. The
            // stamp converts to composite px via (composite_W / refScreenW).
            public float ScreenPxW;
            public float ScreenPxH;
            // Pin caption — stamped below the icon when non-empty (TablePinName.Clean
            // is applied at snapshot time, so the ZenMap tracking suffix is gone).
            public string Name;
            // Pin-kind grouping key (sprite asset name, or "type:N" fallback) —
            // the same key MapCompilePinFilter uses for the PINS panel and the
            // exclusion set. Carried through so the web-map export can group
            // pins into filterable kinds without re-deriving it. Null on
            // PinDraws from paths that don't set it (only the web export reads
            // it; the label stamp ignores it).
            public string Key;
        }

        /// <summary>
        /// Compose the given tiles into one PNG. Output is sized so the longest
        /// world-axis maps to <paramref name="maxDimensionPx"/> pixels (clamped
        /// to a sensible floor of 512 to avoid degenerate output).
        /// </summary>
        public static CompiledMap Compose(IReadOnlyList<MapCompileTile> tiles, int maxDimensionPx)
        {
            if (!ComputeBounds(tiles, out var worldMin, out var worldMax, out var worldSize))
                return null;

            // Output canvas size — preserve aspect, clamp longest axis.
            int maxDim = Mathf.Clamp(maxDimensionPx, 512, 8192);
            float aspect = worldSize.x / worldSize.y;
            int outW, outH;
            if (aspect >= 1f) { outW = maxDim; outH = Mathf.Max(1, Mathf.RoundToInt(maxDim / aspect)); }
            else              { outH = maxDim; outW = Mathf.Max(1, Mathf.RoundToInt(maxDim * aspect)); }

            return Render(tiles, worldMin, worldMax, worldSize, outW, outH, wasClamped: false);
        }

        /// <summary>
        /// Compose at the tiles' true captured resolution: the densest tile
        /// (highest pixels-per-world-metre) maps 1:1 and every other tile is
        /// upscaled to match, so no tile is ever downsampled below what was
        /// captured. The result is clamped to <see cref="NativeMaxDim"/> on the
        /// longest axis; <see cref="CompiledMap.WasClamped"/> reports whether
        /// that clamp actually bit. Used by the directory SAVE so players get a
        /// zoomable, edit-quality PNG instead of the Discord-safe capped output.
        /// </summary>
        public static CompiledMap ComposeNative(IReadOnlyList<MapCompileTile> tiles)
        {
            if (!ComputeBounds(tiles, out var worldMin, out var worldMax, out var worldSize))
                return null;

            // Densest tile sets the scale (px per world-metre) so the sharpest
            // capture is reproduced 1:1 and coarser tiles are only ever
            // upscaled to fit it — never the reverse.
            double scale = 0d;
            foreach (var t in tiles)
            {
                float tw = t.WorldMax.x - t.WorldMin.x;
                float th = t.WorldMax.y - t.WorldMin.y;
                if (tw > 0f && t.PixelWidth  > 0) scale = Math.Max(scale, t.PixelWidth  / (double)tw);
                if (th > 0f && t.PixelHeight > 0) scale = Math.Max(scale, t.PixelHeight / (double)th);
            }
            if (scale <= 0d) scale = 1d;

            double nativeW = worldSize.x * scale;
            double nativeH = worldSize.y * scale;
            bool clamped = false;
            double longest = Math.Max(nativeW, nativeH);
            if (longest > NativeMaxDim)
            {
                double k = NativeMaxDim / longest;
                nativeW *= k;
                nativeH *= k;
                clamped = true;
            }

            int outW = (int)Math.Min(NativeMaxDim, Math.Max(512, Math.Round(nativeW)));
            int outH = (int)Math.Min(NativeMaxDim, Math.Max(512, Math.Round(nativeH)));
            return Render(tiles, worldMin, worldMax, worldSize, outW, outH, clamped);
        }

        // Combined world bounds across every tile. Returns false (and logs) for
        // degenerate input so callers can bail before allocating anything.
        private static bool ComputeBounds(IReadOnlyList<MapCompileTile> tiles,
            out Vector2 worldMin, out Vector2 worldMax, out Vector2 worldSize)
        {
            worldMin = worldMax = worldSize = Vector2.zero;
            if (tiles == null || tiles.Count == 0) return false;

            worldMin = tiles[0].WorldMin;
            worldMax = tiles[0].WorldMax;
            for (int i = 1; i < tiles.Count; i++)
            {
                worldMin = Vector2.Min(worldMin, tiles[i].WorldMin);
                worldMax = Vector2.Max(worldMax, tiles[i].WorldMax);
            }
            worldSize = worldMax - worldMin;
            if (worldSize.x <= 0f || worldSize.y <= 0f)
            {
                ModLog.Error("[NoMapDiscordAdditions] Composer: degenerate world bounds.");
                return false;
            }
            return true;
        }

        // Shared paint for a fixed output canvas. Both Compose paths resolve
        // their own outW/outH then delegate here. Produces only the raw BGRA
        // buffer — captions and PNG encoding happen in the finalize step
        // (MapCompileLabelStamp) so labels use Valheim's real TMP font.
        private static CompiledMap Render(IReadOnlyList<MapCompileTile> tiles,
            Vector2 worldMin, Vector2 worldMax,
            Vector2 worldSize, int outW, int outH, bool wasClamped)
        {
            // ── 3. Allocate output buffer (BGRA, 32 bits) + coverage mask ────
            // Stored in a managed byte[] so we can do per-pixel reads/writes
            // without per-pixel LockBits roundtrips. We'll wrap it in a Bitmap
            // at the end for PNG encoding. Coverage mask tracks whether each
            // pixel has been written by any tile yet — the first write to a
            // pixel paints directly; only subsequent writes go through the
            // chroma-pick. Without this, low-chroma fog (≈ neutral grey) ties
            // with the initial black and gets averaged, producing visibly dim
            // half-fog regions.
            int outStride = outW * 4;
            byte[] outBuf = new byte[outStride * outH];
            bool[] covered = new bool[outW * outH];
            // Per-output-pixel: did its current colour come from a fully-mapped
            // tile? Drives the explored-priority tier in CompositeTile so a
            // partial tile's fog can never paint over a complete tile.
            bool[] fromFull = new bool[outW * outH];
            // Alpha is set on first paint; uncovered pixels stay (0,0,0,0)
            // and get explicit opaque-black treatment in the encode step.

            // ── 4. For each tile: resample to its dest rect, then chroma-pick ─
            foreach (var tile in tiles)
            {
                CompositeTile(tile, outBuf, covered, fromFull, outW, outH, worldMin, worldMax);
            }

            // ── 4b. Force any still-uncovered pixels to opaque black so the
            // encoded PNG doesn't show transparency artefacts.
            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i]) outBuf[i * 4 + 3] = 255;
            }

            // ── 5. Hand back the raw buffer; finalize encodes (with labels) ──
            return new CompiledMap
            {
                Bgra = outBuf,
                PngBytes = null,
                WorldMin = worldMin,
                WorldMax = worldMax,
                Width = outW,
                Height = outH,
                TileCount = tiles.Count,
                WasClamped = wasClamped,
            };
        }

        /// <summary>
        /// Resample one tile into a temp BGRA buffer matching its dest rect,
        /// then merge into the output buffer with chroma-based overlap resolution.
        /// </summary>
        private static void CompositeTile(MapCompileTile tile,
            byte[] outBuf, bool[] covered, bool[] fromFull, int outW, int outH,
            Vector2 worldMin, Vector2 worldMax)
        {
            bool srcFull = tile.FullyMapped;
            if (!File.Exists(tile.PngPath))
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Tile PNG missing during compose: {tile.PngPath}");
                return;
            }

            // ── World rect → pixel rect ──────────────────────────────────────
            Vector2 worldSize = worldMax - worldMin;
            // World Z+ is "up" (north) in Valheim; image rows grow downward.
            // Flip Y so north appears at the top of the composite, matching
            // how the map is drawn in-game.
            float xMinFrac = (tile.WorldMin.x - worldMin.x) / worldSize.x;
            float xMaxFrac = (tile.WorldMax.x - worldMin.x) / worldSize.x;
            float yMinFrac = (worldMax.y - tile.WorldMax.y) / worldSize.y;  // top in pixel space = highest Z
            float yMaxFrac = (worldMax.y - tile.WorldMin.y) / worldSize.y;

            // Floor the min, ceil the max so the dst rect covers every output
            // pixel that overlaps the tile's world rect. Independent RoundToInt
            // on min/max could leave a 1-px gap between adjacent tiles (A's
            // xMaxFrac rounds to 100, B's xMinFrac rounds to 101 → col 100
            // never gets written → black seam). Floor/ceil guarantees adjacent
            // tiles abut or overlap by 1 px (chroma-pick handles overlap).
            int dstX = Mathf.Clamp(Mathf.FloorToInt(xMinFrac * outW), 0, outW);
            int dstX2 = Mathf.Clamp(Mathf.CeilToInt(xMaxFrac * outW), 0, outW);
            int dstY = Mathf.Clamp(Mathf.FloorToInt(yMinFrac * outH), 0, outH);
            int dstY2 = Mathf.Clamp(Mathf.CeilToInt(yMaxFrac * outH), 0, outH);

            int dstW = dstX2 - dstX;
            int dstH = dstY2 - dstY;
            if (dstW <= 0 || dstH <= 0) return;

            // ── Decode + bilinear resample to dstW × dstH ────────────────────
            byte[] tileBuf;
            try
            {
                using (var raw = LoadBitmap(tile.PngPath))
                using (var resized = new Bitmap(dstW, dstH, PixelFormat.Format32bppArgb))
                {
                    // System.Drawing.Graphics fully qualified — bare "Graphics"
                    // would clash with UnityEngine.Graphics under our usings.
                    using (var g = System.Drawing.Graphics.FromImage(resized))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(raw, new Rectangle(0, 0, dstW, dstH));
                    }
                    tileBuf = ExtractBgra(resized);
                }
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Tile decode/resize failed ({tile.PngPath}): {ex.Message}");
                return;
            }

            // ── Chroma-pick merge ───────────────────────────────────────────
            int outStride = outW * 4;
            int tileStride = dstW * 4;
            for (int y = 0; y < dstH; y++)
            {
                int outRow = (dstY + y) * outStride + dstX * 4;
                int tileRow = y * tileStride;
                int coverRow = (dstY + y) * outW + dstX;
                for (int x = 0; x < dstW; x++)
                {
                    int oi = outRow + x * 4;
                    int ti = tileRow + x * 4;
                    int ci = coverRow + x;

                    byte tb = tileBuf[ti];
                    byte tg = tileBuf[ti + 1];
                    byte tr = tileBuf[ti + 2];
                    // Resampled tile alpha is essentially 255 everywhere (source PNG is RGB24);
                    // we don't read it.

                    if (!covered[ci])
                    {
                        // First write to this pixel — paint directly so we
                        // don't average against the cleared (0,0,0) background.
                        outBuf[oi] = tb;
                        outBuf[oi + 1] = tg;
                        outBuf[oi + 2] = tr;
                        outBuf[oi + 3] = 255;
                        covered[ci] = true;
                        fromFull[ci] = srcFull;
                        continue;
                    }

                    // ── Tier 1: explored priority (order-independent) ─────────
                    bool dstFull = fromFull[ci];
                    if (srcFull != dstFull)
                    {
                        if (srcFull)
                        {
                            // Complete tile always beats a partial one.
                            outBuf[oi] = tb;
                            outBuf[oi + 1] = tg;
                            outBuf[oi + 2] = tr;
                            fromFull[ci] = true;
                        }
                        // else: partial src over complete dst — keep dst.
                        continue;
                    }

                    // ── Tier 2: same tier (both complete / both partial) ─────
                    byte ob = outBuf[oi];
                    byte og = outBuf[oi + 1];
                    byte or = outBuf[oi + 2];

                    int outChroma = Chroma(or, og, ob);
                    int tileChroma = Chroma(tr, tg, tb);

                    if (tileChroma > outChroma)
                    {
                        outBuf[oi] = tb;
                        outBuf[oi + 1] = tg;
                        outBuf[oi + 2] = tr;
                    }
                    // Tie or lower chroma: keep the existing pixel. We used to
                    // average on a tie ("softer seam"), but the floor/ceil
                    // destination math leaves a 1-pixel overlap column at
                    // every adjacent-tile boundary; averaging that column
                    // with both tiles' edge pixels produces a visibly
                    // different colour from the un-averaged interior columns
                    // on either side, which lights up as a thin vertical (or
                    // horizontal) seam on the composite. Keeping the
                    // first-paint pixel makes the boundary column read as a
                    // continuation of whichever tile got there first instead
                    // of a 1-pixel-wide blend.
                }
            }
        }

        private static int Chroma(byte r, byte g, byte b)
        {
            int max = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int min = r < g ? (r < b ? r : b) : (g < b ? g : b);
            return max - min;
        }

        // ── PNG / Bitmap helpers ─────────────────────────────────────────────

        // Read into a memory copy so the file handle is closed before we
        // hand the Bitmap to Graphics.DrawImage — System.Drawing.Bitmap holds
        // an exclusive lock on the source stream until disposed otherwise,
        // which can interfere with the session-dir cleanup later.
        private static Bitmap LoadBitmap(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(data))
                return new Bitmap(ms);
        }

        // LockBits → managed byte[] in BGRA8888 order, top-down.
        private static byte[] ExtractBgra(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            byte[] dest = new byte[w * h * 4];
            var rect = new Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int srcStride = bd.Stride;          // may be padded
                int dstStride = w * 4;              // tightly packed
                for (int y = 0; y < h; y++)
                {
                    var rowPtr = new IntPtr(bd.Scan0.ToInt64() + (long)y * srcStride);
                    Marshal.Copy(rowPtr, dest, y * dstStride, dstStride);
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            return dest;
        }

        // Encode a BGRA top-down buffer as a 24bpp BGR PNG. Back-compat overload
        // for callers that always want the lossless PNG (preview/COPY/SEND).
        internal static byte[] EncodeBgra(byte[] bgra, int w, int h)
            => EncodeBgra(bgra, w, h, EncodeOptions.Default);

        // Encode a BGRA top-down buffer in the requested format. The composite
        // is always opaque, so dropping alpha saves ~25% of the file with no
        // visual loss. Pure System.Drawing — safe to call off the main thread
        // (the finalize step does, after the Unity label stamp).
        internal static byte[] EncodeBgra(byte[] bgra, int w, int h, EncodeOptions opts)
        {
            // Indexed PNG is a separate code path: System.Drawing's 8bpp
            // pipeline needs its own LockBits + palette setup, and a 24bpp
            // intermediate would just waste memory at native (8192²) sizes.
            if (opts.Format == EncodeFormat.IndexedPng)
                return EncodeIndexedPngFromBgra(bgra, w, h, opts.IndexedPngColors);

            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int dstStride = bd.Stride;          // 4-byte aligned, may pad
                    byte[] rowBuf = new byte[dstStride];
                    for (int y = 0; y < h; y++)
                    {
                        int src = y * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            int s = src + x * 4;
                            int d = x * 3;
                            rowBuf[d]     = bgra[s];      // B
                            rowBuf[d + 1] = bgra[s + 1];  // G
                            rowBuf[d + 2] = bgra[s + 2];  // R
                        }
                        var rowPtr = new IntPtr(bd.Scan0.ToInt64() + (long)y * dstStride);
                        Marshal.Copy(rowBuf, 0, rowPtr, dstStride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                return SaveBitmap(bmp, opts);
            }
        }

        // Back-compat overload — PNG with default options.
        internal static byte[] EncodeRgbaBottomUp(UnityEngine.Color32[] rgba, int w, int h)
            => EncodeRgbaBottomUp(rgba, w, h, EncodeOptions.Default);

        // Encode a Unity Color32[] (RGBA, BOTTOM-UP — Texture/GetPixels32 row
        // order) in the requested format, flipping rows so the output is
        // top-down. Used by the finalize step after labels have been stamped
        // into the buffer. Pure System.Drawing — runs off the main thread.
        internal static byte[] EncodeRgbaBottomUp(UnityEngine.Color32[] rgba, int w, int h, EncodeOptions opts)
        {
            if (opts.Format == EncodeFormat.IndexedPng)
                return EncodeIndexedPngFromRgbaBottomUp(rgba, w, h, opts.IndexedPngColors);

            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int dstStride = bd.Stride;
                    byte[] rowBuf = new byte[dstStride];
                    for (int y = 0; y < h; y++)
                    {
                        // Output row y (top-down) ← buffer row (h-1-y) (bottom-up).
                        int src = (h - 1 - y) * w;
                        for (int x = 0; x < w; x++)
                        {
                            var c = rgba[src + x];
                            int d = x * 3;
                            rowBuf[d]     = c.b;
                            rowBuf[d + 1] = c.g;
                            rowBuf[d + 2] = c.r;
                        }
                        var rowPtr = new IntPtr(bd.Scan0.ToInt64() + (long)y * dstStride);
                        Marshal.Copy(rowBuf, 0, rowPtr, dstStride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                return SaveBitmap(bmp, opts);
            }
        }

        /// <summary>
        /// Decode any image bytes (PNG, JPEG, BMP, …) and re-encode in the
        /// requested format. Optionally caps the longest dimension first —
        /// pass <paramref name="maxDim"/> &lt;= 0 to disable resize.
        ///
        /// Single entry point for capture pipelines that want to change
        /// format/size after the initial capture: SEND TO DISCORD recodes the
        /// PNG-from-capture into the user's preferred Output Format; COPY
        /// recodes into indexed PNG; both can also clamp the longest edge.
        /// Safe to call off the main thread (pure System.Drawing).
        ///
        /// Returns the input bytes unchanged on null/empty input or on decode
        /// failure (logged) so SEND/COPY never silently produce no output.
        /// </summary>
        public static byte[] Recode(byte[] inputBytes, EncodeOptions opts, int maxDim = 0)
        {
            if (inputBytes == null || inputBytes.Length == 0) return inputBytes;

            try
            {
                using (var ms = new MemoryStream(inputBytes))
                using (var src = new Bitmap(ms))
                {
                    int w = src.Width, h = src.Height;
                    Bitmap resized = null;
                    try
                    {
                        Bitmap source = src;
                        if (maxDim > 0 && Math.Max(w, h) > maxDim)
                        {
                            float k = (float)maxDim / Math.Max(w, h);
                            int newW = Math.Max(1, (int)Math.Round(w * k));
                            int newH = Math.Max(1, (int)Math.Round(h * k));
                            resized = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
                            using (var g = System.Drawing.Graphics.FromImage(resized))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                g.PixelOffsetMode = PixelOffsetMode.Half;
                                g.CompositingMode = CompositingMode.SourceCopy;
                                g.DrawImage(src, 0, 0, newW, newH);
                            }
                            w = newW; h = newH;
                            source = resized;
                        }

                        // ExtractBgra below LockBits-converts whatever pixel
                        // format the source landed in (24bpp from a JPEG, 8bpp
                        // from an indexed PNG, …) into Format32bppArgb on the
                        // way out, so callers never have to pre-convert.
                        byte[] bgra = ExtractBgra(source);
                        byte[] result = EncodeBgra(bgra, w, h, opts);
                        ModLog.Info($"[NoMapDiscordAdditions] Recoded {inputBytes.Length} -> {result.Length} bytes " +
                                    $"({w}×{h}, format={opts.Format}).");
                        return result;
                    }
                    finally
                    {
                        resized?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Recode failed, returning original bytes: {ex.Message}");
                return inputBytes;
            }
        }

        // ── Format dispatch ──────────────────────────────────────────────────

        // PNG / JPEG branch — IndexedPng has already been routed away by the
        // callers above. JPEG quality is passed via EncoderParameters because
        // GDI+'s default (75) is noticeably soft on map terrain.
        private static byte[] SaveBitmap(Bitmap bmp, EncodeOptions opts)
        {
            using (var ms = new MemoryStream())
            {
                if (opts.Format == EncodeFormat.Jpeg)
                {
                    var enc = GetJpegEncoder();
                    if (enc != null)
                    {
                        int q = Math.Min(100, Math.Max(1, opts.JpegQuality));
                        using (var ps = new EncoderParameters(1))
                        {
                            ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)q);
                            bmp.Save(ms, enc, ps);
                            return ms.ToArray();
                        }
                    }
                    // Fallback: encoder lookup failed (very rare); fall through
                    // to the default JPEG encoder so we still produce a file.
                    bmp.Save(ms, ImageFormat.Jpeg);
                    return ms.ToArray();
                }

                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        private static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }

        // ── Indexed PNG (median-cut + Floyd-Steinberg dither) ────────────────

        // BGRA top-down → 8bpp indexed PNG.
        private static byte[] EncodeIndexedPngFromBgra(byte[] bgra, int w, int h, int maxColors)
        {
            int n = w * h;
            var rgb = new byte[n * 3];
            for (int i = 0; i < n; i++)
            {
                int s = i * 4;
                int d = i * 3;
                rgb[d]     = bgra[s + 2]; // R
                rgb[d + 1] = bgra[s + 1]; // G
                rgb[d + 2] = bgra[s];     // B
            }
            return EncodeIndexedPng(rgb, w, h, maxColors);
        }

        // RGBA bottom-up → 8bpp indexed PNG. We flip during the RGB packing
        // step so the quantizer sees a top-down view (matches the output
        // orientation expected by viewers).
        private static byte[] EncodeIndexedPngFromRgbaBottomUp(UnityEngine.Color32[] rgba, int w, int h, int maxColors)
        {
            int n = w * h;
            var rgb = new byte[n * 3];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (h - 1 - y) * w;
                int dstRow = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    var c = rgba[srcRow + x];
                    int d = dstRow + x * 3;
                    rgb[d]     = c.r;
                    rgb[d + 1] = c.g;
                    rgb[d + 2] = c.b;
                }
            }
            return EncodeIndexedPng(rgb, w, h, maxColors);
        }

        // Build palette via median-cut on a 15-bit histogram, then write an
        // 8bpp indexed PNG with Floyd-Steinberg dither. The 15-bit histogram
        // (32k buckets, RRRRR-GGGGG-BBBBB) bounds memory regardless of input
        // size — at 8192² we'd otherwise have 67M entries to keep track of.
        private static byte[] EncodeIndexedPng(byte[] rgbTopDown, int w, int h, int maxColors)
        {
            maxColors = Math.Min(256, Math.Max(2, maxColors));

            // 1. Histogram on 5-bit-per-channel buckets.
            //    bucket = (r5 << 10) | (g5 << 5) | b5
            //    Stored values are full-precision sums so the palette mean
            //    keeps fidelity that pre-quantisation alone would lose.
            const int BucketCount = 32 * 32 * 32;
            var counts = new int[BucketCount];
            var sumR = new long[BucketCount];
            var sumG = new long[BucketCount];
            var sumB = new long[BucketCount];
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                int p = i * 3;
                byte r = rgbTopDown[p];
                byte g = rgbTopDown[p + 1];
                byte b = rgbTopDown[p + 2];
                int idx = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                counts[idx]++;
                sumR[idx] += r;
                sumG[idx] += g;
                sumB[idx] += b;
            }

            // 2. Median-cut on the populated buckets. A "box" is the AABB of
            //    the 5-bit colour cube it covers; split repeatedly along the
            //    widest axis until we have maxColors leaves.
            var boxes = new List<MedianCutBox>(maxColors);
            var initial = new MedianCutBox();
            initial.Init(counts, sumR, sumG, sumB);
            if (initial.PixelCount == 0)
            {
                // Degenerate input — fall back to a single-colour palette so we
                // still produce a valid PNG instead of crashing the save.
                return EncodeIndexedPngWithPalette(rgbTopDown, w, h,
                    new byte[] { 0, 0, 0 }, new int[BucketCount],
                    paletteSize: 1);
            }
            boxes.Add(initial);

            while (boxes.Count < maxColors)
            {
                // Pick the box with the largest "volume × count" — splitting
                // it gives the biggest reduction in quantisation error.
                int splitIdx = -1;
                long bestScore = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    var b = boxes[i];
                    if (b.IsAtomic) continue;
                    long score = (long)b.LongestExtent * b.PixelCount;
                    if (score > bestScore) { bestScore = score; splitIdx = i; }
                }
                if (splitIdx < 0) break;
                var parent = boxes[splitIdx];
                if (!parent.TrySplit(counts, sumR, sumG, sumB, out var left, out var right))
                {
                    parent.IsAtomic = true;
                    boxes[splitIdx] = parent;
                    continue;
                }
                boxes[splitIdx] = left;
                boxes.Add(right);
            }

            // 3. Palette + a 32³ RGB → palette-index LUT.
            int paletteSize = boxes.Count;
            var palette = new byte[paletteSize * 3];
            for (int i = 0; i < paletteSize; i++)
            {
                boxes[i].MeanColor(counts, sumR, sumG, sumB,
                    out byte pr, out byte pg, out byte pb);
                palette[i * 3]     = pr;
                palette[i * 3 + 1] = pg;
                palette[i * 3 + 2] = pb;
            }

            // Map each populated histogram bucket to its closest palette entry
            // (5-bit centres → palette nearest neighbour). 32k entries, brute-
            // forced — negligible against the per-pixel dither pass.
            var lut = new int[BucketCount];
            for (int b5 = 0; b5 < 32; b5++)
                for (int g5 = 0; g5 < 32; g5++)
                    for (int r5 = 0; r5 < 32; r5++)
                    {
                        int idx = (r5 << 10) | (g5 << 5) | b5;
                        int br = (r5 << 3) | (r5 >> 2);  // bucket centre, 0..255
                        int bg = (g5 << 3) | (g5 >> 2);
                        int bb = (b5 << 3) | (b5 >> 2);
                        int best = 0;
                        int bestErr = int.MaxValue;
                        for (int p = 0; p < paletteSize; p++)
                        {
                            int dr = br - palette[p * 3];
                            int dg = bg - palette[p * 3 + 1];
                            int db = bb - palette[p * 3 + 2];
                            int err = dr * dr + dg * dg + db * db;
                            if (err < bestErr) { bestErr = err; best = p; }
                        }
                        lut[idx] = best;
                    }

            return EncodeIndexedPngWithPalette(rgbTopDown, w, h, palette, lut, paletteSize);
        }

        // Write the indexed PNG. Walks pixels in scanline order applying
        // Floyd-Steinberg dither; the LUT gives a fast first-cut palette
        // match, then we refine against the full palette using the dithered
        // colour (the LUT alone would clip the dither to bucket centres).
        private static byte[] EncodeIndexedPngWithPalette(
            byte[] rgbTopDown, int w, int h,
            byte[] palette, int[] lut, int paletteSize)
        {
            // Error-diffusion buffers: one row's worth of (R,G,B) error
            // accumulators, current and next. FS is a 2-row kernel.
            var errCurR = new short[w + 2];
            var errCurG = new short[w + 2];
            var errCurB = new short[w + 2];
            var errNextR = new short[w + 2];
            var errNextG = new short[w + 2];
            var errNextB = new short[w + 2];

            // Indexed pixel output. We'll DMA this into the Bitmap via LockBits
            // after the dither pass to avoid touching the managed/unmanaged
            // boundary per pixel.
            var indexed = new byte[w * h];

            for (int y = 0; y < h; y++)
            {
                int rowSrc = y * w * 3;
                int rowDst = y * w;
                for (int x = 0; x < w; x++)
                {
                    int s = rowSrc + x * 3;
                    int r = rgbTopDown[s]     + errCurR[x + 1];
                    int g = rgbTopDown[s + 1] + errCurG[x + 1];
                    int b = rgbTopDown[s + 2] + errCurB[x + 1];
                    if (r < 0) r = 0; else if (r > 255) r = 255;
                    if (g < 0) g = 0; else if (g > 255) g = 255;
                    if (b < 0) b = 0; else if (b > 255) b = 255;

                    // Palette lookup via the bucket LUT. Bucket centres are
                    // within ~4 units of the actual pixel value so this is
                    // close enough to a real nearest-neighbour search; FS
                    // dither absorbs the residual error. A per-pixel scan of
                    // the full palette would be exact but adds an O(N×P) pass
                    // (~4B ops at 8192² × 64-colour palette).
                    int bucket = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                    int pi = lut[bucket];
                    indexed[rowDst + x] = (byte)pi;

                    int pr = palette[pi * 3];
                    int pg = palette[pi * 3 + 1];
                    int pb = palette[pi * 3 + 2];
                    int er = r - pr;
                    int eg = g - pg;
                    int eb = b - pb;

                    // FS kernel (right=7/16, BL=3/16, B=5/16, BR=1/16).
                    errCurR[x + 2]  += (short)(er * 7 / 16);
                    errCurG[x + 2]  += (short)(eg * 7 / 16);
                    errCurB[x + 2]  += (short)(eb * 7 / 16);
                    errNextR[x]     += (short)(er * 3 / 16);
                    errNextG[x]     += (short)(eg * 3 / 16);
                    errNextB[x]     += (short)(eb * 3 / 16);
                    errNextR[x + 1] += (short)(er * 5 / 16);
                    errNextG[x + 1] += (short)(eg * 5 / 16);
                    errNextB[x + 1] += (short)(eb * 5 / 16);
                    errNextR[x + 2] += (short)(er * 1 / 16);
                    errNextG[x + 2] += (short)(eg * 1 / 16);
                    errNextB[x + 2] += (short)(eb * 1 / 16);
                }

                // Rotate error buffers for the next row.
                var tR = errCurR; errCurR = errNextR; errNextR = tR;
                var tG = errCurG; errCurG = errNextG; errNextG = tG;
                var tB = errCurB; errCurB = errNextB; errNextB = tB;
                Array.Clear(errNextR, 0, errNextR.Length);
                Array.Clear(errNextG, 0, errNextG.Length);
                Array.Clear(errNextB, 0, errNextB.Length);
            }

            using (var bmp = new Bitmap(w, h, PixelFormat.Format8bppIndexed))
            {
                // ColorPalette is opaque + immutable from this side: you must
                // get it, mutate the local copy, then assign back to apply.
                var cp = bmp.Palette;
                // GDI+ hands out a palette pre-sized to the bitmap's format
                // (256 entries for 8bpp). We only fill the first paletteSize
                // entries and leave the rest at (0,0,0) — unused indices are
                // never written into the bitmap so the extras don't matter.
                for (int i = 0; i < paletteSize && i < cp.Entries.Length; i++)
                {
                    cp.Entries[i] = System.Drawing.Color.FromArgb(255,
                        palette[i * 3], palette[i * 3 + 1], palette[i * 3 + 2]);
                }
                bmp.Palette = cp;

                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
                try
                {
                    int dstStride = bd.Stride;
                    if (dstStride == w)
                    {
                        Marshal.Copy(indexed, 0, bd.Scan0, indexed.Length);
                    }
                    else
                    {
                        // Padded stride — copy row by row.
                        for (int y = 0; y < h; y++)
                        {
                            var rowPtr = new IntPtr(bd.Scan0.ToInt64() + (long)y * dstStride);
                            Marshal.Copy(indexed, y * w, rowPtr, w);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        // ── Median-cut box ───────────────────────────────────────────────────
        // Operates on the 32³ histogram only — no per-pixel state. r/g/b axes
        // are inclusive 5-bit bucket coordinates (0..31).
        private struct MedianCutBox
        {
            public byte RMin, RMax, GMin, GMax, BMin, BMax;
            public int PixelCount;
            // Marked true when a split attempt failed (single bucket, etc.) so
            // we don't keep retrying the same dead-end box.
            public bool IsAtomic;

            public int LongestExtent
            {
                get
                {
                    int dr = RMax - RMin;
                    int dg = GMax - GMin;
                    int db = BMax - BMin;
                    return Math.Max(dr, Math.Max(dg, db));
                }
            }

            public void Init(int[] counts, long[] sumR, long[] sumG, long[] sumB)
            {
                RMin = 31; GMin = 31; BMin = 31;
                RMax = 0;  GMax = 0;  BMax = 0;
                long total = 0;
                for (int r = 0; r < 32; r++)
                    for (int g = 0; g < 32; g++)
                        for (int b = 0; b < 32; b++)
                        {
                            int idx = (r << 10) | (g << 5) | b;
                            if (counts[idx] == 0) continue;
                            if (r < RMin) RMin = (byte)r;
                            if (r > RMax) RMax = (byte)r;
                            if (g < GMin) GMin = (byte)g;
                            if (g > GMax) GMax = (byte)g;
                            if (b < BMin) BMin = (byte)b;
                            if (b > BMax) BMax = (byte)b;
                            total += counts[idx];
                        }
                if (total == 0)
                {
                    // Empty histogram — leave box at min=max=0 so IsAtomic is
                    // forced and Mean() returns black for everything.
                    RMin = GMin = BMin = 0; RMax = GMax = BMax = 0;
                }
                PixelCount = (int)Math.Min(total, int.MaxValue);
            }

            // Split along the longest axis at the population median. Each leaf
            // box inherits the same min/max bounds on the other two axes.
            public bool TrySplit(int[] counts, long[] sumR, long[] sumG, long[] sumB,
                out MedianCutBox left, out MedianCutBox right)
            {
                left = default; right = default;
                int dr = RMax - RMin;
                int dg = GMax - GMin;
                int db = BMax - BMin;
                int axis = 0; // 0=R, 1=G, 2=B
                if (dg >= dr && dg >= db) axis = 1;
                else if (db >= dr && db >= dg) axis = 2;

                int axisMin = axis == 0 ? RMin : (axis == 1 ? GMin : BMin);
                int axisMax = axis == 0 ? RMax : (axis == 1 ? GMax : BMax);
                if (axisMax <= axisMin) return false; // Already a single slab.

                // Population per axis bucket inside this box.
                var pop = new int[32];
                AccumulateAxis(counts, axis, pop);
                long half = PixelCount / 2;
                long cum = 0;
                int splitAt = axisMin;
                for (int i = axisMin; i <= axisMax; i++)
                {
                    cum += pop[i];
                    if (cum >= half) { splitAt = i; break; }
                }
                if (splitAt >= axisMax) splitAt = axisMax - 1;
                if (splitAt < axisMin) splitAt = axisMin;

                left = this; right = this;
                if (axis == 0)
                {
                    left.RMax = (byte)splitAt;
                    right.RMin = (byte)(splitAt + 1);
                }
                else if (axis == 1)
                {
                    left.GMax = (byte)splitAt;
                    right.GMin = (byte)(splitAt + 1);
                }
                else
                {
                    left.BMax = (byte)splitAt;
                    right.BMin = (byte)(splitAt + 1);
                }

                left.PixelCount = (int)Math.Min(int.MaxValue, SumIn(counts, ref left));
                right.PixelCount = (int)Math.Min(int.MaxValue, SumIn(counts, ref right));
                left.IsAtomic = false;
                right.IsAtomic = false;
                if (left.PixelCount == 0 || right.PixelCount == 0) return false;
                return true;
            }

            private void AccumulateAxis(int[] counts, int axis, int[] pop)
            {
                for (int r = RMin; r <= RMax; r++)
                    for (int g = GMin; g <= GMax; g++)
                        for (int b = BMin; b <= BMax; b++)
                        {
                            int c = counts[(r << 10) | (g << 5) | b];
                            if (c == 0) continue;
                            int k = axis == 0 ? r : (axis == 1 ? g : b);
                            pop[k] += c;
                        }
            }

            private static long SumIn(int[] counts, ref MedianCutBox box)
            {
                long s = 0;
                for (int r = box.RMin; r <= box.RMax; r++)
                    for (int g = box.GMin; g <= box.GMax; g++)
                        for (int b = box.BMin; b <= box.BMax; b++)
                            s += counts[(r << 10) | (g << 5) | b];
                return s;
            }

            public void MeanColor(int[] counts, long[] sumR, long[] sumG, long[] sumB,
                out byte r, out byte g, out byte b)
            {
                long cnt = 0, sR = 0, sG = 0, sB = 0;
                for (int rr = RMin; rr <= RMax; rr++)
                    for (int gg = GMin; gg <= GMax; gg++)
                        for (int bb = BMin; bb <= BMax; bb++)
                        {
                            int idx = (rr << 10) | (gg << 5) | bb;
                            int c = counts[idx];
                            if (c == 0) continue;
                            cnt += c;
                            sR += sumR[idx];
                            sG += sumG[idx];
                            sB += sumB[idx];
                        }
                if (cnt == 0) { r = g = b = 0; return; }
                r = (byte)Math.Min(255, Math.Max(0, sR / cnt));
                g = (byte)Math.Min(255, Math.Max(0, sG / cnt));
                b = (byte)Math.Min(255, Math.Max(0, sB / cnt));
            }
        }
    }
}
