using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public enum CaptureMethodMode
        {
            TextureCapture,
            ScreenCapture
        }

        public const string PluginGUID = "com.virtualbjorn.nomapdiscordadditions";
        public const string PluginName = "NoMapDiscordAdditions";
        public const string PluginVersion = "1.0.4";
        private const string SyncedWithServerTag = " (Synced with Server)";

        public static Plugin Instance { get; private set; }
        public static ConfigEntry<string> WebhookUrl;
        public static ConfigEntry<string> MessageTemplate;
        public static ConfigEntry<int> CaptureSuperSize;
        public static ConfigEntry<KeyCode> ScreenshotKey;
        public static ConfigEntry<KeyCode> CopyKey;
        public static ConfigEntry<KeyCode> CopyFullResModifier;
        public static ConfigEntry<CaptureMethodMode> CaptureMethod;
        public static ConfigEntry<bool> SpoilerImageData;
        public static ConfigEntry<bool> HideClouds;
        public static ConfigEntry<bool> ShowBiomeText;
        public static ConfigEntry<bool> EnableCartographyTableLabels;
        public static ConfigEntry<bool> SpawnLabelIncludeDistance;
        public static ConfigEntry<bool> SpawnLabelIncludeMapItemSources;
        public static ConfigEntry<int> CompileMaxDimension;
        public static ConfigEntry<string> CompileMessageTemplate;
        public static ConfigEntry<int> SendMaxDimension;
        public static ConfigEntry<bool> EnableLogs;

        private Harmony _harmony;
        private bool _sendingInProgress;

        private void Awake()
        {
            Instance = this;

            WebhookUrl = Config.Bind(
                "Discord", "Webhook URL", "",
                "Discord webhook URL used to send captured map images.");

            MessageTemplate = Config.Bind(
                "Discord", "Message Template", "{player} shared a map update from {biome}{spawnDir}",
                "Message sent with each screenshot. Supports {player}, {biome}, and {spawnDir} placeholders. " +
                "{spawnDir} expands to e.g. \" — NE of Spawn\" when the map center is >200 units from spawn, otherwise empty." +
                SyncedWithServerTag);

            CaptureSuperSize = Config.Bind(
                "General", "Capture Super Size", 2,
                new ConfigDescription(
                    "Screen-capture quality multiplier before map crop. " +
                    "Higher values improve detail but increase frame-time and VRAM use. 1 = native, 2 = recommended, 3-4 = heavy." +
                    SyncedWithServerTag,
                    new AcceptableValueRange<int>(1, 4)));

            ScreenshotKey = Config.Bind(
                "Controls", "Screenshot Key", KeyCode.F10,
                "Press while large map is open to capture and send to Discord.");

            CopyKey = Config.Bind(
                "Controls", "Copy Key", KeyCode.F11,
                "Press while large map is open to capture and copy to the clipboard.");

            CopyFullResModifier = Config.Bind(
                "Controls", "Copy Full Resolution Modifier", KeyCode.LeftControl,
                "Hold this key while clicking COPY MAP / COPY (compiled map) to " +
                "raise the cap from Send Max Dimension to 4096 — useful for high-" +
                "fidelity output when pasting into an image editor.");

            CaptureMethod = Config.Bind(
                "General", "Capture Method", CaptureMethodMode.ScreenCapture,
                "Choose the map capture mode." +
                SyncedWithServerTag);

            EnableLogs = Config.Bind(
                "General", "Enable Logs", false,
                "If true, this mod prints info/warning/error messages to the BepInEx " +
                "console and Player.log. Defaults to false to keep logs quiet during " +
                "normal play; turn on if you need to investigate a problem.");

            SpoilerImageData = Config.Bind(
                "Discord", "Spoiler Image Data", false,
                "If enabled, sent map image attachments are tagged as Discord spoilers." +
                SyncedWithServerTag);

            HideClouds = Config.Bind(
                "UI", "Hide Clouds", true,
                "If enabled, cloud overlay is suppressed while capturing maps." +
                SyncedWithServerTag);

            ShowBiomeText = Config.Bind(
                "UI", "Show Biome Text", false,
                "If enabled, the biome label is included in captured map images. Client-only.");

            EnableCartographyTableLabels = Config.Bind(
                "Pin Label", "Enabled", true,
                "If enabled, cartography-table pins on the large map are decorated with a " +
                "distance/direction-from-spawn caption during a capture, baked into the screenshot." +
                SyncedWithServerTag);

            SpawnLabelIncludeDistance = Config.Bind(
                "Pin Label", "Include Distance", true,
                "If enabled, the label includes the meters from spawn (e.g. \"1240m NorthEast (45°)\"). " +
                "If disabled, only the direction is shown (e.g. \"NorthEast (45°)\")." +
                SyncedWithServerTag);

            SpawnLabelIncludeMapItemSources = Config.Bind(
                "Pin Label", "Include Map Item Sources", false,
                "If enabled, the spawn label is also shown when the map is opened from " +
                "a portable map item (e.g. ZenMap parchment), not just from a cartography table." +
                SyncedWithServerTag);

            CompileMaxDimension = Config.Bind(
                "Map Compile", "Max Output Dimension", 2560,
                new ConfigDescription(
                    "Longest pixel dimension of the compiled output PNG. The other axis " +
                    "is sized to preserve world aspect. File size scales with the square " +
                    "of this value (2× dimension ≈ 4× file size). Default 2560 keeps " +
                    "even dense compositions under Discord's 10MB free-tier attachment " +
                    "limit; raise to 3072 for sharper output if your compositions are " +
                    "lighter, or to 4096+ if you don't plan to send via Discord." +
                    SyncedWithServerTag,
                    new AcceptableValueRange<int>(512, 8192)));

            CompileMessageTemplate = Config.Bind(
                "Map Compile", "Compile Message Template",
                "{player} compiled a map from {tileCount} cartography tables.",
                "Discord message template used when SEND TO DISCORD is clicked from " +
                "the compile result panel. Supports {player} and {tileCount} placeholders." +
                SyncedWithServerTag);

            SendMaxDimension = Config.Bind(
                "Discord", "Send Max Dimension", 2560,
                new ConfigDescription(
                    "Cap on the longest pixel dimension of any image sent to Discord " +
                    "OR copied to the clipboard via COPY MAP / COPY (compiled). Larger " +
                    "captures are downscaled before encoding. Keeps even 4K-screen " +
                    "ScreenCapture output safely under Discord's 10MB free-tier limit. " +
                    "Hold Left CTRL when clicking COPY to raise the cap to 4096 — high " +
                    "fidelity for image editing without producing pathologically large " +
                    "clipboard payloads. Discord-bound paths always honor this setting." +
                    SyncedWithServerTag,
                    new AcceptableValueRange<int>(512, 8192)));

            WebhookUrl.SettingChanged += (_, __) => CaptureButton.RefreshEnabledState();
            ShowBiomeText.SettingChanged += (_, __) => CaptureButton.RefreshBiomeToggleState();
            ScreenshotKey.SettingChanged += (_, __) => CaptureButton.RefreshHotkeyLabels();
            CopyKey.SettingChanged += (_, __) => CaptureButton.RefreshHotkeyLabels();

            // Server authoritative config.
            // Prefer the standard ServerSync approach (used by mods like AzuCraftyBoxes) when present.
            // If ServerSync is not installed, fall back to a lightweight RPC-based sync.
            ServerSyncCompat.Init(Config, PluginGUID, PluginName, PluginVersion);
            if (!ServerSyncCompat.IsActive)
                NetworkConfigSync.Init();

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(CartographyTablePatch));
            _harmony.PatchAll(typeof(MinimapPatch));

            // Hot-reload: if Minimap is already alive when this DLL loads,
            // recreate the button so its click handler is wired to THIS Plugin instance.
            if (Minimap.instance != null)
            {
                bool large = Minimap.instance.m_mode == Minimap.MapMode.Large;
                // Strip any label clones the previous instance left behind. The
                // new TablePinLabel pool is empty, so without this sweep those
                // GameObjects would remain in the scene displaying their
                // last-set text with no Tick to manage them.
                TablePinLabel.DestroyOrphans();
                CaptureButton.Create();
                CaptureButton.SetVisible(large);
                MapCompile.MapCompileButtons.Create();
                MapCompile.MapCompileButtons.SetVisible(large);
            }

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void Update()
        {
            if (_sendingInProgress)
                return;

            if (!ServerSyncCompat.IsActive)
                NetworkConfigSync.Tick();

            // Only capture when the large map is open
            if (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large)
                return;

            if (Input.GetKeyDown(ScreenshotKey.Value))
                TriggerDiscordSend();
            else if (Input.GetKeyDown(CopyKey.Value))
                TriggerClipboardCopy();
        }

        /// <summary>
        /// Captures the visible map and copies it to the system clipboard.
        /// Called by the COPY MAP UI button. By default the copied image is
        /// capped to <see cref="SendMaxDimension"/> (matches the Discord cap)
        /// to keep clipboard payloads sane. Holding Left CTRL at click time
        /// bypasses the cap and copies full-resolution — useful when the
        /// player wants to paste into an image editor at maximum quality.
        /// </summary>
        public void TriggerClipboardCopy()
        {
            if (_sendingInProgress)
                return;

            _sendingInProgress = true;
            bool fullResolution = IsFullResModifierHeld();
            bool preferTexture = ModHelpers.EffectiveConfig.UseTextureCapture;
            StartCoroutine(preferTexture
                ? CaptureAndCopyPreferTexture(fullResolution)
                : CaptureAndCopyScreen(fullResolution));
        }

        // Shared modifier check — the configured key bumps the COPY cap from
        // SendMaxDimension up to FullResolutionMaxDim. Lives on Plugin so
        // MapCompileResultPanel can reuse it. Falls back to LeftControl if
        // the config is unavailable (e.g. very early in startup).
        public static bool IsFullResModifierHeld()
            => Input.GetKey(CopyFullResModifier?.Value ?? KeyCode.LeftControl);

        // Hard ceiling for "full resolution" COPY (CTRL-modified). Even when
        // the player asks for max quality, we still clamp at 4096 — a 4K
        // screen at superSize=2 produces 7680x4320 raw which would balloon
        // the clipboard payload to 50+ MB and choke paste targets like
        // Discord's web client. 4096 keeps screen captures sharp without
        // pathological sizes; texture captures (1920x1080 native) sail
        // under it untouched.
        private const int FullResolutionMaxDim = 4096;

        private System.Collections.IEnumerator CaptureAndCopyScreen(bool fullResolution)
        {
            try
            {
                byte[] imageData = null;
                yield return MapCapture.CaptureVisibleMap(data => imageData = data);
                int cap = fullResolution ? FullResolutionMaxDim : ModHelpers.EffectiveConfig.SendMaxDimension;
                imageData = DownscalePngForSend(imageData, cap);
                CopyToClipboard(imageData);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        private System.Collections.IEnumerator CaptureAndCopyPreferTexture(bool fullResolution)
        {
            try
            {
                yield return new WaitForEndOfFrame();

                byte[] imageData;
                if (fullResolution)
                {
                    // Render at 4K-equivalent (max dim 4096, 16:9 aspect to match
                    // the default 1920×1080) so CTRL+COPY actually produces a
                    // high-resolution image. The shader runs at 4096×2304 fragments
                    // and pin sprites are rasterized at the same scale, so detail
                    // genuinely improves vs. just upscaling a 1920×1080 capture.
                    int targetW = FullResolutionMaxDim;
                    int targetH = FullResolutionMaxDim * MapCaptureTexture.OutputHeight
                                  / MapCaptureTexture.OutputWidth;
                    imageData = MapCaptureTexture.CaptureMap(targetW, targetH);
                }
                else
                {
                    imageData = MapCaptureTexture.CaptureMap();
                }

                if (imageData == null)
                    yield return MapCapture.CaptureVisibleMap(data => imageData = data);
                int cap = fullResolution ? FullResolutionMaxDim : ModHelpers.EffectiveConfig.SendMaxDimension;
                imageData = DownscalePngForSend(imageData, cap);
                CopyToClipboard(imageData);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        /// <summary>
        /// Public entry point for copying arbitrary PNG bytes to the clipboard.
        /// Used by the Map Compile result panel. Default cap is
        /// <see cref="SendMaxDimension"/>; pass <paramref name="fullResolution"/>=true
        /// (when Left CTRL is held) to raise the cap to <see cref="FullResolutionMaxDim"/>
        /// (4096) so high-resolution edits are still possible without producing
        /// a 50+ MB clipboard payload. Safe to call from the Unity main thread.
        /// </summary>
        public void CopyBytesToClipboard(byte[] pngData, bool fullResolution = false)
        {
            int cap = fullResolution ? FullResolutionMaxDim : ModHelpers.EffectiveConfig.SendMaxDimension;
            pngData = DownscalePngForSend(pngData, cap);
            CopyToClipboard(pngData);
        }

        /// <summary>
        /// Copy a string to the system clipboard as CF_UNICODETEXT.
        /// We can't use Unity's <c>GUIUtility.systemCopyBuffer</c> because that
        /// lives in UnityEngine.IMGUIModule which this mod doesn't reference,
        /// and pulling in IMGUI just for one helper isn't worth it. The
        /// existing CF_DIB image path already wires up OpenClipboard /
        /// GlobalAlloc / SetClipboardData, so the text variant is just a
        /// shorter call sequence that reuses those imports.
        /// Returns true on success, false on failure (errors are logged).
        /// </summary>
        public static bool CopyTextToClipboard(string text)
        {
            if (text == null) text = string.Empty;

            // CF_UNICODETEXT requires a null-terminated UTF-16 LE buffer.
            byte[] utf16 = System.Text.Encoding.Unicode.GetBytes(text);
            byte[] buffer = new byte[utf16.Length + 2];
            System.Buffer.BlockCopy(utf16, 0, buffer, 0, utf16.Length);
            // trailing 2 bytes already zero — that's the L'\0' terminator.

            System.IntPtr hMem = System.IntPtr.Zero;
            try
            {
                hMem = AllocGlobal(buffer);
                if (!OpenClipboard(System.IntPtr.Zero))
                {
                    GlobalFree(hMem);
                    ModLog.Error($"[NoMapDiscordAdditions] Text clipboard: OpenClipboard failed " +
                                   $"(error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                    return false;
                }

                EmptyClipboard();
                // Clipboard takes ownership of hMem on success — do not free
                // it after this point.
                System.IntPtr setResult = SetClipboardData(CF_UNICODETEXT, hMem);
                CloseClipboard();
                if (setResult == System.IntPtr.Zero)
                {
                    GlobalFree(hMem);
                    ModLog.Error("[NoMapDiscordAdditions] Text clipboard: SetClipboardData failed.");
                    return false;
                }
                return true;
            }
            catch (System.Exception ex)
            {
                if (hMem != System.IntPtr.Zero) GlobalFree(hMem);
                ModLog.Error($"[NoMapDiscordAdditions] Text clipboard error: {ex.Message}");
                return false;
            }
        }

        private const uint CF_UNICODETEXT = 13;

        /// <summary>
        /// Send a previously-encoded PNG (e.g. a compiled map) to Discord using
        /// the configured webhook + the <see cref="CompileMessageTemplate"/>.
        /// Independent of the live capture pipeline — no map mode requirement.
        /// </summary>
        public void SendCompiledImage(byte[] pngData, int tileCount)
        {
            if (_sendingInProgress) return;
            if (string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl)) return;
            if (pngData == null || pngData.Length == 0) return;

            _sendingInProgress = true;
            StartCoroutine(SendCompiledImageCoroutine(pngData, tileCount));
        }

        private System.Collections.IEnumerator SendCompiledImageCoroutine(byte[] pngData, int tileCount)
        {
            try
            {
                var player = Player.m_localPlayer;
                string playerName = player != null ? player.GetPlayerName() : "unknown";
                string template = ModHelpers.EffectiveConfig.CompileMessageTemplate;
                string message = template
                    .Replace("{player}", playerName)
                    .Replace("{tileCount}", tileCount.ToString());
                string fileName = BuildMapFileName(playerName, $"compiled_{tileCount}t");

                player?.Message(MessageHud.MessageType.Center, "Sending compiled map to Discord...");
                pngData = DownscalePngForSend(pngData, ModHelpers.EffectiveConfig.SendMaxDimension);
                yield return DiscordWebhook.SendImage(
                    pngData, fileName, message, ModHelpers.EffectiveConfig.SpoilerImageData);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        // Called from a coroutine — already on the Unity main thread.
        private static void CopyToClipboard(byte[] pngData)
        {
            if (pngData == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Failed to capture map.");
                return;
            }

            try
            {
                byte[] dib = BuildCfDib(pngData);

                uint cfPng = RegisterClipboardFormat("PNG");
                System.IntPtr hDib = AllocGlobal(dib);
                System.IntPtr hPng = AllocGlobal(pngData);

                if (!OpenClipboard(System.IntPtr.Zero))
                {
                    GlobalFree(hDib);
                    GlobalFree(hPng);
                    throw new System.Exception($"OpenClipboard failed (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
                }

                EmptyClipboard();
                SetClipboardData(CF_DIB, hDib);  // clipboard takes hDib ownership
                SetClipboardData(cfPng, hPng);   // clipboard takes hPng ownership
                CloseClipboard();

                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Map copied to clipboard!");
            }
            catch (System.Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Clipboard copy failed: {ex.Message}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Failed to copy map to clipboard.");
            }
        }

        // Decode PNG via System.Drawing (no UnityEngine.ImageConversionModule needed) and
        // produce a raw 24-bpp CF_DIB block: BITMAPINFOHEADER (40 bytes) + BGR rows, bottom-up.
        // System.Drawing.Bitmap.LockBits with Format24bppRgb gives BGR bytes in memory (GDI convention).
        private static byte[] BuildCfDib(byte[] pngData)
        {
            using (var ms = new System.IO.MemoryStream(pngData))
            using (var src = new System.Drawing.Bitmap(ms))
            {
                int w = src.Width, h = src.Height;

                // Force 24bpp so pixel layout is always BGR with no alpha complications.
                using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.DrawImage(src, 0, 0);

                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var bd = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                          System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    try
                    {
                        // bmpData.Stride > 0 means top-down storage in System.Drawing.
                        // CF_DIB with positive biHeight expects bottom-up (row 0 = image bottom).
                        int stride = bd.Stride; // already 4-byte padded for 24bpp
                        int pixelSize = stride * h;
                        byte[] dib = new byte[40 + pixelSize];

                        WriteLE32(dib,  0, 40);         // biSize
                        WriteLE32(dib,  4, w);          // biWidth
                        WriteLE32(dib,  8, h);          // biHeight  (positive = bottom-up)
                        WriteLE16(dib, 12, 1);          // biPlanes
                        WriteLE16(dib, 14, 24);         // biBitCount
                        WriteLE32(dib, 16, 0);          // biCompression = BI_RGB
                        WriteLE32(dib, 20, pixelSize);  // biSizeImage

                        // Flip rows: DIB row 0 = image bottom = bitmap row (h-1).
                        for (int bmpRow = 0; bmpRow < h; bmpRow++)
                        {
                            int dibRow = h - 1 - bmpRow;
                            var rowPtr = new System.IntPtr(bd.Scan0.ToInt64() + (long)bmpRow * stride);
                            System.Runtime.InteropServices.Marshal.Copy(rowPtr, dib, 40 + dibRow * stride, stride);
                        }

                        return dib;
                    }
                    finally
                    {
                        bmp.UnlockBits(bd);
                    }
                }
            }
        }

        private static System.IntPtr AllocGlobal(byte[] data)
        {
            System.IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (System.UIntPtr)data.Length);
            if (hMem == System.IntPtr.Zero)
                throw new System.Exception($"GlobalAlloc failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            System.IntPtr ptr = GlobalLock(hMem);
            System.Runtime.InteropServices.Marshal.Copy(data, 0, ptr, data.Length);
            GlobalUnlock(hMem);
            return hMem;
        }

        private static void WriteLE32(byte[] buf, int off, int v)
        {
            buf[off]     = (byte) v;
            buf[off + 1] = (byte)(v >>  8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        private static void WriteLE16(byte[] buf, int off, short v)
        {
            buf[off]     = (byte) v;
            buf[off + 1] = (byte)(v >> 8);
        }

        #region Win32 clipboard P/Invoke
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern System.IntPtr SetClipboardData(uint uFormat, System.IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterClipboardFormat(string lpszFormat);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern System.IntPtr GlobalAlloc(uint uFlags, System.UIntPtr dwBytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GlobalLock(System.IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(System.IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GlobalFree(System.IntPtr hMem);

        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint CF_DIB        = 8;
        #endregion

        /// <summary>
        /// Captures the visible map and sends it to Discord.
        /// Called by both the keybind (Update) and the UI button.
        /// </summary>
        public void TriggerDiscordSend()
        {
            if (_sendingInProgress)
                return;

            if (string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl))
                return;

            _sendingInProgress = true;
            bool preferTexture = ModHelpers.EffectiveConfig.UseTextureCapture;
            StartCoroutine(preferTexture ? CaptureAndSendPreferTexture() : CaptureAndSendScreen());
        }

        private System.Collections.IEnumerator CaptureAndSendScreen()
        {
            BuildCaptureContext(out Player player, out string playerName, out string biome, out string message);
            try
            {
                byte[] imageData = null;
                yield return MapCapture.CaptureVisibleMap(data => imageData = data);
                yield return SendCapturedImage(imageData, player, playerName, biome, message);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        private System.Collections.IEnumerator CaptureAndSendPreferTexture()
        {
            BuildCaptureContext(out Player player, out string playerName, out string biome, out string message);
            try
            {
                // Texture capture path.
                yield return new WaitForEndOfFrame();
                byte[] imageData = MapCaptureTexture.CaptureMap();

                // Fallback to screen capture if texture path fails.
                if (imageData == null)
                    yield return MapCapture.CaptureVisibleMap(data => imageData = data);

                yield return SendCapturedImage(imageData, player, playerName, biome, message);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        private static void BuildCaptureContext(
            out Player player, out string playerName, out string biome, out string message)
        {
            player = Player.m_localPlayer;
            playerName = player != null ? player.GetPlayerName() : "unknown";
            biome = player != null ? player.GetCurrentBiome().ToString() : "unknown";

            string spawnLabel = SpawnDirection.GetLabel();
            string spawnDirText = spawnLabel != null ? $" — {spawnLabel}" : "";

            // Legacy configs (from earlier plugin versions) may have a Message Template
            // saved without the {spawnDir} placeholder — BepInEx never overwrites an
            // existing config value with a newer default. Append in that case so the
            // spawn-direction info still reaches Discord without requiring users to
            // manually edit their .cfg.
            message = ModHelpers.EffectiveConfig.MessageTemplate ?? string.Empty;
            if (message.Contains("{spawnDir}"))
                message = message.Replace("{spawnDir}", spawnDirText);
            else if (spawnDirText.Length > 0)
                message += spawnDirText;

            message = message
                .Replace("{player}", playerName)
                .Replace("{biome}", biome);
        }

        private static System.Collections.IEnumerator SendCapturedImage(
            byte[] imageData, Player player, string playerName, string biome, string message)
        {
            if (imageData == null)
            {
                player?.Message(MessageHud.MessageType.Center, "Failed to capture map.");
                yield break;
            }

            // Cap longest dimension before sending so 4K / ultrawide screens
            // don't blow past Discord's 10MB attachment limit. Clipboard
            // copies don't go through here, so COPY MAP keeps full resolution.
            imageData = DownscalePngForSend(imageData, ModHelpers.EffectiveConfig.SendMaxDimension);

            string fileName = BuildMapFileName(playerName, biome);
            player?.Message(MessageHud.MessageType.Center, "Sending map to Discord...");
            yield return DiscordWebhook.SendImage(
                imageData, fileName, message, ModHelpers.EffectiveConfig.SpoilerImageData);
        }

        // Decode → resize → re-encode if the longest pixel dimension exceeds
        // <paramref name="maxDim"/>. Returns the original bytes when no scaling
        // is needed (max dim already at/below the cap) or when decoding fails.
        // Always emits a 24bpp PNG since the SEND paths never carry alpha.
        private static byte[] DownscalePngForSend(byte[] pngBytes, int maxDim)
        {
            if (pngBytes == null || pngBytes.Length == 0) return pngBytes;
            if (maxDim < 64) return pngBytes;

            try
            {
                using (var ms = new System.IO.MemoryStream(pngBytes))
                using (var src = new System.Drawing.Bitmap(ms))
                {
                    int w = src.Width, h = src.Height;
                    int longest = w > h ? w : h;
                    if (longest <= maxDim) return pngBytes;

                    float scale = (float)maxDim / longest;
                    int newW = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(w * scale));
                    int newH = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(h * scale));

                    using (var resized = new System.Drawing.Bitmap(
                        newW, newH, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var g = System.Drawing.Graphics.FromImage(resized))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                            g.DrawImage(src, 0, 0, newW, newH);
                        }
                        using (var outMs = new System.IO.MemoryStream())
                        {
                            resized.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                            byte[] result = outMs.ToArray();
                            ModLog.Info($"[NoMapDiscordAdditions] Discord send downscaled " +
                                      $"{w}x{h} -> {newW}x{newH} ({pngBytes.Length} -> {result.Length} bytes).");
                            return result;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Send downscale failed, sending original: {ex.Message}");
                return pngBytes;
            }
        }

        // Filename: <player>_<biome>_<yyyyMMdd-HHmmss>.png, with anything that
        // isn't a letter/digit/dash/underscore stripped from player and biome so
        // the result is safe across filesystems and Discord's attachment handling.
        private static string BuildMapFileName(string playerName, string biome)
        {
            string player = Sanitize(playerName);
            string biomeSafe = Sanitize(biome);
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (string.IsNullOrEmpty(player)) player = "player";
            if (string.IsNullOrEmpty(biomeSafe)) biomeSafe = "unknown";

            return $"{player}_{biomeSafe}_{timestamp}.png";
        }

        // ASCII-only: non-ASCII letters (e.g. ö) trigger RFC 2047 encoded-word
        // wrapping in the multipart Content-Disposition header, which some clients
        // (including Discord on download) surface as a mangled filename. We first
        // strip diacritics via Unicode NFD decomposition (ö → o + ̈ → o) so most
        // Latin-script names survive; anything still non-ASCII after that is dropped.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            string normalized = s.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                    == System.Globalization.UnicodeCategory.NonSpacingMark)
                    continue;

                if ((c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('_');
            }
            return sb.ToString();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
