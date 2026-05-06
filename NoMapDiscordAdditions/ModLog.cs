using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Gated wrapper around UnityEngine.Debug logging. All mod log output goes
    /// through here so a single client config (Plugin.EnableLogs, default off)
    /// silences the entire mod's chatter without touching external library logs.
    /// Drop-in replacement for Debug.Log / LogWarning / LogError calls — same
    /// signatures, just renamed to make the gating explicit at every call site.
    /// </summary>
    internal static class ModLog
    {
        // Defaults to false when the config hasn't been bound yet (e.g. very
        // early startup), matching the published default behavior.
        private static bool Enabled => Plugin.EnableLogs?.Value ?? false;

        public static void Info(string message)
        {
            if (Enabled) Debug.Log(message);
        }

        public static void Warn(string message)
        {
            if (Enabled) Debug.LogWarning(message);
        }

        public static void Error(string message)
        {
            if (Enabled) Debug.LogError(message);
        }
    }
}
