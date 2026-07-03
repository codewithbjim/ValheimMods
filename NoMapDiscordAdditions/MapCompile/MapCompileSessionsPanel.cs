using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Modal overlay (opened from the Idle bar's SESSIONS button) listing every
    /// saved compile session for the current world+player, one row each: name,
    /// tile count, last-used date, plus RENAME, OPEN and a hold-L-CTRL DELETE.
    /// The footer's NEW SESSION starts an extra session in the lowest free slot.
    /// Idle-only by design — switching sessions mid-compile would race the
    /// capture/compose coroutines, so the player EXITs first. Same modal shape
    /// as MapCompileManagePanel, minus the thumbnail machinery.
    /// </summary>
    public static class MapCompileSessionsPanel
    {
        private const string ContainerName = "MapCompileSessionsPanel";
        private const float Pad = 12f;

        // Wide enough that the text block keeps a usable span with three
        // action buttons on the row (was 520 with two).
        private const float ListWidth = 600f;
        private const float GridPad = 8f;
        private const float RowSpacing = 8f;
        private const float RowHeight = 56f;
        // Tall enough for ~6 rows; the rest scroll.
        private const float MaxListHeight = 400f;

        // Row internals: text block left, the three actions right.
        private const float RowPad = 8f;
        private const float RowInnerSpacing = 8f;
        private const float RowBtnHeight = 30f;
        private const float RenameBtnWidth = 84f;
        private const float OpenBtnWidth = 80f;
        // Sized for the disabled "DELETE (L-CTRL)" hint label so the button
        // doesn't resize when the modifier is pressed/released.
        private const float DeleteBtnWidth = 150f;

        private const float ButtonHeight = 34f;
        private const float NewBtnWidth = 170f;
        private const float DoneBtnWidth = 110f;

        private static float RowWidth => ListWidth - 2f * GridPad;
        private static float TextWidth =>
            RowWidth - 2f * RowPad - RenameBtnWidth - OpenBtnWidth - DeleteBtnWidth
            - 3f * RowInnerSpacing;

        private static GameObject _containerObj;

        // Live row handles so the per-frame delete gate can re-label/re-enable
        // every DELETE button without a rebuild.
        private sealed class RowUI
        {
            public string DirName;
            public string DisplayName;
            public string CustomName; // null when the row shows the default name
            public Button DeleteBtn;
            public TextMeshProUGUI DeleteLabel;
        }
        private static readonly List<RowUI> _rows = new List<RowUI>();

        // Faint row backing (matches the Manage panel's included-tile tint).
        private static readonly Color RowBg = new Color(1f, 1f, 1f, 0.06f);
        // Red tint for the armed DELETE label — same value the compile bar and
        // Manage panel use for their destructive variants.
        private static readonly Color DangerColor = new Color(1f, 0.42f, 0.36f, 1f);

        // Last sampled L-CTRL state so the per-frame driver only touches Unity
        // objects when the key actually flips (same pattern as the compile
        // bar's ClearGateDriver).
        private static bool _deleteGateHeld;

        public static bool IsVisible => _containerObj != null && _containerObj.activeSelf;

        public static void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }

        public static void Show()
        {
            // Idle-only — the SESSIONS button only exists in the Idle layout,
            // but guard anyway so a stale click can't open it mid-compile.
            if (MapCompileSession.CurrentState != MapCompileSession.State.Idle) return;
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return;
            Hide();

            var entries = MapCompileSessionStore.ListForCurrent();

            // Root + sibling backdrop/panel, same modal shape as the PINS and
            // TILES panels: a click inside the panel must not bubble to the
            // backdrop's close.
            _containerObj = new GameObject(ContainerName);
            _containerObj.transform.SetParent(minimap.m_largeRoot.transform, false);
            var rootRect = _containerObj.AddComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

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

            CreateLabel(panel.transform, "SESSIONS", 22f, ListWidth);
            var hint = CreateLabel(panel.transform,
                "Open or rename a session, or hold L-CTRL to delete one.", 13f, ListWidth);

            float listHeight;
            if (entries.Count == 0)
            {
                var empty = CreateLabel(panel.transform,
                    "No saved sessions.", 15f, ListWidth);
                empty.textWrappingMode = TextWrappingModes.Normal;
                listHeight = 28f;
            }
            else
            {
                float contentH = entries.Count * RowHeight
                    + (entries.Count - 1) * RowSpacing + 2f * GridPad;
                listHeight = Mathf.Min(MaxListHeight, contentH);
                BuildScrollList(panel.transform, listHeight, entries);
            }

            // ── NEW SESSION + DONE row ───────────────────────────────────────
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

            var newBtn = MapUI.CreateButton("NewSession", rowObj.transform,
                NewBtnWidth, ButtonHeight, "NEW SESSION", out _);
            newBtn.onClick.AddListener(OnNewSession);
            var doneBtn = MapUI.CreateButton("Done", rowObj.transform,
                DoneBtnWidth, ButtonHeight, "DONE", out _);
            doneBtn.onClick.AddListener(Hide);
            rowObj.GetComponent<RectTransform>().sizeDelta =
                new Vector2(NewBtnWidth + 8f + DoneBtnWidth, ButtonHeight);

            float totalW = ListWidth + Pad * 2f;
            float totalH = 22f + hint.rectTransform.sizeDelta.y + listHeight + ButtonHeight
                + vlg.spacing * 3f + Pad * 2f;
            panelRect.sizeDelta = new Vector2(totalW, totalH);

            // The DELETE hold-to-enable gate depends on per-frame key state, so
            // it needs a MonoBehaviour ticking while the panel is alive.
            _containerObj.AddComponent<DeleteGateDriver>();
            ApplyDeleteGate(force: true);

            _containerObj.SetActive(true);
        }

        public static void Hide()
        {
            bool wasOpen = _containerObj != null;
            _rows.Clear();
            if (_containerObj != null)
            {
                UnityEngine.Object.Destroy(_containerObj);
                _containerObj = null;
            }
            if (wasOpen) MapCompileButtons.RefreshLayout();
        }

        // Rebuild after a DELETE. If that was the last session, just close —
        // the Idle bar reverts to START COMPILE via Hide's RefreshLayout.
        private static void Rebuild()
        {
            if (MapCompileSessionStore.ListForCurrent().Count == 0)
            {
                Hide();
                return;
            }
            Show();
        }

        // ── Row list ─────────────────────────────────────────────────────────

        private static void BuildScrollList(Transform parent, float listHeight,
            List<MapCompileSessionStore.Entry> entries)
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

            var grid = contentObj.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(RowWidth, RowHeight);
            grid.spacing = new Vector2(RowSpacing, RowSpacing);
            grid.padding = new RectOffset((int)GridPad, (int)GridPad, (int)GridPad, (int)GridPad);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 1;
            var fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            sr.viewport = viewportRect;
            sr.content = contentRect;

            foreach (var entry in entries)
                BuildRow(contentObj.transform, entry);
        }

        // One session row: two-line text block (name + count/date) left,
        // OPEN and DELETE right.
        private static void BuildRow(Transform parent, MapCompileSessionStore.Entry entry)
        {
            var rowObj = new GameObject("Session_" + entry.Ordinal);
            rowObj.transform.SetParent(parent, false);
            rowObj.AddComponent<RectTransform>();
            var bg = rowObj.AddComponent<Image>();
            bg.color = RowBg;

            var hlg = rowObj.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = RowInnerSpacing;
            hlg.padding = new RectOffset((int)RowPad, (int)RowPad, (int)RowPad, (int)RowPad);
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;

            var textObj = new GameObject("Text");
            textObj.SetActive(false); // assign font before TMP.Awake
            textObj.transform.SetParent(rowObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.sizeDelta = new Vector2(TextWidth, RowHeight - 2f * RowPad);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.richText = true;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            MapUI.ApplyValheimFont(tmp);
            tmp.fontSize = 15f;
            tmp.color = MapUI.LabelColor;
            tmp.text = RowText(entry);
            textObj.SetActive(true);

            var renameBtn = MapUI.CreateButton("Rename", rowObj.transform,
                RenameBtnWidth, RowBtnHeight, "RENAME", out var renameLabel);
            renameLabel.fontSize = 13f;

            var openBtn = MapUI.CreateButton("Open", rowObj.transform,
                OpenBtnWidth, RowBtnHeight, "OPEN", out var openLabel);
            openLabel.fontSize = 13f;

            var delBtn = MapUI.CreateButton("Delete", rowObj.transform,
                DeleteBtnWidth, RowBtnHeight, "DELETE (L-CTRL)", out var delLabel);
            delLabel.fontSize = 13f;

            var ui = new RowUI
            {
                DirName = entry.DirName,
                DisplayName = entry.DisplayName,
                CustomName = entry.CustomName,
                DeleteBtn = delBtn,
                DeleteLabel = delLabel,
            };
            _rows.Add(ui);

            renameBtn.onClick.AddListener(() => OnRename(ui));
            openBtn.onClick.AddListener(() => OnOpen(ui));
            delBtn.onClick.AddListener(() => OnDelete(ui));
        }

        // Name on top; count + last-used date beneath, dimmer/smaller via
        // rich-text tags (same style as the Manage panel's tile cards).
        private static string RowText(MapCompileSessionStore.Entry entry)
        {
            string tiles = entry.TileCount == 1 ? "1 tile" : $"{entry.TileCount} tiles";
            if (entry.IncludedCount != entry.TileCount)
                tiles += $" ({entry.IncludedCount} in)";
            string when = entry.LastUsedMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(entry.LastUsedMs)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "—";
            return $"{entry.DisplayName}\n<size=10><color=#C8B48C>{tiles} · {when}</color></size>";
        }

        // ── Actions ──────────────────────────────────────────────────────────

        private static void OnOpen(RowUI row)
        {
            Hide();
            if (MapCompileSession.Resume(row.DirName))
                MapCompileButtons.AfterSessionOpened();
        }

        // RENAME → Valheim's own sign/portal text dialog. While it's open,
        // Minimap.Update skips ALL map key handling (its gate checks
        // TextInput.IsVisible()), so typing 'm'/Escape in the name can't close
        // the map underneath — same protection vanilla pin naming gets. The
        // receiver commits on Enter; Escape cancels without a callback.
        private static void OnRename(RowUI row)
        {
            var input = TextInput.m_instance;
            if (input == null) return;
            input.RequestText(new RenameReceiver(row),
                "Rename session", MapCompileSessionStore.MaxCustomNameLength);
        }

        private sealed class RenameReceiver : TextReceiver
        {
            private readonly string _dirName;
            private readonly string _currentCustomName;

            public RenameReceiver(RowUI row)
            {
                _dirName = row.DirName;
                _currentCustomName = row.CustomName;
            }

            // Prefill with the custom name only — a default-named session
            // starts empty, and submitting empty clears back to "Session N".
            public string GetText() => _currentCustomName ?? "";

            public void SetText(string text)
            {
                if (MapCompileSessionStore.RenameSession(_dirName, text))
                    Rebuild(); // relist so the row shows the new name
            }
        }

        private static void OnDelete(RowUI row)
        {
            // Belt-and-braces: the gate keeps the button non-interactable
            // without L-CTRL; re-check at click time so a queued/synthetic
            // click can't slip through.
            if (!Input.GetKey(KeyCode.LeftControl)) return;
            if (!MapCompileSessionStore.DeleteSession(row.DirName)) return;

            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"{row.DisplayName} deleted.");
            Rebuild();
        }

        private static void OnNewSession()
        {
            Hide();
            MapCompileSession.Start(); // resolves the lowest free slot
            MapCompileButtons.AfterSessionOpened();
        }

        // ── Delete gate ──────────────────────────────────────────────────────

        // Hold-to-enable for every row's DELETE, following the compile bar's
        // "L-CTRL = destructive variant" rule: disabled hint label without the
        // modifier, red armed label with it.
        private static void ApplyDeleteGate(bool force = false)
        {
            bool held = Input.GetKey(KeyCode.LeftControl);
            if (!force && held == _deleteGateHeld) return;
            _deleteGateHeld = held;

            foreach (var row in _rows)
            {
                if (row.DeleteBtn == null) continue;
                row.DeleteBtn.interactable = held;
                row.DeleteLabel.text = held ? "DELETE" : "DELETE (L-CTRL)";
                row.DeleteLabel.color = held ? DangerColor : MapUI.LabelColor;
            }
        }

        // Per-frame driver for the delete gate. Lives on the panel container so
        // it dies with it.
        private sealed class DeleteGateDriver : MonoBehaviour
        {
            private void Update() => ApplyDeleteGate();
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
