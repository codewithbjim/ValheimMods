using HarmonyLib;

namespace CarveAndPlunder
{
    // Makes Valheim's skill system aware of the custom Looting skill. Skills.GetSkill
    // calls GetSkillDef to build a Skill when the player first uses one, so supplying
    // our SkillDef here is enough for RaiseSkill / GetSkillFactor / the skills UI to
    // work. (When SkillManager replaces LootingSkill, this patch is removed.)
    [HarmonyPatch(typeof(Skills), nameof(Skills.GetSkillDef))]
    internal static class Skills_GetSkillDef_Patch
    {
        private static void Postfix(Skills.SkillType type, ref Skills.SkillDef __result)
        {
            if (__result == null && type == LootingSkill.SkillType)
                __result = LootingSkill.Def;
        }
    }

    // Registers the "Looting" display name. The skills UI localizes the name as
    // "$skill_<hash>"; without a word for that key it renders the raw token
    // ("[skill_478747128]"). SetupLanguage rebuilds the translation table on every
    // language load, so we re-add the word here to keep it present.
    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    internal static class Localization_SetupLanguage_Patch
    {
        private static void Postfix(Localization __instance)
        {
            LootingSkill.RegisterLocalization(__instance);
        }
    }
}
