using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Shared widget factory for map-overlay UI: creates buttons styled like
    /// Valheim's native ones (sprite, hover/press colors, AveriaSerifLibre font
    /// with the orange tint) and translucent background panels matching the
    /// large map's existing chrome. Originally lived inside CaptureButton; was
    /// extracted so MapCompileButtons and MapCompileResultPanel can reuse it
    /// without duplicating the asset-lookup caches.
    /// </summary>
    public static class MapUI
    {
        // Valheim's body-label orange (matches in-game button labels).
        public static readonly Color LabelColor = new Color(1f, 0.631f, 0.235f, 1f);

        private static Button _cachedRefButton;
        private static TMP_FontAsset _cachedFont;
        private static Material _cachedFontMaterial;
        private static bool _fontLookupAttempted;

        public static Button CreateButton(string name, Transform parent,
            float width, float height, string label, out TextMeshProUGUI labelText)
        {
            var btnObj = new GameObject(name);
            btnObj.transform.SetParent(parent, false);

            var rect = btnObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(width, height);

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
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            ApplyValheimFont(text);

            labelText = text;
            return button;
        }

        public static void ApplyPanelBackground(Image image, Minimap minimap)
        {
            // Try to find an existing translucent panel in the large map to copy from.
            // Fall back to a hardcoded dark tint if none is present.
            Transform panelT = Utils.FindChild(minimap.m_largeRoot.transform, "panel");
            if (panelT == null) panelT = Utils.FindChild(minimap.m_largeRoot.transform, "Bkg");
            if (panelT == null) panelT = Utils.FindChild(minimap.m_largeRoot.transform, "bg");

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

        public static void ApplyValheimFont(TMP_Text text)
        {
            text.color = LabelColor;

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
                if (_cachedFontMaterial != null)
                    text.fontSharedMaterial = _cachedFontMaterial;
                text.fontSize = 16f;
                return;
            }

            TMP_Text fallback = FindMinimapButtonText() ?? FindAnySceneText();
            if (fallback != null && fallback.font != null)
            {
                text.font = fallback.font;
                text.fontSharedMaterial = fallback.fontSharedMaterial;
                text.fontSize = fallback.fontSize;
                text.fontStyle = fallback.fontStyle;
                return;
            }

            ModLog.Warn("[NoMapDiscordAdditions] No TMP_FontAsset found — button label will not render.");
            text.fontSize = 16f;
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

        private static TMP_FontAsset FindValheimFontAsset()
        {
            // Multiple Averia variants exist (Sans, bold, SDF presets) — prefer
            // the exact serif asset Valheim uses for body labels, then loose
            // matches in priority order.
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

        private static TMP_Text FindMinimapButtonText()
        {
            var minimap = Minimap.instance;
            if (minimap == null || minimap.m_largeRoot == null) return null;

            foreach (var b in minimap.m_largeRoot.GetComponentsInChildren<Button>(true))
            {
                if (b == null) continue;
                string n = b.gameObject.name;
                // Skip our own buttons by name prefix to avoid font self-reference.
                if (n == "CaptureBtn" || n == "ClipboardBtn" || n.StartsWith("Compile")) continue;
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
            if (_cachedRefButton != null) return _cachedRefButton;

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

        // Called on hot-reload to clear cached scene refs that may have been destroyed.
        public static void InvalidateCaches()
        {
            if (!_cachedRefButton) _cachedRefButton = null;
        }
    }
}
