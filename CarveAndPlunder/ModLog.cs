using BepInEx.Logging;

namespace CarveAndPlunder
{
    // Thin logger wrapper. All messages route through here so the EnableLogs
    // config gate can mute info/warning noise during normal play (errors always
    // print). Mirrors the logging pattern used by the sibling mods.
    internal static class ModLog
    {
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log) => _log = log;

        private static bool Verbose => ModConfig.EnableLogs != null && ModConfig.EnableLogs.Value;

        public static void Info(string msg)
        {
            if (Verbose) _log?.LogInfo(msg);
        }

        public static void Warn(string msg)
        {
            if (Verbose) _log?.LogWarning(msg);
        }

        public static void Error(string msg) => _log?.LogError(msg);

        // Unconditional — used for bring-up diagnostics that must show even when
        // Enable Logs is off. Remove the call sites once the mod is verified.
        public static void Diag(string msg) => _log?.LogInfo("[diag] " + msg);
    }
}
