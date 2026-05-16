using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Cross-player tile sharing for compile mode. A player exports the tiles
    /// of their current compile session as individual self-describing PNGs
    /// (world rect embedded via <see cref="TilePngMeta"/>); these go out over
    /// the Discord webhook and are also dropped in the share/out folder so they
    /// can be attached manually. Teammates save the received PNGs into
    /// share/incoming, which is auto-scanned whenever the large map opens
    /// during a compile session — matching tiles are merged into the active
    /// session and then participate in the normal compose step.
    ///
    /// A Discord webhook is send-only (it cannot read a channel back), so the
    /// receive side is deliberately file-based rather than pulling from Discord.
    /// </summary>
    public static class MapTileShare
    {
        public struct OutgoingTile
        {
            public string FileName;
            public byte[] Bytes;
        }

        // ── Export ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds metadata-embedded copies of every tile in the current session,
        /// also writing them to share/out for manual attachment. Returns the
        /// list to hand to the Discord sender (empty list if nothing to share).
        /// Safe to call on the Unity main thread (small synchronous disk I/O).
        /// </summary>
        public static List<OutgoingTile> PrepareExport()
        {
            var outgoing = new List<OutgoingTile>();
            var tiles = MapCompileSession.Tiles;
            if (tiles == null || tiles.Count == 0) return outgoing;

            long worldUid = CurrentWorldUid();
            string player = LocalPlayerName();
            string outDir = Path.Combine(MapCompileEnvironment.ShareOutDir,
                $"{worldUid:X16}");

            try { MapCompileEnvironment.EnsureDirectory(outDir); }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Could not create share dir: {ex.Message}");
                outDir = null;
            }

            string srcSafe = Sanitize(player);
            foreach (var tile in tiles)
            {
                if (tile.PngPath == null || !File.Exists(tile.PngPath)) continue;

                byte[] raw;
                try { raw = File.ReadAllBytes(tile.PngPath); }
                catch (Exception ex)
                {
                    ModLog.Warn($"[NoMapDiscordAdditions] Share read failed ({tile.PngPath}): {ex.Message}");
                    continue;
                }

                var meta = new TilePngMeta.TileMeta
                {
                    WorldUid = worldUid,
                    WorldMin = new[] { tile.WorldMin.x, tile.WorldMin.y },
                    WorldMax = new[] { tile.WorldMax.x, tile.WorldMax.y },
                    PixelWidth = tile.PixelWidth,
                    PixelHeight = tile.PixelHeight,
                    // Preserve the original capturer when re-sharing an imported tile.
                    SourcePlayer = tile.IsImported && !string.IsNullOrEmpty(tile.SourcePlayer)
                        ? tile.SourcePlayer : player,
                    FullyMapped = tile.FullyMapped,
                };

                byte[] embedded = TilePngMeta.Embed(raw, meta);
                string fileName = $"nmtile_{worldUid:X16}_{srcSafe}_{tile.Index:D3}.png";

                if (outDir != null)
                {
                    try { File.WriteAllBytes(Path.Combine(outDir, fileName), embedded); }
                    catch (Exception ex)
                    {
                        ModLog.Warn($"[NoMapDiscordAdditions] Share write failed: {ex.Message}");
                    }
                }

                outgoing.Add(new OutgoingTile { FileName = fileName, Bytes = embedded });
            }
            return outgoing;
        }

        public static string ShareOutDirForCurrentWorld() =>
            Path.Combine(MapCompileEnvironment.ShareOutDir, $"{CurrentWorldUid():X16}");

        // ── Import (auto-scan) ───────────────────────────────────────────────

        /// <summary>
        /// Scans share/incoming for shared tile PNGs, merges any that belong to
        /// the current world into the active compile session, and moves every
        /// handled file out of incoming so it isn't reprocessed. Returns the
        /// number of tiles actually imported. No-op unless a session is
        /// Compiling. Main-thread only.
        /// </summary>
        public static int ScanAndImport()
        {
            if (MapCompileSession.CurrentState != MapCompileSession.State.Compiling)
                return 0;

            string inDir = MapCompileEnvironment.IncomingDir;
            if (!Directory.Exists(inDir)) return 0;

            string[] files;
            try { files = Directory.GetFiles(inDir, "*.png"); }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Incoming scan failed: {ex.Message}");
                return 0;
            }
            if (files.Length == 0) return 0;

            long worldUid = CurrentWorldUid();
            int imported = 0;

            foreach (string path in files)
            {
                if (!TilePngMeta.TryExtractFromFile(path, out var meta))
                {
                    // Not one of our shared tiles — get it out of the way.
                    MoveTo(path, MapCompileEnvironment.IncomingIgnoredDir);
                    continue;
                }

                if (meta.WorldUid != worldUid)
                {
                    ModLog.Info($"[NoMapDiscordAdditions] Skipping shared tile for a " +
                                $"different world ({meta.WorldUid:X16}).");
                    MoveTo(path, MapCompileEnvironment.IncomingIgnoredDir);
                    continue;
                }

                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex)
                {
                    ModLog.Warn($"[NoMapDiscordAdditions] Incoming read failed: {ex.Message}");
                    continue;
                }

                var min = new Vector2(meta.WorldMin[0], meta.WorldMin[1]);
                var max = new Vector2(meta.WorldMax[0], meta.WorldMax[1]);
                string key = BuildImportKey(meta, min, max);

                bool ok = MapCompileSession.AddImportedTile(
                    bytes, meta.PixelWidth, meta.PixelHeight, min, max,
                    key, meta.SourcePlayer, meta.FullyMapped);

                if (ok)
                {
                    imported++;
                    MoveTo(path, MapCompileEnvironment.IncomingProcessedDir);
                }
            }

            if (imported > 0)
                ModLog.Info($"[NoMapDiscordAdditions] Imported {imported} shared tile(s).");
            return imported;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        // Stable identity for a shared tile: same world + same world rect +
        // same original capturer => same key, so re-dropping (or re-sharing)
        // the same tile replaces in place instead of stacking duplicates.
        private static string BuildImportKey(TilePngMeta.TileMeta meta,
            Vector2 min, Vector2 max)
        {
            string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
            string src = string.IsNullOrEmpty(meta.SourcePlayer) ? "?" : meta.SourcePlayer;
            return $"{meta.WorldUid:X16}|{F(min.x)},{F(min.y)}|{F(max.x)},{F(max.y)}|{src}";
        }

        private static void MoveTo(string file, string destDir)
        {
            try
            {
                MapCompileEnvironment.EnsureDirectory(destDir);
                string dest = Path.Combine(destDir, Path.GetFileName(file));
                // Disambiguate collisions so a re-shared tile with the same
                // name never overwrites an earlier one in the archive folder.
                if (File.Exists(dest))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file);
                    dest = Path.Combine(destDir,
                        $"{stem}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}");
                }
                File.Move(file, dest);
            }
            catch (Exception ex)
            {
                // If we can't move it, delete so it isn't endlessly rescanned.
                ModLog.Warn($"[NoMapDiscordAdditions] Could not archive {file}: {ex.Message}");
                try { File.Delete(file); } catch { /* give up quietly */ }
            }
        }

        private static long CurrentWorldUid() =>
            ZNet.instance != null ? ZNet.instance.GetWorldUID() : 0L;

        private static string LocalPlayerName()
        {
            var p = Player.m_localPlayer;
            string n = p != null ? p.GetPlayerName() : null;
            return string.IsNullOrEmpty(n) ? "unknown" : n;
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
    }
}
