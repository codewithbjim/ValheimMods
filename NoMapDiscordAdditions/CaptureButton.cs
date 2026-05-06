using NoMapDiscordAdditions.MapCompile;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions
{
    public static class CaptureButton
    {
        private static GameObject _containerObj;
        private static Button _captureBtn;
        private static TextMeshProUGUI _captureBtnText;
        private static Button _clipboardBtn;
        private static TextMeshProUGUI _clipboardBtnText;
        private static GameObject _biomeToggleObj;        // cloned SharedPanel root
        private static Toggle _biomeToggle;               // Toggle component within the clone
        private static Image _biomeToggleCheckmark;       // checkmark dot for active-state tinting
        private static float _biomeToggleWidth  = 130f;  // read from clone at Create() time
        // Forced to BtnHeight at Create() time so the toggle matches the
        // adjacent buttons (and the bottom-row MapCompile panel).
        private static float _biomeToggleHeight = BtnHeight;

        private const string ContainerName = "CaptureButtonPanel";

        // Layout: [Show Biome Text toggle] [SEND MAP (F10)?] [COPY MAP (F11)]
        // Base width  = 8 + _biomeToggleWidth + HlgSpacing + BtnWidth + 8
        // +SEND MAP   = + BtnWidth + HlgSpacing
        // BtnWidth is sized to fit "SEND MAP (F10)" on one line at the
        // Valheim-AveriaSerifLibre font's natural 16pt rendering.
        private const float BtnWidth        = 180f;
        private const float BtnHeight       = 38f;
        // ContainerHeight is derived at runtime: Mathf.Max(toggle, button) + top/bottom padding.
        private const float HlgPadVertical = 6f;
        private const float HlgSpacing      = 8f;

        /// <summary>
        /// Creates the button container on the large map.
        /// Call once after Minimap.Start (with HarmonyAfter ZenMap).
        /// </summary>
        public static void Create()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null)
                return;

            Transform largeRoot = minimap.m_largeRoot.transform;
            DestroyExisting(largeRoot);

            if (_containerObj != null)
                return;

            _containerObj = new GameObject(ContainerName);
            _containerObj.transform.SetParent(largeRoot, false);

            var rect = _containerObj.AddComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 1f);
            float containerH = Mathf.Max(_biomeToggleHeight, BtnHeight) + HlgPadVertical * 2f;
            rect.sizeDelta = new Vector2(8f + _biomeToggleWidth + HlgSpacing + BtnWidth + 8f, containerH);
            ApplyAlignment(rect);

            var panelBg = _containerObj.AddComponent<Image>();
            MapUI.ApplyPanelBackground(panelBg, minimap);

            var hlg = _containerObj.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.spacing = 8f;
            hlg.padding = new RectOffset(8, 8, 6, 6);

            // Clone the SharedPanel toggle that Valheim uses for pin filters so we get
            // the native styling (checkbox, checkmark, label, sound effects) for free.
            // Added first so it appears to the left of the action buttons in the HLG.
            var sharedPanelT = Utils.FindChild(minimap.transform, "SharedPanel");
            if (sharedPanelT != null)
            {
                _biomeToggleObj = Object.Instantiate(sharedPanelT.gameObject, _containerObj.transform);
                _biomeToggleObj.name = "BiomeToggle";
                // SharedPanel is an inactive template — activate the clone explicitly.
                _biomeToggleObj.SetActive(true);

                // Width: use the SharedPanel template's natural width so the
                // "Show Biome Text" label has room. Height: force to BtnHeight
                // so the toggle stays uniform with the adjacent SEND/COPY
                // buttons and the bottom-row MapCompile panel — the cloned
                // toggle's natural height (~42px) made the container taller
                // than MapCompileButtons (38px buttons), breaking the row alignment.
                var toggleRect = _biomeToggleObj.GetComponent<RectTransform>();
                if (toggleRect != null)
                {
                    if (toggleRect.sizeDelta.x > 0f) _biomeToggleWidth = toggleRect.sizeDelta.x;
                    _biomeToggleHeight = BtnHeight;
                    toggleRect.sizeDelta = new Vector2(_biomeToggleWidth, BtnHeight);
                }

                _biomeToggle = _biomeToggleObj.GetComponentInChildren<Toggle>(true);
                if (_biomeToggle != null)
                {
                    _biomeToggle.onValueChanged.RemoveAllListeners();
                    _biomeToggle.isOn = Plugin.ShowBiomeText?.Value ?? false;
                    _biomeToggle.onValueChanged.AddListener(val =>
                    {
                        if (Plugin.ShowBiomeText != null)
                            Plugin.ShowBiomeText.Value = val;
                    });
                }

                var labelT = Utils.FindChild(_biomeToggleObj.transform, "Label");
                if (labelT != null)
                {
                    var labelTmp = labelT.GetComponent<TextMeshProUGUI>();
                    if (labelTmp != null) labelTmp.text = "Show Biome Text";
                }

                var checkmarkT = Utils.FindChild(_biomeToggleObj.transform, "Checkmark");
                _biomeToggleCheckmark = checkmarkT?.GetComponent<Image>();
            }
            else
            {
                ModLog.Warn("[NoMapDiscordAdditions] SharedPanel not found — biome toggle skipped.");
            }

            _captureBtn = MapUI.CreateButton("CaptureBtn", _containerObj.transform,
                BtnWidth, BtnHeight, BuildSendLabel(), out _captureBtnText);
            _captureBtn.onClick.AddListener(() =>
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;
                plugin.TriggerDiscordSend();
            });

            _clipboardBtn = MapUI.CreateButton("ClipboardBtn", _containerObj.transform,
                BtnWidth, BtnHeight, BuildCopyLabel(), out _clipboardBtnText);
            _clipboardBtn.onClick.AddListener(() =>
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;
                plugin.TriggerClipboardCopy();
            });

            _containerObj.SetActive(true);
            RefreshEnabledState();
            RefreshBiomeToggleState();

            ModLog.Info("[NoMapDiscordAdditions] Map button container created.");
        }

        /// <summary>
        /// Enables the Capture button only when a webhook URL is configured.
        /// The Copy button is always interactable.
        /// </summary>
        public static void RefreshEnabledState()
        {
            if (_containerObj == null) return;

            bool captureBtnActive = false;
            if (_captureBtn != null)
            {
                captureBtnActive = !string.IsNullOrEmpty(ModHelpers.EffectiveConfig.WebhookUrl);
                _captureBtn.gameObject.SetActive(captureBtnActive);
            }

            var rect = _containerObj.GetComponent<RectTransform>();
            if (rect != null)
            {
                float baseW = 8f + _biomeToggleWidth + HlgSpacing + BtnWidth + 8f;
                float w = baseW + (captureBtnActive ? BtnWidth + HlgSpacing : 0f);
                float h = Mathf.Max(_biomeToggleHeight, BtnHeight) + HlgPadVertical * 2f;
                rect.sizeDelta = new Vector2(w, h);
                ApplyAlignment(rect);
            }
        }

        /// <summary>
        /// Updates both button labels to reflect the current hotkey configs,
        /// e.g. "SEND MAP (F10)" / "COPY MAP (F11)". Called from a SettingChanged
        /// listener so live edits in BepInEx Config Manager show up immediately.
        /// </summary>
        public static void RefreshHotkeyLabels()
        {
            if (_captureBtnText != null) _captureBtnText.text = BuildSendLabel();
            if (_clipboardBtnText != null) _clipboardBtnText.text = BuildCopyLabel();
        }

        private static string BuildSendLabel() =>
            $"SEND MAP ({Plugin.ScreenshotKey?.Value.ToString() ?? "F10"})";

        private static string BuildCopyLabel() =>
            $"COPY MAP ({Plugin.CopyKey?.Value.ToString() ?? "F11"})";

        /// <summary>
        /// Syncs the toggle's checked state to the current config value.
        /// Called when the config changes externally (e.g. via BepInEx config manager).
        /// </summary>
        public static void RefreshBiomeToggleState()
        {
            if (_biomeToggle == null) return;
            bool isOn = Plugin.ShowBiomeText?.Value ?? false;
            _biomeToggle.isOn = isOn;
            if (_biomeToggleCheckmark != null)
                _biomeToggleCheckmark.color = isOn
                    ? new Color(1f, 0.631f, 0.235f, 1f) // Valheim orange — matches button labels
                    : Color.white;
        }

        /// <summary>
        /// Shows or hides the container when the large map opens/closes.
        /// </summary>
        public static void SetVisible(bool visible)
        {
            if (_containerObj == null) return;
            _containerObj.SetActive(visible);
            if (visible)
                RefreshEnabledState();
        }

        /// <summary>
        /// Pins the container to the bottom-right of the large-map root.
        /// MapCompileButtons mirrors this on the left so the two panels share
        /// a single row at the bottom of the map without overlapping. Safe to
        /// call at any time after Create().
        /// </summary>
        public static void ApplyAlignment()
        {
            if (_containerObj == null) return;
            ApplyAlignment(_containerObj.GetComponent<RectTransform>());
        }

        private static void ApplyAlignment(RectTransform rect)
        {
            if (rect == null) return;
            float halfW = rect.sizeDelta.x * 0.5f;
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-(8f + halfW), -8f);
        }

        // Destroy any leftover containers or legacy button names under largeRoot.
        private static void DestroyExisting(Transform largeRoot)
        {
            for (int i = largeRoot.childCount - 1; i >= 0; i--)
            {
                var child = largeRoot.GetChild(i);
                if (child == null) continue;

                var n = child.name;
                if (n == ContainerName) Object.Destroy(child.gameObject);
            }

            if (!_containerObj) _containerObj = null;
            if (!_captureBtn) _captureBtn = null;
            if (!_captureBtnText) _captureBtnText = null;
            if (!_clipboardBtn) _clipboardBtn = null;
            if (!_clipboardBtnText) _clipboardBtnText = null;
            if (!_biomeToggleObj) _biomeToggleObj = null;
            if (!_biomeToggle) _biomeToggle = null;
            if (!_biomeToggleCheckmark) _biomeToggleCheckmark = null;
            MapUI.InvalidateCaches();
        }
    }
}
