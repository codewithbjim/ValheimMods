namespace HackAndSap
{
    internal static class AxeUtil
    {
        // Attack.m_attackRange describes the attack cast, not a reliable
        // player-to-collider interaction distance. This strict surface distance
        // keeps the prompt limited to standing directly beside the wood.
        private const float CloseInteractionDistance = 1.1f;

        public static ItemDrop.ItemData GetEquippedAxe(Player player)
        {
            if (player == null) return null;
            ItemDrop.ItemData weapon = player.GetCurrentWeapon();
            return IsUsableAxe(weapon) ? weapon : null;
        }

        public static bool IsUsableAxe(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return false;
            if (item.m_shared.m_skillType != Skills.SkillType.Axes) return false;
            if (item.m_shared.m_damages.m_chop <= 0f) return false;
            if (!item.HavePrimaryAttack()) return false;
            return !item.m_shared.m_useDurability || item.m_durability > 0f;
        }

        public static float GetInteractionReach()
        {
            return System.Math.Min(CloseInteractionDistance,
                System.Math.Max(0.5f, ModConfig.MaximumDistance.Value));
        }
    }
}
