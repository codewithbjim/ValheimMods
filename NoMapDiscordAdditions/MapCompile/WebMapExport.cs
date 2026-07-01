using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Exports a compiled map as a self-contained, offline, interactive web
    /// viewer: a folder holding the pin-free base image, the pins as DATA
    /// (world position + kind + tint), one baked PNG per distinct pin
    /// icon/tint, and an <c>index.html</c> that pans, zooms and filters the
    /// pins by kind. Double-clicking <c>index.html</c> opens it in any browser
    /// — no server, no internet, no third-party libraries.
    ///
    /// Why re-compose instead of reusing the panel's PNG: the compile result's
    /// pixels already have pins STAMPED into them (and its raw buffer is freed
    /// after the stamp). For a filterable overlay we need the base WITHOUT pins,
    /// so we recompose from the on-disk tiles via <see cref="MapCompositor.ComposeNative"/>
    /// (same path SAVE uses for native resolution) and encode its raw
    /// <see cref="MapCompositor.CompiledMap.Bgra"/> before any stamp runs. The
    /// pins come from the same snapshot the stamp would have used, so the web
    /// overlay lands pixel-for-pixel where the baked pins would have.
    ///
    /// Threading mirrors the SAVE flow: the System.Drawing compose + base encode
    /// run off the main thread; pin projection, icon baking (Unity texture
    /// reads) and file writes happen back on the main thread.
    /// </summary>
    public static class WebMapExport
    {
        // ── DTOs serialized into data.js (short field names keep it compact) ──

        private class WorldInfo
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("min")]  public float[] Min;   // [x, z]
            [JsonProperty("max")]  public float[] Max;   // [x, z]
        }

        private class ImageInfo
        {
            [JsonProperty("file")] public string File;
            [JsonProperty("w")]    public int W;
            [JsonProperty("h")]    public int H;
        }

        private class GroupInfo
        {
            [JsonProperty("key")]   public string Key;
            [JsonProperty("name")]  public string Name;
            [JsonProperty("icon")]  public string Icon;   // representative icon path
            [JsonProperty("count")] public int Count;
        }

        private class PinInfo
        {
            [JsonProperty("x")]    public float X;        // world X
            [JsonProperty("z")]    public float Z;        // world Z
            [JsonProperty("px")]   public int Px;         // image pixel X (from left)
            [JsonProperty("py")]   public int Py;         // image pixel Y (from top)
            [JsonProperty("key")]  public string Key;     // group key
            [JsonProperty("n")]    public string Name;    // caption (may be null)
            [JsonProperty("icon")] public int Icon;       // index into icons[]
        }

        private class WebData
        {
            [JsonProperty("world")]  public WorldInfo World;
            [JsonProperty("image")]  public ImageInfo Image;
            [JsonProperty("groups")] public List<GroupInfo> Groups;
            [JsonProperty("pins")]   public List<PinInfo> Pins;
        }

        /// <summary>
        /// Run the export. Yield this from a coroutine on the main thread. On
        /// completion <paramref name="onDone"/> fires with (success, message);
        /// message is the export folder path on success or an error hint.
        /// </summary>
        public static IEnumerator Run(MapCompositor.CompiledMap result,
            Action<bool, string> onDone)
        {
            // Snapshot the compile inputs on the main thread. Excluded tiles are
            // dropped so the web map matches the compiled preview. Pins MUST be
            // captured on the main thread (live TMP / UI components).
            var tiles = new List<MapCompileTile>();
            foreach (var t in MapCompileSession.Tiles)
                if (!t.ExcludedFromCompile) tiles.Add(t);

            if (tiles.Count == 0)
            {
                onDone?.Invoke(false, "No tiles to export.");
                yield break;
            }

            var pins = MapCompilePinSnapshot.Capture(out float _);

            // Off-thread: recompose the pin-free base at native resolution and
            // encode it. Keeping the encode in the same worker avoids a second
            // main-thread hop and matches the SAVE encoder choice.
            var encodeOpts = Plugin.GetEncodeOptions();
            MapCompositor.CompiledMap full = null;
            byte[] baseBytes = null;
            Exception error = null;

            var done = new ManualResetEventSlim(false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    full = MapCompositor.ComposeNative(tiles);
                    if (full != null && full.Bgra != null)
                        baseBytes = MapCompositor.EncodeBgra(
                            full.Bgra, full.Width, full.Height, encodeOpts);
                }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            while (!done.IsSet) yield return null;
            done.Dispose();

            if (error != null || full == null || baseBytes == null)
            {
                if (error != null)
                    ModLog.Error($"[NoMapDiscordAdditions] Web map compose failed: {error.Message}");
                onDone?.Invoke(false, "Compose failed — see log.");
                yield break;
            }

            // Back on the main thread: project pins, bake icons, write files.
            string message;
            bool ok;
            try
            {
                string dir = BuildBundle(full, baseBytes, encodeOpts, tiles, pins);
                message = dir;
                ok = true;
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Web map export failed: {ex.Message}");
                message = "Export failed — see log.";
                ok = false;
            }

            onDone?.Invoke(ok, message);
        }

        // Writes the whole bundle to disk and returns the folder path.
        private static string BuildBundle(MapCompositor.CompiledMap full,
            byte[] baseBytes, MapCompositor.EncodeOptions encodeOpts,
            IReadOnlyList<MapCompileTile> tiles,
            IReadOnlyList<MapCompositor.PinDraw> pins)
        {
            int w = full.Width, h = full.Height;
            Vector2 wmin = full.WorldMin;
            Vector2 wsize = full.WorldMax - full.WorldMin;

            // Stable per-world output folder: compiled/webmap_<world>/. Re-exporting
            // UPDATES this folder in place — same path, same bookmarkable index.html
            // — instead of spawning a new timestamped copy each time. Keyed by world
            // so different worlds don't clobber each other.
            string uid = ZNet.instance != null
                ? ZNet.instance.GetWorldUID().ToString("X16") : "world";
            string worldKey = Sanitize(
                ZNet.instance != null ? ZNet.instance.GetWorldName() : null, uid);
            string dir = Path.Combine(MapCompileEnvironment.CompiledOutDir, "webmap_" + worldKey);
            string iconsDir = Path.Combine(dir, "icons");
            MapCompileEnvironment.EnsureDirectory(dir);

            // Wipe stale per-kind icons and any previous base image so an update
            // never leaves orphaned files behind (a pin kind that's since been
            // filtered out, or a map.png left over when now saving .jpg).
            if (Directory.Exists(iconsDir))
                try { Directory.Delete(iconsDir, true); } catch { }
            MapCompileEnvironment.EnsureDirectory(iconsDir);
            foreach (var stale in Directory.GetFiles(dir, "map.*"))
                try { File.Delete(stale); } catch { }

            // Base image (pin-free). Extension follows the SAVE encoder choice.
            string imageFile = "map" + encodeOpts.Extension;
            File.WriteAllBytes(Path.Combine(dir, imageFile), baseBytes);

            // Distinct (kind × tint) → baked icon PNG. Two pins of the same kind
            // but different tint (e.g. a shared-map grey copy vs a local white
            // one) get their own icon so the overlay mirrors the game exactly.
            var iconIndex = new Dictionary<string, int>();   // "key|#rrggbb" → i
            var pinRecords = new List<PinInfo>();
            var groupOrder = new List<string>();             // first-seen key order
            var groupCount = new Dictionary<string, int>();
            var groupName = new Dictionary<string, string>();
            var groupIcon = new Dictionary<string, string>();

            foreach (var p in pins)
            {
                if (p.Icon == null) continue;
                if (!IsInsideAnyTile(p.WorldX, p.WorldZ, tiles)) continue;

                float fx = (p.WorldX - wmin.x) / wsize.x;
                float fyTop = (wsize.y - (p.WorldZ - wmin.y)) / wsize.y;
                if (fx < 0f || fx > 1f || fyTop < 0f || fyTop > 1f) continue;

                string key = string.IsNullOrEmpty(p.Key) ? "pin" : p.Key;
                string hex = ToHex(p.Tint);
                string iconKey = key + "|" + hex;

                if (!iconIndex.TryGetValue(iconKey, out int idx))
                {
                    idx = iconIndex.Count;
                    byte[] iconPng = BakeIcon(p.Icon, p.Tint);
                    string iconPath = "icons/icon_" + idx + ".png";
                    if (iconPng != null)
                        File.WriteAllBytes(Path.Combine(dir, iconPath), iconPng);
                    iconIndex[iconKey] = idx;

                    if (!groupIcon.ContainsKey(key)) groupIcon[key] = iconPath;
                }

                if (!groupCount.ContainsKey(key))
                {
                    groupOrder.Add(key);
                    groupCount[key] = 0;
                    groupName[key] = Humanize(key, p.Icon);
                }
                groupCount[key]++;

                pinRecords.Add(new PinInfo
                {
                    X = p.WorldX,
                    Z = p.WorldZ,
                    Px = Mathf.RoundToInt(fx * w),
                    Py = Mathf.RoundToInt(fyTop * h),
                    Key = key,
                    Name = string.IsNullOrEmpty(p.Name) ? null : p.Name,
                    Icon = idx,
                });
            }

            var groups = new List<GroupInfo>();
            foreach (var key in groupOrder)
                groups.Add(new GroupInfo
                {
                    Key = key,
                    Name = groupName[key],
                    Icon = groupIcon.TryGetValue(key, out var gi) ? gi : null,
                    Count = groupCount[key],
                });
            // Most-common kind first, then alphabetical — matches the PINS panel.
            groups.Sort((a, b) =>
            {
                int c = b.Count.CompareTo(a.Count);
                return c != 0 ? c
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            var data = new WebData
            {
                World = new WorldInfo
                {
                    Name = ZNet.instance != null ? ZNet.instance.GetWorldName() : null,
                    Min = new[] { full.WorldMin.x, full.WorldMin.y },
                    Max = new[] { full.WorldMax.x, full.WorldMax.y },
                },
                Image = new ImageInfo { File = imageFile, W = w, H = h },
                Groups = groups,
                Pins = pinRecords,
            };

            // data.js (not .json) so the viewer loads over file:// without the
            // fetch/CORS restrictions browsers place on local JSON.
            string json = JsonConvert.SerializeObject(data);
            File.WriteAllText(Path.Combine(dir, "data.js"),
                "window.NMDA_MAP = " + json + ";", new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(dir, "index.html"),
                WebMapViewer.Html, new UTF8Encoding(false));

            return dir;
        }

        // ── Icon baking ──────────────────────────────────────────────────────

        // Read the sprite's atlas region, multiply the pin tint into it
        // (preserving alpha), and encode a small standalone PNG. Monochrome
        // Valheim pin icons come out tinted exactly as the live map shows them;
        // already-coloured icons keep their colour (white tint is a no-op).
        private static byte[] BakeIcon(Sprite sprite, Color32 tint)
        {
            try
            {
                Color32[] atlas = ReadAtlas(sprite.texture, out int atlasW, out int atlasH);
                if (atlas == null) return null;

                Rect rect = sprite.textureRect;
                int sx = Mathf.RoundToInt(rect.x);
                int sy = Mathf.RoundToInt(rect.y);
                int sw = Mathf.RoundToInt(rect.width);
                int sh = Mathf.RoundToInt(rect.height);
                if (sw <= 0 || sh <= 0) return null;

                var outPx = new Color32[sw * sh];
                for (int y = 0; y < sh; y++)
                {
                    int ay = sy + y;
                    if (ay < 0 || ay >= atlasH) continue;
                    int srcRow = ay * atlasW;
                    int dstRow = y * sw;
                    for (int x = 0; x < sw; x++)
                    {
                        int ax = sx + x;
                        if (ax < 0 || ax >= atlasW) continue;
                        Color32 sp = atlas[srcRow + ax];
                        outPx[dstRow + x] = new Color32(
                            (byte)((sp.r * tint.r) / 255),
                            (byte)((sp.g * tint.g) / 255),
                            (byte)((sp.b * tint.b) / 255),
                            (byte)((sp.a * tint.a) / 255));
                    }
                }

                var tex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
                tex.SetPixels32(outPx);
                tex.Apply();
                byte[] png = ImageConversion.EncodeToPNG(tex);
                UnityEngine.Object.Destroy(tex);
                return png;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Icon bake failed: {ex.Message}");
                return null;
            }
        }

        // GetPixels32 for a readable texture; round-trip through a RenderTexture
        // for GPU-only atlases (same fallback MapCaptureTexture.ReadPixelsSafe
        // uses). Returns pixels in bottom-up row order — SetPixels32 consumes
        // the same order, and EncodeToPNG flips to top-down, so orientation is
        // preserved end to end.
        private static Color32[] ReadAtlas(Texture2D src, out int w, out int h)
        {
            w = src != null ? src.width : 0;
            h = src != null ? src.height : 0;
            if (src == null) return null;
            try
            {
                return src.GetPixels32();
            }
            catch
            {
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
                UnityEngine.Object.Destroy(readable);
                return px;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // Friendly kind label from the sprite key — mirrors
        // MapCompilePinFilter.Humanize so the web legend reads like the PINS
        // panel ("MapIconShip" → "Ship"). Falls back to the raw key.
        private static string Humanize(string key, Sprite sprite)
        {
            string n = sprite != null && !string.IsNullOrEmpty(sprite.name)
                ? sprite.name : key;
            if (string.IsNullOrEmpty(n)) return key;
            n = n.Replace("(Clone)", "")
                 .Replace("MapIcon", "")
                 .Replace("mapicon", "")
                 .Replace("_pin", "")
                 .Replace("pin_", "");
            n = n.Replace('_', ' ').Replace('-', ' ').Trim();
            return string.IsNullOrEmpty(n) ? key : n;
        }

        private static string ToHex(Color32 c) =>
            "#" + c.r.ToString("x2") + c.g.ToString("x2") + c.b.ToString("x2");

        // Same per-tile world-rect test the stamp uses to cull pins in the
        // black gaps between non-adjacent tiles.
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

        // Keep only filename-safe chars; fall back to the given default (which is
        // itself sanitized) when nothing usable remains.
        private static string Sanitize(string s, string fallback)
        {
            if (!string.IsNullOrEmpty(s))
            {
                var sb = new StringBuilder(s.Length);
                foreach (char c in s)
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                        (c >= '0' && c <= '9') || c == '-' || c == '_') sb.Append(c);
                if (sb.Length > 0) return sb.ToString();
            }
            return string.IsNullOrEmpty(fallback) ? "world" : fallback;
        }
    }
}
