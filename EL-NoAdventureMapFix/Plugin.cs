using BepInEx;
using HarmonyLib;

namespace EL_NoAdventureMapFix
{
    [BepInPlugin("com.virtualbjorn.el.noadventuremapfix", "EL-NoAdventureMapFix", "1.0.1")]
    [BepInDependency("randyknapp.mods.epicloot")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Harmony.CreateAndPatchAll(typeof(Plugin));
        }

        [HarmonyPatch(typeof(Minimap), "Start")]
        [HarmonyAfter("randyknapp.mods.epicloot")]
        [HarmonyPostfix]
        static void Postfix(Minimap __instance)
        {
            __instance.transform.Find("large/EpicLoot Toggle Container")?.gameObject.SetActive(false);
        }
    }
}