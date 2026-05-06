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
        private static float _biomeToggleHeight =  42f;  // read from clone at Create() time
        private static Button _cachedRefButton;
        private static TMP_FontAsset _cachedFont;
        private static Material _cachedFontMaterial;
        private static bool _fontLookupAttempted;

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
            ApplyPanelBackground(panelBg, minimap);

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

                // Read the natural size so the container can size itself correctly.
                var toggleRect = _biomeToggleObj.GetComponent<RectTransform>();
                if (toggleRect != null)
                {
                    if (toggleRect.sizeDelta.x > 0f) _biomeToggleWidth  = toggleRect.sizeDelta.x;
                    if (toggleRect.sizeDelta.y > 0f) _biomeToggleHeight = toggleRect.sizeDelta.y;
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
                Debug.LogWarning("[NoMapDiscordAdditions] SharedPanel not found — biome toggle skipped.");
            }

            _captureBtn = CreateButton("CaptureBtn", _containerObj.transform,
                BuildSendLabel(), out _captureBtnText);
            _captureBtn.onClick.AddListener(() =>
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;
                plugin.TriggerDiscordSend();
            });

            _clipboardBtn = CreateButton("ClipboardBtn", _containerObj.transform,
                BuildCopyLabel(), out _clipboardBtnText);
            _clipboardBtn.onClick.AddListener(() =>
            {
                var plugin = Plugin.Instance;
                if (plugin == null) return;
                plugin.TriggerClipboardCopy();
            });

            _containerObj.SetActive(true);
            RefreshEnabledState();
            RefreshBiomeToggleState();

            Debug.Log("[NoMapDiscordAdditions] Map button container created.");
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
        /// Repositions the container to match the current ButtonAlignment config.
        /// Safe to call at any time after Create().
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
            var align = Plugin.ButtonAlignment?.Value ?? Plugin.ButtonAlignmentMode.Center;
            switch (align)
            {
                case Plugin.ButtonAlignmentMode.Left:
                    rect.anchorMin = new Vector2(0f, 0f);
                    rect.anchorMax = new Vector2(0f, 0f);
                    rect.anchoredPosition = new Vector2(8f + halfW, -8f);
                    break;
                case Plugin.ButtonAlignmentMode.Right:
                    rect.anchorMin = new Vector2(1f, 0f);
                    rect.anchorMax = new Vector2(1f, 0f);
                    rect.anchoredPosition = new Vector2(-(8f + halfW), -8f);
                    break;
                default:
                    rect.anchorMin = new Vector2(0.5f, 0f);
                    rect.anchorMax = new Vector2(0.5f, 0f);
                    rect.anchoredPosition = new Vector2(0f, -8f);
                    break;
            }
        }

        private static Button CreateButton(string name, Transform parent, string label,
            out TextMeshProUGUI labelText)
        {
            var btnObj = new GameObject(name);
            btnObj.transform.SetParent(parent, false);

            var rect = btnObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(BtnWidth, BtnHeight);

            var bgImage = btnObj.AddComponent<Image>();
            var button = btnObj.AddComponent<Button>();
            button.targetGraphic = bgImage;

            ApplyValheimStyle(bgImage, button);

            var textObj = new GameObject("Text");
            textObj.transform.SetParent(btnObj.transform, false);

            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 4f);
            textRect.offsetMax = new Vector2(-8f, -4f);

            var text = textObj.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.alignment = TextAlignmentOptions.Center;
            // Force single-line so "SEND MAP (F10)" doesn't break onto two lines
            // when Valheim's wider hotkey labels (F11, KeyPad0, etc.) push past
            // the natural button width.
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            ApplyValheimFont(text);

            labelText = text;
            return button;
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
            if (!_cachedRefButton) _cachedRefButton = null;
        }

        private static void ApplyPanelBackground(Image image, Minimap minimap)
        {
            // Try to find an existing translucent panel in the large map to copy from.
            // Common names in Valheim's map UI; fall back to a hardcoded dark tint.
            Transform panelT = Utils.FindChild(minimap.m_largeRoot.transform, "panel");
            if (panelT == null)
                panelT = Utils.FindChild(minimap.m_largeRoot.transform, "Bkg");
            if (panelT == null)
                panelT = Utils.FindChild(minimap.m_largeRoot.transform, "bg");

            var src = panelT?.GetComponent<Image>();
            if (src != null && src.sprite != null)
            {
                image.sprite = src.sprite;
                image.type = src.type;
                image.material = src.material;
                image.color = src.color;
                image.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
            }
            else
            {
                image.color = new Color(0.1f, 0.1f, 0.1f, 0.75f);
            }
        }

        private static void ApplyValheimStyle(Image image, Button button)
        {
            Button refButton = FindReferenceButton();
            if (refButton == null)
            {
                image.color = new Color(0f, 0f, 0f, 0.6f);
                return;
            }

            var refImage = refButton.GetComponent<Image>();
            if (refImage != null)
            {
                image.sprite = refImage.sprite;
                image.type = refImage.type;
                image.material = refImage.material;
                image.color = refImage.color;
                image.pixelsPerUnitMultiplier = refImage.pixelsPerUnitMultiplier;
            }

            button.transition = refButton.transition;
            button.colors = refButton.colors;
            button.spriteState = refButton.spriteState;
        }

        private static void ApplyValheimFont(TextMeshProUGUI text)
        {
            var labelColor = new Color(1f, 0.631f, 0.235f, 1f);
            text.color = labelColor;

            // Cached after the first lookup: Resources.FindObjectsOfTypeAll<>
            // walks every loaded asset (TMP_FontAsset and Material), so doing it
            // once per button per session is wasteful — fonts/materials don't
            // appear or disappear at runtime.
            if (!_fontLookupAttempted)
            {
                _fontLookupAttempted = true;
                _cachedFont = FindValheimFontAsset();
                if (_cachedFont != null)
                    _cachedFontMaterial = FindOutlineMaterialFor(_cachedFont);
            }

            if (_cachedFont != null)
            {
                text.font = _cachedFont;
                // Use the matching outline material preset (e.g. "Valheim-AveriaSerifLibre - Outline")
                // so glyph SDF and material's font atlas are consistent. Copying a material from a
                // sibling button is unsafe — a different font asset's material won't match this font.
                if (_cachedFontMaterial != null)
                    text.fontSharedMaterial = _cachedFontMaterial;
                text.fontSize = 16f;
                return;
            }

            // Fall back: copy everything from a native minimap button TMP_Text.
            TMP_Text fallback = FindMinimapButtonText() ?? FindAnySceneText();
            if (fallback != null && fallback.font != null)
            {
                text.font = fallback.font;
                text.fontSharedMaterial = fallback.fontSharedMaterial;
                text.fontSize = fallback.fontSize;
                text.fontStyle = fallback.fontStyle;
                return;
            }

            Debug.LogWarning("[NoMapDiscordAdditions] No TMP_FontAsset found — button label will not render.");
            text.fontSize = 16f;
        }

        private static TMP_FontAsset FindValheimFontAsset()
        {
            // Prefer the exact serif asset Valheim uses for body labels; multiple "Averia"
            // variants exist (Sans, bold, SDF presets) and iteration order is undefined.
            TMP_FontAsset exact = null;
            TMP_FontAsset serifLoose = null;
            TMP_FontAsset anyAveria = null;

            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (f == null || f.name == null) continue;
                string n = f.name;

                if (n == "Valheim-AveriaSerifLibre") { exact = f; break; }
                if (serifLoose == null && n.IndexOf("AveriaSerif", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    serifLoose = f;
                if (anyAveria == null && n.IndexOf("Averia", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    anyAveria = f;
            }

            return exact ?? serifLoose ?? anyAveria;
        }

        // Find the TMP material preset matching this font asset, preferring the outline variant
        // (Valheim labels use "<font> - Outline"). Returns null if no specific match is found,
        // letting TMP fall back to the font's default material.
        private static Material FindOutlineMaterialFor(TMP_FontAsset font)
        {
            if (font == null) return null;
            string fontName = font.name;
            Material outline = null;
            Material plain = null;

            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.name == null) continue;
                string n = m.name;
                if (n.IndexOf(fontName, System.StringComparison.Ordinal) < 0) continue;

                if (outline == null && n.IndexOf("Outline", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    outline = m;
                else if (plain == null)
                    plain = m;
            }

            return outline ?? plain;
        }

        // Find TMP_Text on a native minimap button (skipping our own).
        private static TMP_Text FindMinimapButtonText()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return null;

            foreach (var b in minimap.m_largeRoot.GetComponentsInChildren<Button>(true))
            {
                if (b == null || b.gameObject.name == "CaptureBtn" || b.gameObject.name == "ClipboardBtn") continue;
                if (_biomeToggleObj != null && b.transform.IsChildOf(_biomeToggleObj.transform)) continue;
                var t = b.GetComponentInChildren<TMP_Text>(true);
                if (t != null && t.font != null) return t;
            }
            return null;
        }

        private static TMP_Text FindAnySceneText()
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (t != null && t.font != null) return t;
            return null;
        }

        private static Button FindReferenceButton()
        {
            if (_cachedRefButton != null)
                return _cachedRefButton;

            foreach (var b in Resources.FindObjectsOfTypeAll<Button>())
            {
                if (b == null) continue;
                var img = b.GetComponent<Image>();
                if (img == null || img.sprite == null) continue;

                var spriteName = img.sprite.name ?? string.Empty;
                if (spriteName == "button" || spriteName.StartsWith("button_"))
                {
                    _cachedRefButton = b;
                    return b;
                }
            }

            return null;
        }
    }
}
