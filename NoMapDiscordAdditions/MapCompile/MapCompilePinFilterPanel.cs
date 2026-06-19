using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Modal overlay (opened from the compile panel's PINS button) listing every
    /// distinct pin icon present in the captured tiles, each with a toggle, a
    /// thumbnail and a count. Turning a row off adds its sprite key to
    /// <see cref="MapCompilePinFilter"/>; the next COMPILE skips every pin drawing
    /// that icon. Only pin kinds that actually land inside a captured tile are
    /// listed — the panel mirrors what the composite would stamp, so a mod's
    /// custom pin shows up automatically and a pin in an un-captured region never
    /// clutters the list.
    /// </summary>
    public static class MapCompilePinFilterPanel
    {
        private const string ContainerName = "MapCompilePinFilterPanel";
        private const float Pad = 12f;
        private const float ListWidth = 360f;
        private const float MaxListHeight = 340f;
        private const float ButtonHeight = 34f;

        // Icon tiles laid out as a wrapping grid (flex-wrap). Each tile is a
        // single clickable icon — no checkbox, no label.
        private const float CellSize = 52f;
        private const float CellSpacing = 6f;
        private const float GridPad = 8f;

        private static GameObject _containerObj;

        // Live tiles so ALL / NONE can restyle without a full rebuild.
        private sealed class CellUI
        {
            public string Key;
            public Image Bg;
            public Image Icon;
            public bool Included;
        }
        private static readonly List<CellUI> _cells = new List<CellUI>();

        // Tile background tint: faint highlight when included, dim red when off.
        private static readonly Color BgOn = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color BgOff = new Color(0.6f, 0.12f, 0.12f, 0.35f);

        public static bool IsVisible => _containerObj != null && _containerObj.activeSelf;

        public static void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }

        public static void Show()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return;
            Hide();

            var groups = MapCompilePinFilter.BuildGroups(MapCompileSession.Tiles);

            // Root is a plain full-screen transform; the backdrop and the panel
            // are SIBLINGS under it (not parent/child) so a click inside the
            // panel doesn't bubble up to the backdrop's close handler. The panel
            // is created after the backdrop so it renders on top.
            _containerObj = new GameObject(ContainerName);
            _containerObj.transform.SetParent(minimap.m_largeRoot.transform, false);
            var rootRect = _containerObj.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // ── Full-screen backdrop (modal): blocks clicks to the compile
            // panel behind it; a click outside the panel closes (DONE).
            var backdrop = new GameObject("Backdrop");
            backdrop.transform.SetParent(_containerObj.transform, false);
            var backRect = backdrop.AddComponent<RectTransform>();
            backRect.anchorMin = Vector2.zero;
            backRect.anchorMax = Vector2.one;
            backRect.offsetMin = Vector2.zero;
            backRect.offsetMax = Vector2.zero;
            var backImg = backdrop.AddComponent<Image>();
            backImg.color = new Color(0f, 0f, 0f, 0.5f);
            var backBtn = backdrop.AddComponent<Button>();
            backBtn.transition = Selectable.Transition.None;
            backBtn.onClick.AddListener(Hide);

            // ── Panel ──────────────────────────────────────────────────────────
            var panel = new GameObject("Panel");
            panel.transform.SetParent(_containerObj.transform, false);
            var panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            var panelBg = panel.AddComponent<Image>();
            MapUI.ApplyPanelBackground(panelBg, minimap);
            panelBg.raycastTarget = true; // swallow clicks so they don't hit the backdrop

            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childForceExpandWidth = false;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = false;
            vlg.childControlHeight = false;
            vlg.spacing = 10f;
            vlg.padding = new RectOffset((int)Pad, (int)Pad, (int)Pad, (int)Pad);

            CreateLabel(panel.transform, "INCLUDE PINS", 22f, ListWidth);
            var hint = CreateLabel(panel.transform,
                "Toggle which pins appear on the map.", 13f, ListWidth);

            float listHeight;
            if (groups.Count == 0)
            {
                // No pins in the captured tiles — show a single explanatory row
                // instead of an empty scroll box.
                var empty = CreateLabel(panel.transform,
                    "No pins in the captured tiles.", 15f, ListWidth);
                empty.textWrappingMode = TextWrappingModes.Normal;
                listHeight = 28f;
            }
            else
            {
                // Grid wraps to fit ListWidth; height grows with the row count.
                int cols = Mathf.Max(1,
                    Mathf.FloorToInt((ListWidth - 2f * GridPad + CellSpacing)
                                     / (CellSize + CellSpacing)));
                int rows = Mathf.CeilToInt(groups.Count / (float)cols);
                float contentH = rows * CellSize + (rows - 1) * CellSpacing + 2f * GridPad;
                listHeight = Mathf.Min(MaxListHeight, contentH);
                BuildScrollList(panel.transform, groups, listHeight);
            }

            // ── ALL / NONE / DONE row ────────────────────────────────────────
            var rowObj = new GameObject("Buttons");
            rowObj.transform.SetParent(panel.transform, false);
            rowObj.AddComponent<RectTransform>();
            var rowHlg = rowObj.AddComponent<HorizontalLayoutGroup>();
            rowHlg.spacing = 8f;
            rowHlg.childAlignment = TextAnchor.MiddleCenter;
            rowHlg.childForceExpandWidth = false;
            rowHlg.childForceExpandHeight = false;
            rowHlg.childControlWidth = false;
            rowHlg.childControlHeight = false;

            float wAll = 90f, wNone = 90f, wDone = 110f;
            var allBtn = MapUI.CreateButton("All", rowObj.transform, wAll, ButtonHeight, "ALL", out _);
            var noneBtn = MapUI.CreateButton("None", rowObj.transform, wNone, ButtonHeight, "NONE", out _);
            var doneBtn = MapUI.CreateButton("Done", rowObj.transform, wDone, ButtonHeight, "DONE", out _);
            allBtn.onClick.AddListener(() => SetAll(true));
            noneBtn.onClick.AddListener(() => SetAll(false));
            doneBtn.onClick.AddListener(Hide);
            float rowW = wAll + wNone + wDone + 8f * 2f;
            rowObj.GetComponent<RectTransform>().sizeDelta = new Vector2(rowW, ButtonHeight);
            // Disable ALL/NONE when there's nothing to act on.
            allBtn.interactable = groups.Count > 0;
            noneBtn.interactable = groups.Count > 0;

            float totalW = Mathf.Max(ListWidth, rowW) + Pad * 2f;
            float totalH = 22f + hint.rectTransform.sizeDelta.y + listHeight + ButtonHeight
                + vlg.spacing * 3f + Pad * 2f;
            panelRect.sizeDelta = new Vector2(totalW, totalH);

            _containerObj.SetActive(true);
        }

        public static void Hide()
        {
            bool wasOpen = _containerObj != null;
            _cells.Clear();
            if (_containerObj != null)
            {
                Object.Destroy(_containerObj);
                _containerObj = null;
            }
            // On a real close (DONE / backdrop / map close), persist the
            // selection to the session index so it survives a logoff. Guard on
            // wasOpen so the Show()-time teardown of a non-existent panel doesn't
            // trigger a redundant write.
            if (wasOpen) MapCompileSession.PersistPinFilter();
            // Refresh the compile panel so the PINS button label picks up any
            // newly-hidden count.
            MapCompileButtons.RefreshLayout();
        }

        // ── Icon grid (wrapping) ─────────────────────────────────────────────

        private static void BuildScrollList(Transform parent,
            List<MapCompilePinFilter.PinGroup> groups, float listHeight)
        {
            var scrollObj = new GameObject("ScrollView");
            scrollObj.transform.SetParent(parent, false);
            var scrollRect = scrollObj.AddComponent<RectTransform>();
            scrollRect.sizeDelta = new Vector2(ListWidth, listHeight);
            var scrollBg = scrollObj.AddComponent<Image>();
            scrollBg.color = new Color(0f, 0f, 0f, 0.35f);
            var sr = scrollObj.AddComponent<ScrollRect>();
            sr.horizontal = false;
            sr.vertical = true;
            sr.scrollSensitivity = 24f;
            sr.movementType = ScrollRect.MovementType.Clamped;

            var viewportObj = new GameObject("Viewport");
            viewportObj.transform.SetParent(scrollObj.transform, false);
            var viewportRect = viewportObj.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportObj.AddComponent<RectMask2D>();

            var contentObj = new GameObject("Content");
            contentObj.transform.SetParent(viewportObj.transform, false);
            var contentRect = contentObj.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.sizeDelta = Vector2.zero;
            contentRect.anchoredPosition = Vector2.zero;
            // GridLayoutGroup gives the flex-wrap behavior: fixed-size cells
            // flow left-to-right and wrap to a new row when the width runs out.
            var grid = contentObj.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(CellSize, CellSize);
            grid.spacing = new Vector2(CellSpacing, CellSpacing);
            grid.padding = new RectOffset((int)GridPad, (int)GridPad, (int)GridPad, (int)GridPad);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.Flexible;
            var fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            sr.viewport = viewportRect;
            sr.content = contentRect;

            foreach (var g in groups)
                BuildCell(contentObj.transform, g);
        }

        // One compact icon tile: the pin sprite fills the cell, the cell itself
        // is the click target. Include/exclude state shows in the tile tint +
        // icon opacity — no checkbox or label.
        private static void BuildCell(Transform parent, MapCompilePinFilter.PinGroup g)
        {
            var cellObj = new GameObject("Cell_" + g.Key);
            cellObj.transform.SetParent(parent, false);
            cellObj.AddComponent<RectTransform>();
            var bg = cellObj.AddComponent<Image>();
            var btn = cellObj.AddComponent<Button>();
            btn.targetGraphic = bg;

            // Pin icon, inset a few px so the tile tint frames it.
            var iconObj = new GameObject("Icon");
            iconObj.transform.SetParent(cellObj.transform, false);
            var iconRect = iconObj.AddComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(6f, 6f);
            iconRect.offsetMax = new Vector2(-6f, -6f);
            var iconImg = iconObj.AddComponent<Image>();
            iconImg.sprite = g.Icon;
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;

            var ui = new CellUI
            {
                Key = g.Key,
                Bg = bg,
                Icon = iconImg,
                Included = g.Included,
            };
            _cells.Add(ui);
            btn.onClick.AddListener(() =>
            {
                ui.Included = !ui.Included;
                MapCompilePinFilter.SetIncluded(ui.Key, ui.Included);
                Restyle(ui);
            });
            Restyle(ui);
        }

        // Mirror a tile's include state into its visuals: faint highlight + full
        // icon when included, dim-red tile + faded icon when excluded.
        private static void Restyle(CellUI ui)
        {
            ui.Bg.color = ui.Included ? BgOn : BgOff;
            ui.Icon.color = ui.Included
                ? Color.white
                : new Color(1f, 1f, 1f, 0.25f);
        }

        private static void SetAll(bool included)
        {
            foreach (var ui in _cells)
            {
                ui.Included = included;
                MapCompilePinFilter.SetIncluded(ui.Key, included);
                Restyle(ui);
            }
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string text,
            float size, float width)
        {
            var obj = new GameObject("Label");
            obj.SetActive(false); // assign font before TMP.Awake to avoid the default-font warning
            obj.transform.SetParent(parent, false);
            var rt = obj.AddComponent<RectTransform>();
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            MapUI.ApplyValheimFont(tmp);
            tmp.fontSize = size;
            tmp.color = MapUI.LabelColor;
            rt.sizeDelta = new Vector2(width, size + 8f);
            obj.SetActive(true);
            return tmp;
        }
    }
}
