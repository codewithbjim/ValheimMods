using BepInEx.Logging;

namespace HackAndSap
{
    internal static class ModLog
    {
        private static ManualLogSource _log;

        public static void Init(ManualLogSource log) => _log = log;

        public static void Info(string message)
        {
            if (ModConfig.EnableLogs != null && ModConfig.EnableLogs.Value)
                _log?.LogInfo(message);
        }

        public static void Warn(string message) => _log?.LogWarning(message);
        public static void Error(string message) => _log?.LogError(message);
    }
}
