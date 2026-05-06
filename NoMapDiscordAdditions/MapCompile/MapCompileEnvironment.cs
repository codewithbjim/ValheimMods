using System.IO;
using BepInEx;
using BepInEx.Bootstrap;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Environment checks and path resolution for Map Compile Mode.
    /// Compile mode is only meaningful when (a) ZenMap is loaded — its per-table
    /// revealed-area behavior is what makes per-tile capture produce different
    /// content per location — and (b) the world is in nomap mode. Without nomap,
    /// every tile would just be a copy of the same fully-explored map.
    /// </summary>
    public static class MapCompileEnvironment
    {
        // GUID confirmed against MinimapPatch.cs ([HarmonyAfter("ZenDragon.ZenMap")]).
        private const string ZenMapGuid = "ZenDragon.ZenMap";

        private const string SessionsDirName  = "compile-sessions";
        private const string CompiledOutDirName = "compiled";

        private static bool? _zenMapPresent;

        // Cached after first call — BepInEx loads plugins exactly once at startup,
        // so a runtime change is impossible without a process restart.
        public static bool IsZenMapLoaded
        {
            get
            {
                if (_zenMapPresent.HasValue) return _zenMapPresent.Value;
                _zenMapPresent = Chainloader.PluginInfos != null
                                 && Chainloader.PluginInfos.ContainsKey(ZenMapGuid);
                return _zenMapPresent.Value;
            }
        }

        // Read the world-modifier global key directly. ZenMap mutates Game.m_noMap
        // (toggles it false during MapLocation.Show, and CheckNoMapMode rewrites it
        // every second when god-mode-map is on), so that flag is unreliable when
        // ZenMap is loaded. The global key is the source of truth — same check
        // ZenMap itself uses via Map.IsNoMapKey.
        public static bool IsNoMap =>
            ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoMap);

        /// <summary>
        /// True when compile-mode UI should be visible right now: ZenMap loaded,
        /// world is nomap, we have a player + ZNet so a session key resolves,
        /// AND the currently-open map is from a cartography table (not a portable
        /// map item — compile mode aggregates per-table revealed regions, so
        /// item-opened maps have no compile semantics).
        /// While `Compiling`, we keep the panel up regardless of source so the
        /// player can see/cancel an in-progress session even if they pop a map
        /// item mid-flow.
        /// </summary>
        public static bool IsAvailable =>
            IsZenMapLoaded && IsNoMap && HasSessionKey() && IsTableOrCompiling;

        private static bool IsTableOrCompiling =>
            MapCompileSession.CurrentState == MapCompileSession.State.Compiling
            || SpawnDirection.ActiveSource == SpawnDirection.Source.CartographyTable;

        public static bool HasSessionKey() =>
            Player.m_localPlayer != null && ZNet.instance != null;

        /// <summary>
        /// Per-world, per-player session key. Two players on the same dedi server
        /// each get their own session directory; the same player across different
        /// worlds also stays separate.
        /// </summary>
        public static string GetSessionKey()
        {
            long worldUid = ZNet.instance.GetWorldUID();
            long playerId = Player.m_localPlayer.GetPlayerID();
            return $"{worldUid:X16}_{playerId:X16}";
        }

        public static string ModConfigRoot  => Path.Combine(Paths.ConfigPath, Plugin.PluginName);
        public static string SessionsRoot   => Path.Combine(ModConfigRoot, SessionsDirName);
        public static string CompiledOutDir => Path.Combine(ModConfigRoot, CompiledOutDirName);

        public static string GetSessionDir(string sessionKey) =>
            Path.Combine(SessionsRoot, sessionKey);

        public static string GetSessionFile(string sessionDir) =>
            Path.Combine(sessionDir, "session.json");

        public static string GetTileFile(string sessionDir, int index) =>
            Path.Combine(sessionDir, $"tile_{index:D3}.png");

        public static void EnsureDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
