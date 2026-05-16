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
        public class CompiledMap
        {
            // Final encoded PNG. Null until the finalize step (Unity TMP label
            // stamp + single PNG encode) runs — Compose/ComposeNative now only
            // produce the pixel buffer; encoding moved out so captions can be
            // rendered with Valheim's real TMP font on the main thread.
            public byte[] PngBytes;
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
        /// Compose the given tiles into one PNG. Output is sized so the longest
        /// world-axis maps to <paramref name="maxDimensionPx"/> pixels (clamped
        /// to a sensible floor of 512 to avoid degenerate output).
        /// </summary>
        /// <summary>
        /// A "{dist}m {dir}" caption to stamp onto the finished composite at a
        /// world position. Compile mode draws labels here — once, on top of the
        /// merged image — instead of baking them into every tile. Baked labels
        /// were getting eaten by the chroma-pick: a white caption is near-zero
        /// chroma, so in any region a later tile overlapped an already-painted
        /// tile the label lost to the existing terrain and only the
        /// first-painted tile kept its captions.
        /// </summary>
        public struct LabelDraw
        {
            public float WorldX;   // world X
            public float WorldZ;   // world Z (north = +)
            public string Text;
        }

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
                    else if (tileChroma == outChroma)
                    {
                        // Tie — average for a softer seam. Avoids hard edges
                        // when two captures of the same area are equally informative.
                        outBuf[oi]     = (byte)((tb + ob) >> 1);
                        outBuf[oi + 1] = (byte)((tg + og) >> 1);
                        outBuf[oi + 2] = (byte)((tr + or) >> 1);
                    }
                    // else: existing output pixel wins, no write needed.
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

        // Encode a BGRA top-down buffer as a 24bpp BGR PNG. The composite is
        // always opaque, so dropping alpha saves ~25% of the file with no
        // visual loss. Pure System.Drawing — safe to call off the main thread
        // (the finalize step does, after the Unity label stamp).
        internal static byte[] EncodeBgra(byte[] bgra, int w, int h)
        {
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

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        // Encode a Unity Color32[] (RGBA, BOTTOM-UP — Texture/GetPixels32 row
        // order) as a 24bpp PNG, flipping rows so the PNG is top-down. Used by
        // the finalize step after labels have been stamped into the buffer.
        // Pure System.Drawing — runs off the main thread.
        internal static byte[] EncodeRgbaBottomUp(UnityEngine.Color32[] rgba, int w, int h)
        {
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
                        // PNG row y (top-down) ← buffer row (h-1-y) (bottom-up).
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

                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }
    }
}
