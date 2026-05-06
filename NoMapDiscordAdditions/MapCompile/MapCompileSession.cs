using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Per-player, per-world compile session. At most one is active at a time
    /// for the local player. Persists to disk after every tile add so a crash
    /// or disconnect mid-mapping doesn't lose progress.
    /// </summary>
    public static class MapCompileSession
    {
        public enum State { Idle, Compiling, Reviewing }

        public static State CurrentState { get; private set; } = State.Idle;
        public static IReadOnlyList<MapCompileTile> Tiles => _tiles;

        public static event Action StateChanged;

        // The cartography table the player most recently opened the map at,
        // limited to the current map session (cleared on map close).
        // Compile-mode "Add Tile" is only enabled while this is non-null AND
        // we're in the Compiling state — gating per the user's "tables only" rule.
        public static Vector3? ActiveTablePos { get; private set; }

        private static readonly List<MapCompileTile> _tiles = new List<MapCompileTile>();
        private static string _sessionKey;
        private static string _sessionDir;

        // Re-adds within this radius of an existing tile's table position
        // replace the existing tile in place. 8m comfortably covers the same
        // table (which the player walks up to from any side) without merging
        // adjacent tables that just happen to be close.
        private const float DedupRadius = 8f;

        // ─── Active-table plumbing (called from CartographyTablePatch) ───────

        public static void SetActiveTable(Vector3 worldPos)
        {
            ActiveTablePos = worldPos;
            StateChanged?.Invoke();
        }

        public static void ClearActiveTable()
        {
            if (ActiveTablePos == null) return;
            ActiveTablePos = null;
            StateChanged?.Invoke();
        }

        public static bool CanAddTile =>
            CurrentState == State.Compiling && ActiveTablePos != null;

        // ─── Resume detection ────────────────────────────────────────────────

        /// <summary>
        /// True if a saved session for the current world+player exists on disk and
        /// we're currently Idle. UI uses this to show "RESUME" instead of "START".
        /// </summary>
        public static bool HasResumableSession()
        {
            if (CurrentState != State.Idle) return false;
            if (!MapCompileEnvironment.HasSessionKey()) return false;

            string key = MapCompileEnvironment.GetSessionKey();
            string dir = MapCompileEnvironment.GetSessionDir(key);
            return File.Exists(MapCompileEnvironment.GetSessionFile(dir));
        }

        // ─── Lifecycle ───────────────────────────────────────────────────────

        public static void Start()
        {
            if (CurrentState != State.Idle) return;
            if (!MapCompileEnvironment.HasSessionKey())
            {
                ModLog.Warn("[NoMapDiscordAdditions] Compile start aborted — session key unavailable.");
                return;
            }

            _sessionKey = MapCompileEnvironment.GetSessionKey();
            _sessionDir = MapCompileEnvironment.GetSessionDir(_sessionKey);
            MapCompileEnvironment.EnsureDirectory(_sessionDir);

            _tiles.Clear();
            CurrentState = State.Compiling;
            SaveIndex();
            StateChanged?.Invoke();

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Map compile started.");
        }

        public static bool Resume()
        {
            if (CurrentState != State.Idle) return false;
            if (!MapCompileEnvironment.HasSessionKey()) return false;

            _sessionKey = MapCompileEnvironment.GetSessionKey();
            _sessionDir = MapCompileEnvironment.GetSessionDir(_sessionKey);
            string sessionFile = MapCompileEnvironment.GetSessionFile(_sessionDir);
            if (!File.Exists(sessionFile)) return false;

            _tiles.Clear();
            try
            {
                LoadIndex();
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Failed to resume compile session: {ex.Message}");
                _tiles.Clear();
                return false;
            }

            CurrentState = State.Compiling;
            StateChanged?.Invoke();
            string tileWord = _tiles.Count == 1 ? "tile" : "tiles";
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center, $"Resumed compile session ({_tiles.Count} {tileWord}).");
            return true;
        }

        /// <summary>
        /// Adds a tile. PNG bytes are written to the session directory and the
        /// index is saved atomically (.tmp + rename) before this returns.
        /// Re-adding from the same table replaces in place rather than appending.
        /// </summary>
        public static void AddTile(byte[] pngBytes, int width, int height,
            Vector2 worldMin, Vector2 worldMax, Vector3 tablePos)
        {
            if (CurrentState != State.Compiling) return;
            if (pngBytes == null || pngBytes.Length == 0) return;

            int existing = FindTileNearTable(tablePos);
            int index;
            if (existing >= 0)
            {
                index = _tiles[existing].Index;
                _tiles.RemoveAt(existing);
            }
            else
            {
                index = NextTileIndex();
            }

            string pngPath = MapCompileEnvironment.GetTileFile(_sessionDir, index);
            try
            {
                File.WriteAllBytes(pngPath, pngBytes);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Tile write failed: {ex.Message}");
                return;
            }

            var tile = new MapCompileTile
            {
                Index = index,
                WorldMin = worldMin,
                WorldMax = worldMax,
                TableWorldPos = tablePos,
                PixelWidth = width,
                PixelHeight = height,
                PngPath = pngPath,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _tiles.Add(tile);
            SaveIndex();
            StateChanged?.Invoke();

            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                existing >= 0 ? $"Tile updated ({_tiles.Count} total)"
                              : $"Tile added ({_tiles.Count} total)");
        }

        /// <summary>
        /// Move from Compiling to Reviewing. Tile data is preserved on disk
        /// until the result panel completes (Save / Copy / Send / Discard / Done).
        /// </summary>
        public static void Finish()
        {
            if (CurrentState != State.Compiling) return;
            if (_tiles.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No tiles to compile.");
                return;
            }
            CurrentState = State.Reviewing;
            StateChanged?.Invoke();
        }

        /// <summary>Discard everything (Cancel button + Discard action).</summary>
        public static void Discard()
        {
            _tiles.Clear();
            try
            {
                if (!string.IsNullOrEmpty(_sessionDir) && Directory.Exists(_sessionDir))
                    Directory.Delete(_sessionDir, recursive: true);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Failed to clean session dir: {ex.Message}");
            }
            _sessionKey = null;
            _sessionDir = null;
            CurrentState = State.Idle;
            StateChanged?.Invoke();
        }

        /// <summary>"Done" in the result panel — same cleanup as Discard but the
        /// player has already saved/copied/sent so it's not really a discard.</summary>
        public static void EndReview()
        {
            if (CurrentState != State.Reviewing) return;
            Discard();
        }

        // ─── Internal helpers ────────────────────────────────────────────────

        private static int NextTileIndex()
        {
            int max = -1;
            foreach (var t in _tiles)
                if (t.Index > max) max = t.Index;
            return max + 1;
        }

        private static int FindTileNearTable(Vector3 tablePos)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                var t = _tiles[i];
                float dx = t.TableWorldPos.x - tablePos.x;
                float dz = t.TableWorldPos.z - tablePos.z;
                if (dx * dx + dz * dz <= DedupRadius * DedupRadius)
                    return i;
            }
            return -1;
        }

        // ─── Persistence (Newtonsoft.Json via Thunderstore JsonDotNET) ────────

        private class SessionIndexDto
        {
            public int Version = 1;
            public string SessionKey;
            public List<TileDto> Tiles = new List<TileDto>();
        }

        private class TileDto
        {
            public int Index;
            public string File;
            // Vector2/3 are serialized as float arrays — Newtonsoft serializes
            // UnityEngine.Vector* by exposing every field/property (including
            // .magnitude, .normalized) which produces giant noisy JSON. Arrays
            // are explicit and stable across Unity versions.
            public float[] WorldMin;
            public float[] WorldMax;
            public float[] TablePos;
            public int PixelW;
            public int PixelH;
            public long TimestampMs;
        }

        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
        };

        private static void SaveIndex()
        {
            if (string.IsNullOrEmpty(_sessionDir)) return;
            string sessionFile = MapCompileEnvironment.GetSessionFile(_sessionDir);
            string tmpFile = sessionFile + ".tmp";

            var dto = new SessionIndexDto { SessionKey = _sessionKey };
            foreach (var t in _tiles)
            {
                dto.Tiles.Add(new TileDto
                {
                    Index = t.Index,
                    File = $"tile_{t.Index:D3}.png",
                    WorldMin = new[] { t.WorldMin.x, t.WorldMin.y },
                    WorldMax = new[] { t.WorldMax.x, t.WorldMax.y },
                    TablePos = new[] { t.TableWorldPos.x, t.TableWorldPos.y, t.TableWorldPos.z },
                    PixelW = t.PixelWidth,
                    PixelH = t.PixelHeight,
                    TimestampMs = t.TimestampUnixMs,
                });
            }

            try
            {
                string json = JsonConvert.SerializeObject(dto, _jsonSettings);
                File.WriteAllText(tmpFile, json, Encoding.UTF8);
                if (File.Exists(sessionFile)) File.Delete(sessionFile);
                File.Move(tmpFile, sessionFile);
            }
            catch (Exception ex)
            {
                ModLog.Error($"[NoMapDiscordAdditions] SaveIndex failed: {ex.Message}");
            }
        }

        private static void LoadIndex()
        {
            string sessionFile = MapCompileEnvironment.GetSessionFile(_sessionDir);
            string text = File.ReadAllText(sessionFile, Encoding.UTF8);
            var dto = JsonConvert.DeserializeObject<SessionIndexDto>(text);
            if (dto == null || dto.Tiles == null) return;

            foreach (var d in dto.Tiles)
            {
                if (d.WorldMin == null || d.WorldMin.Length < 2 ||
                    d.WorldMax == null || d.WorldMax.Length < 2 ||
                    d.TablePos == null || d.TablePos.Length < 3)
                {
                    ModLog.Warn($"[NoMapDiscordAdditions] Tile {d.Index} has malformed vector data — skipping.");
                    continue;
                }

                string pngPath = MapCompileEnvironment.GetTileFile(_sessionDir, d.Index);
                if (!File.Exists(pngPath))
                {
                    ModLog.Warn($"[NoMapDiscordAdditions] Tile PNG missing: {pngPath} — skipping.");
                    continue;
                }

                _tiles.Add(new MapCompileTile
                {
                    Index = d.Index,
                    WorldMin = new Vector2(d.WorldMin[0], d.WorldMin[1]),
                    WorldMax = new Vector2(d.WorldMax[0], d.WorldMax[1]),
                    TableWorldPos = new Vector3(d.TablePos[0], d.TablePos[1], d.TablePos[2]),
                    PixelWidth = d.PixelW,
                    PixelHeight = d.PixelH,
                    PngPath = pngPath,
                    TimestampUnixMs = d.TimestampMs,
                });
            }
        }
    }
}
