using System;
using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;

namespace HackAndSap
{
    internal static class RenewableRegistry
    {
        private static readonly Dictionary<string, GameObject> SaplingsByTree =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, HashSet<string>> TreesByStump =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        public static void Initialize(ZNetScene scene)
        {
            if (scene == null) return;
            SaplingsByTree.Clear();
            TreesByStump.Clear();

            List<GameObject> prefabs = scene.m_prefabs.Where(p => p != null).ToList();
            List<TreeBase> trees = prefabs
                .Select(p => p.GetComponent<TreeBase>())
                .Where(t => t != null && t.m_logPrefab != null)
                .ToList();
            List<Plant> plants = prefabs
                .Select(p => p.GetComponent<Plant>())
                .Where(p => p != null)
                .ToList();
            Plant fallbackPlant = plants.FirstOrDefault();

            foreach (TreeBase tree in trees)
            {
                string treeName = Normalize(tree.gameObject.name);
                if (tree.m_stubPrefab != null)
                    RegisterStump(tree.m_stubPrefab, treeName);

                Plant nativePlant = plants.FirstOrDefault(p =>
                    p.m_grownPrefabs != null && p.m_grownPrefabs.Any(g =>
                        g != null && Normalize(g.name).Equals(treeName, StringComparison.OrdinalIgnoreCase)));
                GameObject sapling = CreateNaturalSapling(tree.gameObject, nativePlant ?? fallbackPlant);
                if (sapling != null) SaplingsByTree[treeName] = sapling;
            }

            _initialized = true;
            ModLog.Info($"Discovered {trees.Count} fellable tree prefabs and " +
                        $"{SaplingsByTree.Count} renewable saplings.");
        }

        private static void RegisterStump(GameObject stump, string treeName)
        {
            string stumpName = Normalize(stump.name);
            if (!TreesByStump.TryGetValue(stumpName, out HashSet<string> sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                TreesByStump[stumpName] = sources;
            }
            sources.Add(treeName);
            if (stump.GetComponent<StumpRenewal>() == null)
                stump.AddComponent<StumpRenewal>();
        }

        private static GameObject CreateNaturalSapling(GameObject matureTree, Plant basePlant)
        {
            if (matureTree == null || basePlant == null) return null;
            string matureName = Normalize(matureTree.name);
            string cloneName = $"HackAndSap_Sapling_{StableId(matureName)}";
            PrefabManager manager = PrefabManager.Instance;
            GameObject clone = manager.GetPrefab(cloneName);
            bool created = clone == null;
            if (created) clone = manager.CreateClonedPrefab(cloneName, basePlant.gameObject);
            if (clone == null) return null;

            clone.name = cloneName;
            Plant plant = clone.GetComponent<Plant>();
            if (plant == null) return null;
            plant.m_name = "Young " + PrettyName(matureName);
            plant.m_needCultivatedGround = false;
            plant.m_destroyIfCantGrow = false;
            plant.m_grownPrefabs = new[] { matureTree };

            if (created) manager.AddPrefab(clone);
            manager.RegisterToZNetScene(clone);
            return clone;
        }

        public static GameObject GetSapling(string treePrefab)
        {
            if (!_initialized || string.IsNullOrEmpty(treePrefab)) return null;
            SaplingsByTree.TryGetValue(Normalize(treePrefab), out GameObject prefab);
            return prefab;
        }

        public static string InferUniqueTree(string stumpPrefab)
        {
            if (!_initialized || string.IsNullOrEmpty(stumpPrefab)) return null;
            if (!TreesByStump.TryGetValue(Normalize(stumpPrefab), out HashSet<string> sources)) return null;
            return sources.Count == 1 ? sources.First() : null;
        }

        public static string GetPrefabName(ZNetView nview, GameObject gameObject)
        {
            if (nview != null && nview.IsValid())
            {
                ZDO zdo = nview.GetZDO();
                GameObject prefab = zdo != null && ZNetScene.instance != null
                    ? ZNetScene.instance.GetPrefab(zdo.GetPrefab())
                    : null;
                if (prefab != null) return Normalize(prefab.name);
            }
            return gameObject != null ? Normalize(gameObject.name) : null;
        }

        public static double GetDayLengthSeconds()
        {
            return EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0
                ? EnvMan.instance.m_dayLengthSec
                : 1800d;
        }

        public static long NowTicks()
        {
            return ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;
        }

        public static bool IsBaseProtected(Vector3 point)
        {
            return ModConfig.ProtectPlayerBases.Value &&
                   EffectArea.IsPointInsideArea(point, EffectArea.Type.PlayerBase, 0f) != null;
        }

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("(Clone)", string.Empty).Trim();
        }

        private static string PrettyName(string value) => value.Replace('_', ' ');

        private static string StableId(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in value)
                    hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;
                return hash.ToString("X8");
            }
        }
    }
}
