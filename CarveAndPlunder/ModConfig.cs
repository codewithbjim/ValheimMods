using BepInEx.Configuration;
using Jotunn.Extensions;

namespace CarveAndPlunder
{
    // All tunables live here so the gameplay code reads config through one
    // surface. Server-authoritative entries are bound with synced: true — Jotunn's
    // SynchronizationManager pushes the server's value to every client and locks it
    // there (the plugin declares Jotunn as a BepInEx dependency, so its config file
    // is auto-included in sync). Client-only preferences (diagnostics, the hold/press
    // input choice) use synced: false so each player keeps their own.
    internal static class ModConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> EnableLogs;

        // ── Interaction ────────────────────────────────────────────────
        // When true, you must hold the Use key to work a corpse (releasing
        // cancels). When false, a single press starts the work and it runs on its
        // own until done. Client-only input preference.
        public static ConfigEntry<bool> HoldToWork;
        public static ConfigEntry<float> MaxWorkDistance;
        // How long a corpse lingers before it rots away (and its un-worked loot is
        // reduced — see ExpiryLootFraction). Replaces the short vanilla ragdoll
        // TTL so there's time to skin/loot. Humanoid and animal corpses get
        // separate timers.
        public static ConfigEntry<float> HumanoidCorpseLifetime;
        public static ConfigEntry<float> AnimalCorpseLifetime;
        // Fraction of the stashed loot a corpse still drops if it rots away
        // un-worked, instead of losing everything. 0 = nothing, 1 = full.
        public static ConfigEntry<float> ExpiryLootFraction;

        // ── Skinning (animals, Knives skill) ───────────────────────────
        public static ConfigEntry<float> SkinTimeBase;          // seconds at Knives 0 (with a knife)
        public static ConfigEntry<float> SkinTimeReduction;     // seconds removed at Knives 100
        public static ConfigEntry<float> DismantleTime;         // seconds, bare-handed (no knife)
        public static ConfigEntry<float> KnifeBonusReferenceDamage; // knife damage that maps to full bonus
        public static ConfigEntry<int> SkinExtraMax;            // extra items per drop stack at/above reference damage
        public static ConfigEntry<float> KnivesXpPerSkin;

        // ── Looting (humanoids, custom Looting skill) ──────────────────
        public static ConfigEntry<float> LootTimeBase;          // seconds at Looting 0
        public static ConfigEntry<float> LootTimeReduction;     // seconds removed at Looting 100
        public static ConfigEntry<int> LootExtraMax;            // extra items per drop stack at Looting 100
        public static ConfigEntry<float> LootingXpPerLoot;

        // ── Classification ─────────────────────────────────────────────
        // Comma-separated ragdoll/creature base names (no "(Clone)" / "_ragdoll"
        // suffix). Anything matching is "humanoid" (Loot); everything else with a
        // ragdoll is "animal" (Skin).
        public static ConfigEntry<string> HumanoidCreatures;
        // Excluded creatures keep vanilla instant-drop (bosses by default).
        public static ConfigEntry<string> ExcludedCreatures;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.BindConfig("General", "Enabled", true,
                "Master switch. When off, corpses drop loot the vanilla way.",
                synced: true);
            EnableLogs = cfg.BindConfig("General", "Enable Logs", false,
                "Print info/warning diagnostics to the BepInEx console and Player.log. Client-only.",
                synced: false);

            HoldToWork = cfg.BindConfig("Interaction", "Hold To Work", true,
                "When on, hold the Use key to work a corpse (releasing cancels). When off, a single " +
                "Use press starts the work and it continues on its own — press Use again, attack, or " +
                "walk away to cancel. Client-only input preference.",
                synced: false);
            MaxWorkDistance = cfg.BindConfig("Interaction", "Max Work Distance", 3.0f,
                "If you move further than this from the corpse while working, the action cancels.",
                synced: true);
            HumanoidCorpseLifetime = cfg.BindConfig("Interaction", "Humanoid Corpse Lifetime", 300f,
                "Seconds a humanoid corpse lingers before it rots away (un-worked loot is reduced to " +
                "Expiry Loot Fraction). Replaces the short vanilla ragdoll despawn timer.",
                synced: true);
            AnimalCorpseLifetime = cfg.BindConfig("Interaction", "Animal Corpse Lifetime", 60f,
                "Seconds an animal carcass lingers before it rots away (un-worked loot is reduced to " +
                "Expiry Loot Fraction). Replaces the short vanilla ragdoll despawn timer.",
                synced: true);
            ExpiryLootFraction = cfg.BindConfig("Interaction", "Expiry Loot Fraction", 0.5f,
                "Fraction of its loot a corpse still drops if it rots away un-worked (stacks are " +
                "scaled by this, minimum 1 of each item). 0 = drop nothing, 1 = full loot.",
                synced: true);

            SkinTimeBase = cfg.BindConfig("Skinning", "Skin Time Base", 2.0f,
                "Seconds to skin an animal at Knives level 0 (with a knife).",
                synced: true);
            SkinTimeReduction = cfg.BindConfig("Skinning", "Skin Time Reduction", 1.0f,
                "Seconds removed from skin time at Knives level 100 (linear).",
                synced: true);
            DismantleTime = cfg.BindConfig("Skinning", "Dismantle Time", 4.0f,
                "Seconds to dismantle an animal bare-handed (no knife). Knives skill does not help.",
                synced: true);
            KnifeBonusReferenceDamage = cfg.BindConfig("Skinning", "Knife Bonus Reference Damage", 50f,
                "Knife physical damage (slash+pierce+blunt, incl. upgrades) that maps to the full skin bonus.",
                synced: true);
            SkinExtraMax = cfg.BindConfig("Skinning", "Skin Extra Max", 2,
                "Extra items added per drop stack when skinning with a knife at/above the reference damage. 0 = no bonus.",
                synced: true);
            KnivesXpPerSkin = cfg.BindConfig("Skinning", "Knives XP Per Skin", 1.0f,
                "Knives skill XP granted per successful skinning.",
                synced: true);

            LootTimeBase = cfg.BindConfig("Looting", "Loot Time Base", 2.5f,
                "Seconds to loot a humanoid corpse at Looting level 0.",
                synced: true);
            LootTimeReduction = cfg.BindConfig("Looting", "Loot Time Reduction", 1.5f,
                "Seconds removed from loot time at Looting level 100 (linear).",
                synced: true);
            LootExtraMax = cfg.BindConfig("Looting", "Loot Extra Max", 2,
                "Extra items added per drop stack at Looting level 100. 0 = no bonus.",
                synced: true);
            LootingXpPerLoot = cfg.BindConfig("Looting", "Looting XP Per Loot", 1.0f,
                "Looting skill XP granted per successful loot.",
                synced: true);

            HumanoidCreatures = cfg.BindConfig("Classification", "Humanoid Creatures",
                "Greydwarf,Greydwarf_Elite,Greydwarf_Shaman,Skeleton,Skeleton_Poison,Draugr," +
                "Draugr_Elite,Draugr_Ranged,Goblin,GoblinBrute,GoblinShaman,Fuling,Cultist,Dverger",
                "Comma-separated creature base names treated as humanoid (Loot + Looting skill). " +
                "Use the name without the (Clone)/_ragdoll suffix. Everything else with a ragdoll is an animal (Skin).",
                synced: true);
            ExcludedCreatures = cfg.BindConfig("Classification", "Excluded Creatures",
                "Eikthyr,gd_king,Bonemass,Dragon,GoblinKing,SeekerQueen,Fader",
                "Comma-separated creatures that keep vanilla instant-drop (bosses by default).",
                synced: true);
        }
    }
}
