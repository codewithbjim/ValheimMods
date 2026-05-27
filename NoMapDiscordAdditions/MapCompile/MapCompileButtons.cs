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
    ///   Compiling — "ADD TILE (N)" (greyed without an active table), "COMPILE (N)", "CANCEL"
    ///   Reviewing — hidden (result panel takes over)
    /// </summary>
    public static class MapCompileButtons
    {
        private const string ContainerName = "MapCompilePanel";
        private const float BtnHeight = 38f;
        private const float StartBtnWidth = 220f;
        private const float ActionBtnWidth = 160f;
        // Wide enough for the disabled "CLEAR (R-CTRL)" hint label so the
        // button doesn't resize when the modifier is pressed/released.
        private const float ClearBtnWidth = 150f;
        private const float HlgPadVertical = 6f;
        private const float HlgSpacing = 8f;

        private static GameObject _containerObj;
        private static Button _btn1, _btn2, _btn3, _btn4, _btn5;
        private static TextMeshProUGUI _btn1Text, _btn2Text, _btn3Text, _btn4Text, _btn5Text;

        private static bool _composeInProgress;
        // Styled tile captures run a Map Style pipeline off-thread (~hundreds
        // of ms to a second per tile) — long enough for the player to spam
        // ADD TILE if there's no visible feedback. This flag drives the same
        // disable + re-label treatment _composeInProgress does, so every
        // compile-panel button greys out and ADD TILE shows "CAPTURING TILE..."
        // until the in-flight capture finishes.
        private static bool _captureInProgress;

        // Last sampled R-CTRL state for the CLEAR gate. Tracked so the
        // per-frame driver only touches Unity objects when it actually flips.
        private static bool _clearGateHeld;

        // Auto-import the incoming share folder at most once per map-open so a
        // session stays predictable; closing/reopening the large map pulls in
        // anything new dropped since. Reset by SetVisible(false).
        private static bool _autoImportedThisOpen;

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
            _btn4 = MapUI.CreateButton("CompileBtn4", _containerObj.transform,
                ActionBtnWidth, BtnHeight, "", out _btn4Text);
            _btn5 = MapUI.CreateButton("CompileBtn5", _containerObj.transform,
                ClearBtnWidth, BtnHeight, "", out _btn5Text);

            // The CLEAR hold-to-enable gate depends on per-frame key state,
            // not session events (all RefreshLayout reacts to), so it needs a
            // MonoBehaviour ticking every frame the panel is alive.
            _containerObj.AddComponent<ClearGateDriver>();

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
            if (effective)
            {
                TryAutoImport();
                RefreshLayout();
            }
            if (!visible) _autoImportedThisOpen = false;
        }

        // Pull any teammate-shared tiles waiting in the incoming folder into
        // the active session. Only meaningful while Compiling; ScanAndImport
        // itself no-ops otherwise. Runs once per map-open (see guard).
        private static void TryAutoImport()
        {
            if (_autoImportedThisOpen) return;
            if (!ModHelpers.EffectiveConfig.EnableCompileMapSharing) return;
            if (MapCompileSession.CurrentState != MapCompileSession.State.Compiling)
                return;

            _autoImportedThisOpen = true;
            int n = MapTileShare.ScanAndImport();
            if (n > 0)
            {
                string word = n == 1 ? "tile" : "tiles";
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    $"Imported {n} shared {word} ({MapCompileSession.Tiles.Count} total).");
            }
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
            _btn4.onClick.RemoveAllListeners();
            _btn5.onClick.RemoveAllListeners();

            switch (MapCompileSession.CurrentState)
            {
                case MapCompileSession.State.Idle:
                    LayoutIdle();
                    break;
                case MapCompileSession.State.Compiling:
                    LayoutCompiling();
                    break;
                case MapCompileSession.State.Reviewing:
                    // Result panel up (it takes over the screen) or nothing to
                    // restore → keep the compile panel hidden.
                    if (MapCompileResultPanel.IsVisible
                        || !MapCompileResultPanel.HasPendingResult)
                    {
                        _containerObj.SetActive(false);
                        return;
                    }
                    // Map was closed/reopened while reviewing: the result panel
                    // was torn down but the compiled map is preserved. Don't
                    // auto-pop it — offer a single RESUME COMPILE button so the
                    // player chooses when to bring it back.
                    LayoutResumeReview();
                    break;
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
            if (_btn4.gameObject.activeSelf) { w += _btn4.GetComponent<RectTransform>().sizeDelta.x; active++; }
            if (_btn5.gameObject.activeSelf) { w += _btn5.GetComponent<RectTransform>().sizeDelta.x; active++; }
            if (active > 1) w += (active - 1) * HlgSpacing;
            float h = BtnHeight + HlgPadVertical * 2f;
            var containerRect = _containerObj.GetComponent<RectTransform>();
            containerRect.sizeDelta = new Vector2(w, h);
            // Re-anchor — the bottom-left position depends on halfW.
            ApplyAlignment(containerRect);
        }

        // Single button shown when a compile was awaiting review but the result
        // panel was dismissed by closing the map. Clicking it does NOT reopen
        // the (now stale) result panel — it drops straight back into compile
        // mode with every tile intact, so the player can keep adding tables and
        // re-COMPILE when ready. The in-memory compiled PNG is discarded; the
        // on-disk session is untouched and stays resumable.
        private static void LayoutResumeReview()
        {
            int n = MapCompileSession.Tiles.Count;

            _btn1.gameObject.SetActive(true);
            _btn1Text.text = $"RESUME COMPILE ({n})";
            _btn1.GetComponent<RectTransform>().sizeDelta = new Vector2(StartBtnWidth, BtnHeight);
            _btn1.interactable = !_composeInProgress;
            _btn1.onClick.AddListener(() =>
            {
                MapCompileResultPanel.Hide();          // drop the stale compiled PNG
                MapCompileSession.ReturnToCompiling(); // → StateChanged → LayoutCompiling
            });

            _btn2.gameObject.SetActive(false);
            _btn3.gameObject.SetActive(false);
            _btn4.gameObject.SetActive(false);
            ShowClearButton(true);
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

                // A freshly started/resumed session immediately absorbs
                // anything already waiting in the incoming share folder.
                TryAutoImport();
            });

            _btn2.gameObject.SetActive(false);
            _btn3.gameObject.SetActive(false);
            _btn4.gameObject.SetActive(false);
            // Only worth offering when there's a saved session on disk to wipe.
            ShowClearButton(resumable);
        }

        private static void LayoutCompiling()
        {
            int n = MapCompileSession.Tiles.Count;
            bool busy = _composeInProgress || _captureInProgress;

            _btn1.gameObject.SetActive(true);
            // While a capture is in flight, the ADD TILE button doubles as a
            // progress indicator — the label changes and the button greys out
            // along with the rest of the panel.
            _btn1Text.text = _captureInProgress
                ? "CAPTURING TILE..."
                : MapCompileSession.CanAddTile
                    ? (MapCompileSession.ActiveTableAlreadyAdded
                        ? $"UPDATE TILE ({n})"
                        : $"ADD TILE ({n})")
                    : "ADD TILE — go to a table";
            // The progress label is longer than the normal ADD TILE label, so
            // widen the button when it's showing (mirrors the "go to a table"
            // case below).
            float btn1W = _captureInProgress || !MapCompileSession.CanAddTile
                ? 240f
                : ActionBtnWidth;
            _btn1.GetComponent<RectTransform>().sizeDelta = new Vector2(btn1W, BtnHeight);
            _btn1.interactable = MapCompileSession.CanAddTile && !busy;
            _btn1.onClick.AddListener(OnAddTileClicked);

            _btn2.gameObject.SetActive(true);
            _btn2Text.text = $"COMPILE ({n})";
            _btn2.GetComponent<RectTransform>().sizeDelta = new Vector2(ActionBtnWidth, BtnHeight);
            _btn2.interactable = n > 0 && !busy;
            _btn2.onClick.AddListener(OnCompileClicked);

            // SHARE: export every tile (incl. previously-imported) as
            // metadata-embedded PNGs to Discord + the share/out folder.
            // Hidden entirely when tile sharing is disabled (config /
            // server-synced) so compile mode stays purely local.
            bool sharingEnabled = ModHelpers.EffectiveConfig.EnableCompileMapSharing;
            _btn3.gameObject.SetActive(sharingEnabled);
            if (sharingEnabled)
            {
                bool webhookSet = !string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl);
                _btn3Text.text = webhookSet ? $"SHARE ({n})" : $"EXPORT ({n})";
                _btn3.GetComponent<RectTransform>().sizeDelta = new Vector2(ActionBtnWidth, BtnHeight);
                _btn3.interactable = n > 0 && !busy;
                _btn3.onClick.AddListener(OnShareClicked);
            }

            _btn4.gameObject.SetActive(true);
            _btn4Text.text = "CANCEL";
            _btn4.GetComponent<RectTransform>().sizeDelta = new Vector2(110f, BtnHeight);
            _btn4.interactable = !busy;
            _btn4.onClick.AddListener(() => MapCompileSession.Suspend());

            ShowClearButton(true);
        }

        // CLEAR wipes the whole session (in-memory + on-disk) and drops back to
        // Idle. It stays non-interactable unless L-CTRL is held — a destructive
        // action one stray click away from CANCEL, so the modifier is the
        // accident guard. ApplyClearGate (driven per-frame) keeps interactable +
        // label in sync with the live key state.
        private static void ShowClearButton(bool show)
        {
            _btn5.gameObject.SetActive(show);
            if (!show) return;

            _btn5.GetComponent<RectTransform>().sizeDelta = new Vector2(ClearBtnWidth, BtnHeight);
            _btn5.onClick.AddListener(OnClearClicked);
            ApplyClearGate(force: true);
        }

        // Re-evaluate the CLEAR hold-to-enable gate. Cheap enough to call every
        // frame — only touches Unity objects when L-CTRL actually flips (or on
        // a forced re-apply after a layout rebuild).
        private static void ApplyClearGate(bool force = false)
        {
            if (_btn5 == null || !_btn5.gameObject.activeSelf) return;

            bool held = Input.GetKey(KeyCode.LeftControl);
            if (!force && held == _clearGateHeld) return;

            _clearGateHeld = held;
            _btn5.interactable = held && !_composeInProgress && !_captureInProgress;
            _btn5Text.text = held ? "CLEAR" : "CLEAR (L-CTRL)";
        }

        private static void OnClearClicked()
        {
            if (_composeInProgress || _captureInProgress) return;
            // Belt-and-braces: the gate already keeps the button non-interactable
            // without L-CTRL; re-check at click time anyway so a synthetic/queued
            // click can't slip through.
            if (!Input.GetKey(KeyCode.LeftControl)) return;

            MapCompileResultPanel.Hide();      // drop any stale compiled preview
            MapCompileSession.ClearSession();  // → StateChanged → RefreshLayout
        }

        // Per-frame driver for the CLEAR gate (see ApplyClearGate). Lives on the
        // panel container so it dies with it; no-ops while the panel is hidden
        // (ApplyClearGate early-returns on an inactive button).
        private sealed class ClearGateDriver : MonoBehaviour
        {
            private void Update() => ApplyClearGate();
        }

        private static void OnShareClicked()
        {
            if (_composeInProgress || _captureInProgress) return;
            if (MapCompileSession.Tiles.Count == 0)
            {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    "No tiles to share.");
                return;
            }
            Plugin.Instance?.ShareCompileTiles();
        }

        private static void OnAddTileClicked()
        {
            // Re-entry guard: the styled-tile path can take a noticeable
            // moment, and a player will repeat-click ADD TILE if there's
            // no visible feedback. Drop subsequent clicks while a capture
            // (or compose) is in flight — the layout also greys the button,
            // but a button OnClick listener can still fire if it was queued
            // before the layout refresh ran, so guard at the entry point too.
            if (_captureInProgress || _composeInProgress) return;
            if (!MapCompileSession.CanAddTile) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;

            // MapCompileCapture.Capture owns its own WaitForEndOfFrame timing
            // so the live map shader has rendered the current uvRect before
            // we sample it.
            plugin.StartCoroutine(CaptureTileCoroutine());
        }

        private static IEnumerator CaptureTileCoroutine()
        {
            // Snapshot table pos BEFORE the capture yields — the player can
            // walk away from the table mid-frame, but the tile we're about to
            // capture still belongs to where they were when they clicked.
            if (!MapCompileSession.CanAddTile) yield break;
            Vector3 tablePos = MapCompileSession.ActiveTablePos.Value;

            // Flip the in-progress flag immediately so a second click can't
            // start a parallel capture; try/finally guarantees the flag clears
            // even if the coroutine is stopped (map closed, scene change).
            _captureInProgress = true;
            try
            {
                RefreshLayout();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    MapStyleRender.IsStyleActive()
                        ? "Rendering styled tile..."
                        : "Capturing tile...");

                MapCompileCapture.Result? result = null;
                yield return MapCompileCapture.Capture(r => result = r);

                if (result.HasValue)
                {
                    var r = result.Value;
                    MapCompileSession.AddTile(
                        r.Png, r.Width, r.Height,
                        r.WorldMin, r.WorldMax, tablePos,
                        r.FullyMapped);
                }
                else
                {
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Tile capture failed.");
                }
            }
            finally
            {
                _captureInProgress = false;
                RefreshLayout();
            }
        }

        private static void OnCompileClicked()
        {
            if (_composeInProgress || _captureInProgress) return;
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
            // Hardcoded preview cap — 4096 keeps the compile preview snappy to
            // recode for COPY / SEND while still covering large compositions
            // at usable density. SAVE uses ComposeNative which goes up to 8192.
            const int maxDim = 4096;

            // Snapshot the currently visible Minimap pins on the main thread
            // — the compose runs off-thread and can't touch Unity UI. Pins are
            // stamped once on the finished composite (uniform size) instead of
            // being baked into each tile capture.
            var pins = MapCompilePinSnapshot.Capture(out float refScreenW);

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
            done.Dispose();

            if (error != null)
            {
                _composeInProgress = false;
                ModLog.Error($"[NoMapDiscordAdditions] Compose failed: {error}");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compile failed.");
                RefreshLayout();
                yield break;
            }
            if (result == null)
            {
                _composeInProgress = false;
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compile produced no output.");
                RefreshLayout();
                yield break;
            }

            // Stamp pin icons + names (Valheim TMP font, main thread) + encode.
            // tilesSnapshot drives the per-tile pin clip so pins in the empty
            // gaps between non-adjacent tiles don't stamp.
            yield return MapCompileLabelStamp.Finalize(result, pins, tilesSnapshot, refScreenW);

            _composeInProgress = false;

            if (result.PngBytes == null)
            {
                ModLog.Error("[NoMapDiscordAdditions] Compile encode produced no PNG.");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Compile failed.");
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
