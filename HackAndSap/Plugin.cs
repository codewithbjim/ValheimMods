using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace HackAndSap
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.virtualbjorn.hackandsap";
        public const string PluginName = "HackAndSap";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            ModConfig.Bind(Config);
            ModLog.Init(Logger);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"Hack 'n Sap v{PluginVersion} loaded. Get Hacking.");
        }

        private void Update()
        {
            CuttingSession.Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            CuttingSession.Cancel(false);
            _harmony?.UnpatchSelf();
        }
    }
}
