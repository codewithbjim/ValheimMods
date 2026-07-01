using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Extensions;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Declaring Jotunn as a hard dependency makes Jotunn's SynchronizationManager
    // include this plugin's config file in server -> client sync. Entries bound
    // with synced: true (IsAdminOnly) are pushed from the server and locked on
    // clients; the local value is cached and restored on disconnect.
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public enum MapStyleMode
        {
            None,
            OldMap,
            Chart,
            Topographical,
            Satellite
        }

        // Output format for SAVE and SEND TO DISCORD. Lossless 24bpp PNG was
        // dropped because at native (8192²) resolution it produced ~20 MB
        // files for no perceptible gain over either of these two — and the
        // test build keeps both options since one is better for smooth
        // gradients (Map Style) and the other keeps label edges crisp without
        // DCT ringing. COPY (clipboard) does NOT honour this — it always
        // emits indexed PNG so paste targets get a real PNG with crisp text.
        public enum OutputImageFormat
        {
            JPEG,       // 24bpp DCT. ~5-8× smaller than lossless PNG; can blur pin labels below q~85.
            IndexedPNG  // 8bpp palette + FS dither. ~5× smaller; keeps labels crisp.
        }

        public const string PluginGUID = "com.virtualbjorn.nomapdiscordadditions";
        public const string PluginName = "NoMapDiscordAdditions";
        public const string PluginVersion = "1.3.0";

        public static Plugin Instance { get; private set; }
        public static ConfigEntry<string> WebhookUrl;
        public static ConfigEntry<string> MessageTemplate;
        public static ConfigEntry<KeyCode> SendKey;
        public static ConfigEntry<KeyCode> CopyKey;
        public static ConfigEntry<MapStyleMode> MapStyle;
        public static ConfigEntry<OutputImageFormat> OutputFormat;
        public static ConfigEntry<int> JpegQuality;
        public static ConfigEntry<int> IndexedPngColors;
        public static ConfigEntry<bool> SpoilerImageData;
        public static ConfigEntry<bool> HideClouds;
        public static ConfigEntry<bool> ShowBiomeText;
        public static ConfigEntry<bool> NormalizeCaptureLighting;
        public static ConfigEntry<string> CompileMessageTemplate;
        public static ConfigEntry<bool> EnableCompileMapSharing;
        public static ConfigEntry<bool> AllowCompileFromMapItems;
        public static ConfigEntry<string> CompileShareMessageTemplate;
        public static ConfigEntry<bool> EnableLogs;

        private Harmony _harmony;

        // ── Send/Copy in-flight state ───────────────────────────────────────
        // Backed by a private bool but exposed through a property so the setter
        // can fan a state-change event out to the UI. The Send Map / Copy Map
        // buttons subscribe to SendingStateChanged so they can switch into a
        // loading label + disabled state while a capture is running (the work
        // can take noticeable wall time, especially with a Map Style active or
        // a full-resolution copy). CurrentSendingOp lets each button show the
        // right verb (SENDING / COPYING) instead of a generic "BUSY".
        public enum SendingOp { None, Send, Copy }
        public static SendingOp CurrentSendingOp { get; private set; } = SendingOp.None;
        public static bool IsSendingInProgress { get; private set; }
        public static event System.Action SendingStateChanged;

        private bool _sendingInProgressBacking;
        private bool _sendingInProgress
        {
            get => _sendingInProgressBacking;
            set
            {
                if (_sendingInProgressBacking == value) return;
                _sendingInProgressBacking = value;
                IsSendingInProgress = value;
                if (!value) CurrentSendingOp = SendingOp.None;
                try { SendingStateChanged?.Invoke(); }
                catch (System.Exception ex)
                {
                    // A misbehaving subscriber must not break the capture path.
                    ModLog.Warn($"[NoMapDiscordAdditions] SendingStateChanged listener threw: {ex.Message}");
                }
            }
        }

        private void Awake()
        {
            Instance = this;

            // synced: true  -> Jotunn marks the entry IsAdminOnly and syncs the
            //                   server's value to clients (locked client-side).
            // synced: false -> client-local setting, never synced.
            // BindConfig appends its own "(Synced with Server)" / "(Not Synced)"
            // note to the description, so we no longer hand-append a tag.

            // synced: true so the server's webhook reaches clients (sending
            // works for everyone), but Browsable = false keeps it out of the
            // ConfigurationManager window so non-admin players can't read the
            // URL from the in-game settings UI. It still lives in client memory
            // / on the wire — same exposure as the previous RPC design, no
            // worse — so this is not a true secret; it just isn't displayed.
            WebhookUrl = Config.BindConfig(
                "Discord", "Webhook URL", "",
                "Discord webhook URL used to send captured map images.",
                synced: true,
                configAttributes: new ConfigurationManagerAttributes { Browsable = false });

            MessageTemplate = Config.BindConfig(
                "Discord", "Message Template", "{player} shared a map update from {biome}{table}",
                "Message sent with each screenshot. Supports {player}, {biome} and {table} placeholders. " +
                "{table} expands to \" — <name>\" using the name of the map pin on the cartography table " +
                "(empty when reading a map item, or no named pin sits on the table).",
                synced: true);

            SendKey = Config.BindConfig(
                "Controls", "Send Key", KeyCode.F10,
                "Press while large map is open to capture and send to Discord.",
                synced: false);

            CopyKey = Config.BindConfig(
                "Controls", "Copy Key", KeyCode.F11,
                "Press while large map is open to capture and copy to the clipboard.",
                synced: false);

            EnableLogs = Config.BindConfig(
                "General", "Enable Logs", false,
                "If true, this mod prints info/warning/error messages to the BepInEx " +
                "console and Player.log. Defaults to false to keep logs quiet during " +
                "normal play; turn on if you need to investigate a problem.",
                synced: false);

            SpoilerImageData = Config.BindConfig(
                "Discord", "Spoiler Image Data", false,
                "If enabled, sent map image attachments are tagged as Discord spoilers.",
                synced: true);

            HideClouds = Config.BindConfig(
                "UI", "Hide Clouds", true,
                "If enabled, cloud overlay is suppressed while capturing maps.",
                synced: true);

            ShowBiomeText = Config.BindConfig(
                "UI", "Show Biome Text", false,
                "If enabled, the biome label is included in captured map images. Client-only.",
                synced: false);

            NormalizeCaptureLighting = Config.BindConfig(
                "General", "Normalize Capture Lighting", true,
                "If enabled, captures render the map as if at noon regardless of " +
                "the in-game time of day. Keeps brightness consistent so a " +
                "multi-tile compiled map doesn't show dark/light seams between " +
                "tiles captured at different times, and gives SEND/COPY a " +
                "stable look across an in-game day. Applies to every capture " +
                "path (texture and screen, SEND/COPY and compile). Disable to " +
                "have captures reflect the live time of day. Client-only.",
                synced: false);

            MapStyle = Config.BindConfig(
                "Map Style", "Style", MapStyleMode.None,
                "Optional stylized rendering for SEND / COPY map captures, " +
                "reconstructed from Valheim's own map data — explored areas show " +
                "detail, unexplored areas stay fogged. " +
                "None: the normal in-game map look. " +
                "Old Map: aged-parchment chart (biome wash, Perlin grain, contour & biome-edge lines). " +
                "Chart: flat topographic chart with contour & biome-edge lines. " +
                "Topographical: shaded-relief terrain with hillshading, contours & biome-edge lines. " +
                "Satellite: naturalistic shaded terrain, no line work. " +
                "A styled capture always uses the texture-capture path and is " +
                "not applied to MAP COMPILE tiles. Client-only.",
                synced: false);

            OutputFormat = Config.BindConfig(
                "Output", "Output Format", OutputImageFormat.JPEG,
                "Image format used for the SAVE button (compiled map → disk) " +
                "AND for SEND TO DISCORD. COPY (clipboard) always emits an " +
                "indexed PNG regardless of this setting so paste targets get a " +
                "real PNG with crisp text. " +
                "JPEG: lossy DCT at the configured quality (~5-8× smaller than " +
                "a lossless PNG; can blur sharp pin-label edges below quality ~85). " +
                "IndexedPNG: 8bpp palette via median-cut + Floyd-Steinberg " +
                "dither at the configured colour count (~5× smaller; keeps " +
                "label edges crisp because there's no DCT — best fit for " +
                "typical maps).",
                synced: false);

            JpegQuality = Config.BindConfig(
                "Output", "JPEG Quality", 88,
                "JPEG encoder quality used when Output Format = JPEG. " +
                "Higher = larger file, less ringing around hard edges. 88 keeps " +
                "pin captions readable; values below 80 noticeably blur text.",
                synced: false,
                acceptableValues: new AcceptableValueRange<int>(50, 100));

            IndexedPngColors = Config.BindConfig(
                "Output", "Indexed PNG Colours", 64,
                "Palette size used for indexed-PNG encoding — applies to " +
                "Output Format = IndexedPNG and to ALL clipboard COPY output. " +
                "Maps have ~6 dominant colours (water, grass, dirt, ice, swamp, " +
                "fog) so 32-64 is usually indistinguishable from full-colour; " +
                "128-256 is overkill for a map but smooths gradient regions " +
                "if you have Map Style enabled.",
                synced: false,
                acceptableValues: new AcceptableValueRange<int>(16, 256));

            CompileMessageTemplate = Config.BindConfig(
                "Map Compile", "Compile Message Template",
                "{player} compiled a map from {tileCount} cartography tables.",
                "Discord message template used when SEND TO DISCORD is clicked from " +
                "the compile result panel. Supports {player} and {tileCount} placeholders.",
                synced: true);

            EnableCompileMapSharing = Config.BindConfig(
                "Map Compile", "Enable Map Sharing", true,
                "If enabled, compile mode can share its tiles with teammates: the " +
                "SHARE/EXPORT button is shown and the incoming share folder is " +
                "auto-imported into the active session. Disable to keep compile " +
                "mode purely local — the SHARE/EXPORT button is hidden and no " +
                "incoming tiles are imported.",
                synced: true);

            AllowCompileFromMapItems = Config.BindConfig(
                "Map Compile", "Allow From Map Items", true,
                "If enabled, compile mode is also available when the map is " +
                "opened from a portable map item (e.g. ZenMap parchment), not " +
                "just from a cartography table. Tiles added while reading a " +
                "map item dedup by the item's read position. Disable to " +
                "restrict compile mode to cartography tables only.",
                synced: true);

            CompileShareMessageTemplate = Config.BindConfig(
                "Map Compile", "Share Message Template",
                "{player} shared {tileCount} map tile(s) for compile mode. " +
                "Save the attached PNG(s) into BepInEx/config/" + PluginName +
                "/compile-share/incoming and they auto-import next time you open the map.",
                "Discord message sent (once, with the first attachment) when " +
                "SHARE TILES is clicked in compile mode. Supports {player} and " +
                "{tileCount} placeholders.",
                synced: true);

            WebhookUrl.SettingChanged += (_, __) => CaptureButton.RefreshEnabledState();
            ShowBiomeText.SettingChanged += (_, __) => CaptureButton.RefreshBiomeToggleState();
            SendKey.SettingChanged += (_, __) => CaptureButton.RefreshHotkeyLabels();
            CopyKey.SettingChanged += (_, __) => CaptureButton.RefreshHotkeyLabels();

            // Server-authoritative config is handled entirely by Jotunn's
            // SynchronizationManager: because this plugin declares Jotunn as a
            // BepInEx dependency, its config file is auto-included in sync, and
            // every entry bound with synced: true is pushed from the server and
            // locked on clients. No custom RPC or ServerSync reflection needed.

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(CartographyTablePatch));
            _harmony.PatchAll(typeof(MinimapPatch));

            // Hot-reload: if Minimap is already alive when this DLL loads,
            // recreate the button so its click handler is wired to THIS Plugin instance.
            if (Minimap.instance != null)
            {
                bool large = Minimap.instance.m_mode == Minimap.MapMode.Large;
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

            // Only capture when the large map is open
            if (Minimap.instance == null || Minimap.instance.m_mode != Minimap.MapMode.Large)
                return;

            if (Input.GetKeyDown(SendKey.Value))
                TriggerDiscordSend();
            else if (Input.GetKeyDown(CopyKey.Value))
                TriggerClipboardCopy();
        }

        /// <summary>
        /// Captures the visible map and copies it to the system clipboard.
        /// Called by the COPY MAP UI button. Always copies at full resolution
        /// (capped at <see cref="CaptureMaxDim"/>) — the previous CTRL-modifier
        /// behaviour was removed because the Discord-safe cap (which still
        /// applies to SEND) was the only reason to downscale, and a clipboard
        /// payload at native screen resolution is what image-editor work needs.
        /// </summary>
        public void TriggerClipboardCopy()
        {
            if (_sendingInProgress)
                return;

            // Tag the op BEFORE flipping the flag — the property setter fires
            // SendingStateChanged once the flag changes, and subscribers read
            // CurrentSendingOp to decide which button label to swap.
            CurrentSendingOp = SendingOp.Copy;
            _sendingInProgress = true;
            StartCoroutine(CaptureAndCopy());
        }

        // Hard ceiling for COPY MAP and SEND TO DISCORD render targets.
        // 8192 matches the compile-SAVE NativeMaxDim so a single capture and
        // a compile tile end up at the same per-px density. COPY targets the
        // system clipboard (paste into an image editor wants full per-pixel
        // detail); SEND targets Discord and leans on the Output Format
        // encoder (JPEG / IndexedPNG via Recode) to stay under the 10MB
        // attachment cap at this resolution. Map Style intentionally bypasses
        // this and renders at native screen size — see CaptureTextureWithStyle.
        // Note: 8192² × 4 bytes = 256 MB in a clipboard DIB, so paste targets
        // that mmap the whole clipboard (e.g. Discord's web client) may
        // struggle on COPY; for those, use SEND TO DISCORD.
        private const int CaptureMaxDim = 8192;

        /// <summary>
        /// EncodeOptions for SAVE + SEND, derived from the Output config
        /// section. Defaults are filled in from <see cref="MapCompositor.EncodeOptions.Default"/>
        /// when the ConfigEntries are unbound (early-load defensive path —
        /// shouldn't fire in practice once Awake has run).
        /// </summary>
        public static MapCompile.MapCompositor.EncodeOptions GetEncodeOptions()
        {
            var opts = MapCompile.MapCompositor.EncodeOptions.Default;
            opts.Format = (OutputFormat?.Value ?? OutputImageFormat.JPEG)
                          == OutputImageFormat.IndexedPNG
                ? MapCompile.MapCompositor.EncodeFormat.IndexedPng
                : MapCompile.MapCompositor.EncodeFormat.Jpeg;
            if (JpegQuality      != null) opts.JpegQuality      = JpegQuality.Value;
            if (IndexedPngColors != null) opts.IndexedPngColors = IndexedPngColors.Value;
            return opts;
        }

        /// <summary>
        /// EncodeOptions for COPY (clipboard). JPEG — IndexedPNG palette
        /// quantization on an 8192² image takes multiple seconds on the
        /// main thread (long enough that the COPY button looks frozen);
        /// JPEG encodes the same image in well under a second. The shared
        /// <see cref="JpegQuality"/> config controls quality so a tuned
        /// SAVE / SEND value carries over.
        /// </summary>
        public static MapCompile.MapCompositor.EncodeOptions GetCopyEncodeOptions()
        {
            var opts = MapCompile.MapCompositor.EncodeOptions.Default;
            opts.Format = MapCompile.MapCompositor.EncodeFormat.Jpeg;
            if (JpegQuality != null) opts.JpegQuality = JpegQuality.Value;
            return opts;
        }

        private System.Collections.IEnumerator CaptureAndCopy()
        {
            try
            {
                yield return new WaitForEndOfFrame();

                byte[] imageData = null;
                yield return CaptureTextureWithStyle(d => imageData = d);

                imageData = MapCompile.MapCompositor.Recode(
                    imageData, GetCopyEncodeOptions(), maxDim: CaptureMaxDim);
                CopyToClipboard(imageData);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        /// <summary>
        /// Public entry point for copying arbitrary PNG bytes to the clipboard.
        /// Re-encodes via <see cref="GetCopyEncodeOptions"/> so paste targets
        /// get a clipboard-friendly payload. Capped at
        /// <see cref="CaptureMaxDim"/> on the longest axis. Safe to call from
        /// the Unity main thread.
        /// </summary>
        public void CopyBytesToClipboard(byte[] pngData)
        {
            pngData = MapCompile.MapCompositor.Recode(
                pngData, GetCopyEncodeOptions(), maxDim: CaptureMaxDim);
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
        public void SendCompiledImage(byte[] pngData, int tileCount,
            System.Action<bool> onComplete = null)
        {
            if (_sendingInProgress) { onComplete?.Invoke(false); return; }
            if (string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl))
            { onComplete?.Invoke(false); return; }
            if (pngData == null || pngData.Length == 0)
            { onComplete?.Invoke(false); return; }

            _sendingInProgress = true;
            StartCoroutine(SendCompiledImageCoroutine(pngData, tileCount, onComplete));
        }

        private System.Collections.IEnumerator SendCompiledImageCoroutine(byte[] pngData, int tileCount,
            System.Action<bool> onComplete)
        {
            // ok flips false on any exception or recode/encode failure so the
            // caller's completion callback can swap its "Sending..." status
            // for a concrete success/error message instead of leaving the
            // panel stuck on the in-flight text.
            bool ok = false;
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
                // Recode into the user's Output Format (default JPEG q88).
                // No dimensional downscale here — the compile preview is
                // already capped at the compose step; this just gets it
                // under Discord's 10MB attachment limit via the chosen
                // encoder.
                pngData = MapCompile.MapCompositor.Recode(pngData, GetEncodeOptions());
                yield return DiscordWebhook.SendImage(
                    pngData, fileName, message, ModHelpers.EffectiveConfig.SpoilerImageData);
                ok = true;
            }
            finally
            {
                _sendingInProgress = false;
                onComplete?.Invoke(ok);
            }
        }

        /// <summary>
        /// Exports the current compile session's tiles as metadata-embedded
        /// PNGs (also written to compile-share/out for manual attachment) and,
        /// when a webhook is configured, sends them to Discord so teammates can
        /// drop them into their incoming folder and merge them in. Called by the
        /// SHARE TILES compile-mode button.
        /// </summary>
        public void ShareCompileTiles()
        {
            if (_sendingInProgress) return;
            if (!ModHelpers.EffectiveConfig.EnableCompileMapSharing) return;

            var outgoing = MapCompile.MapTileShare.PrepareExport();
            if (outgoing == null || outgoing.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "No tiles to share.");
                return;
            }

            string dir = MapCompile.MapTileShare.ShareOutDirForCurrentWorld();
            if (string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl))
            {
                // No webhook — the PNGs are still on disk; point the player there.
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Saved {outgoing.Count} shareable tile(s). Drag them into Discord from the compile-share/out folder.");
                ModLog.Info($"[NoMapDiscordAdditions] Shareable tiles written to {dir}");
                return;
            }

            _sendingInProgress = true;
            StartCoroutine(ShareCompileTilesCoroutine(outgoing));
        }

        private System.Collections.IEnumerator ShareCompileTilesCoroutine(
            System.Collections.Generic.List<MapCompile.MapTileShare.OutgoingTile> outgoing)
        {
            try
            {
                var player = Player.m_localPlayer;
                string playerName = player != null ? player.GetPlayerName() : "unknown";
                string template = ModHelpers.EffectiveConfig.CompileShareMessageTemplate;
                string message = template
                    .Replace("{player}", playerName)
                    .Replace("{tileCount}", outgoing.Count.ToString());

                player?.Message(MessageHud.MessageType.Center,
                    $"Sharing {outgoing.Count} tile(s) to Discord...");

                // Batch up to 5 attachments per Discord message; sets larger
                // than that spill into additional 5-image messages. The
                // explanatory content rides only on the first message so the
                // channel isn't spammed with repeated text. Shared tiles are
                // never spoiler-tagged — the recipient needs the preview to
                // tell regions apart before importing.
                var images = new System.Collections.Generic.List<DiscordWebhook.OutgoingImage>(outgoing.Count);
                foreach (var t in outgoing)
                    images.Add(new DiscordWebhook.OutgoingImage { Bytes = t.Bytes, FileName = t.FileName });

                yield return DiscordWebhook.SendImageBatches(images, message, false);

                player?.Message(MessageHud.MessageType.Center,
                    $"Shared {outgoing.Count} tile(s).");
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

            // Same Op-before-flag ordering as TriggerClipboardCopy — see the
            // comment there for why subscribers need CurrentSendingOp set when
            // SendingStateChanged fires.
            CurrentSendingOp = SendingOp.Send;
            _sendingInProgress = true;
            StartCoroutine(CaptureAndSend());
        }

        private System.Collections.IEnumerator CaptureAndSend()
        {
            BuildCaptureContext(out Player player, out string playerName, out string biome, out string message);
            try
            {
                yield return new WaitForEndOfFrame();
                byte[] imageData = null;
                yield return CaptureTextureWithStyle(d => imageData = d);

                yield return SendCapturedImage(imageData, player, playerName, biome, message);
            }
            finally
            {
                _sendingInProgress = false;
            }
        }

        // The one and only capture path. Renders the map shader off-screen at
        // CaptureMaxDim on the longest edge (screen-native aspect, scaled up
        // from the on-screen map rect), optionally compositing a stylized
        // base (Map Style) handed in from MapStyleRender. Pins / markers /
        // TMP labels are rasterized CPU-side on top via MapCaptureTexture.
        // Map Style intentionally bypasses the upscale — MapStyleRender's
        // per-pixel pipeline (biome wash, perlin grain, contours, fog) at
        // 8192² is 67M pixels and 30+ seconds on a worker thread, which
        // presents as the SEND / COPY button stuck on its loading label for
        // what looks like forever. Native screen size keeps the style render
        // well under a second.
        private System.Collections.IEnumerator CaptureTextureWithStyle(System.Action<byte[]> onResult)
        {
            MapCaptureTexture.GetDefaultCaptureSize(out int width, out int height);
            bool styleActive = MapStyleRender.IsStyleActive();
            if (!styleActive)
            {
                int longest = Mathf.Max(width, height);
                if (longest > 0 && longest < CaptureMaxDim)
                {
                    float k = (float)CaptureMaxDim / longest;
                    width = Mathf.RoundToInt(width * k);
                    height = Mathf.RoundToInt(height * k);
                }
            }

            Texture2D styled = null;
            if (styleActive)
            {
                var mapImage = Minimap.instance != null ? Minimap.instance.m_mapImageLarge : null;
                if (mapImage != null)
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Rendering map style...");
                    yield return MapStyleRender.BuildAsync(
                        mapImage.uvRect, width, height, t => styled = t);
                }
            }

            try
            {
                onResult(MapCaptureTexture.CaptureMap(width, height, styled));
            }
            finally
            {
                if (styled != null) Object.Destroy(styled);
            }
        }

        private static void BuildCaptureContext(
            out Player player, out string playerName, out string biome, out string message)
        {
            player = Player.m_localPlayer;
            playerName = player != null ? player.GetPlayerName() : "unknown";
            biome = player != null ? player.GetCurrentBiome().ToString() : "unknown";

            string tableName = MapCompile.MapCompileSession.ActiveTableName;
            string tableText = !string.IsNullOrEmpty(tableName) ? $" — {tableName}" : "";

            message = ModHelpers.EffectiveConfig.MessageTemplate ?? string.Empty;
            // Strip the legacy {spawnDir} placeholder from older configs that
            // still have it — the feature is gone but a saved template won't
            // be rewritten by BepInEx.
            message = message.Replace("{spawnDir}", string.Empty);

            // Replace in place when the template has the placeholder, otherwise
            // append so older configs (saved before {table} existed) still get
            // the table name.
            if (message.Contains("{table}"))
                message = message.Replace("{table}", tableText);
            else if (tableText.Length > 0)
                message += tableText;

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

            // Recode into the user's Output Format (default JPEG q88) — keeps
            // full resolution and relies on encoder compression to stay under
            // Discord's 10MB attachment limit. A single capture at native
            // screen resolution × superSize 2 typically fits well within that
            // budget at JPEG q88 or IndexedPNG 64-colour.
            imageData = MapCompile.MapCompositor.Recode(imageData, GetEncodeOptions());

            string fileName = BuildMapFileName(playerName, biome);
            player?.Message(MessageHud.MessageType.Center, "Sending map to Discord...");
            yield return DiscordWebhook.SendImage(
                imageData, fileName, message, ModHelpers.EffectiveConfig.SpoilerImageData);
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
