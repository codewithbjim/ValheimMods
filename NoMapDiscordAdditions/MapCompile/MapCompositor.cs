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
    /// world-rect onto a shared canvas. Overlapping pixels are resolved by
    /// chromatic preference: ZenMap's fog overlay is low-saturation grey, so
    /// the higher-chroma candidate (max(R,G,B) - min(R,G,B)) wins. Real terrain
    /// always beats fog without any tile-ordering bias.
    ///
    /// Built on System.Drawing — already a dependency for clipboard PNG-to-DIB
    /// conversion, so no new package is required.
    /// </summary>
    public static class MapCompositor
    {
        public class CompiledMap
        {
            public byte[] PngBytes;
            public Vector2 WorldMin;
            public Vector2 WorldMax;
            public int Width;
            public int Height;
            public int TileCount;
        }

        /// <summary>
        /// Compose the given tiles into one PNG. Output is sized so the longest
        /// world-axis maps to <paramref name="maxDimensionPx"/> pixels (clamped
        /// to a sensible floor of 512 to avoid degenerate output).
        /// </summary>
        public static CompiledMap Compose(IReadOnlyList<MapCompileTile> tiles, int maxDimensionPx)
        {
            if (tiles == null || tiles.Count == 0) return null;

            // ── 1. Combined world bounds ─────────────────────────────────────
            Vector2 worldMin = tiles[0].WorldMin;
            Vector2 worldMax = tiles[0].WorldMax;
            for (int i = 1; i < tiles.Count; i++)
            {
                worldMin = Vector2.Min(worldMin, tiles[i].WorldMin);
                worldMax = Vector2.Max(worldMax, tiles[i].WorldMax);
            }
            Vector2 worldSize = worldMax - worldMin;
            if (worldSize.x <= 0f || worldSize.y <= 0f)
            {
                ModLog.Error("[NoMapDiscordAdditions] Composer: degenerate world bounds.");
                return null;
            }

            // ── 2. Output canvas size — preserve aspect, clamp longest axis ──
            int maxDim = Mathf.Clamp(maxDimensionPx, 512, 8192);
            float aspect = worldSize.x / worldSize.y;
            int outW, outH;
            if (aspect >= 1f) { outW = maxDim; outH = Mathf.Max(1, Mathf.RoundToInt(maxDim / aspect)); }
            else              { outH = maxDim; outW = Mathf.Max(1, Mathf.RoundToInt(maxDim * aspect)); }

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
            // Alpha is set on first paint; uncovered pixels stay (0,0,0,0)
            // and get explicit opaque-black treatment in the encode step.

            // ── 4. For each tile: resample to its dest rect, then chroma-pick ─
            foreach (var tile in tiles)
            {
                CompositeTile(tile, outBuf, covered, outW, outH, worldMin, worldMax);
            }

            // ── 4b. Force any still-uncovered pixels to opaque black so the
            // encoded PNG doesn't show transparency artefacts.
            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i]) outBuf[i * 4 + 3] = 255;
            }

            // ── 5. Encode to PNG ─────────────────────────────────────────────
            byte[] png = EncodePng(outBuf, outW, outH);
            return new CompiledMap
            {
                PngBytes = png,
                WorldMin = worldMin,
                WorldMax = worldMax,
                Width = outW,
                Height = outH,
                TileCount = tiles.Count,
            };
        }

        /// <summary>
        /// Resample one tile into a temp BGRA buffer matching its dest rect,
        /// then merge into the output buffer with chroma-based overlap resolution.
        /// </summary>
        private static void CompositeTile(MapCompileTile tile,
            byte[] outBuf, bool[] covered, int outW, int outH,
            Vector2 worldMin, Vector2 worldMax)
        {
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
                        continue;
                    }

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

        // Encode as 24bpp BGR PNG. The composite always produces opaque pixels,
        // so dropping the alpha channel saves ~25% of the encoded file size with
        // no visual loss. We pack BGR rows into a managed buffer and copy into
        // a Format24bppRgb Bitmap, which System.Drawing then writes as a 24bpp
        // PNG (vs 32bpp ARGB if we'd used Format32bppArgb).
        private static byte[] EncodePng(byte[] bgra, int w, int h)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                var rect = new Rectangle(0, 0, w, h);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int dstStride = bd.Stride;          // 4-byte aligned, may pad
                    int dstRowBytes = w * 3;            // tight BGR row width
                    byte[] rowBuf = new byte[dstStride];
                    for (int y = 0; y < h; y++)
                    {
                        // BGRA → BGR (drop A) for this row.
                        int src = y * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            int s = src + x * 4;
                            int d = x * 3;
                            rowBuf[d]     = bgra[s];      // B
                            rowBuf[d + 1] = bgra[s + 1];  // G
                            rowBuf[d + 2] = bgra[s + 2];  // R
                        }
                        // Trailing stride padding (dstStride - dstRowBytes) is
                        // already zero from the rowBuf allocation.
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
