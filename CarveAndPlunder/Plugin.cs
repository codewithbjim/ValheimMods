using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace CarveAndPlunder
{
    // Carve & Plunder — creature corpses no longer auto-drop loot. You must kneel
    // and work the body (Skin animals with the Knives skill, Loot humanoids with a
    // custom Looting skill) or the loot rots away with the corpse.
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    // Declaring Jotunn as a hard dependency makes Jotunn's SynchronizationManager
    // include this plugin's config file in server -> client sync. Entries bound
    // with synced: true (IsAdminOnly) are pushed from the server and locked on
    // clients; the local value is cached and restored on disconnect.
    [BepInDependency(Jotunn.Main.ModGuid)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.virtualbjorn.carveandplunder";
        public const string PluginName = "CarveAndPlunder";
        public const string PluginVersion = "0.1.0";

        public static Plugin Instance { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            ModConfig.Bind(Config);
            ModLog.Init(Logger);
            LootingSkill.Init();

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll(typeof(Ragdoll_Awake_Patch));
            _harmony.PatchAll(typeof(Ragdoll_SpawnLoot_Patch));
            _harmony.PatchAll(typeof(Ragdoll_DestroyNow_Patch));
            _harmony.PatchAll(typeof(Skills_GetSkillDef_Patch));
            _harmony.PatchAll(typeof(Localization_SetupLanguage_Patch));

            // Host the hold-to-work progress bar (a clone of the native stamina
            // bar) on the plugin's own GameObject.
            gameObject.AddComponent<WorkProgressUI>();

            // Bring-up diagnostic: confirm the patches actually took. If this
            // count is 0 (or omits Ragdoll.Awake / SpawnLoot), the patches
            // aren't applying and nothing downstream will work.
            int patched = 0;
            foreach (var m in _harmony.GetPatchedMethods())
            {
                patched++;
                ModLog.Diag($"patched: {m.DeclaringType?.Name}.{m.Name}");
            }
            ModLog.Diag($"total patched methods: {patched}");

            Logger.LogInfo($"{PluginName} v{PluginVersion} loaded.");
        }

        private void Update()
        {
            // Pump the active skin/loot timer. Cheap no-op when nothing is running.
            CorpseWorkSession.Tick(Time.deltaTime);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
