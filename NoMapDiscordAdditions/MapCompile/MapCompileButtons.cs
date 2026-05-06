using System.Collections;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Compile-mode button container parented to the large-map root, positioned
    /// at top-center so it's clearly separate from the existing send/copy panel
    /// at bottom. Layout adapts to the current <see cref="MapCompileSession"/>
    /// state:
    ///   Idle      — single button: "START COMPILE" or "RESUME COMPILE (N)"
    ///   Compiling — "ADD TILE (N)" (greyed without an active table), "FINISH (N)", "CANCEL"
    ///   Reviewing — hidden (result panel takes over)
    /// </summary>
    public static class MapCompileButtons
    {
        private const string ContainerName = "MapCompilePanel";
        private const float BtnHeight = 38f;
        private const float StartBtnWidth = 220f;
        private const float ActionBtnWidth = 160f;
        private const float HlgPadVertical = 6f;
        private const float HlgSpacing = 8f;

        private static GameObject _containerObj;
        private static Button _btn1, _btn2, _btn3;
        private static TextMeshProUGUI _btn1Text, _btn2Text, _btn3Text;

        private static bool _composeInProgress;

        public static void Create()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return;

            // Parent to m_largeRoot so we share the bottom row with
            // CaptureButton (which is bottom-right on the same parent). Also
            // sweep any leftover container that an older build may have
            // parented under the canvas root.
            if (minimap.transform.parent != null)
                DestroyExisting(minimap.transform.parent);
            DestroyExisting(minimap.m_largeRoot.transform);
            if (_containerObj != null) return;

            _containerObj = new GameObject(ContainerName);
            _containerObj.transform.SetParent(minimap.m_largeRoot.transform, false);

            var rect = _containerObj.AddComponent<RectTransform>();
            // Pivot at top-center matches CaptureButton — same Y math, just
            // mirrored on X. ApplyAlignment sets the actual anchor + position.
            rect.pivot = new Vector2(0.5f, 1f);

            var bg = _containerObj.AddComponent<Image>();
            MapUI.ApplyPanelBackground(bg, minimap);

            var hlg = _containerObj.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.spacing = HlgSpacing;
            hlg.padding = new RectOffset(8, 8, (int)HlgPadVertical, (int)HlgPadVertical);
            hlg.childAlignment = TextAnchor.MiddleCenter;

            _btn1 = MapUI.CreateButton("CompileBtn1", _containerObj.transform,
                StartBtnWidth, BtnHeight, "", out _btn1Text);
            _btn2 = MapUI.CreateButton("CompileBtn2", _containerObj.transform,
                ActionBtnWidth, BtnHeight, "", out _btn2Text);
            _btn3 = MapUI.CreateButton("CompileBtn3", _containerObj.transform,
                ActionBtnWidth, BtnHeight, "", out _btn3Text);

            MapCompileSession.StateChanged -= RefreshLayout;
            MapCompileSession.StateChanged += RefreshLayout;

            _containerObj.SetActive(false); // shown by SetVisible(true) when map opens
            RefreshLayout();
            ModLog.Info("[NoMapDiscordAdditions] Map compile panel created.");
        }

        /// <summary>Shown only when the large map is open AND compile mode is available.</summary>
        public static void SetVisible(bool visible)
        {
            if (_containerObj == null) return;
            bool effective = visible && MapCompileEnvironment.IsAvailable;
            _containerObj.SetActive(effective);
            if (effective) RefreshLayout();
        }

        // Pin the container to the bottom-left of the large-map root, mirror
        // image of CaptureButton (which sits bottom-right on the same parent).
        // Together the two panels share a single row at the bottom of the map
        // without overlapping. Called after every resize since the anchored
        // position depends on the panel's half-width.
        private static void ApplyAlignment(RectTransform rect)
        {
            if (rect == null) return;
            float halfW = rect.sizeDelta.x * 0.5f;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(8f + halfW, -8f);
        }

        /// <summary>Rebuild button labels + click handlers based on current session state.</summary>
        public static void RefreshLayout()
        {
            if (_containerObj == null) return;
            if (!MapCompileEnvironment.IsAvailable)
            {
                _containerObj.SetActive(false);
                return;
            }

            // Reset all click listeners — we re-bind below per state to keep
            // each handler a closure over the right action.
            _btn1.onClick.RemoveAllListeners();
            _btn2.onClick.RemoveAllListeners();
            _btn3.onClick.RemoveAllListeners();

            switch (MapCompileSession.CurrentState)
            {
                case MapCompileSession.State.Idle:
                    LayoutIdle();
                    break;
                case MapCompileSession.State.Compiling:
                    LayoutCompiling();
                    break;
                case MapCompileSession.State.Reviewing:
                    _containerObj.SetActive(false);
                    return;
            }

            // Re-show the panel in case a previous Reviewing transition deactivated
            // it — RefreshLayout fires on every StateChanged, so this catches the
            // Reviewing → Idle/Compiling return path.
            if (!_containerObj.activeSelf)
                _containerObj.SetActive(Minimap.instance != null
                    && Minimap.instance.m_mode == Minimap.MapMode.Large);

            // Match container width to active children + padding so the
            // background panel isn't oversized when only one button shows.
            float w = 16f; // l/r padding
            int active = 0;
            if (_btn1.gameObject.activeSelf) { w += _btn1.GetComponent<RectTransform>().sizeDelta.x; active++; }
            if (_btn2.gameObject.activeSelf) { w += _btn2.GetComponent<RectTransform>().sizeDelta.x; active++; }
            if (_btn3.gameObject.activeSelf) { w += _btn3.GetComponent<RectTransform>().sizeDelta.x; active++; }
            if (active > 1) w += (active - 1) * HlgSpacing;
            float h = BtnHeight + HlgPadVertical * 2f;
            var containerRect = _containerObj.GetComponent<RectTransform>();
            containerRect.sizeDelta = new Vector2(w, h);
            // Re-anchor — the bottom-left position depends on halfW.
            ApplyAlignment(containerRect);
        }

        private static void LayoutIdle()
        {
            bool resumable = MapCompileSession.HasResumableSession();
            int existingCount = resumable ? CountSavedTiles() : 0;

            _btn1.gameObject.SetActive(true);
            _btn1Text.text = resumable
                ? $"RESUME COMPILE ({existingCount})"
                : "START COMPILE";
            _btn1.GetComponent<RectTransform>().sizeDelta = new Vector2(StartBtnWidth, BtnHeight);
            _btn1.interactable = !_composeInProgress;
            _btn1.onClick.AddListener(() =>
            {
                if (resumable) MapCompileSession.Resume();
                else MapCompileSession.Start();

                // The player almost always clicks START/RESUME while standing
                // at the cartography table they just opened the map from.
                // Capture that table automatically — dedup-by-table-position
                // in AddTile prevents an accidental duplicate when resuming.
                if (MapCompileSession.CanAddTile)
                    OnAddTileClicked();
            });

            _btn2.gameObject.SetActive(false);
            _btn3.gameObject.SetActive(false);
        }

        private static void LayoutCompiling()
        {
            int n = MapCompileSession.Tiles.Count;

            _btn1.gameObject.SetActive(true);
            _btn1Text.text = MapCompileSession.CanAddTile
                ? $"ADD TILE ({n})"
                : "ADD TILE — go to a table";
            _btn1.GetComponent<RectTransform>().sizeDelta =
                new Vector2(MapCompileSession.CanAddTile ? ActionBtnWidth : 240f, BtnHeight);
            _btn1.interactable = MapCompileSession.CanAddTile && !_composeInProgress;
            _btn1.onClick.AddListener(OnAddTileClicked);

            _btn2.gameObject.SetActive(true);
            _btn2Text.text = $"FINISH ({n})";
            _btn2.GetComponent<RectTransform>().sizeDelta = new Vector2(ActionBtnWidth, BtnHeight);
            _btn2.interactable = n > 0 && !_composeInProgress;
            _btn2.onClick.AddListener(OnFinishClicked);

            _btn3.gameObject.SetActive(true);
            _btn3Text.text = "CANCEL";
            _btn3.GetComponent<RectTransform>().sizeDelta = new Vector2(110f, BtnHeight);
            _btn3.interactable = !_composeInProgress;
            _btn3.onClick.AddListener(() => MapCompileSession.Discard());
        }

        private static void OnAddTileClicked()
        {
            if (!MapCompileSession.CanAddTile) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            // Capture happens inside a coroutine because MapCaptureTexture must
            // run after WaitForEndOfFrame for the screen-buffer-dependent path.
            // The texture path used by compile mode doesn't strictly require it,
            // but matching the existing capture flow keeps timing consistent.
            plugin.StartCoroutine(CaptureTileCoroutine());
        }

        private static IEnumerator CaptureTileCoroutine()
        {
            yield return new WaitForEndOfFrame();

            if (!MapCompileSession.CanAddTile) yield break;
            Vector3 tablePos = MapCompileSession.ActiveTablePos.Value;

            if (MapCompileCapture.TryCapture(out var result))
            {
                MapCompileSession.AddTile(
                    result.Png, result.Width, result.Height,
                    result.WorldMin, result.WorldMax, tablePos);
            }
            else
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Tile capture failed.");
            }
        }

        private static void OnFinishClicked()
        {
            if (_composeInProgress) return;
            if (MapCompileSession.Tiles.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "No tiles to compile.");
                return;
            }

            var plugin = Plugin.Instance;
            if (plugin == null) return;
            plugin.StartCoroutine(ComposeCoroutine());
        }

        private static IEnumerator ComposeCoroutine()
        {
            _composeInProgress = true;
            RefreshLayout();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compiling map...");

            // Snapshot tile list — session state may change if Discard runs
            // before this completes (we ignore that and finish anyway).
            var tilesSnapshot = new System.Collections.Generic.List<MapCompileTile>(MapCompileSession.Tiles);
            int maxDim = ModHelpers.EffectiveConfig.CompileMaxDimension;

            MapCompositor.CompiledMap result = null;
            System.Exception error = null;
            var done = new ManualResetEventSlim(false);

            // System.Drawing work is CPU-bound and can take seconds for large
            // sessions — push it to the thread pool so the game doesn't freeze.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { result = MapCompositor.Compose(tilesSnapshot, maxDim); }
                catch (System.Exception ex) { error = ex; }
                finally { done.Set(); }
            });

            while (!done.IsSet) yield return null;

            _composeInProgress = false;

            if (error != null)
            {
                ModLog.Error($"[NoMapDiscordAdditions] Compose failed: {error}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compile failed.");
                RefreshLayout();
                yield break;
            }
            if (result == null)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compile produced no output.");
                RefreshLayout();
                yield break;
            }

            MapCompileSession.Finish();
            MapCompileResultPanel.Show(result);
        }

        private static int CountSavedTiles()
        {
            // Light-weight: count tile_*.png files in the would-be session dir.
            // We don't load the index here — that happens lazily on Resume() to
            // avoid disk I/O every time the panel re-lays out.
            try
            {
                string key = MapCompileEnvironment.GetSessionKey();
                string dir = MapCompileEnvironment.GetSessionDir(key);
                if (!System.IO.Directory.Exists(dir)) return 0;
                return System.IO.Directory.GetFiles(dir, "tile_*.png").Length;
            }
            catch { return 0; }
        }

        private static void DestroyExisting(Transform largeRoot)
        {
            for (int i = largeRoot.childCount - 1; i >= 0; i--)
            {
                var child = largeRoot.GetChild(i);
                if (child != null && child.name == ContainerName)
                    Object.Destroy(child.gameObject);
            }
            if (!_containerObj) _containerObj = null;
        }
    }
}
