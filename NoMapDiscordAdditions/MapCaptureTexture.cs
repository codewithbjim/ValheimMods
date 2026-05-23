using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Alternative map capture that renders the large map through Valheim's
    /// own map material, then overlays the live UI pin Images on top — so we
    /// get the rich shader output (forest masks, height shading, water, fog)
    /// the player sees, without going through ScreenCapture and without the
    /// animated cloud/wind layer (cloud-named shader properties are zeroed
    /// for the single render pass).
    /// </summary>
    public static class MapCaptureTexture
    {
        // ── Fallback output size ────────────────────────────────────────────
        // Used only when the on-screen map rect can't be measured (see
        // GetDefaultCaptureSize) and as the compile degenerate-uv fallback.
        // The normal default size is derived from the player's resolution.
        public const int OutputWidth = 1920;
        public const int OutputHeight = 1080;

        // Per-capture atlas cache so each unique sprite atlas only pays one
        // GetPixels32() (potentially MBs of data) regardless of how many pins
        // share it. Cleared at the end of every CaptureMap call so unloaded
        // textures don't leak.
        private static readonly Dictionary<Texture2D, Color32[]> _atlasCache =
            new Dictionary<Texture2D, Color32[]>();

        // ── uv-clamp remap (the compile "squished icons" fix) ───────────────
        // The map terrain is rendered from the uvRect *clamped* to [0,1]
        // (DrawMapBase) and the world rect is derived the same way
        // (MapCompileTile.ComputeWorldRect), so terrain composites correctly.
        // But Valheim lays pins out on screen using the *raw* uvRect
        // (Minimap.MapPointToLocalGuiPos: (mx-uvRect.xMin)/uvRect.width). When
        // the large map is zoomed out, uvRect.width = m_largeZoom*screenAspect
        // exceeds 1.0 (m_maxZoom=1, aspect≈1.78) while height ≤ 1.0, so the
        // clamp shrinks width but not height. Blitting icons by their on-screen
        // position then stretches every icon horizontally by rawW/clampedW and
        // shifts it — the "height squished, icons wider" compile artefact.
        //
        // These fields carry the raw vs clamped uv span for one capture so the
        // pin/label blit can map screen px → the clamped uv space the PNG
        // actually represents. _uvRemapActive gates it: it's only set for the
        // CaptureMap pin pass, so RasterizeTmpInto (compile label stamp onto
        // the finished composite) keeps its plain 1:1 mapping. Every helper
        // collapses to the old formula when inactive or unclamped, so framed
        // COPY/SEND captures are bit-for-bit unchanged.
        private static bool _uvRemapActive;
        private static float _ruX0, _ruX1, _ruY0, _ruY1; // raw uv span
        private static float _cuX0, _cuY0, _cuW, _cuH;    // clamped uv origin/size

        // ═══════════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// The default capture size: the on-screen large-map image's pixel
        /// dimensions — so a texture capture matches the player's resolution
        /// instead of a fixed 1920×1080. Falls back to <see cref="OutputWidth"/>
        /// × <see cref="OutputHeight"/> when the map rect can't be measured.
        /// </summary>
        public static void GetDefaultCaptureSize(out int width, out int height)
        {
            width = OutputWidth;
            height = OutputHeight;

            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mapImageLarge == null)
                return;

            var corners = new Vector3[4];
            minimap.m_mapImageLarge.rectTransform.GetWorldCorners(corners);
            int w = Mathf.RoundToInt(Mathf.Abs(corners[2].x - corners[0].x));
            int h = Mathf.RoundToInt(Mathf.Abs(corners[2].y - corners[0].y));
            if (w >= 64 && h >= 64)
            {
                width = w;
                height = h;
            }
        }

        /// <summary>
        /// Captures the visible large map without the cloud overlay at the
        /// player's resolution (see <see cref="GetDefaultCaptureSize"/>).
        /// Synchronous — does not require yielding to end-of-frame.
        /// </summary>
        public static byte[] CaptureMap()
        {
            GetDefaultCaptureSize(out int w, out int h);
            return CaptureMap(w, h, true);
        }

        /// <summary>
        /// Default-resolution capture, with explicit control over whether the
        /// per-pin "of Spawn" captions are baked in. Compile mode uses this so
        /// the labels can be gated by its own config independently of plain
        /// COPY/SEND captures.
        /// </summary>
        public static byte[] CaptureMap(bool includePinLabels)
        {
            GetDefaultCaptureSize(out int w, out int h);
            return CaptureMap(w, h, includePinLabels);
        }

        /// <summary>
        /// Captures the visible large map at a custom output resolution.
        /// The shader runs at <paramref name="outputWidth"/> × <paramref name="outputHeight"/>
        /// fragments and pin sprites are rasterized in CPU at the same scale,
        /// so output is sharp regardless of zoom or input map size.
        /// <paramref name="includePinLabels"/> gates the TMP caption pass.
        /// </summary>
        public static byte[] CaptureMap(int outputWidth, int outputHeight,
            bool includePinLabels = true, Texture2D styledBase = null)
        {
            if (outputWidth < 64 || outputHeight < 64)
            {
                ModLog.Error($"[NoMapDiscordAdditions] CaptureMap: invalid size {outputWidth}x{outputHeight}.");
                return null;
            }

            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mode != Minimap.MapMode.Large)
            {
                ModLog.Error("[NoMapDiscordAdditions] Large map not active.");
                return null;
            }

            Texture2D mapTex = minimap.m_mapTexture;
            RawImage mapImage = minimap.m_mapImageLarge;
            RectTransform pinRoot = minimap.m_pinRootLarge;

            if (mapTex == null || mapImage == null)
            {
                ModLog.Error("[NoMapDiscordAdditions] Required Minimap fields not available.");
                return null;
            }

            // Use the actual map material so we get the rich shader output (forest masks,
            // height shading, water, fog) — anything sampled CPU-side from m_mapTexture
            // alone is just flat biome colors.
            Material mat = minimap.m_mapLargeShader;
            if (mat == null) mat = mapImage.material;

            var mapRT = mapImage.rectTransform;
            Vector3[] mapCorners = new Vector3[4];
            mapRT.GetWorldCorners(mapCorners);
            float mapMinX = mapCorners[0].x;
            float mapMinY = mapCorners[0].y;
            float mapMaxX = mapCorners[2].x;
            float mapMaxY = mapCorners[2].y;
            float mapW = mapMaxX - mapMinX;
            float mapH = mapMaxY - mapMinY;
            if (mapW <= 0f || mapH <= 0f)
            {
                ModLog.Error("[NoMapDiscordAdditions] Map RectTransform has zero size.");
                return null;
            }

            // ── GPU pass: map base on a single RT ─────────────────────────────
            // With a Map Style active the caller passes a pre-rendered stylized
            // texture — already the captured viewport at output resolution — so
            // we blit it straight in as the base in place of the shader pass.
            // Pins/markers/labels below overlay it unchanged. styledBase is
            // owned by the caller — not destroyed here.
            var cropRT = RenderTexture.GetTemporary(outputWidth, outputHeight, 0,
                RenderTextureFormat.ARGB32);
            Color32[] output;
            try
            {
                if (styledBase != null)
                    DrawStyledBase(cropRT, styledBase);
                else
                    DrawMapBase(cropRT, mapTex, mat, mapImage.uvRect);
                output = ReadRTPixels(cropRT);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(cropRT);
            }

            if (output == null)
            {
                ModLog.Error("[NoMapDiscordAdditions] GPU pass failed.");
                return null;
            }

            // ── CPU pass: pins + markers on top of the readback ───────────────
            // Snapshot the raw vs clamped uv span so the icon/label blit can
            // undo Valheim's screen-space pin layout (raw uvRect) into the
            // clamped uv the PNG actually shows. Disabled (identity) when the
            // viewport is fully inside [0,1] or degenerate.
            BeginUvRemap(mapImage.uvRect);

            try
            {
                // Activate the per-pin "of Spawn" captions so they bake into
                // the output, same as the screen-capture path does. Without
                // this the texture path (used by compile mode) never showed
                // them even with the label config enabled. Hidden again in
                // the finally below. Compile mode passes includePinLabels=false
                // to opt out via its own "Show on Compile Mode" config.
                if (includePinLabels)
                    TablePinLabel.ShowForCapture();

                if (pinRoot != null)
                    BlitUIChildren(output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH, pinRoot);

                BlitMarker(minimap.m_largeMarker, output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH);
                BlitMarker(minimap.m_largeShipMarker, output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH);

                // Spawn-direction text now lives in the Discord message
                // ({spawnDir} placeholder) and on the in-game per-pin labels;
                // burning a software-rasterized copy into the PNG produced an
                // ugly bitmap-looking overlay, so this path is intentionally
                // omitted.

                return Encode(output, outputWidth, outputHeight);
            }
            finally
            {
                TablePinLabel.HideAll();
                // Drop atlas references so unloaded textures can be GC'd.
                _atlasCache.Clear();
                EndUvRemap();
            }
        }

        // ── uv-clamp remap helpers ───────────────────────────────────────────

        // Capture the raw uvRect and its [0,1]-clamped counterpart. Remap is
        // only armed when the clamp actually shrinks a span (i.e. the viewport
        // pokes outside the map texture — the zoomed-out compile case) and the
        // clamped span is non-degenerate; otherwise every helper stays identity.
        private static void BeginUvRemap(Rect uv)
        {
            _ruX0 = uv.xMin; _ruX1 = uv.xMax;
            _ruY0 = uv.yMin; _ruY1 = uv.yMax;

            _cuX0 = Mathf.Clamp01(_ruX0);
            _cuY0 = Mathf.Clamp01(_ruY0);
            _cuW = Mathf.Clamp01(_ruX1) - _cuX0;
            _cuH = Mathf.Clamp01(_ruY1) - _cuY0;

            float rawW = _ruX1 - _ruX0;
            float rawH = _ruY1 - _ruY0;
            _uvRemapActive =
                _cuW > 1e-6f && _cuH > 1e-6f && rawW > 1e-6f && rawH > 1e-6f &&
                (Mathf.Abs(rawW - _cuW) > 1e-5f || Mathf.Abs(rawH - _cuH) > 1e-5f);
        }

        private static void EndUvRemap() => _uvRemapActive = false;

        // screen X (px) → output X (px), through raw→clamped uv. Identity
        // (fx*outW) when the remap is inactive or unclamped — matches the
        // original ((cx-mapMinX)/mapW)*outputWidth exactly.
        private static float ProjectX(float screenX, float mapMinX, float mapW, int outW)
        {
            float fx = (screenX - mapMinX) / mapW;
            if (!_uvRemapActive) return fx * outW;
            float ux = _ruX0 + fx * (_ruX1 - _ruX0);
            return (ux - _cuX0) / _cuW * outW;
        }

        private static float ProjectY(float screenY, float mapMinY, float mapH, int outH)
        {
            float fy = (screenY - mapMinY) / mapH;
            if (!_uvRemapActive) return fy * outH;
            float uy = _ruY0 + fy * (_ruY1 - _ruY0);
            return (uy - _cuY0) / _cuH * outH;
        }

        // Output px per screen px. Identity (outW/mapW) when inactive; folds in
        // the rawSpan/clampedSpan correction otherwise so icons keep the same
        // px size in clamped uv space that they had on screen.
        private static float RemapScaleX(float mapW, int outW)
        {
            if (!_uvRemapActive) return outW / mapW;
            return (outW / _cuW) * ((_ruX1 - _ruX0) / mapW);
        }

        private static float RemapScaleY(float mapH, int outH)
        {
            if (!_uvRemapActive) return outH / mapH;
            return (outH / _cuH) * ((_ruY1 - _ruY0) / mapH);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Map base — render through the actual map material, then crop to viewport
        // ═══════════════════════════════════════════════════════════════════

        // Render the map material directly at the target RT size by drawing
        // a fullscreen quad whose texcoords are the cropped viewport. The shader
        // runs at output resolution (one fragment per output pixel), each sampling
        // textures at the right place — screen-quality output regardless of zoom.
        // Cloud shader properties are temporarily zeroed for this single pass.
        private static void DrawMapBase(RenderTexture target, Texture2D mapTex, Material mat, Rect uv)
        {
            float uMin = Mathf.Clamp01(uv.xMin);
            float vMin = Mathf.Clamp01(uv.yMin);
            float uMax = Mathf.Clamp01(uv.xMax);
            float vMax = Mathf.Clamp01(uv.yMax);

            List<ModHelpers.SavedShaderProp> savedClouds = null;
            Texture savedMainTex = null;
            bool hasMainTex = mat != null && mat.HasProperty("_MainTex");

            // Normalize time-of-day lighting so tiles captured at any hour
            // composite without brightness seams (globals restored in finally).
            bool normalizeLighting = Plugin.NormalizeCaptureLighting?.Value ?? true;
            ModHelpers.SavedLighting savedLighting = default;
            if (normalizeLighting)
                savedLighting = ModHelpers.OverrideLightingToNoon();

            if (ModHelpers.EffectiveConfig.HideClouds && mat != null)
                savedClouds = ModHelpers.SuppressShaderPropsContaining(mat, "cloud");
            if (hasMainTex)
            {
                savedMainTex = mat.GetTexture("_MainTex");
                mat.SetTexture("_MainTex", mapTex);
            }

            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.black);

                bool drewWithMaterial = false;
                if (mat != null && mat.SetPass(0))
                {
                    GL.PushMatrix();
                    GL.LoadOrtho();
                    GL.Begin(GL.QUADS);
                    GL.TexCoord2(uMin, vMin); GL.Vertex3(0f, 0f, 0f);
                    GL.TexCoord2(uMax, vMin); GL.Vertex3(1f, 0f, 0f);
                    GL.TexCoord2(uMax, vMax); GL.Vertex3(1f, 1f, 0f);
                    GL.TexCoord2(uMin, vMax); GL.Vertex3(0f, 1f, 0f);
                    GL.End();
                    GL.PopMatrix();
                    drewWithMaterial = true;
                }

                if (!drewWithMaterial)
                {
                    // Fallback: at least show the cropped biome colors.
                    Graphics.Blit(mapTex, target,
                        new Vector2(uMax - uMin, vMax - vMin),
                        new Vector2(uMin, vMin));
                }
            }
            finally
            {
                RenderTexture.active = prev;
                if (hasMainTex && savedMainTex != null)
                    mat.SetTexture("_MainTex", savedMainTex);
                if (savedClouds != null) ModHelpers.RestoreShaderProps(mat, savedClouds);
                ModHelpers.RestoreLighting(savedLighting);
            }
        }

        // Blit the pre-rendered stylized map texture into the output RT. The
        // styled texture already covers exactly the captured viewport (the
        // clamped uvRect) at output resolution — the same framing DrawMapBase
        // produces — so this is a straight 1:1 blit and pins, positioned via
        // the same clamped-uv remap below, line up identically.
        private static void DrawStyledBase(RenderTexture target, Texture2D styled)
        {
            Graphics.Blit(styled, target);
        }

        private static Color32[] ReadRTPixels(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            Color32[] pixels = tex.GetPixels32();
            Object.Destroy(tex);
            return pixels;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  UI extraction — walk live UI children and blit each Image's sprite
        // ═══════════════════════════════════════════════════════════════════

        private static void BlitUIChildren(Color32[] output, int outputWidth, int outputHeight,
            float mapMinX, float mapMinY, float mapW, float mapH, Transform parent)
        {
            int count = parent.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = parent.GetChild(i);
                if (child == null || !child.gameObject.activeInHierarchy)
                    continue;

                var rt = child as RectTransform;
                if (rt == null) continue;

                var img = child.GetComponent<Image>();
                if (img != null && img.enabled && img.sprite != null)
                    BlitImage(output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH, rt, img);

                // TMP captions (the per-pin "of Spawn" labels) aren't Images,
                // so the sprite blit above never sees them — rasterize their
                // glyphs from the font atlas here.
                var tmp = child.GetComponent<TMP_Text>();
                if (tmp != null && tmp.enabled && !string.IsNullOrEmpty(tmp.text))
                    BlitText(output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH, tmp);

                // Recurse — pin GameObjects sometimes nest icons under a wrapper.
                if (child.childCount > 0)
                    BlitUIChildren(output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH, child);
            }
        }

        private static void BlitMarker(RectTransform rt,
            Color32[] output, int outputWidth, int outputHeight,
            float mapMinX, float mapMinY, float mapW, float mapH)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return;

            var img = rt.GetComponent<Image>();
            if (img != null && img.enabled && img.sprite != null)
                BlitImage(output, outputWidth, outputHeight, mapMinX, mapMinY, mapW, mapH, rt, img);
        }

        private static void BlitImage(Color32[] output,
            int outputWidth, int outputHeight,
            float mapMinX, float mapMinY, float mapW, float mapH,
            RectTransform target, Image img)
        {
            Vector3[] corners = new Vector3[4];
            target.GetWorldCorners(corners);

            // Center + size in screen-pixel space (Screen Space Overlay).
            float cx = (corners[0].x + corners[2].x) * 0.5f;
            float cy = (corners[0].y + corners[2].y) * 0.5f;
            float wPx = corners[2].x - corners[0].x;
            float hPx = corners[2].y - corners[0].y;
            if (wPx <= 0f || hPx <= 0f) return;

            // Map the icon's on-screen centre into output px through the
            // raw→clamped uv remap (identity when unclamped) so it lines up
            // with the clamped-uv terrain instead of being stretched/shifted.
            int outCx = Mathf.RoundToInt(ProjectX(cx, mapMinX, mapW, outputWidth));
            int outCy = Mathf.RoundToInt(ProjectY(cy, mapMinY, mapH, outputHeight));

            float scaleX = RemapScaleX(mapW, outputWidth);
            float scaleY = RemapScaleY(mapH, outputHeight);
            int outW = Mathf.Max(1, Mathf.RoundToInt(wPx * scaleX));
            int outH = Mathf.Max(1, Mathf.RoundToInt(hPx * scaleY));

            // Fast cull if completely outside the buffer.
            int halfW = outW / 2;
            int halfH = outH / 2;
            if (outCx + halfW < 0 || outCx - halfW >= outputWidth ||
                outCy + halfH < 0 || outCy - halfH >= outputHeight)
                return;

            // Read sprite sub-rect from its atlas.
            Sprite sprite = img.sprite;
            Rect rect = sprite.textureRect;
            int srcX = Mathf.RoundToInt(rect.x);
            int srcY = Mathf.RoundToInt(rect.y);
            int srcW = Mathf.RoundToInt(rect.width);
            int srcH = Mathf.RoundToInt(rect.height);
            if (srcW <= 0 || srcH <= 0) return;

            Color32[] atlasPixels = GetCachedAtlasPixels(sprite.texture);
            if (atlasPixels == null) return;
            int atlasW = sprite.texture.width;

            Color32 tint = img.color;
            float invOutW = (float)srcW / outW;
            float invOutH = (float)srcH / outH;

            for (int dy = 0; dy < outH; dy++)
            {
                int py = outCy - halfH + dy;
                if (py < 0 || py >= outputHeight) continue;

                int sy = Mathf.Clamp((int)(dy * invOutH), 0, srcH - 1);
                int srcRowOffset = (srcY + sy) * atlasW + srcX;
                int dstRowOffset = py * outputWidth;

                for (int dx = 0; dx < outW; dx++)
                {
                    int px = outCx - halfW + dx;
                    if (px < 0 || px >= outputWidth) continue;

                    int sx = Mathf.Clamp((int)(dx * invOutW), 0, srcW - 1);
                    Color32 sp = atlasPixels[srcRowOffset + sx];

                    // Apply Image.color tint.
                    int sa = (sp.a * tint.a) / 255;
                    if (sa == 0) continue;

                    int sr = (sp.r * tint.r) / 255;
                    int sg = (sp.g * tint.g) / 255;
                    int sb = (sp.b * tint.b) / 255;

                    int di = dstRowOffset + px;
                    if (sa >= 250)
                    {
                        output[di] = new Color32((byte)sr, (byte)sg, (byte)sb, 255);
                    }
                    else
                    {
                        Color32 dst = output[di];
                        int ia = 255 - sa;
                        output[di] = new Color32(
                            (byte)((sr * sa + dst.r * ia) / 255),
                            (byte)((sg * sa + dst.g * ia) / 255),
                            (byte)((sb * sa + dst.b * ia) / 255),
                            255);
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  TMP text — rasterize glyphs from the font atlas (SDF aware)
        // ═══════════════════════════════════════════════════════════════════

        // Software-render a TMP_Text by walking its generated character mesh.
        // Each glyph quad is mapped from the text RectTransform (screen-pixel
        // space, Screen Space Overlay) into the output buffer the same way
        // BlitImage maps sprites, then sampled out of the font atlas. Valheim's
        // TMP fonts are SDF, so the sampled value is a signed distance — we
        // threshold around the 0.5 edge with a small soft band for mild AA.
        // forceMeshUpdate=false skips the (expensive) TMP mesh rebuild — only
        // safe when the caller guarantees the text/size/color/font are
        // unchanged since the last update and only the RectTransform moved
        // (glyph positions are read live via TransformPoint, so a move alone
        // needs no rebuild). The compile label stamp exploits this to do one
        // rebuild per outline pass instead of one per offset.
        private static void BlitText(Color32[] output,
            int outputWidth, int outputHeight,
            float mapMinX, float mapMinY, float mapW, float mapH,
            TMP_Text tmp, bool forceMeshUpdate = true)
        {
            if (forceMeshUpdate) tmp.ForceMeshUpdate();
            var info = tmp.textInfo;
            if (info == null || info.characterCount == 0) return;

            var rt = tmp.rectTransform;

            for (int ci = 0; ci < info.characterCount; ci++)
            {
                var ch = info.characterInfo[ci];
                if (!ch.isVisible) continue;

                Texture2D atlas = (ch.material != null ? ch.material.mainTexture : null) as Texture2D;
                if (atlas == null && ch.fontAsset != null) atlas = ch.fontAsset.atlasTexture;
                if (atlas == null) continue;

                Color32[] atlasPixels = GetCachedAtlasPixels(atlas);
                if (atlasPixels == null) continue;
                int atlasW = atlas.width;
                int atlasH = atlas.height;

                // Glyph quad → world (screen px for an overlay canvas) → output px.
                Vector3 blW = rt.TransformPoint(ch.vertex_BL.position);
                Vector3 trW = rt.TransformPoint(ch.vertex_TR.position);

                // Same raw→clamped uv remap as the sprite path (identity for
                // the 1:1 compile label stamp, where the remap is inactive).
                float ox0 = ProjectX(blW.x, mapMinX, mapW, outputWidth);
                float ox1 = ProjectX(trW.x, mapMinX, mapW, outputWidth);
                float oy0 = ProjectY(blW.y, mapMinY, mapH, outputHeight);
                float oy1 = ProjectY(trW.y, mapMinY, mapH, outputHeight);

                int px0 = Mathf.FloorToInt(Mathf.Min(ox0, ox1));
                int px1 = Mathf.CeilToInt(Mathf.Max(ox0, ox1));
                int py0 = Mathf.FloorToInt(Mathf.Min(oy0, oy1));
                int py1 = Mathf.CeilToInt(Mathf.Max(oy0, oy1));
                if (px1 <= px0 || py1 <= py0) continue;
                if (px1 < 0 || px0 >= outputWidth || py1 < 0 || py0 >= outputHeight) continue;

                // Atlas UV span for this glyph (uv.xy; y origin bottom, matching
                // Texture2D.GetPixels32 row order).
                Vector4 uvBL = ch.vertex_BL.uv;
                Vector4 uvTR = ch.vertex_TR.uv;
                float uMin = uvBL.x, uMax = uvTR.x;
                float vMin = uvBL.y, vMax = uvTR.y;

                Color32 col = ch.color;
                if (col.a == 0) col = tmp.color;

                float invW = 1f / (ox1 - ox0);
                float invH = 1f / (oy1 - oy0);

                for (int py = Mathf.Max(py0, 0); py < Mathf.Min(py1, outputHeight); py++)
                {
                    float t = (py + 0.5f - oy0) * invH;     // 0 at BL.y → 1 at TR.y
                    float v = Mathf.Lerp(vMin, vMax, Mathf.Clamp01(t));
                    int ay = Mathf.Clamp((int)(v * atlasH), 0, atlasH - 1);
                    int atlasRow = ay * atlasW;
                    int dstRow = py * outputWidth;

                    for (int px = Mathf.Max(px0, 0); px < Mathf.Min(px1, outputWidth); px++)
                    {
                        float s = (px + 0.5f - ox0) * invW;
                        float u = Mathf.Lerp(uMin, uMax, Mathf.Clamp01(s));
                        int ax = Mathf.Clamp((int)(u * atlasW), 0, atlasW - 1);

                        Color32 sp = atlasPixels[atlasRow + ax];
                        // Robust across Alpha8 / SDF-in-alpha / luminance atlases.
                        int dv = sp.a;
                        if (sp.r > dv) dv = sp.r;
                        if (sp.g > dv) dv = sp.g;
                        if (sp.b > dv) dv = sp.b;
                        float d = dv / 255f;

                        // SDF edge ≈ 0.5; small band gives soft (not jaggy) edges.
                        float cov = Mathf.Clamp01((d - 0.46f) / 0.08f);
                        if (cov <= 0f) continue;

                        int sa = (int)(cov * col.a);
                        if (sa <= 0) continue;

                        int di = dstRow + px;
                        Color32 dst = output[di];
                        if (sa >= 255)
                        {
                            output[di] = new Color32(col.r, col.g, col.b, 255);
                        }
                        else
                        {
                            int ia = 255 - sa;
                            output[di] = new Color32(
                                (byte)((col.r * sa + dst.r * ia) / 255),
                                (byte)((col.g * sa + dst.g * ia) / 255),
                                (byte)((col.b * sa + dst.b * ia) / 255),
                                255);
                        }
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Texture sampling helpers
        // ═══════════════════════════════════════════════════════════════════

        // Many pins share the same atlas. ReadPixelsSafe allocates a full
        // Color32[] of the atlas (often 1024×1024 = ~4MB) per call, so caching
        // by texture instance for the duration of a single capture pays off
        // immediately as soon as 2+ pins share an atlas.
        private static Color32[] GetCachedAtlasPixels(Texture2D src)
        {
            if (src == null) return null;
            if (_atlasCache.TryGetValue(src, out var cached))
                return cached;

            Color32[] pixels = ReadPixelsSafe(src);
            _atlasCache[src] = pixels;
            return pixels;
        }

        private static Color32[] ReadPixelsSafe(Texture2D src)
        {
            if (src == null) return null;
            try
            {
                return src.GetPixels32();
            }
            catch
            {
                // GPU-only texture — round-trip through a RenderTexture.
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0,
                    RenderTextureFormat.ARGB32);
                Graphics.Blit(src, rt);

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                readable.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                Color32[] px = readable.GetPixels32();
                Object.Destroy(readable);
                return px;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Encode
        // ═══════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════
        //  Compile-mode label stamp reuse
        // ═══════════════════════════════════════════════════════════════════

        // Rasterize one laid-out TMP_Text straight into a Color32[] buffer in
        // 1:1 pixel space (buffer index = py*w + px, y bottom-up). Lets the
        // compile finalize step draw captions with Valheim's real SDF font
        // through the exact same glyph path the screen capture uses.
        internal static void RasterizeTmpInto(Color32[] buf, int w, int h, TMP_Text tmp,
            bool forceMeshUpdate = true)
            => BlitText(buf, w, h, 0f, 0f, w, h, tmp, forceMeshUpdate);

        // The atlas pixel cache is keyed by texture for the duration of one
        // operation; the compile stamp must clear it when done so an unloaded
        // font atlas can't leak (same contract as the end of CaptureMap).
        internal static void ClearLabelAtlasCache() => _atlasCache.Clear();

        private static byte[] Encode(Color32[] output, int outputWidth, int outputHeight)
        {
            var tex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);
            tex.SetPixels32(output);
            tex.Apply();
            byte[] data = ImageConversion.EncodeToPNG(tex);
            Object.Destroy(tex);
            return data;
        }
    }
}
