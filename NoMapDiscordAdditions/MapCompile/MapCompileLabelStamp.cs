using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using TMPro;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Finalizes a <see cref="MapCompositor.CompiledMap"/>: stamps the table
    /// captions onto the composite using Valheim's real TMP font (the exact
    /// SDF asset + outline material the in-game UI uses), then encodes one PNG.
    ///
    /// The label pass MUST run on the Unity main thread (TMP layout + font
    /// atlas reads), so it can't live inside the off-thread System.Drawing
    /// compose. Flow: compose off-thread → this stamps on the main thread →
    /// the single PNG encode is pushed back off-thread. Captions are drawn once
    /// onto the merged image (never baked per tile — they'd be eaten by the
    /// chroma-pick in tile-overlap regions).
    /// </summary>
    public static class MapCompileLabelStamp
    {
        /// <summary>
        /// Main-thread + off-thread finalize. Stamps labels (if any, and if the
        /// Valheim font resolves) into the composite, then encodes
        /// <see cref="MapCompositor.CompiledMap.PngBytes"/>. Yield this from a
        /// coroutine; on return <c>r.PngBytes</c> is set (or null on failure).
        /// </summary>
        public static IEnumerator Finalize(MapCompositor.CompiledMap r,
            IReadOnlyList<MapCompositor.LabelDraw> labels)
        {
            if (r == null || r.Bgra == null) yield break;

            // Main thread: try to stamp labels. Returns a labelled RGBA
            // (bottom-up) buffer, or null if there's nothing to draw / the
            // font is unavailable (then we encode the raw BGRA directly).
            Color32[] labelled = null;
            try
            {
                labelled = Stamp(r.Bgra, r.Width, r.Height,
                    r.WorldMin, r.WorldMax - r.WorldMin, labels);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Label stamp failed: {ex.Message}");
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
                        ? MapCompositor.EncodeRgbaBottomUp(labelled, r.Width, r.Height)
                        : MapCompositor.EncodeBgra(rawBgra, r.Width, r.Height);
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
            IReadOnlyList<MapCompositor.LabelDraw> labels)
        {
            if (labels == null || labels.Count == 0) return null;
            if (worldSize.x <= 0f || worldSize.y <= 0f) return null;

            float fontPx = Mathf.Clamp(w / 120f, 10f, 25f);
            int outline = Mathf.Max(1, Mathf.RoundToInt(fontPx / 12f));

            // Cull to on-canvas captions BEFORE touching the big buffer. A
            // native (8192²) save converts to a ~268MB Color32[]; doing that
            // unconditionally meant a session whose labels all fell outside the
            // composed bounds paid the full allocation + per-pixel copy only to
            // discard it. Resolve the draw list first; if nothing survives we
            // return null without allocating anything.
            var draws = new List<(string text, float px, float pyBottom)>();
            foreach (var l in labels)
            {
                if (string.IsNullOrEmpty(l.Text)) continue;
                float fx = (l.WorldX - worldMin.x) / worldSize.x;
                float fyTop = (worldSize.y - (l.WorldZ - worldMin.y)) / worldSize.y;
                if (fx < 0f || fx > 1f || fyTop < 0f || fyTop > 1f) continue;
                // Sit just below the table point (matches the in-game caption);
                // convert to the bottom-up buffer's Y.
                float pyTop = fyTop * h + fontPx * 0.9f;
                draws.Add((l.Text, fx * w, h - pyTop));
            }
            if (draws.Count == 0) return null;

            // Lazily filled the first time a label actually rasterizes — and
            // only after the Valheim font resolves, so a missing font also
            // skips the big allocation entirely.
            Color32[] buf = null;

            // Hidden world-space canvas at the origin, 1 unit = 1 output pixel,
            // pivot at bottom-left so a child anchored at (px,py) sits exactly
            // at output pixel (px,py). Canvas disabled so it never renders;
            // everything below is synchronous (no yields) so no frame draws it.
            var root = new GameObject("NMDA_CompileLabelStamp")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            bool fontOk = true;
            bool drewAny = false;
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

                foreach (var (text, px, pyBottom) in draws)
                {
                    var go = new GameObject("lbl");
                    // Inactive until the font is assigned so TMP's Awake
                    // doesn't log the missing "LiberationSans SDF"
                    // default-font warning. Activated below once the real
                    // font is set, before the mesh is built/rasterized.
                    go.SetActive(false);
                    go.transform.SetParent(root.transform, false);
                    var tmp = go.AddComponent<TextMeshProUGUI>();

                    MapUI.ApplyValheimFont(tmp);
                    if (tmp.font == null) { fontOk = false; break; }

                    // Font resolved and we have an on-canvas caption — now it's
                    // worth materializing the full-resolution buffer. BGRA
                    // top-down → RGBA bottom-up (Unity Color32 / GetPixels32
                    // order) so BlitText and EncodeRgbaBottomUp agree on
                    // orientation.
                    if (buf == null)
                    {
                        buf = new Color32[w * h];
                        for (int row = 0; row < h; row++)
                        {
                            int dst = (h - 1 - row) * w;   // flip vertically
                            int src = row * w * 4;
                            for (int x = 0; x < w; x++)
                            {
                                int s = src + x * 4;
                                buf[dst + x] = new Color32(
                                    bgraTopDown[s + 2], bgraTopDown[s + 1],
                                    bgraTopDown[s], 255);
                            }
                        }
                    }

                    tmp.text = text;
                    tmp.fontSize = fontPx;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    tmp.raycastTarget = false;

                    var trt = tmp.rectTransform;
                    trt.anchorMin = Vector2.zero;
                    trt.anchorMax = Vector2.zero;
                    trt.pivot = new Vector2(0.5f, 0.5f);
                    trt.sizeDelta = new Vector2(
                        Mathf.Max(64f, text.Length * fontPx), fontPx * 2f);

                    // Font + layout set — safe to activate. The mesh build
                    // (ForceMeshUpdate) and rasterization happen below.
                    go.SetActive(true);

                    // Outline: black face stamped around the centre, then the
                    // white face on top — the Valheim outline material isn't in
                    // the SDF atlas so BlitText can't sample it; this keeps the
                    // caption readable over any biome (same idea as before, now
                    // with the real serif glyphs).
                    // One TMP mesh rebuild per colour, NOT one per offset. The
                    // outline is the same glyphs in the same colour at 48
                    // shifted positions; only the RectTransform moves between
                    // blits and BlitText reads glyph positions live, so a
                    // rebuild there is pure waste (it was ~49 rebuilds/label,
                    // seconds of main-thread freeze on a big native save).
                    // ForceMeshUpdate IS required after a colour change because
                    // TMP bakes vertex colour into the mesh BlitText samples.
                    tmp.color = new Color32(0, 0, 0, 220);
                    tmp.ForceMeshUpdate();
                    for (int oy = -outline; oy <= outline; oy++)
                        for (int ox = -outline; ox <= outline; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            trt.anchoredPosition = new Vector2(px + ox, pyBottom + oy);
                            MapCaptureTexture.RasterizeTmpInto(buf, w, h, tmp,
                                forceMeshUpdate: false);
                        }

                    tmp.color = Color.white;
                    tmp.ForceMeshUpdate();
                    trt.anchoredPosition = new Vector2(px, pyBottom);
                    MapCaptureTexture.RasterizeTmpInto(buf, w, h, tmp,
                        forceMeshUpdate: false);
                    drewAny = true;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                MapCaptureTexture.ClearLabelAtlasCache();
            }

            // Font missing, or nothing was on-canvas: discard the converted
            // buffer and let the caller encode the raw BGRA (labels skipped).
            if (!fontOk || !drewAny) return null;
            return buf;
        }
    }
}
