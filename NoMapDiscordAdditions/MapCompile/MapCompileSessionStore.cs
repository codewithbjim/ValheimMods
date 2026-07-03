using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Catalog of every compile session the current (world, player) owns on
    /// disk. Session slots are sibling directories under compile-sessions/:
    /// the original single-session dir "&lt;worldUid&gt;_&lt;playerId&gt;"
    /// doubles as "Session 1", and every later slot appends an ordinal —
    /// "&lt;base&gt;_s2", "&lt;base&gt;_s3", … Nothing is ever migrated: a
    /// pre-1.4.0 install reads/writes the base dir exactly as before, and
    /// older builds simply never see the suffixed siblings.
    /// </summary>
    public static class MapCompileSessionStore
    {
        // Between the base key and the ordinal for slots >= 2. Base keys are
        // fixed-width ({worldUid:X16}_{playerId:X16}, 33 chars), so an
        // exact-prefix + "_s<digits>" parse can't collide with another
        // (world, player)'s dirs.
        private const string SlotInfix = "_s";

        public sealed class Entry
        {
            public string DirName;   // "<base>" or "<base>_sN" — doubles as the session key
            public string Dir;       // full path
            public int Ordinal;      // 1 = legacy base dir, N for "_sN"
            public int TileCount;
            public int IncludedCount;
            public long LastUsedMs;  // LastOpenedMs from session.json; file mtime fallback
            public string CustomName; // player-chosen name from session.json; null = default

            public string DisplayName =>
                string.IsNullOrEmpty(CustomName) ? $"Session {Ordinal}" : CustomName;
        }

        // Cap for player-chosen session names. Generous enough for a
        // descriptive label, short enough that the SESSIONS row and the
        // Manage panel header never need to wrap (both ellipsize anyway).
        public const int MaxCustomNameLength = 40;

        /// <summary>
        /// Every saved session owned by the current world+player, sorted by
        /// ordinal. A directory only counts when its session.json exists —
        /// the same rule the old single-session resume check used — so a
        /// corrupt dir with stray tile PNGs is invisible here.
        /// </summary>
        public static List<Entry> ListForCurrent()
        {
            var entries = new List<Entry>();
            if (!MapCompileEnvironment.HasSessionKey()) return entries;

            string root = MapCompileEnvironment.SessionsRoot;
            if (!Directory.Exists(root)) return entries;

            string baseKey = MapCompileEnvironment.GetSessionKey();
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return entries; }

            foreach (var dir in dirs)
            {
                string dirName = Path.GetFileName(dir);
                int ordinal = OrdinalOf(baseKey, dirName);
                if (ordinal < 1) continue;

                string sessionFile = MapCompileEnvironment.GetSessionFile(dir);
                if (!File.Exists(sessionFile)) continue;

                var entry = new Entry { DirName = dirName, Dir = dir, Ordinal = ordinal };
                if (MapCompileSession.TryReadIndexSummary(dir,
                        out int tiles, out int included, out long lastOpened,
                        out string customName))
                {
                    entry.TileCount = tiles;
                    entry.IncludedCount = included;
                    entry.LastUsedMs = lastOpened;
                    entry.CustomName = customName;
                }
                else
                {
                    // Unreadable index — count the PNGs so the list still shows
                    // something useful; treat every tile as included.
                    try { entry.TileCount = Directory.GetFiles(dir, "tile_*.png").Length; }
                    catch { entry.TileCount = 0; }
                    entry.IncludedCount = entry.TileCount;
                }
                if (entry.LastUsedMs <= 0)
                {
                    // Session written before LastOpenedMs existed — the index
                    // file's mtime is the best available "last used" signal.
                    try
                    {
                        entry.LastUsedMs = new DateTimeOffset(
                            File.GetLastWriteTimeUtc(sessionFile)).ToUnixTimeMilliseconds();
                    }
                    catch { entry.LastUsedMs = 0; }
                }
                entries.Add(entry);
            }

            entries.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
            return entries;
        }

        /// <summary>
        /// Most recently used session — the default RESUME target. Null when
        /// none exist.
        /// </summary>
        public static Entry MostRecent()
        {
            Entry best = null;
            foreach (var e in ListForCurrent())
                if (best == null || e.LastUsedMs > best.LastUsedMs) best = e;
            return best;
        }

        /// <summary>
        /// Dir name for a brand-new session: the lowest ordinal whose directory
        /// doesn't exist yet. Checks directory existence (not session.json) so
        /// a corrupt slot is skipped rather than inherited along with its stray
        /// tile PNGs. With nothing on disk this returns the base key —
        /// identical to the pre-multi-session behavior.
        /// </summary>
        public static string NextNewDirName()
        {
            string baseKey = MapCompileEnvironment.GetSessionKey();
            for (int n = 1; ; n++)
            {
                string dirName = DirNameFor(baseKey, n);
                if (!Directory.Exists(MapCompileEnvironment.GetSessionDir(dirName)))
                    return dirName;
            }
        }

        /// <summary>1 for the base dir, N for "&lt;base&gt;_sN" (N ≥ 2), -1 for anything else.</summary>
        public static int OrdinalOfDirName(string dirName)
        {
            if (string.IsNullOrEmpty(dirName)) return -1;
            if (!MapCompileEnvironment.HasSessionKey()) return -1;
            return OrdinalOf(MapCompileEnvironment.GetSessionKey(), dirName);
        }

        /// <summary>"Session N" for one of our dirs, null otherwise.</summary>
        public static string DisplayNameFor(string dirName)
        {
            int ordinal = OrdinalOfDirName(dirName);
            return ordinal >= 1 ? $"Session {ordinal}" : null;
        }

        /// <summary>
        /// Folder-name suffix for this session's web-map bundle. Empty for the
        /// legacy Session 1 (or an unrecognized dir) so bundles exported before
        /// multi-session keep updating in place; "_sN" for later sessions.
        /// </summary>
        public static string WebMapSuffixFor(string dirName)
        {
            int ordinal = OrdinalOfDirName(dirName);
            return ordinal >= 2
                ? SlotInfix + ordinal.ToString(CultureInfo.InvariantCulture)
                : "";
        }

        /// <summary>
        /// Rename a saved session (the SESSIONS panel's per-row RENAME) —
        /// cosmetic only, the directory/slot key never changes, so tiles,
        /// resume behaviour and web-map bundle folders are all unaffected.
        /// Empty/whitespace input clears the custom name back to the default
        /// "Session N". Returns true when the index was rewritten.
        /// </summary>
        public static bool RenameSession(string dirName, string newName)
        {
            if (string.IsNullOrEmpty(dirName)) return false;
            if (OrdinalOfDirName(dirName) < 1) return false; // not one of ours

            string dir = MapCompileEnvironment.GetSessionDir(dirName);
            return MapCompileSession.TrySetCustomNameOnDisk(dir, CleanCustomName(newName));
        }

        /// <summary>
        /// Normalize a player-typed session name: trim, strip the TMP
        /// rich-text brackets (row labels render with richText enabled, so a
        /// literal &lt;color&gt; in a name would inject markup), cap the
        /// length. Null when nothing usable remains — which RenameSession
        /// treats as "revert to the default name".
        /// </summary>
        public static string CleanCustomName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Replace("<", "").Replace(">", "").Trim();
            if (s.Length > MaxCustomNameLength) s = s.Substring(0, MaxCustomNameLength).Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>
        /// Recursively delete a saved session (the SESSIONS panel's per-row
        /// DELETE). Refuses the loaded session outside Idle — the panel only
        /// opens while Idle, so this is belt-and-braces. Never touches
        /// compiled/ outputs or web-map bundles (user artifacts). Returns true
        /// when the dir is gone.
        /// </summary>
        public static bool DeleteSession(string dirName)
        {
            if (string.IsNullOrEmpty(dirName)) return false;
            if (dirName == MapCompileSession.ActiveDirName
                && MapCompileSession.CurrentState != MapCompileSession.State.Idle)
                return false;

            string dir = MapCompileEnvironment.GetSessionDir(dirName);
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Failed to delete session dir: {ex.Message}");
                return false;
            }
        }

        // ─── Internals ───────────────────────────────────────────────────────

        private static string DirNameFor(string baseKey, int ordinal) =>
            ordinal <= 1
                ? baseKey
                : baseKey + SlotInfix + ordinal.ToString(CultureInfo.InvariantCulture);

        private static int OrdinalOf(string baseKey, string dirName)
        {
            if (dirName == baseKey) return 1;
            if (!dirName.StartsWith(baseKey + SlotInfix, StringComparison.Ordinal)) return -1;
            string rest = dirName.Substring(baseKey.Length + SlotInfix.Length);
            if (int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                && n >= 2)
                return n;
            return -1;
        }
    }
}
