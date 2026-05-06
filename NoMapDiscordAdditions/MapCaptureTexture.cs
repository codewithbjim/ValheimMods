using System.Collections.Generic;
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
        // ── Output settings ─────────────────────────────────────────────────
        private const int OutputWidth = 1920;
        private const int OutputHeight = 1080;

        // Per-capture atlas cache so each unique sprite atlas only pays one
        // GetPixels32() (potentially MBs of data) regardless of how many pins
        // share it. Cleared at the end of every CaptureMap call so unloaded
        // textures don't leak.
        private static readonly Dictionary<Texture2D, Color32[]> _atlasCache =
            new Dictionary<Texture2D, Color32[]>();

        // ═══════════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Captures the visible large map without the cloud overlay.
        /// Synchronous — does not require yielding to end-of-frame.
        /// </summary>
        public static byte[] CaptureMap()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mode != Minimap.MapMode.Large)
            {
                Debug.LogError("[NoMapDiscordAdditions] Large map not active.");
                return null;
            }

            Texture2D mapTex = minimap.m_mapTexture;
            RawImage mapImage = minimap.m_mapImageLarge;
            RectTransform pinRoot = minimap.m_pinRootLarge;

            if (mapTex == null || mapImage == null)
            {
                Debug.LogError("[NoMapDiscordAdditions] Required Minimap fields not available.");
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
                Debug.LogError("[NoMapDiscordAdditions] Map RectTransform has zero size.");
                return null;
            }

            // ── GPU pass: map base on a single RT ─────────────────────────────
            var cropRT = RenderTexture.GetTemporary(OutputWidth, OutputHeight, 0,
                RenderTextureFormat.ARGB32);
            Color32[] output;
            try
            {
                DrawMapBase(cropRT, mapTex, mat, mapImage.uvRect);
                output = ReadRTPixels(cropRT);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(cropRT);
            }

            if (output == null)
            {
                Debug.LogError("[NoMapDiscordAdditions] GPU pass failed.");
                return null;
            }

            // ── CPU pass: pins + markers on top of the readback ───────────────
            try
            {
                if (pinRoot != null)
                    BlitUIChildren(output, mapMinX, mapMinY, mapW, mapH, pinRoot);

                BlitMarker(minimap.m_largeMarker, output, mapMinX, mapMinY, mapW, mapH);
                BlitMarker(minimap.m_largeShipMarker, output, mapMinX, mapMinY, mapW, mapH);

                // Spawn-direction text now lives in the Discord message
                // ({spawnDir} placeholder) and on the in-game per-pin labels;
                // burning a software-rasterized copy into the PNG produced an
                // ugly bitmap-looking overlay, so this path is intentionally
                // omitted.

                return Encode(output);
            }
            finally
            {
                // Drop atlas references so unloaded textures can be GC'd.
                _atlasCache.Clear();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Map base — render through the actual map material, then crop to viewport
        // ═══════════════════════════════════════════════════════════════════

        // Render the map material directly at OutputWidth × OutputHeight by drawing
        // a fullscreen quad whose texcoords are the cropped viewport. The shader
        // runs at output resolution (1920×1080 fragments), each sampling textures at
        // the right place — this gives screen-quality output regardless of zoom level.
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
            }
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

        private static void BlitUIChildren(Color32[] output,
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
                    BlitImage(output, mapMinX, mapMinY, mapW, mapH, rt, img);

                // Recurse — pin GameObjects sometimes nest icons under a wrapper.
                if (child.childCount > 0)
                    BlitUIChildren(output, mapMinX, mapMinY, mapW, mapH, child);
            }
        }

        private static void BlitMarker(RectTransform rt,
            Color32[] output, float mapMinX, float mapMinY, float mapW, float mapH)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return;

            var img = rt.GetComponent<Image>();
            if (img != null && img.enabled && img.sprite != null)
                BlitImage(output, mapMinX, mapMinY, mapW, mapH, rt, img);
        }

        private static void BlitImage(Color32[] output,
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

            // Translate to fractional position inside the map's screen rect, then to output px.
            float fx = (cx - mapMinX) / mapW;
            float fy = (cy - mapMinY) / mapH;
            int outCx = Mathf.RoundToInt(fx * OutputWidth);
            int outCy = Mathf.RoundToInt(fy * OutputHeight);

            float scaleX = OutputWidth / mapW;
            float scaleY = OutputHeight / mapH;
            int outW = Mathf.Max(1, Mathf.RoundToInt(wPx * scaleX));
            int outH = Mathf.Max(1, Mathf.RoundToInt(hPx * scaleY));

            // Fast cull if completely outside the buffer.
            int halfW = outW / 2;
            int halfH = outH / 2;
            if (outCx + halfW < 0 || outCx - halfW >= OutputWidth ||
                outCy + halfH < 0 || outCy - halfH >= OutputHeight)
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
                if (py < 0 || py >= OutputHeight) continue;

                int sy = Mathf.Clamp((int)(dy * invOutH), 0, srcH - 1);
                int srcRowOffset = (srcY + sy) * atlasW + srcX;
                int dstRowOffset = py * OutputWidth;

                for (int dx = 0; dx < outW; dx++)
                {
                    int px = outCx - halfW + dx;
                    if (px < 0 || px >= OutputWidth) continue;

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

        private static byte[] Encode(Color32[] output)
        {
            var tex = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGB24, false);
            tex.SetPixels32(output);
            tex.Apply();
            byte[] data = ImageConversion.EncodeToPNG(tex);
            Object.Destroy(tex);
            return data;
        }
    }
}
