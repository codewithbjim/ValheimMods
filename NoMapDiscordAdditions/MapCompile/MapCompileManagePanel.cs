using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Modal overlay (opened from the compile panel's TILES button) listing every
    /// captured tile in the active session, each with a thumbnail, a label +
    /// metadata line, and two per-row actions: REMOVE (delete that tile
    /// outright) and EXCLUDE/INCLUDE (hold it back from — or return it to — the
    /// next COMPILE without deleting it). Unlike the at-the-table UPDATE/REMOVE
    /// TILE controls this can act on ANY tile, including imported ones and tables
    /// the player is nowhere near. Thumbnails are decoded lazily (one per frame)
    /// so a large session doesn't hitch when the panel opens.
    /// </summary>
    public static class MapCompileManagePanel
    {
        private const string ContainerName = "MapCompileManagePanel";
        private const float Pad = 12f;

        // Cards flow in a fixed-column grid so a big session (the player runs
        // ~90 tiles on a server) packs many per screen; the rest scroll. Card
        // width is derived from the column count so cards fill the list width.
        private const int Columns = 5;
        private const float ListWidth = 900f;
        private const float GridPad = 8f;
        private const float CardSpacing = 8f;
        private const float CardHeight = 92f;
        private static float CardWidth =>
            (ListWidth - 2f * GridPad - (Columns - 1) * CardSpacing) / Columns;
        // Tall enough to show ~5 card rows; the rest scroll.
        private const float MaxListHeight = 500f;

        // Card internals: thumbnail + text on top, the two actions across the
        // bottom. Widths derive from CardWidth so they track the column count.
        private const float CardPad = 6f;
        private const float TopRowHeight = 46f;
        private const float ActionRowHeight = 26f;
        private const float CardInnerSpacing = 6f;
        // Thumbnail cell is a fixed box (~16:9, matching a map tile); the tile
        // image is fitted inside it preserving aspect, so it never stretches.
        // Kept small so the narrow 5-column cards still leave room for text.
        private const float ThumbCellW = 64f;
        private const float ThumbCellH = 36f;

        // Per-card action buttons — shorter than the panel's DONE to stay compact.
        private const float RowBtnHeight = 26f;
        private const float ButtonHeight = 34f;

        private static GameObject _containerObj;

        // Live row handles so an EXCLUDE/INCLUDE toggle can restyle in place
        // without a full rebuild (a REMOVE does rebuild, since indices shift).
        private sealed class RowUI
        {
            public int Index;
            public bool Excluded;
            public Image Bg;
            public RawImage Thumb;
            public TextMeshProUGUI ExcludeLabel;
            public string PngPath;
        }
        private static readonly List<RowUI> _rows = new List<RowUI>();

        // Thumbnail textures decoded for the current open; destroyed on Hide.
        private static readonly List<Texture2D> _thumbTextures = new List<Texture2D>();
        // Bumped every Show/Hide so a thumbnail coroutine from a previous open
        // (or a rebuild) stops touching torn-down objects.
        private static int _thumbGen;

        // Row background tint: faint highlight when included, dim red when off.
        private static readonly Color RowOn = new Color(1f, 1f, 1f, 0.06f);
        private static readonly Color RowOff = new Color(0.6f, 0.12f, 0.12f, 0.30f);
        // REMOVE reads as destructive at a glance (matches the compile panel's
        // red destructive-variant labels) without needing an L-CTRL gate — a
        // click in this dedicated management window is already deliberate.
        private static readonly Color RemoveColor = new Color(1f, 0.42f, 0.36f, 1f);

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

            // Root + sibling backdrop/panel, same modal shape as the PINS panel:
            // a click inside the panel must not bubble to the backdrop's close.
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

            var display = DisplayTiles();
            int n = display.Count;
            CreateLabel(panel.transform, $"MANAGE TILES ({n})", 22f, ListWidth);
            var hint = CreateLabel(panel.transform,
                "Remove a tile, or exclude it from the next compile.", 13f, ListWidth);

            float listHeight;
            if (n == 0)
            {
                var empty = CreateLabel(panel.transform,
                    "No tiles captured yet.", 15f, ListWidth);
                empty.textWrappingMode = TextWrappingModes.Normal;
                listHeight = 28f;
            }
            else
            {
                int gridRows = Mathf.CeilToInt(n / (float)Columns);
                float contentH = gridRows * CardHeight + (gridRows - 1) * CardSpacing + 2f * GridPad;
                listHeight = Mathf.Min(MaxListHeight, contentH);
                BuildScrollList(panel.transform, listHeight, display);
            }

            // ── DONE row ─────────────────────────────────────────────────────
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

            float wDone = 110f;
            var doneBtn = MapUI.CreateButton("Done", rowObj.transform, wDone, ButtonHeight, "DONE", out _);
            doneBtn.onClick.AddListener(Hide);
            rowObj.GetComponent<RectTransform>().sizeDelta = new Vector2(wDone, ButtonHeight);

            float totalW = ListWidth + Pad * 2f;
            float totalH = 22f + hint.rectTransform.sizeDelta.y + listHeight + ButtonHeight
                + vlg.spacing * 3f + Pad * 2f;
            panelRect.sizeDelta = new Vector2(totalW, totalH);

            _containerObj.SetActive(true);

            // Kick off lazy thumbnail decoding for the freshly-built rows.
            if (_rows.Count > 0)
            {
                _thumbGen++;
                var plugin = Plugin.Instance;
                if (plugin != null) plugin.StartCoroutine(DecodeThumbnails(_thumbGen));
            }
        }

        public static void Hide()
        {
            bool wasOpen = _containerObj != null;
            _thumbGen++; // cancel any in-flight thumbnail coroutine
            _rows.Clear();
            foreach (var t in _thumbTextures)
                if (t != null) Object.Destroy(t);
            _thumbTextures.Clear();
            if (_containerObj != null)
            {
                Object.Destroy(_containerObj);
                _containerObj = null;
            }
            if (wasOpen) MapCompileButtons.RefreshLayout();
        }

        // ── Rebuild after a REMOVE: indices shift, so rebuild the whole list
        // rather than patching one row. If that was the last tile, just close —
        // there's nothing left to manage (and the TILES button greys out).
        private static void Rebuild()
        {
            if (MapCompileSession.Tiles.Count == 0)
            {
                Hide();
                return;
            }
            Show();
        }

        // ─────────────────────────────────────────────────────────────────────
        // TEMP DEBUG — remove before release. Repeats the real captured tiles up
        // to DebugPadTiles so the grid/scroll can be eyeballed at server scale
        // (~90 tiles) without actually capturing that many. Set DebugPadTiles = 0
        // to disable. Duplicates reference the SAME MapCompileTile, so REMOVE /
        // EXCLUDE on a padded card acts on the underlying real tile.
        private const int DebugPadTiles = 96;

        private static List<MapCompileTile> DisplayTiles()
        {
            var real = MapCompileSession.Tiles;
            var list = new List<MapCompileTile>(real);
            if (DebugPadTiles > 0 && real.Count > 0)
            {
                int i = 0;
                while (list.Count < DebugPadTiles)
                    list.Add(real[i++ % real.Count]);
            }
            return list;
        }
        // ─────────────────────────────────────────────────────────────────────

        // ── Row list ─────────────────────────────────────────────────────────

        private static void BuildScrollList(Transform parent, float listHeight,
            List<MapCompileTile> tiles)
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
            grid.cellSize = new Vector2(CardWidth, CardHeight);
            grid.spacing = new Vector2(CardSpacing, CardSpacing);
            grid.padding = new RectOffset((int)GridPad, (int)GridPad, (int)GridPad, (int)GridPad);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Columns;
            var fitter = contentObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            sr.viewport = viewportRect;
            sr.content = contentRect;

            foreach (var tile in tiles)
                BuildCard(contentObj.transform, tile);
        }

        // One tile card: thumbnail + text across the top, the two actions
        // (EXCLUDE/INCLUDE + REMOVE) across the bottom. The GridLayoutGroup sets
        // the card's outer size; an inner VerticalLayoutGroup stacks the two
        // rows, each sized by a LayoutElement.
        private static void BuildCard(Transform parent, MapCompileTile tile)
        {
            float innerW = CardWidth - 2f * CardPad;

            var cardObj = new GameObject("Card_" + tile.Index);
            cardObj.transform.SetParent(parent, false);
            cardObj.AddComponent<RectTransform>();
            var bg = cardObj.AddComponent<Image>();

            var cardVlg = cardObj.AddComponent<VerticalLayoutGroup>();
            cardVlg.spacing = CardInnerSpacing;
            cardVlg.padding = new RectOffset((int)CardPad, (int)CardPad, (int)CardPad, (int)CardPad);
            cardVlg.childAlignment = TextAnchor.UpperLeft;
            cardVlg.childControlWidth = true;
            cardVlg.childControlHeight = true;
            cardVlg.childForceExpandWidth = true;
            cardVlg.childForceExpandHeight = false;

            // ── Top: thumbnail + text ────────────────────────────────────────
            var topObj = new GameObject("Top");
            topObj.transform.SetParent(cardObj.transform, false);
            topObj.AddComponent<RectTransform>();
            var topLe = topObj.AddComponent<LayoutElement>();
            topLe.preferredHeight = TopRowHeight;
            topLe.minHeight = TopRowHeight;
            var topHlg = topObj.AddComponent<HorizontalLayoutGroup>();
            topHlg.spacing = CardInnerSpacing;
            topHlg.childAlignment = TextAnchor.MiddleLeft;
            topHlg.childForceExpandWidth = false;
            topHlg.childForceExpandHeight = false;
            topHlg.childControlWidth = false;
            topHlg.childControlHeight = false;

            // Thumbnail: a fixed-size cell (participates in layout) with a dark
            // backing, plus an inner image sized to the tile's aspect at decode
            // time so the map never stretches. The inner image is NOT in a
            // layout group, so resizing it doesn't disturb the card.
            var thumbCell = new GameObject("Thumb");
            thumbCell.transform.SetParent(topObj.transform, false);
            var thumbCellRect = thumbCell.AddComponent<RectTransform>();
            thumbCellRect.sizeDelta = new Vector2(ThumbCellW, ThumbCellH);
            var cellBg = thumbCell.AddComponent<Image>();
            cellBg.color = new Color(0f, 0f, 0f, 0.45f);
            cellBg.raycastTarget = false;

            var thumbImgObj = new GameObject("ThumbImg");
            thumbImgObj.transform.SetParent(thumbCell.transform, false);
            var thumbRect = thumbImgObj.AddComponent<RectTransform>();
            thumbRect.anchorMin = new Vector2(0.5f, 0.5f);
            thumbRect.anchorMax = new Vector2(0.5f, 0.5f);
            thumbRect.pivot = new Vector2(0.5f, 0.5f);
            thumbRect.anchoredPosition = Vector2.zero;
            thumbRect.sizeDelta = new Vector2(ThumbCellW, ThumbCellH);
            var thumb = thumbImgObj.AddComponent<RawImage>();
            thumb.color = new Color(1f, 1f, 1f, 0f); // transparent until textured
            thumb.raycastTarget = false;

            // Text block: label on top, metadata beneath.
            var textObj = new GameObject("Text");
            textObj.SetActive(false); // assign font before TMP.Awake
            textObj.transform.SetParent(topObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.sizeDelta = new Vector2(innerW - ThumbCellW - CardInnerSpacing, TopRowHeight);
            var tmp = textObj.AddComponent<TextMeshProUGUI>();
            tmp.richText = true;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            MapUI.ApplyValheimFont(tmp);
            tmp.fontSize = 13f;
            tmp.color = MapUI.LabelColor;
            tmp.text = RowText(tile);
            textObj.SetActive(true);

            // ── Bottom: EXCLUDE/INCLUDE + REMOVE, side by side, full width ───
            var actionObj = new GameObject("Actions");
            actionObj.transform.SetParent(cardObj.transform, false);
            actionObj.AddComponent<RectTransform>();
            var actionLe = actionObj.AddComponent<LayoutElement>();
            actionLe.preferredHeight = ActionRowHeight;
            actionLe.minHeight = ActionRowHeight;
            var actionHlg = actionObj.AddComponent<HorizontalLayoutGroup>();
            actionHlg.spacing = CardInnerSpacing;
            actionHlg.childAlignment = TextAnchor.MiddleCenter;
            actionHlg.childForceExpandWidth = false;
            actionHlg.childForceExpandHeight = false;
            actionHlg.childControlWidth = false;
            actionHlg.childControlHeight = false;

            float actionBtnW = (innerW - CardInnerSpacing) / 2f;

            // EXCLUDE / INCLUDE toggle.
            var exBtn = MapUI.CreateButton("Exclude", actionObj.transform,
                actionBtnW, RowBtnHeight, tile.ExcludedFromCompile ? "INCLUDE" : "EXCLUDE",
                out var exLabel);
            exLabel.fontSize = 12f;

            // REMOVE (red label, direct click).
            var rmBtn = MapUI.CreateButton("Remove", actionObj.transform,
                actionBtnW, RowBtnHeight, "REMOVE", out var rmLabel);
            rmLabel.fontSize = 12f;
            rmLabel.color = RemoveColor;

            var ui = new RowUI
            {
                Index = tile.Index,
                Excluded = tile.ExcludedFromCompile,
                Bg = bg,
                Thumb = thumb,
                ExcludeLabel = exLabel,
                PngPath = tile.PngPath,
            };
            _rows.Add(ui);

            exBtn.onClick.AddListener(() =>
            {
                ui.Excluded = !ui.Excluded;
                MapCompileSession.SetTileExcluded(ui.Index, ui.Excluded);
                ui.ExcludeLabel.text = ui.Excluded ? "INCLUDE" : "EXCLUDE";
                Restyle(ui);
            });
            rmBtn.onClick.AddListener(() =>
            {
                MapCompileSession.RemoveTile(ui.Index);
                Rebuild();
            });

            Restyle(ui);
        }

        // Label + metadata line for a tile: a named table wins, then an imported
        // tile shows its source, else a 1-based ordinal. The sub-line carries
        // world-center coords plus partial/imported badges. Pixel size is
        // omitted — it's the same across a session's tiles and the cards are
        // narrow; the thumbnail conveys far more.
        private static string RowText(MapCompileTile tile)
        {
            string label =
                !string.IsNullOrEmpty(tile.TableName) ? tile.TableName
                : tile.IsImported ? $"From {tile.SourcePlayer}"
                : $"Tile {tile.Index + 1}";

            Vector2 c = (tile.WorldMin + tile.WorldMax) * 0.5f;
            string sub = $"({c.x:0}, {c.y:0})";
            if (!tile.FullyMapped) sub += "  · partial";
            if (tile.IsImported) sub += "  ◆";

            // Second line dimmer/smaller via rich-text tags on the same TMP.
            return $"{label}\n<size=10><color=#C8B48C>{sub}</color></size>";
        }

        // Mirror a row's excluded state into its visuals: faint tile + full
        // thumbnail when included, dim-red tile + faded thumbnail when excluded.
        // The thumbnail image is transparent until DecodeThumbnails supplies a
        // texture (the cell's dark backing shows through as the placeholder).
        private static void Restyle(RowUI ui)
        {
            ui.Bg.color = ui.Excluded ? RowOff : RowOn;
            if (ui.Thumb.texture == null)
                ui.Thumb.color = new Color(1f, 1f, 1f, 0f);
            else
                ui.Thumb.color = ui.Excluded ? new Color(1f, 1f, 1f, 0.30f) : Color.white;
        }

        // ── Lazy thumbnails ──────────────────────────────────────────────────

        // Decode one tile PNG per frame (downscaled) so opening a large session
        // doesn't stall. Bails the moment the generation token moves (panel
        // hidden or rebuilt) so it never writes into destroyed rows.
        private static IEnumerator DecodeThumbnails(int gen)
        {
            // Snapshot the row list — Rebuild()/Hide() replace _rows wholesale.
            var rows = new List<RowUI>(_rows);
            foreach (var ui in rows)
            {
                if (gen != _thumbGen) yield break;
                yield return null;
                if (gen != _thumbGen) yield break;
                if (ui.Thumb == null) continue;

                Texture2D tex = null;
                try { tex = DecodeThumbnail(ui.PngPath, (int)(ThumbCellW * 2f)); }
                catch (System.Exception ex)
                {
                    ModLog.Warn($"[NoMapDiscordAdditions] Thumbnail decode failed: {ex.Message}");
                }
                if (tex == null) continue;
                if (gen != _thumbGen) { Object.Destroy(tex); yield break; }

                _thumbTextures.Add(tex);
                ui.Thumb.texture = tex;

                // Fit the tile image inside the cell preserving aspect (letterbox
                // on whichever axis is proportionally shorter) so it never stretches.
                float aspect = tex.width / (float)Mathf.Max(1, tex.height);
                float w, h;
                if (aspect >= ThumbCellW / ThumbCellH) { w = ThumbCellW; h = ThumbCellW / aspect; }
                else                                   { h = ThumbCellH; w = ThumbCellH * aspect; }
                ui.Thumb.rectTransform.sizeDelta = new Vector2(w, h);

                Restyle(ui); // now that a texture exists, drop the placeholder transparency
            }
        }

        // Decode a tile PNG to a small Texture2D via System.Drawing (the same
        // dependency the compositor and result preview use — avoids Unity's
        // ImageConversion ReadOnlySpan overload that net481's mscorlib lacks).
        // Downscales during decode so only a thumbnail-sized bitmap is copied.
        private static Texture2D DecodeThumbnail(string path, int maxSize)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;

            using (var src = new System.Drawing.Bitmap(path))
            {
                int sw = src.Width, sh = src.Height;
                if (sw <= 0 || sh <= 0) return null;
                float scale = Mathf.Min((float)maxSize / sw, (float)maxSize / sh, 1f);
                int tw = Mathf.Max(1, Mathf.RoundToInt(sw * scale));
                int th = Mathf.Max(1, Mathf.RoundToInt(sh * scale));

                using (var dst = new System.Drawing.Bitmap(tw, th,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(dst))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(src, 0, 0, tw, th);
                    }
                    return BitmapToTexture(dst);
                }
            }
        }

        // GDI gives BGRA top-down; Texture2D wants RGBA bottom-up. Mirrors the
        // conversion in MapCompileResultPanel.DecodePngToTexture.
        private static Texture2D BitmapToTexture(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var pixels = new Color32[w * h];
            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            var bd = bmp.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                int srcStride = bd.Stride;
                byte[] row = new byte[srcStride];
                for (int y = 0; y < h; y++)
                {
                    var rowPtr = new System.IntPtr(bd.Scan0.ToInt64() + (long)y * srcStride);
                    System.Runtime.InteropServices.Marshal.Copy(rowPtr, row, 0, srcStride);
                    int dstY = h - 1 - y;
                    int dstRow = dstY * w;
                    for (int x = 0; x < w; x++)
                    {
                        int si = x * 4;
                        pixels[dstRow + x] = new Color32(
                            row[si + 2], row[si + 1], row[si], row[si + 3]);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bd);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
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
