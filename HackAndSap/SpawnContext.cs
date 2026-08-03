using UnityEngine;

namespace HackAndSap
{
    internal static class SpawnContext
    {
        public static string CurrentTreePrefab { get; private set; }

        public static void Enter(TreeBase tree)
        {
            ZNetView nview = tree != null ? tree.GetComponent<ZNetView>() : null;
            CurrentTreePrefab = RenewableRegistry.GetPrefabName(nview, tree?.gameObject);
        }

        public static void Exit() => CurrentTreePrefab = null;
    }
}
