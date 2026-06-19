using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using TMPro;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Finalizes a <see cref="MapCompositor.CompiledMap"/>: stamps pin icons
    /// + pin names onto the composite using Valheim's real TMP font (the exact
    /// SDF asset + outline material the in-game UI uses), then encodes one PNG.
    ///
    /// MUST run on the Unity main thread (TMP layout + font atlas reads), so
    /// it can't live inside the off-thread System.Drawing compose. Flow:
    /// compose off-thread → this stamps on the main thread → the single PNG
    /// encode is pushed back off-thread. Pins are drawn once onto the merged
    /// image (never baked per tile — they'd be eaten by the chroma-pick in
    /// tile-overlap regions, and would land at inconsistent sizes).
    /// </summary>
    public static class MapCompileLabelStamp
    {
        /// <summary>
        /// Main-thread + off-thread finalize. Stamps pin icons + names (when
        /// the Valheim font resolves) into the composite, then encodes
        /// <see cref="MapCompositor.CompiledMap.PngBytes"/>. Yield this from a
        /// coroutine; on return <c>r.PngBytes</c> is set (or null on failure).
        ///
        /// <paramref name="pins"/> are the snapshot-time visible Minimap pins.
        /// <paramref name="referenceScreenWidth"/> is the on-screen large-map
        /// width at snapshot time — pins keep their on-screen size on the
        /// composite by scaling with (composite_W / referenceScreenWidth).
        /// </summary>
        public static IEnumerator Finalize(MapCompositor.CompiledMap r,
            IReadOnlyList<MapCompositor.PinDraw> pins,
            IReadOnlyList<MapCompileTile> tiles,
            float referenceScreenWidth)
            => Finalize(r, pins, tiles, referenceScreenWidth, MapCompositor.EncodeOptions.Default);

        /// <summary>
        /// As <see cref="Finalize(MapCompositor.CompiledMap, IReadOnlyList{MapCompositor.PinDraw}, IReadOnlyList{MapCompileTile}, float)"/>,
        /// but lets the caller pick an encoder format (PNG, JPEG, IndexedPNG +
        /// parameters). Currently only the SAVE path passes anything other than
        /// <see cref="MapCompositor.EncodeOptions.Default"/> — preview / COPY /
        /// SEND deliberately keep the lossless PNG so clipboard paste targets
        /// and Discord previews keep working.
        ///
        /// <paramref name="tiles"/> are clipped against each pin's world
        /// position — only pins falling inside a captured tile's world rect
        /// are stamped. The composite's bounding rect includes the gaps
        /// between non-adjacent tiles, so a bounding-only cull would draw
        /// pins on the black "no data" regions between tiles.
        /// </summary>
        public static IEnumerator Finalize(MapCompositor.CompiledMap r,
            IReadOnlyList<MapCompositor.PinDraw> pins,
            IReadOnlyList<MapCompileTile> tiles,
            float referenceScreenWidth,
            MapCompositor.EncodeOptions opts)
        {
            if (r == null || r.Bgra == null) yield break;
            // Stash the extension on the result up front so callers can use it
            // for filename construction even if the encode itself bails.
            r.Extension = opts.Extension;

            // Main thread: try to stamp pins. Returns a stamped RGBA
            // (bottom-up) buffer, or null when no pin survives the on-canvas
            // cull (then we encode the raw BGRA directly). Missing font only
            // skips pin captions — icons still rasterize.
            Color32[] labelled = null;
            try
            {
                labelled = Stamp(r.Bgra, r.Width, r.Height,
                    r.WorldMin, r.WorldMax - r.WorldMin,
                    pins, tiles, referenceScreenWidth);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Pin stamp failed: {ex.Message}");
                labelled = null;
            }

            // When the stamp produced a labelled buffer, the raw composite
            // (~268MB at 8192²) is dead — drop it now so the GC can reclaim it
            // BEFORE the encode allocates its own large bitmap, instead of both
            // being resident at once. Only the labelled==null path still needs
            // r.Bgra (it encodes it directly).
            byte[] rawBgra = r.Bgra;
            if (labelled != null) { r.Bgra = null; rawBgra = null; }

            // Off-thread: encode once (pure System.Drawing). Heavy for the
            // 8192² native save — keep it off the game thread like the compose.
            byte[] png = null;
            Exception err = null;
            var done = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    png = labelled != null
                        ? MapCompositor.EncodeRgbaBottomUp(labelled, r.Width, r.Height, opts)
                        : MapCompositor.EncodeBgra(rawBgra, r.Width, r.Height, opts);
                }
                catch (Exception ex) { err = ex; }
                finally { done.Set(); }
            });
            while (!done.IsSet) yield return null;
            done.Dispose();

            if (err != null)
                ModLog.Error($"[NoMapDiscordAdditions] Compile encode failed: {err.Message}");
            r.PngBytes = png;
        }

        // World→pixel projection MUST match MapCompositor.CompositeTile:
        // X linear, Z flipped so north is at the top of the image.
        private static Color32[] Stamp(byte[] bgraTopDown, int w, int h,
            Vector2 worldMin, Vector2 worldSize,
            IReadOnlyList<MapCompositor.PinDraw> pins,
            IReadOnlyList<MapCompileTile> tiles,
            float referenceScreenWidth)
        {
            if (worldSize.x <= 0f || worldSize.y <= 0f) return null;
            if (pins == null || pins.Count == 0) return null;

            // Size pins + captions so they read at the size they had on the
            // live large map. Each captured tile IS one live-map view, so a pin
            // that occupied a given fraction of the screen should occupy that
            // same fraction of ONE tile on the composite — independent of how
            // many tiles the composite spans. The scale factor is therefore
            //   (tile_px_on_composite / referenceScreenWidth)
            // where tile_px_on_composite = (tileWorldWidth / compositeWorldWidth) * w.
            //
            // This replaces an older constant 0.25 icon multiplier + 4096-px
            // font anchor, which baked in an implicit "~4 tiles across"
            // assumption: it looked right for a typical multi-tile composite
            // but shrank pins ~4x (and left captions at raw vanilla px) on a
            // single-tile compile. For a single tile the world ratio is 1, so
            // the factor collapses to w/referenceScreenWidth — full on-screen
            // size. For an N-across composite it scales down ~1/N, matching the
            // old behaviour at N≈4 while staying correct for any tile count.
            float refW = referenceScreenWidth > 1f ? referenceScreenWidth : 1920f;
            float tileWorldW = AverageTileWorldWidth(tiles, worldSize.x);
            float scaleFactor = (tileWorldW / worldSize.x) * ((float)w / refW);
            if (scaleFactor <= 0f) scaleFactor = ((float)w / refW) * 0.25f; // defensive
            float pinScale = scaleFactor;

            // Caption font style comes straight from Valheim's pin name prefab
            // so compile captions inherit the live-map FontStyles. The size
            // uses the same world-aware scale factor as the icons, so captions
            // grow/shrink in lockstep with their markers. Falls back to 16 px
            // Normal if the prefab isn't reachable (shouldn't happen — Stamp
            // only runs while the large map is open, but be defensive).
            SampleVanillaPinTmp(out float vanillaFontPx, out FontStyles pinFontStyle);
            float pinFontPx = vanillaFontPx * scaleFactor;

            // Resolve pin draws (cull to on-canvas) BEFORE touching the big
            // buffer. A native (8192²) save converts to a ~268MB Color32[];
            // doing that unconditionally meant a session whose pins all fell
            // outside the composed bounds paid the full allocation + per-pixel
            // copy only to discard it. If nothing survives we return null
            // without allocating anything.
            var pinDraws = new List<(Sprite icon, Color32 tint,
                                     float cx, float cyBottom,
                                     float iconW, float iconH,
                                     string name)>();
            foreach (var p in pins)
            {
                if (p.Icon == null) continue;
                float fx = (p.WorldX - worldMin.x) / worldSize.x;
                float fyTop = (worldSize.y - (p.WorldZ - worldMin.y)) / worldSize.y;
                // Composite bounding box cull (cheap reject for pins far
                // outside any tile).
                if (fx < 0f || fx > 1f || fyTop < 0f || fyTop > 1f) continue;

                // Per-tile cull: the composite's rectangular bounding box can
                // include large empty gaps between non-adjacent tiles (black
                // regions in the final PNG). A pin sitting in a gap is at a
                // world position the player never actually captured, so it
                // shouldn't appear on the composite. Require the pin's world
                // position to land inside at least one captured tile's world
                // rect.
                if (!IsInsideAnyTile(p.WorldX, p.WorldZ, tiles)) continue;

                float iconW = Mathf.Max(4f, p.ScreenPxW * pinScale);
                float iconH = Mathf.Max(4f, p.ScreenPxH * pinScale);
                // The pin marker rect is square, but a pin's sprite need not
                // be (the trader pouch is taller than wide). BlitSpriteInto
                // stretches the source rect to fill the destination box, so a
                // non-square sprite drawn into a square box comes out
                // distorted. Valheim's pin Image keeps the sprite's aspect, so
                // letterbox the icon inside the square box to match the
                // in-game look. Square sprites (the common case) are untouched.
                FitSpriteAspect(p.Icon, ref iconW, ref iconH);
                float cyBottom = h - fyTop * h;
                pinDraws.Add((p.Icon, p.Tint, fx * w, cyBottom,
                              iconW, iconH, TablePinName.Clean(p.Name)));
            }

            if (pinDraws.Count == 0) return null;

            // Materialize the buffer up front (one-time cost; we know we have
            // at least one on-canvas pin to draw). BGRA top-down → RGBA
            // bottom-up so BlitText, BlitSpriteInto and EncodeRgbaBottomUp all
            // agree on orientation.
            Color32[] buf = MaterializeBuffer(bgraTopDown, w, h);

            // Hidden world-space canvas at the origin, 1 unit = 1 output pixel,
            // pivot at bottom-left so a child anchored at (px,py) sits exactly
            // at output pixel (px,py). Canvas disabled so it never renders;
            // everything below is synchronous (no yields) so no frame draws it.
            var root = new GameObject("NMDA_CompilePinStamp")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            bool fontOk = true;
            try
            {
                var canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.enabled = false;
                var crt = root.GetComponent<RectTransform>();
                crt.pivot = Vector2.zero;
                crt.sizeDelta = new Vector2(w, h);
                crt.localScale = Vector3.one;
                crt.position = Vector3.zero;
                crt.rotation = Quaternion.identity;

                // ── Pass 1: pin icons (no font needed) ───────────────────────
                // Survives the chroma-pick because compositing is already done;
                // survives any tile-overlap inconsistency because we draw each
                // pin once from the snapshot, not from whatever the per-tile
                // capture happened to bake.
                foreach (var pd in pinDraws)
                {
                    MapCaptureTexture.BlitSpriteInto(buf, w, h,
                        pd.icon, pd.tint,
                        pd.cx, pd.cyBottom, pd.iconW, pd.iconH);
                }

                // ── Pass 2: pin captions (need TMP font) ─────────────────────
                // Draws below each icon. Skipped silently when the Valheim font
                // can't be resolved (DrawTmpCaption sets fontOk=false) — the
                // icons still made it onto the composite.
                foreach (var pd in pinDraws)
                {
                    if (!fontOk) break;
                    if (string.IsNullOrEmpty(pd.name)) continue;
                    float captionY = pd.cyBottom - pd.iconH * 0.5f - pinFontPx * 0.6f;
                    DrawTmpCaption(root, buf, w, h,
                        pd.name, pd.cx, captionY,
                        pinFontPx, pinFontStyle, ref fontOk);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                MapCaptureTexture.ClearLabelAtlasCache();
            }

            return buf;
        }

        // BGRA top-down → RGBA bottom-up, opaque alpha. Single allocation +
        // copy; shared by the label and pin passes.
        private static Color32[] MaterializeBuffer(byte[] bgraTopDown, int w, int h)
        {
            var buf = new Color32[w * h];
            for (int row = 0; row < h; row++)
            {
                int dst = (h - 1 - row) * w;
                int src = row * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int s = src + x * 4;
                    buf[dst + x] = new Color32(
                        bgraTopDown[s + 2], bgraTopDown[s + 1],
                        bgraTopDown[s], 255);
                }
            }
            return buf;
        }

        // Draw one caption with a black outline + white face into buf. Uses
        // BlitText's single-pass SDF-band outline (drawOutline=true) — the
        // same path the live send/copy walk uses — so a composite full of
        // pins pays one rasterize per caption instead of N offset rasterizes.
        // Returns true if the caption actually rasterized; sets fontOk=false
        // (and returns false) when the Valheim font can't be resolved on the
        // first attempt — caller is expected to short-circuit further text
        // draws after that.
        private static bool DrawTmpCaption(GameObject root, Color32[] buf, int w, int h,
            string text, float px, float pyBottom, float fontPx, FontStyles fontStyle,
            ref bool fontOk)
        {
            var go = new GameObject("lbl");
            // Inactive until the font is assigned so TMP's Awake doesn't log
            // the missing "LiberationSans SDF" default-font warning. Activated
            // below once the real font is set, before the mesh is built.
            go.SetActive(false);
            go.transform.SetParent(root.transform, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();

            MapUI.ApplyValheimFont(tmp);
            if (tmp.font == null) { fontOk = false; return false; }

            tmp.text = text;
            tmp.fontSize = fontPx;
            // Take the FontStyles enum from the vanilla pin-name prefab
            // verbatim — assigning Normal here would override any italic /
            // smallcaps / uppercase the prefab uses, so the compile caption
            // would render in a different style than the live map.
            tmp.fontStyle = fontStyle;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            tmp.color = Color.white;

            var trt = tmp.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.zero;
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(
                Mathf.Max(64f, text.Length * fontPx), fontPx * 2f);
            trt.anchoredPosition = new Vector2(px, pyBottom);

            go.SetActive(true);
            tmp.ForceMeshUpdate();
            MapCaptureTexture.RasterizeTmpInto(buf, w, h, tmp,
                forceMeshUpdate: false, drawOutline: true);

            UnityEngine.Object.DestroyImmediate(go);
            return true;
        }

        // Shrink one axis of the (square) pin box so the sprite is drawn at
        // its native aspect ratio instead of being stretched to fill the box —
        // matching Unity's Image.preserveAspect, which Valheim's pin markers
        // use. Sizes come from the sprite's textureRect (the actual sampled
        // region). No-op for square sprites or a missing/degenerate rect.
        private static void FitSpriteAspect(Sprite icon, ref float boxW, ref float boxH)
        {
            if (icon == null) return;
            Rect r = icon.textureRect;
            if (r.width <= 0f || r.height <= 0f) return;
            float spriteAspect = r.width / r.height;
            float boxAspect = boxW / boxH;
            if (spriteAspect > boxAspect)
                boxH = boxW / spriteAspect;   // sprite is wider — pillar-box vertically
            else
                boxW = boxH * spriteAspect;   // sprite is taller — letter-box horizontally
        }

        // Mean world-X width of the captured tiles, used to size pins relative
        // to a single tile (= one live-map view) rather than the whole
        // composite. All local captures share the same large-map zoom so the
        // widths are near-identical; averaging just keeps an odd imported tile
        // from skewing the result. Falls back to the full composite width when
        // the tile list is empty (treated as one implicit tile ⇒ factor 1).
        private static float AverageTileWorldWidth(
            IReadOnlyList<MapCompileTile> tiles, float compositeWorldW)
        {
            if (tiles == null || tiles.Count == 0) return compositeWorldW;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                float tw = tiles[i].WorldSize.x;
                if (tw > 0f) { sum += tw; n++; }
            }
            return n > 0 ? sum / n : compositeWorldW;
        }

        // True when (wx, wz) lands inside any captured tile's world rect.
        // Used to cull pins that sit in the black gaps between non-adjacent
        // tiles within the composite's bounding rect. If the tile list is
        // null/empty, fall back to permissive behavior (keep all pins) — the
        // outer bounding-rect cull already happened.
        private static bool IsInsideAnyTile(float wx, float wz,
            IReadOnlyList<MapCompileTile> tiles)
        {
            if (tiles == null || tiles.Count == 0) return true;
            for (int i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (wx >= t.WorldMin.x && wx <= t.WorldMax.x
                    && wz >= t.WorldMin.y && wz <= t.WorldMax.y)
                    return true;
            }
            return false;
        }

        // ── Vanilla pin caption attributes ───────────────────────────────────
        // Looked up once per session from Minimap.m_pinNamePrefab so compile
        // captions inherit the exact fontSize / FontStyles the live map uses.
        // Cached after the first successful read because the prefab is a
        // scene-stable asset for the lifetime of the Minimap; missing-prefab
        // path falls back to 16 px Normal but doesn't cache so a future Stamp
        // can retry once the prefab becomes reachable.
        private static float _vanillaPinFontSize = -1f;
        private static FontStyles _vanillaPinFontStyle;

        private static void SampleVanillaPinTmp(out float fontSize, out FontStyles fontStyle)
        {
            if (_vanillaPinFontSize > 0f)
            {
                fontSize = _vanillaPinFontSize;
                fontStyle = _vanillaPinFontStyle;
                return;
            }

            var mini = Minimap.instance;
            var prefab = mini != null ? mini.m_pinNamePrefab : null;
            if (prefab != null)
            {
                var sample = prefab.GetComponentInChildren<TMP_Text>(true);
                if (sample != null)
                {
                    _vanillaPinFontSize = sample.fontSize;
                    _vanillaPinFontStyle = sample.fontStyle;
                    fontSize = _vanillaPinFontSize;
                    fontStyle = _vanillaPinFontStyle;
                    return;
                }
            }

            fontSize = 16f;
            fontStyle = FontStyles.Normal;
        }
    }
}
