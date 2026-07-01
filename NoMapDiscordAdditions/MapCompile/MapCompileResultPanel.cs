using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Modal-style overlay shown after a successful Compose. Has a thumbnail
    /// preview and five buttons: COPY, SAVE, SEND TO DISCORD, DISCARD, DONE.
    /// SAVE recomposes at full native per-tile resolution (the preview/COPY/SEND
    /// payload stays at the Discord-safe capped size) and writes that PNG to the
    /// compiled/ folder, then morphs into COPY DIR so the next click puts the
    /// containing folder on the clipboard. COPY/SAVE/COPY DIR/SEND/DONE are all
    /// non-destructive — the compile session survives them and stays resumable
    /// at the next table or after a restart. Only DISCARD wipes the on-disk
    /// session (and also throws away the in-memory PNG). DONE just drops back
    /// to compile mode so the player can keep adding tiles.
    /// </summary>
    public static class MapCompileResultPanel
    {
        private const string ContainerName = "MapCompileResultPanel";
        private const float Pad = 12f;
        private const float ButtonHeight = 38f;
        private const float MaxPreviewSize = 360f;

        private static GameObject _containerObj;
        private static MapCompositor.CompiledMap _result;
        private static Texture2D _previewTex;
        private static TextMeshProUGUI _statusText;
        // Save → Copy Dir morph: held so OnSave can rewrite the label.
        private static TextMeshProUGUI _saveBtnText;

        public static bool IsVisible => _containerObj != null && _containerObj.activeSelf;

        /// <summary>
        /// True while a compiled map is awaiting the player's decision, even if
        /// the panel UI was torn down because the large map closed. The map
        /// re-open path uses this to rebuild the panel instead of stranding the
        /// session in Reviewing with no UI.
        /// </summary>
        public static bool HasPendingResult => _result != null;

        public static void Show(MapCompositor.CompiledMap result)
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return;
            Hide(); // tear down any previous instance

            _result = result;

            _containerObj = new GameObject(ContainerName);
            _containerObj.transform.SetParent(minimap.m_largeRoot.transform, false);

            var rect = _containerObj.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            var bg = _containerObj.AddComponent<Image>();
            MapUI.ApplyPanelBackground(bg, minimap);

            var vlg = _containerObj.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.spacing = 10f;
            vlg.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);

            // ── Title ────────────────────────────────────────────────────────
            CreateLabel("MAP COMPILED", 22f);

            // ── Preview ──────────────────────────────────────────────────────
            float previewW, previewH;
            ComputePreviewSize(result.Width, result.Height, out previewW, out previewH);
            var previewObj = new GameObject("Preview");
            previewObj.transform.SetParent(_containerObj.transform, false);
            var previewRect = previewObj.AddComponent<RectTransform>();
            previewRect.sizeDelta = new Vector2(previewW, previewH);

            _previewTex = DecodePngToTexture(result.PngBytes);
            var raw = previewObj.AddComponent<RawImage>();
            raw.texture = _previewTex;

            // ── Status text ──────────────────────────────────────────────────
            // Wrap on long status messages (e.g. saved-path) so they stay inside
            // the panel rather than overflowing into the map content beside it.
            string tileWord = result.TileCount == 1 ? "tile" : "tiles";
            _statusText = CreateLabel(
                $"{result.TileCount} {tileWord} | {result.Width}×{result.Height}px", 14f);
            _statusText.textWrappingMode = TextWrappingModes.Normal;
            _statusText.rectTransform.sizeDelta = new Vector2(640f, 36f);

            // ── Button row ───────────────────────────────────────────────────
            var rowObj = new GameObject("Buttons");
            rowObj.transform.SetParent(_containerObj.transform, false);
            var rowRect = rowObj.AddComponent<RectTransform>();
            var rowHlg = rowObj.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 8f;
            rowHlg.childAlignment = TextAnchor.MiddleCenter;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = false;
            rowHlg.childControlWidth = false;
            rowHlg.childControlHeight = false;

            // Specific widths chosen so each label fits without truncation at
            // the Valheim font's natural 16pt rendering. The save slot is
            // sized for "COPY DIR" since that's the wider of the two labels
            // it has to host (SAVE → COPY DIR after first save).
            float wCopy    = 100f;
            float wSave    = 130f;
            float wSend    = 180f;
            float wDiscard = 110f;
            float wDone    = 90f;

            var saveBtn    = MapUI.CreateButton("Save",    rowObj.transform, wSave,    ButtonHeight, "SAVE",            out _saveBtnText);
            var copyBtn    = MapUI.CreateButton("Copy",    rowObj.transform, wCopy,    ButtonHeight, "COPY",            out _);
            var sendBtn    = MapUI.CreateButton("Send",    rowObj.transform, wSend,    ButtonHeight, "SEND TO DISCORD", out _);
            var discardBtn = MapUI.CreateButton("Discard", rowObj.transform, wDiscard, ButtonHeight, "DISCARD",         out _);
            var doneBtn    = MapUI.CreateButton("Done",    rowObj.transform, wDone,    ButtonHeight, "DONE",            out _);

            copyBtn.onClick.AddListener(OnCopy);
            saveBtn.onClick.AddListener(OnSaveOrCopyDir);
            sendBtn.onClick.AddListener(OnSend);
            discardBtn.onClick.AddListener(OnDiscard);
            doneBtn.onClick.AddListener(OnDone);

            // SEND is gated on a configured webhook URL; everything else is always usable.
            sendBtn.interactable = !string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl);

            float rowW = wCopy + wSave + wSend + wDiscard + wDone + 8f * 4f;
            rowRect.sizeDelta = new Vector2(rowW, ButtonHeight);

            // Total panel size: preview + title + status + buttons + spacing/padding.
            float totalW = Mathf.Max(previewW, rowW) + Pad * 2f;
            float totalH = previewH + ButtonHeight + 22f + _statusText.rectTransform.sizeDelta.y
                + vlg.spacing * 3f + Pad * 2f;
            rect.sizeDelta = new Vector2(totalW, totalH);

            _containerObj.SetActive(true);
        }

        public static void Hide()
        {
            if (_previewTex != null)
            {
                UnityEngine.Object.Destroy(_previewTex);
                _previewTex = null;
            }
            if (_containerObj != null)
            {
                UnityEngine.Object.Destroy(_containerObj);
                _containerObj = null;
            }
            _result = null;
            _statusText = null;
            _saveBtnText = null;
            _savedFilePath = null;
            _saveInProgress = false;
        }

        /// <summary>
        /// Tear down the panel UI but keep the compiled result so the session
        /// stays coherently in Reviewing (HasPendingResult). Called when the
        /// large map closes while the player is still reviewing — a plain
        /// <see cref="Hide"/> there would leave the session in Reviewing with no
        /// panel and no button. On the next map open the compile panel shows a
        /// RESUME COMPILE button which drops back into compile mode (the stale
        /// PNG is discarded then — the panel is never auto-rebuilt).
        /// </summary>
        public static void HideKeepingResult()
        {
            var keepResult = _result;
            var keepPath = _savedFilePath;
            Hide();
            _result = keepResult;
            _savedFilePath = keepPath;
        }

        // ── Action handlers ──────────────────────────────────────────────────

        // Path the compiled PNG was written to. Drives the SAVE → COPY DIR
        // morph: null = SAVE state (button writes the file), non-null = COPY
        // DIR state (button copies the containing folder to the clipboard).
        // Cleared on Hide.
        private static string _savedFilePath;

        // Guards against a second SAVE click while the full-resolution recompose
        // coroutine is still running (it can take a few seconds).
        private static bool _saveInProgress;

        private static void OnCopy()
        {
            if (_result == null) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            // CopyBytesToClipboard caps at 8192 (CaptureMaxDim) — for compile the
            // preview PNG is already capped to Map Compile.Max Output Dimension
            // (default 2560), so this rarely actually downscales here. SAVE is
            // the path for native per-tile resolution.
            plugin.CopyBytesToClipboard(_result.PngBytes);
            SetStatus("Image copied to clipboard.");
        }

        // The SAVE / COPY DIR slot. First click writes the PNG to compiled/
        // and morphs the button to "COPY DIR"; subsequent clicks copy the
        // directory path to the clipboard so the player can paste into
        // Explorer to find the saved file.
        private static void OnSaveOrCopyDir()
        {
            if (_savedFilePath == null) DoSave();
            else                        DoCopyDir();
        }

        // SAVE deliberately does NOT reuse _result.PngBytes (that's the
        // Discord-safe capped compose used for the preview/COPY/SEND). It
        // recomposes from the on-disk tiles at full native per-tile resolution
        // so the saved PNG is zoom/edit quality. The recompose is System.Drawing
        // heavy, so it runs off the main thread via a coroutine like the
        // original compile.
        private static void DoSave()
        {
            if (_result == null || _saveInProgress) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            plugin.StartCoroutine(SaveFullResCoroutine());
        }

        private static IEnumerator SaveFullResCoroutine()
        {
            _saveInProgress = true;
            SetStatus("Saving full-resolution map…");

            // Capture _result up front — DISCARD/DONE clicked mid-save calls
            // Hide() which nulls it, and we dereference it after the yields.
            var result = _result;
            if (result == null) { _saveInProgress = false; yield break; }

            // Tiles are stable while Reviewing (AddTile requires Compiling), but
            // snapshot anyway. Excluded tiles are filtered out so SAVE matches
            // the compiled preview the player is looking at. Pins MUST be
            // captured on the main thread (TMP layout, live UI components).
            var tiles = new List<MapCompileTile>();
            foreach (var t in MapCompileSession.Tiles)
                if (!t.ExcludedFromCompile) tiles.Add(t);
            var pins = MapCompilePinSnapshot.Capture(out float refScreenW);

            MapCompositor.CompiledMap full = null;
            Exception error = null;

            // SAVE-time encoder choice. Resolved here (main thread) so the
            // ConfigEntry reads can't race with the off-thread compose. The
            // fallback path (capped preview PNG) uses result.Extension, which
            // is always ".png" since that path was encoded with default options.
            var saveOpts = Plugin.GetEncodeOptions();

            if (tiles.Count > 0)
            {
                var done = new ManualResetEventSlim(false);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { full = MapCompositor.ComposeNative(tiles); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });
                while (!done.IsSet) yield return null;
                done.Dispose();

                // Stamp pin icons + names (Valheim TMP font, main thread) + encode.
                if (error == null && full != null)
                {
                    yield return MapCompileLabelStamp.Finalize(full, pins, tiles, refScreenW, saveOpts);
                    if (full.PngBytes == null)
                        error = new Exception("full-res encode produced no PNG");
                }
            }

            // Fall back to the already-composed capped PNG if the native
            // recompose produced nothing (no tiles / degenerate / error) so
            // SAVE still writes *something* rather than silently failing.
            byte[] bytes;
            int w, h;
            bool clamped;
            string ext;
            if (error == null && full != null)
            {
                bytes = full.PngBytes; w = full.Width; h = full.Height;
                clamped = full.WasClamped; ext = full.Extension;
            }
            else
            {
                if (error != null)
                    ModLog.Error($"[NoMapDiscordAdditions] Full-res recompose failed: {error.Message}");
                bytes = result.PngBytes; w = result.Width; h = result.Height;
                clamped = true; ext = result.Extension;
            }

            try
            {
                MapCompileEnvironment.EnsureDirectory(MapCompileEnvironment.CompiledOutDir);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string playerName = Sanitize(Player.m_localPlayer?.GetPlayerName() ?? "player");
                string fileName = $"{playerName}_compiled_{result.TileCount}tiles_{w}x{h}_{ts}{ext}";
                _savedFilePath = Path.Combine(MapCompileEnvironment.CompiledOutDir, fileName);
                File.WriteAllBytes(_savedFilePath, bytes);

                // Morph SAVE → COPY DIR now that the file's on disk.
                if (_saveBtnText != null) _saveBtnText.text = "COPY DIR";

                string res = clamped ? "downscaled to fit 8192" : "native resolution";
                string fmt = DescribeFormat(saveOpts, ext);
                string size = FormatBytes(bytes.LongLength);
                SetStatus($"Saved {w}×{h}px {fmt} {size} ({res}) to {SanitizePathForDisplay(_savedFilePath)}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Compiled map saved ({w}×{h}, {fmt} {size}).");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Save failed: {ex.Message}");
                SetStatus("Save failed — see log.");
            }

            _saveInProgress = false;
        }

        // Short human-readable tag for the SAVE status line. Mirrors the
        // encoder choice so the player can A/B formats by glancing at the
        // panel after each save without opening the file. Falls back to the
        // raw extension if a future encoder path lacks a dedicated case.
        private static string DescribeFormat(MapCompositor.EncodeOptions opts, string ext)
        {
            switch (opts.Format)
            {
                case MapCompositor.EncodeFormat.Jpeg:       return $"JPEG q{opts.JpegQuality}";
                case MapCompositor.EncodeFormat.IndexedPng: return $"PNG-{opts.IndexedPngColors}c";
                default:                                    return ext.TrimStart('.').ToUpperInvariant();
            }
        }

        // 1024-base size formatter. Status line uses this so the user can
        // compare formats at a glance after each SAVE.
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L)            return $"{bytes} B";
            if (bytes < 1024L * 1024)     return $"{bytes / 1024.0:0.#} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.##} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
        }

        private static void DoCopyDir()
        {
            string dir = MapCompileEnvironment.CompiledOutDir;
            try
            {
                MapCompileEnvironment.EnsureDirectory(dir);
                bool ok = Plugin.CopyTextToClipboard(dir);
                SetStatus(ok
                    ? $"Directory copied to clipboard: {SanitizePathForDisplay(dir)}"
                    : "Copy directory failed — see log.");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Copy dir failed: {ex.Message}");
                SetStatus("Copy directory failed — see log.");
            }
        }

        private static void OnSend()
        {
            if (_result == null) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            if (string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl))
            {
                SetStatus("No webhook URL configured.");
                return;
            }
            SetStatus("Sending to Discord...");
            // Completion callback updates the status panel when the send
            // finishes — without this the label stays stuck on the in-flight
            // text and the user has no idea whether the post landed.
            plugin.SendCompiledImage(_result.PngBytes, _result.TileCount,
                ok => SetStatus(ok ? "Sent to Discord." : "Send failed — see log."));
        }

        private static void OnDiscard()
        {
            // Throw away both the in-memory PNG and the on-disk session.
            Hide();
            MapCompileSession.Discard();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compiled map discarded.");
        }

        private static void OnDone()
        {
            // Non-destructive: the player saved/copied/sent (or just wants to
            // keep mapping). Drop the panel and return to compile mode with
            // every tile intact — they can add more at the next table, or
            // close the game and RESUME later. Nothing on disk is wiped.
            Hide();
            MapCompileSession.ReturnToCompiling();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static TextMeshProUGUI CreateLabel(string text, float size)
        {
            var obj = new GameObject("Label");
            // Inactive until the font is assigned so TMP's Awake doesn't log
            // the missing "LiberationSans SDF" default-font warning.
            obj.SetActive(false);
            obj.transform.SetParent(_containerObj.transform, false);
            var rt = obj.AddComponent<RectTransform>();
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            MapUI.ApplyValheimFont(tmp);
            tmp.fontSize = size;
            // Re-set color in case ApplyValheimFont's override differs from intent.
            tmp.color = MapUI.LabelColor;
            // Sized to fit the text plus a bit of padding so the layout group doesn't clip it.
            rt.sizeDelta = new Vector2(360f, size + 8f);
            obj.SetActive(true);
            return tmp;
        }

        private static void SetStatus(string s)
        {
            if (_statusText != null) _statusText.text = s;
        }

        private static void ComputePreviewSize(int srcW, int srcH, out float w, out float h)
        {
            float aspect = (float)srcW / Mathf.Max(1, srcH);
            if (aspect >= 1f) { w = MaxPreviewSize; h = MaxPreviewSize / aspect; }
            else              { h = MaxPreviewSize; w = MaxPreviewSize * aspect; }
        }

        // Decode PNG bytes to a Texture2D via System.Drawing instead of
        // UnityEngine.ImageConversion. The latter has a ReadOnlySpan<byte>
        // overload that drags in System.ReadOnlySpan during overload
        // resolution, which net481's mscorlib doesn't expose (CS7069).
        // System.Drawing is already a project dependency for the compositor.
        private static Texture2D DecodePngToTexture(byte[] pngBytes)
        {
            using (var ms = new System.IO.MemoryStream(pngBytes))
            using (var src = new System.Drawing.Bitmap(ms))
            {
                int w = src.Width, h = src.Height;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var pixels = new Color32[w * h];
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var bd = src.LockBits(rect,
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int srcStride = bd.Stride;
                    byte[] row = new byte[srcStride];
                    for (int y = 0; y < h; y++)
                    {
                        var rowPtr = new System.IntPtr(bd.Scan0.ToInt64() + (long)y * srcStride);
                        System.Runtime.InteropServices.Marshal.Copy(rowPtr, row, 0, srcStride);

                        // GDI gives BGRA top-down; Texture2D wants RGBA bottom-up.
                        int dstY = h - 1 - y;
                        int dstRow = dstY * w;
                        for (int x = 0; x < w; x++)
                        {
                            int si = x * 4;
                            pixels[dstRow + x] = new Color32(
                                row[si + 2], row[si + 1], row[si], row[si + 3]);
                        }
                    }
                }
                finally
                {
                    src.UnlockBits(bd);
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                return tex;
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "player";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
            return sb.Length == 0 ? "player" : sb.ToString();
        }

        // Convert a Windows file path into something safe to display:
        //   1. Replace backslashes with forward slashes — Valheim's serif font
        //      lacks a clean glyph for `\` and renders it as a garbled rune.
        //   2. Strip identifying prefixes (the user's home directory and any
        //      r2modman profile root) so screenshots posted online don't leak
        //      the OS username or directory structure.
        // The actual on-disk path is unchanged — this is display-only.
        private static string SanitizePathForDisplay(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            string s = path.Replace('\\', '/');

            // Trim the user's home dir if the path starts inside it. Use
            // Environment.GetFolderPath instead of UserProfile env var so this
            // works even if the env var was unset.
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                ?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(userHome) && s.StartsWith(userHome, StringComparison.OrdinalIgnoreCase))
                s = "~" + s.Substring(userHome.Length);

            // r2modman wraps profiles inside the AppData path; collapse the
            // long prefix so what remains is just `<profile>/BepInEx/...`.
            int idx = s.IndexOf("/profiles/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                s = ".../" + s.Substring(idx + "/profiles/".Length);

            return s;
        }
    }
}
