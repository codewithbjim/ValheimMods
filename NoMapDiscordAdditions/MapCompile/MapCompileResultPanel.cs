using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Modal-style overlay shown after a successful Compose. Has a thumbnail
    /// preview and five buttons: COPY, SAVE, SEND TO DISCORD, DISCARD, DONE.
    /// The SAVE button morphs into COPY DIR after the first successful save —
    /// at that point the file is on disk and the useful next action is
    /// putting its containing folder on the clipboard so the player can paste
    /// into Explorer. COPY/SAVE/COPY DIR/SEND are non-destructive. DONE and
    /// DISCARD both end the review and wipe the on-disk session directory;
    /// DISCARD additionally throws away the in-memory PNG (no save).
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
        }

        // ── Action handlers ──────────────────────────────────────────────────

        // Path the compiled PNG was written to. Drives the SAVE → COPY DIR
        // morph: null = SAVE state (button writes the file), non-null = COPY
        // DIR state (button copies the containing folder to the clipboard).
        // Cleared on Hide.
        private static string _savedFilePath;

        private static void OnCopy()
        {
            if (_result == null) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            // Left CTRL held → cap raised to 4096 (high fidelity); otherwise
            // capped to SendMaxDimension so the clipboard payload stays sane.
            bool fullRes = Plugin.IsFullResModifierHeld();
            plugin.CopyBytesToClipboard(_result.PngBytes, fullRes);
            SetStatus(fullRes
                ? "Image copied to clipboard (high resolution, capped at 4096)."
                : "Image copied to clipboard.");
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

        private static void DoSave()
        {
            if (_result == null) return;
            try
            {
                MapCompileEnvironment.EnsureDirectory(MapCompileEnvironment.CompiledOutDir);
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string playerName = Sanitize(Player.m_localPlayer?.GetPlayerName() ?? "player");
                string fileName = $"{playerName}_compiled_{_result.TileCount}tiles_{ts}.png";
                _savedFilePath = Path.Combine(MapCompileEnvironment.CompiledOutDir, fileName);
                File.WriteAllBytes(_savedFilePath, _result.PngBytes);

                // Morph SAVE → COPY DIR now that the file's on disk.
                if (_saveBtnText != null) _saveBtnText.text = "COPY DIR";

                SetStatus($"Saved to {SanitizePathForDisplay(_savedFilePath)}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compiled map saved.");
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Save failed: {ex.Message}");
                SetStatus("Save failed — see log.");
            }
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
            plugin.SendCompiledImage(_result.PngBytes, _result.TileCount);
            SetStatus("Sending to Discord...");
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
            // Player has already saved/copied/sent — clean up the disk session
            // and dismiss. The compiled PNG itself is gone from memory; if the
            // player needs it again, they can re-open from the saved file.
            Hide();
            MapCompileSession.EndReview();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static TextMeshProUGUI CreateLabel(string text, float size)
        {
            var obj = new GameObject("Label");
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
