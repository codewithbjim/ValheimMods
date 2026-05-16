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

        /// <summary>
        /// True while standing at a table whose position already matches an
        /// existing (non-imported) tile within <see cref="DedupRadius"/> — i.e.
        /// clicking ADD TILE will replace that tile in place rather than append
        /// a new one. Lets the UI label the button "UPDATE TILE" instead of
        /// "ADD TILE", which matters most on a resumed session where the tiles
        /// were loaded from disk.
        /// </summary>
        public static bool ActiveTableAlreadyAdded =>
            CanAddTile && FindTileNearTable(ActiveTablePos.Value) >= 0;

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
            Vector2 worldMin, Vector2 worldMax, Vector3 tablePos,
            bool fullyMapped = true)
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
                TableName = TablePinName.Resolve(tablePos),
                PixelWidth = width,
                PixelHeight = height,
                PngPath = pngPath,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FullyMapped = fullyMapped,
            };
            _tiles.Add(tile);
            SaveIndex();
            StateChanged?.Invoke();

            string verb = existing >= 0 ? "updated" : "added";
            string partial = fullyMapped ? "" : " — partial map";
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                $"Tile {verb} ({_tiles.Count} total){partial}");
        }

        /// <summary>
        /// Adds (or replaces) a tile received from another player's share.
        /// Unlike <see cref="AddTile"/> this dedups by <paramref name="importKey"/>
        /// (stable per shared tile) rather than by table position, so the same
        /// shared tile re-appearing in the incoming folder updates in place
        /// instead of stacking. Returns true if a session was active and the
        /// tile was stored.
        /// </summary>
        public static bool AddImportedTile(byte[] pngBytes, int width, int height,
            Vector2 worldMin, Vector2 worldMax, string importKey, string sourcePlayer,
            bool fullyMapped = true)
        {
            if (CurrentState != State.Compiling) return false;
            if (pngBytes == null || pngBytes.Length == 0) return false;
            if (string.IsNullOrEmpty(importKey)) return false;
            if (string.IsNullOrEmpty(_sessionDir)) return false;

            int existing = FindTileByImportKey(importKey);
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
                ModLog.Error($"[NoMapDiscordAdditions] Imported tile write failed: {ex.Message}");
                return false;
            }

            // TableWorldPos is irrelevant for imported tiles; a far sentinel
            // keeps FindTileNearTable's distance check from ever matching it
            // (it also filters IsImported, this is belt-and-braces).
            _tiles.Add(new MapCompileTile
            {
                Index = index,
                WorldMin = worldMin,
                WorldMax = worldMax,
                TableWorldPos = new Vector3(1e9f, 0f, 1e9f),
                PixelWidth = width,
                PixelHeight = height,
                PngPath = pngPath,
                TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FullyMapped = fullyMapped,
                ImportKey = importKey,
                SourcePlayer = string.IsNullOrEmpty(sourcePlayer) ? "unknown" : sourcePlayer,
            });
            SaveIndex();
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Move from Compiling to Reviewing. Tile data is preserved on disk and
        /// survives Save / Copy / Send / Done / Cancel — only the explicit
        /// DISCARD action in the result panel ever wipes it, so the session can
        /// be resumed at any later table or after a game restart.
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

        /// <summary>
        /// Exit compile mode without destroying anything. Tiles stay on disk so
        /// the player can RESUME later — at the next table or after a game
        /// restart. This is what the CANCEL button does. If the session has no
        /// tiles there is nothing worth resuming, so the empty session folder is
        /// cleaned up like a Discard to avoid a phantom "RESUME COMPILE (0)".
        /// </summary>
        public static void Suspend()
        {
            if (CurrentState == State.Idle) return;

            if (_tiles.Count == 0)
            {
                Discard();
                return;
            }

            _tiles.Clear();
            _sessionKey = null;
            _sessionDir = null;
            CurrentState = State.Idle;
            StateChanged?.Invoke();
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center, "Compile session saved — resume any time.");
        }

        /// <summary>Discard everything (explicit DISCARD action in result panel).</summary>
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

        /// <summary>
        /// "Done" in the result panel. The player has saved/copied/sent (or just
        /// wants to keep mapping); drop back to Compiling with every tile still
        /// in memory and on disk. The player can add more tiles at the next
        /// table, or close the game and RESUME later — nothing is wiped here.
        /// </summary>
        public static void ReturnToCompiling()
        {
            if (CurrentState != State.Reviewing) return;
            CurrentState = State.Compiling;
            StateChanged?.Invoke();
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center, "Compile session kept — resume any time.");
        }

        // ─── Internal helpers ────────────────────────────────────────────────

        private static int NextTileIndex()
        {
            int max = -1;
            foreach (var t in _tiles)
                if (t.Index > max) max = t.Index;
            return max + 1;
        }

        private static int FindTileByImportKey(string importKey)
        {
            for (int i = 0; i < _tiles.Count; i++)
                if (_tiles[i].ImportKey == importKey) return i;
            return -1;
        }

        private static int FindTileNearTable(Vector3 tablePos)
        {
            for (int i = 0; i < _tiles.Count; i++)
            {
                var t = _tiles[i];
                if (t.IsImported) continue; // imported tiles dedup by key, not position
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
            // NullValueHandling.Ignore keeps this out of the JSON for unnamed
            // tables, so sessions written by older builds load unchanged.
            public string TableName;
            public int PixelW;
            public int PixelH;
            public long TimestampMs;
            // true when absent (older sessions) → loaded as a complete tile.
            public bool FullyMapped = true;
            // Present only for imported tiles. NullValueHandling.Ignore keeps
            // them out of the JSON for ordinary table captures, so sessions
            // written by older builds load unchanged.
            public string ImportKey;
            public string Src;
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
                    TableName = t.TableName,
                    PixelW = t.PixelWidth,
                    PixelH = t.PixelHeight,
                    TimestampMs = t.TimestampUnixMs,
                    FullyMapped = t.FullyMapped,
                    ImportKey = t.ImportKey,
                    Src = t.SourcePlayer,
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
                    TableName = d.TableName,
                    PixelWidth = d.PixelW,
                    PixelHeight = d.PixelH,
                    PngPath = pngPath,
                    TimestampUnixMs = d.TimestampMs,
                    FullyMapped = d.FullyMapped,
                    ImportKey = d.ImportKey,
                    SourcePlayer = d.Src,
                });
            }
        }
    }
}
