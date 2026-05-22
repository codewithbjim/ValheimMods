using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Captures the currently visible large map by screenshotting the rendered frame
    /// and cropping to the map RawImage's screen rect. Picks up every pin/icon the
    /// game (and other mods like ZenMap) drew — sacrificial stones, bed/spawn,
    /// locations, custom pins, etc.
    /// </summary>
    public static class MapCapture
    {
        /// <summary>
        /// Captures the visible large map. All large-map UI children except the map itself
        /// are hidden for the screenshot frame so they do not appear in the result.
        /// <paramref name="cropToClampedUv"/> trims the output to only the portion
        /// of the map RawImage that maps to clamped uv ∈ [0,1] — used by compile
        /// mode so each tile's PNG aligns with the world rect we record (and to
        /// drop the black bars Valheim's shader paints outside the world). Plain
        /// SEND/COPY keeps the full map-rect crop.
        /// <paramref name="includePinLabels"/> gates the per-pin "of Spawn" caption
        /// bake (off for compile — labels stamp once on the composite instead).
        /// <paramref name="sizeCallback"/> fires with the final PNG's width/height
        /// after crop so compile can record the tile's pixel size.
        /// Time-of-day lighting is normalized to noon for the capture frame when
        /// the <c>Normalize Capture Lighting</c> config is on — applies to every
        /// caller (SEND/COPY/compile) so the texture-capture path and screen-
        /// capture path stay visually consistent.
        /// </summary>
        public static IEnumerator CaptureVisibleMap(System.Action<byte[]> callback,
            bool cropToClampedUv = false,
            bool includePinLabels = true,
            System.Action<int, int> sizeCallback = null)
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_mode != Minimap.MapMode.Large)
            {
                callback(null);
                yield break;
            }

            // Re-render the frame at superSize× resolution before cropping so the
            // map ends up with superSize² as many pixels (preserves pin/icon detail).
            int superSize = Mathf.Clamp(ModHelpers.EffectiveConfig.CaptureSuperSize, 1, 4);

            // Hide every direct child of the large-map root except the map itself so no UI
            // (panels, buttons, hints, biome label, etc.) bleeds into the capture.
            var hiddenChildren = new List<(GameObject go, bool wasActive)>();
            if (minimap.m_largeRoot != null)
            {
                foreach (Transform child in minimap.m_largeRoot.transform)
                {
                    if (child.name == "map") continue;
                    hiddenChildren.Add((child.gameObject, child.gameObject.activeSelf));
                    child.gameObject.SetActive(false);
                }
            }

            // When ShowBiomeText is off, UpdateBiome() would re-enable large_biome
            // via SetActive after the children loop hides it. Disabling the TMP component
            // survives that because UpdateBiome only touches activeSelf, not component.enabled.
            bool showBiome = Plugin.ShowBiomeText?.Value ?? false;
            TextMeshProUGUI largeBiomeTmp = null;
            if (!showBiome && minimap.m_largeRoot != null)
            {
                var largeBiome = Utils.FindChild(minimap.m_largeRoot.transform, "large_biome");
                largeBiomeTmp = largeBiome?.GetComponent<TextMeshProUGUI>();
                if (largeBiomeTmp != null) largeBiomeTmp.enabled = false;
            }

            // Elevate the large-map subtree above every other HUD Canvas so nothing
            // external renders on top of it in the captured frame.
            // If m_largeRoot already has a Canvas (e.g. a mod added one) we patch it
            // in-place; otherwise we add a temporary one and destroy it in finally.
            Canvas existingCanvas = minimap.m_largeRoot != null
                ? minimap.m_largeRoot.GetComponent<Canvas>() : null;
            Canvas addedCanvas    = null;
            int  savedOrder    = 0;
            bool savedOverride = false;
            if (existingCanvas != null)
            {
                savedOrder    = existingCanvas.sortingOrder;
                savedOverride = existingCanvas.overrideSorting;
                existingCanvas.overrideSorting = true;
                existingCanvas.sortingOrder    = 32767;
            }
            else if (minimap.m_largeRoot != null)
            {
                addedCanvas                = minimap.m_largeRoot.AddComponent<Canvas>();
                addedCanvas.overrideSorting = true;
                addedCanvas.sortingOrder    = 32767;
            }

            List<ModHelpers.SavedShaderProp> savedClouds = null;
            Material mapMaterial = GetMapMaterial(minimap);
            if (ModHelpers.EffectiveConfig.HideClouds && mapMaterial != null)
                savedClouds = ModHelpers.SuppressShaderPropsContaining(mapMaterial, "cloud");

            // Override the time-of-day globals BEFORE yielding to end-of-frame.
            // EnvMan.FixedUpdate (the only place sun/ambient globals get written
            // — EnvMan.Update only touches clouds) has already run by the time
            // we're here in Update phase, so our values stick through LateUpdate
            // and rendering. Restored in finally after WaitForEndOfFrame /
            // capture, before EnvMan's next FixedUpdate writes time-of-day back.
            // Honours the same config the texture-capture path uses so a player
            // who toggles it off gets it off everywhere.
            ModHelpers.SavedLighting savedLighting = default;
            if (Plugin.NormalizeCaptureLighting?.Value ?? true)
                savedLighting = ModHelpers.OverrideLightingToNoon();

            // Activate the per-pin "of Spawn" labels for the duration of the capture
            // so they bake into the screenshot. Filtered to pins whose vanilla
            // marker is currently rendered. Hidden again in the finally block.
            // Compile mode opts out (includePinLabels=false) because baked labels
            // get eaten by the compositor's chroma-pick where tiles overlap;
            // labels are stamped once onto the finished composite instead.
            if (includePinLabels)
                TablePinLabel.ShowForCapture();

            try
            {
                // Wait until the frame finishes rendering so hidden UI children are gone from the back buffer.
                yield return new WaitForEndOfFrame();

                Texture2D screen = null;
                try
                {
                    screen = ScreenCapture.CaptureScreenshotAsTexture(superSize);
                }
                catch (System.Exception ex)
                {
                    ModLog.Error($"[NoMapDiscordAdditions] Screen capture failed: {ex.Message}");
                }

                if (screen == null)
                {
                    callback(null);
                    yield break;
                }

                Texture2D mapOnly = cropToClampedUv
                    ? CropToClampedUvRect(minimap, screen, superSize)
                    : CropToMapRect(minimap, screen, superSize);
                Texture2D toEncode = mapOnly ?? screen;
                sizeCallback?.Invoke(toEncode.width, toEncode.height);

                // Spawn-direction text now lives in the Discord message
                // ({spawnDir} placeholder) and on the in-game per-pin labels;
                // burning a software-rasterized copy into the PNG produced an
                // ugly bitmap-looking overlay, so this path is intentionally
                // omitted.

                byte[] data = null;
                try
                {
                    data = ImageConversion.EncodeToPNG(toEncode);
                }
                catch (System.Exception ex)
                {
                    ModLog.Error($"[NoMapDiscordAdditions] PNG encode failed: {ex.Message}");
                }
                finally
                {
                    Object.Destroy(screen);
                    if (mapOnly != null) Object.Destroy(mapOnly);
                }

                callback(data);
            }
            finally
            {
                TablePinLabel.HideAll();
                if (addedCanvas != null)
                    Object.Destroy(addedCanvas);
                else if (existingCanvas != null)
                {
                    existingCanvas.sortingOrder    = savedOrder;
                    existingCanvas.overrideSorting = savedOverride;
                }
                if (largeBiomeTmp != null) largeBiomeTmp.enabled = true;
                foreach (var (go, wasActive) in hiddenChildren)
                    go.SetActive(wasActive);
                if (savedClouds != null && mapMaterial != null)
                    ModHelpers.RestoreShaderProps(mapMaterial, savedClouds);
                ModHelpers.RestoreLighting(savedLighting);
                CaptureButton.SetVisible(minimap.m_mode == Minimap.MapMode.Large);
            }
        }

        private static Material GetMapMaterial(Minimap minimap)
        {
            if (minimap == null) return null;
            if (minimap.m_mapLargeShader != null) return minimap.m_mapLargeShader;
            return minimap.m_mapImageLarge != null ? minimap.m_mapImageLarge.material : null;
        }

        // Crop the supersized capture down to the large-map RawImage's screen rect.
        // For Screen Space Overlay canvases, RectTransform world corners are in screen
        // pixel space (origin bottom-left). The captured texture is superSize× larger
        // than the screen in each axis, so we scale corner coords up to match.
        private static Texture2D CropToMapRect(Minimap minimap, Texture2D screen, int superSize)
        {
            RawImage mapImage = minimap.m_mapImageLarge;
            if (mapImage == null) return null;

            var rt = mapImage.rectTransform;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);

            float minX = Mathf.Min(corners[0].x, corners[2].x) * superSize;
            float minY = Mathf.Min(corners[0].y, corners[2].y) * superSize;
            float maxX = Mathf.Max(corners[0].x, corners[2].x) * superSize;
            float maxY = Mathf.Max(corners[0].y, corners[2].y) * superSize;

            int x = Mathf.Clamp(Mathf.RoundToInt(minX), 0, screen.width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(minY), 0, screen.height - 1);
            int w = Mathf.Clamp(Mathf.RoundToInt(maxX) - x, 1, screen.width - x);
            int h = Mathf.Clamp(Mathf.RoundToInt(maxY) - y, 1, screen.height - y);

            try
            {
                Color[] pixels = screen.GetPixels(x, y, w, h);
                var cropped = new Texture2D(w, h, TextureFormat.RGB24, false);
                cropped.SetPixels(pixels);
                cropped.Apply();
                return cropped;
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Crop failed, sending full screen: {ex.Message}");
                return null;
            }
        }

        // Crop down to just the on-screen sub-rect that corresponds to the
        // clamped uv span ([0,1] ∩ uvRect). Compile mode uses this so a tile's
        // PNG covers exactly the world rect MapCompileTile.ComputeWorldRect
        // records (which is derived from the same clamped uv). Without the
        // clamp-aware crop, a zoomed-out tile would include the black bars
        // Valheim's shader draws past the world edge, and the bars would land
        // on top of neighbouring tiles in the composite.
        private static Texture2D CropToClampedUvRect(Minimap minimap, Texture2D screen, int superSize)
        {
            RawImage mapImage = minimap.m_mapImageLarge;
            if (mapImage == null) return null;

            Rect uv = mapImage.uvRect;
            float rawW = uv.xMax - uv.xMin;
            float rawH = uv.yMax - uv.yMin;
            if (rawW <= 1e-6f || rawH <= 1e-6f) return null;

            float cuX0 = Mathf.Clamp01(uv.xMin);
            float cuX1 = Mathf.Clamp01(uv.xMax);
            float cuY0 = Mathf.Clamp01(uv.yMin);
            float cuY1 = Mathf.Clamp01(uv.yMax);
            if (cuX1 - cuX0 <= 1e-6f || cuY1 - cuY0 <= 1e-6f) return null;

            var rt = mapImage.rectTransform;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            float mapMinX = Mathf.Min(corners[0].x, corners[2].x);
            float mapMinY = Mathf.Min(corners[0].y, corners[2].y);
            float mapMaxX = Mathf.Max(corners[0].x, corners[2].x);
            float mapMaxY = Mathf.Max(corners[0].y, corners[2].y);
            float mapW = mapMaxX - mapMinX;
            float mapH = mapMaxY - mapMinY;

            // Screen-space fractions of the RawImage that correspond to the
            // clamped uv span — collapses to (0,1) on both axes when the
            // viewport is fully inside [0,1], so normal-zoom captures keep the
            // full map rect.
            float fxLo = (cuX0 - uv.xMin) / rawW;
            float fxHi = (cuX1 - uv.xMin) / rawW;
            float fyLo = (cuY0 - uv.yMin) / rawH;
            float fyHi = (cuY1 - uv.yMin) / rawH;

            float minX = (mapMinX + fxLo * mapW) * superSize;
            float maxX = (mapMinX + fxHi * mapW) * superSize;
            float minY = (mapMinY + fyLo * mapH) * superSize;
            float maxY = (mapMinY + fyHi * mapH) * superSize;

            int x = Mathf.Clamp(Mathf.RoundToInt(minX), 0, screen.width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(minY), 0, screen.height - 1);
            int w = Mathf.Clamp(Mathf.RoundToInt(maxX) - x, 1, screen.width - x);
            int h = Mathf.Clamp(Mathf.RoundToInt(maxY) - y, 1, screen.height - y);

            try
            {
                Color[] pixels = screen.GetPixels(x, y, w, h);
                var cropped = new Texture2D(w, h, TextureFormat.RGB24, false);
                cropped.SetPixels(pixels);
                cropped.Apply();
                return cropped;
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Clamped-uv crop failed: {ex.Message}");
                return null;
            }
        }
    }
}
