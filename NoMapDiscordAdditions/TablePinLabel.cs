using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Renders a "{N}m {DIR} of Spawn" caption beneath every visible cartography
    /// table pin on the large map — but only for the duration of a Copy/Capture
    /// operation, so the labels are baked into the screenshot without polluting
    /// the live UI the rest of the time.
    ///
    /// Design notes:
    /// - One label GameObject per pin, kept in a pool keyed by PinData reference.
    /// - Labels are clones of <c>Minimap.m_pinNamePrefab</c> (same prefab vanilla
    ///   uses), parented to <c>m_pinRootLarge</c>. Inherits Valheim's TMP font
    ///   without referencing a font asset.
    /// - <c>ShowForCapture</c> is called by the screen-capture coroutine just
    ///   before <c>WaitForEndOfFrame</c>; <c>HideAll</c> is called in the finally
    ///   block. Between captures every label sits inactive in the pool.
    /// - "Pin is visible" = vanilla's UpdatePins decided to keep its m_uiElement
    ///   this frame (icon-type filter, viewport, shared-map fade all consulted).
    ///   We piggyback on that — no need to re-derive the predicate.
    /// </summary>
    public static class TablePinLabel
    {
        // ZenMap auto-pins cartography tables as the vanilla "house" icon —
        // enum value 1, which vanilla's enum names Icon1 (ZenMap aliases it as
        // House internally). Filtering on it gives us cartography tables, plus
        // any manual Town pins the player placed (accepted minor cost vs
        // reading ZenMap-private state).
        private const Minimap.PinType TablePinType = Minimap.PinType.Icon1;

        // Vertical pixel offset from the pin anchor. The prefab is laid out so
        // its text sits just under its anchor point already, so this is a small
        // nudge to clear the icon, not a full offset.
        private const float YOffset = -2f;

        private sealed class LabelEntry
        {
            public GameObject Root;
            public TMP_Text Text;
            public RectTransform Rect;
            public string LastText;
        }

        // Pool of labels keyed by the pin they decorate. We never destroy
        // entries; deactivated labels stay in the pool so re-encountering the
        // same pin is allocation-free.
        private static readonly Dictionary<Minimap.PinData, LabelEntry> _labels =
            new Dictionary<Minimap.PinData, LabelEntry>();

        // Scratch list reused each Show/Hide pass to avoid allocating during
        // dictionary mutation. Holds keys whose backing GameObject went null.
        private static readonly List<Minimap.PinData> _toRemove = new List<Minimap.PinData>();

        // Names of every label GameObject we've ever instantiated (current
        // implementation + historical ones from earlier iterations). Used by
        // DestroyOrphans on plugin (re)load to clean up clones that the
        // previous instance left behind in the scene — without this, a
        // BepInEx script-engine hot-reload leaves the old labels frozen
        // under m_pinRootLarge with no manager.
        private static readonly string[] _orphanPrefixes =
        {
            "NoMapDiscord_TablePinLabel",
            "NoMapDiscord_SpawnLabel",
        };

        /// <summary>
        /// Activate a label under every cartography-table pin that vanilla's
        /// UpdatePins is currently rendering (m_uiElement != null). Position
        /// each label by reading the pin's freshly-set anchoredPosition so we
        /// stay glued to the icon under any pan/zoom. Pair every call with
        /// <see cref="HideAll"/> in a finally block.
        /// </summary>
        public static void ShowForCapture()
        {
            // Master gate — server-authoritative when ServerSync is loaded,
            // otherwise the locally-effective value (server override, falling
            // back to the user's own config).
            if (!ModHelpers.EffectiveConfig.EnableCartographyTableLabels) return;

            Minimap mm = Minimap.instance;
            if (mm == null || mm.m_mode != Minimap.MapMode.Large) return;
            if (mm.m_pinNamePrefab == null || mm.m_pinRootLarge == null) return;

            List<Minimap.PinData> pins = mm.m_pins;
            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                Minimap.PinData pin = pins[i];
                if (pin == null || pin.m_type != TablePinType) continue;

                // Vanilla destroys m_uiElement whenever a pin is filtered,
                // offscreen, or hidden by sharedMapDataFade — so a non-null
                // marker is the cheapest, most accurate "visible" check.
                if (pin.m_uiElement == null) continue;

                string label = SpawnDirection.GetLabelForPos(pin.m_pos);
                if (label == null) continue; // too close to spawn → no label

                LabelEntry entry;
                if (!_labels.TryGetValue(pin, out entry) || entry.Root == null)
                {
                    entry = CreateEntry(mm);
                    if (entry == null) continue;
                    _labels[pin] = entry;
                }

                // Reuse the position vanilla just computed for the pin icon.
                Vector2 anchored = pin.m_uiElement.anchoredPosition;
                anchored.y += YOffset;
                entry.Rect.anchoredPosition = anchored;

                if (entry.LastText != label)
                {
                    entry.Text.text = label;
                    entry.LastText = label;
                }

                if (!entry.Root.activeSelf)
                    entry.Root.SetActive(true);
            }
        }

        /// <summary>
        /// Hide every pooled label without destroying them — the pool stays
        /// warm so the next capture is allocation-free.
        /// Also drops entries whose backing GameObject Unity has destroyed.
        /// </summary>
        public static void HideAll()
        {
            _toRemove.Clear();
            foreach (KeyValuePair<Minimap.PinData, LabelEntry> kv in _labels)
            {
                LabelEntry e = kv.Value;
                if (e.Root == null)
                {
                    _toRemove.Add(kv.Key);
                    continue;
                }
                if (e.Root.activeSelf)
                    e.Root.SetActive(false);
            }
            for (int i = 0; i < _toRemove.Count; i++)
                _labels.Remove(_toRemove[i]);
        }

        /// <summary>
        /// Walk every immediate child of <c>m_pinRootLarge</c> and destroy any
        /// whose name matches a label we've ever produced. Call on plugin load
        /// (and on Minimap.Start) to clean up clones leaked by a previous
        /// hot-reloaded instance.
        /// </summary>
        public static void DestroyOrphans()
        {
            Minimap mm = Minimap.instance;
            if (mm == null || mm.m_pinRootLarge == null) return;

            Transform root = mm.m_pinRootLarge.transform;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                GameObject child = root.GetChild(i).gameObject;
                if (HasOrphanPrefix(child.name))
                    Object.Destroy(child);
            }
        }

        private static bool HasOrphanPrefix(string name)
        {
            for (int i = 0; i < _orphanPrefixes.Length; i++)
                if (name.StartsWith(_orphanPrefixes[i], System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static LabelEntry CreateEntry(Minimap mm)
        {
            GameObject go = Object.Instantiate(mm.m_pinNamePrefab, mm.m_pinRootLarge);
            go.name = "NoMapDiscord_TablePinLabel";

            TMP_Text text = go.GetComponentInChildren<TMP_Text>(includeInactive: true);
            RectTransform rect = go.GetComponent<RectTransform>();
            if (text == null || rect == null)
            {
                Object.Destroy(go);
                return null;
            }

            text.fontSize = 16.25f; // 13 * 1.25 — 25% larger than the previous baseline.
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;

            go.SetActive(false);
            return new LabelEntry { Root = go, Text = text, Rect = rect };
        }
    }
}
