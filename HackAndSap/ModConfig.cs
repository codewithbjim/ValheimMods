using BepInEx.Configuration;
using Jotunn.Extensions;

namespace HackAndSap
{
    internal static class ModConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> EnableTreeInteraction;
        public static ConfigEntry<bool> EnableLogInteraction;
        public static ConfigEntry<float> MaximumDistance;
        public static ConfigEntry<float> StumpRenewalDays;
        public static ConfigEntry<float> LogDecayDays;
        public static ConfigEntry<bool> ProtectPlayerBases;
        public static ConfigEntry<bool> EnableLogs;

        public static void Bind(ConfigFile config)
        {
            Enabled = config.BindConfig("General", "Enabled", true,
                "Master switch for Hack 'n Sap gameplay.", synced: true);
            EnableLogs = config.BindConfig("General", "Enable Logs", false,
                "Write detailed discovery and lifecycle messages to the BepInEx log. Client-only.", synced: false);

            EnableTreeInteraction = config.BindConfig("Interaction", "Standing Trees", true,
                "Show the Get Hacking interaction on every discovered tree that produces a felled log.", synced: true);
            EnableLogInteraction = config.BindConfig("Interaction", "Felled Logs", true,
                "Show the Get Hacking interaction on felled logs and split logs.", synced: true);
            MaximumDistance = config.BindConfig("Interaction", "Maximum Distance", 3.2f,
                "Optional upper cap for interaction distance. Hack 'n Sap always requires the player to stand directly beside the tree or log.", synced: true);

            StumpRenewalDays = config.BindConfig("Renewal", "Stump Renewal Days", 5f,
                "In-game days before an unprotected stump becomes a natural sapling. Zero disables renewal.", synced: true);
            LogDecayDays = config.BindConfig("Renewal", "Log Decay Days", 3f,
                "In-game days since a log was spawned or last chopped before it decays away without drops. Zero disables decay.", synced: true);
            ProtectPlayerBases = config.BindConfig("Renewal", "Protect Player Bases", true,
                "Prevent stump renewal and log decay inside a player-base effect area.", synced: true);
        }
    }
}
