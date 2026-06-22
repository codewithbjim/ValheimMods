using System.Collections.Generic;

namespace CarveAndPlunder
{
    // Knife detection + "sharpness" scoring for the skinning bonus. In Valheim
    // every knife/dagger uses Skills.SkillType.Knives, so that's the reliable
    // discriminator (no name matching needed).
    internal static class KnifeUtil
    {
        public static bool HasKnife(Player player) => BestKnifeSharpness(player) > 0f;

        // Highest physical damage (slash + pierce + blunt, including quality
        // upgrades) among knives in the player's inventory. 0 if none.
        public static float BestKnifeSharpness(Player player)
        {
            if (player == null) return 0f;
            Inventory inv = player.GetInventory();
            if (inv == null) return 0f;

            float best = 0f;
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            foreach (ItemDrop.ItemData item in items)
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_skillType != Skills.SkillType.Knives) continue;

                HitData.DamageTypes dmg = item.GetDamage();
                float sharpness = dmg.m_slash + dmg.m_pierce + dmg.m_blunt;
                if (sharpness > best) best = sharpness;
            }
            return best;
        }
    }
}
