using System;
using System.Collections.Generic;

namespace CarveAndPlunder
{
    public enum CorpseKind
    {
        // Out of scope — keep vanilla instant-drop (bosses, anything excluded,
        // or when the mod is disabled). The SpawnLoot patch never blocks these.
        Passthrough,
        Animal,    // Skin, Knives skill
        Humanoid,  // Loot, Looting skill
    }

    // Decides what a ragdoll is from its prefab name. We can't reach the source
    // Character from a Ragdoll, but the ragdoll GameObject is named after the
    // creature (e.g. "Boar_ragdoll(Clone)"), which is enough to classify.
    internal static class CorpseClassifier
    {
        private static HashSet<string> _humanoids;
        private static HashSet<string> _excluded;
        private static string _humanoidsRaw;
        private static string _excludedRaw;

        public static CorpseKind Classify(string rawName)
        {
            if (ModConfig.Enabled == null || !ModConfig.Enabled.Value)
                return CorpseKind.Passthrough;

            string name = Normalize(rawName);
            RefreshIfNeeded();

            if (_excluded.Contains(name))
                return CorpseKind.Passthrough;
            if (_humanoids.Contains(name))
                return CorpseKind.Humanoid;
            return CorpseKind.Animal;
        }

        // "Boar_ragdoll(Clone)" -> "Boar". Strips the clone marker and the
        // "_ragdoll" / "_ragdoll0" style suffix the death effect appends.
        public static string Normalize(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return string.Empty;
            string s = rawName.Replace("(Clone)", "").Trim();
            int idx = s.IndexOf("_ragdoll", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) s = s.Substring(0, idx);
            return s.Trim();
        }

        // Rebuild the lookup sets only when the config strings change, so live
        // config edits take effect without a restart but we don't re-split on
        // every corpse.
        private static void RefreshIfNeeded()
        {
            string h = ModConfig.HumanoidCreatures?.Value ?? string.Empty;
            string e = ModConfig.ExcludedCreatures?.Value ?? string.Empty;
            if (_humanoids != null && h == _humanoidsRaw && e == _excludedRaw)
                return;

            _humanoids = Parse(h);
            _excluded = Parse(e);
            _humanoidsRaw = h;
            _excludedRaw = e;
        }

        private static HashSet<string> Parse(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in csv.Split(','))
            {
                string t = part.Trim();
                if (t.Length > 0) set.Add(t);
            }
            return set;
        }
    }
}
