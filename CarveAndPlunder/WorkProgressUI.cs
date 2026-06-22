using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CarveAndPlunder
{
    // Hold-to-work progress bar, built entirely from scratch on our own
    // ScreenSpaceOverlay canvas. We previously cloned the native stamina bar, but
    // that dragged in the stamina Animator's faded-out alpha (frozen the instant
    // we destroyed the Animator), so the clone rendered invisible. A self-owned
    // canvas has no such dependency: it's built once under the (persistent) plugin
    // GameObject and simply toggled on/off.
    public class WorkProgressUI : MonoBehaviour
    {
        private static WorkProgressUI _instance;

        private bool _visible;
        private float _progress;
        private string _label = "";

        private GameObject _root;     // the canvas root
        private RectTransform _fillRt; // width-fraction = progress
        private TMP_Text _text;

        // Layout (reference-resolution pixels; CanvasScaler handles DPI).
        private const float BarWidth = 360f;
        private const float BarHeight = 22f;
        private const float BottomOffset = 220f;

        public static void Show(string label)
        {
            if (_instance == null) return;
            _instance._label = label;
            _instance._progress = 0f;
            _instance._visible = true;
            _instance.Apply();
        }

        public static void SetProgress(float p)
        {
            if (_instance == null) return;
            _instance._progress = Mathf.Clamp01(p);
            _instance.Apply();
        }

        public static void Hide()
        {
            if (_instance == null) return;
            _instance._visible = false;
            _instance.Apply();
        }

        private void Awake() => _instance = this;

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Apply()
        {
            EnsureBuilt();
            if (_root == null) return;

            if (_visible)
            {
                if (_fillRt != null)
                {
                    Vector2 a = _fillRt.anchorMax;
                    a.x = _progress;
                    _fillRt.anchorMax = a;
                }
                if (_text != null) _text.text = _label;
            }
            _root.SetActive(_visible);
        }

        // Build our own overlay canvas + bar once. Parented under the plugin's
        // GameObject (which persists for the session), so it survives scene loads
        // and never needs a rebuild.
        private void EnsureBuilt()
        {
            if (_root != null) return;

            _root = new GameObject("CarveAndPlunder_WorkBar",
                typeof(Canvas), typeof(CanvasScaler));
            _root.transform.SetParent(transform, worldPositionStays: false);

            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000; // above the game HUD

            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            // Container anchored bottom-center of the screen.
            RectTransform panel = NewRect("Panel", _root.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, BottomOffset);
            panel.sizeDelta = new Vector2(BarWidth, BarHeight + 28f);

            // Label above the bar.
            RectTransform labelRt = NewRect("Label", panel);
            labelRt.anchorMin = new Vector2(0f, 1f);
            labelRt.anchorMax = new Vector2(1f, 1f);
            labelRt.pivot = new Vector2(0.5f, 1f);
            labelRt.anchoredPosition = Vector2.zero;
            labelRt.sizeDelta = new Vector2(0f, 26f);
            _text = labelRt.gameObject.AddComponent<TextMeshProUGUI>();
            _text.alignment = TextAlignmentOptions.Center;
            _text.fontSize = 20f;
            _text.color = Color.white;
            _text.raycastTarget = false;
            TMP_FontAsset font = FindFont();
            if (font != null) _text.font = font;

            // Track (dark background) along the bottom of the panel.
            RectTransform trackRt = NewRect("Track", panel);
            trackRt.anchorMin = new Vector2(0f, 0f);
            trackRt.anchorMax = new Vector2(1f, 0f);
            trackRt.pivot = new Vector2(0.5f, 0f);
            trackRt.anchoredPosition = Vector2.zero;
            trackRt.sizeDelta = new Vector2(0f, BarHeight);
            var track = trackRt.gameObject.AddComponent<Image>();
            track.color = new Color(0f, 0f, 0f, 0.7f);
            track.raycastTarget = false;

            // Fill: stretches from the left; width fraction == progress via
            // anchorMax.x (no sprite needed — a spriteless Image is a solid quad).
            _fillRt = NewRect("Fill", trackRt);
            _fillRt.anchorMin = new Vector2(0f, 0f);
            _fillRt.anchorMax = new Vector2(0f, 1f);
            _fillRt.pivot = new Vector2(0f, 0.5f);
            _fillRt.offsetMin = Vector2.zero;
            _fillRt.offsetMax = Vector2.zero;
            var fill = _fillRt.gameObject.AddComponent<Image>();
            fill.color = new Color(0.85f, 0.7f, 0.2f, 1f);
            fill.raycastTarget = false;

            _root.SetActive(false);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, worldPositionStays: false);
            return rt;
        }

        // TMP needs a font asset to render text. Prefer the project default; fall
        // back to any loaded font so the label still appears.
        private static TMP_FontAsset FindFont()
        {
            if (TMP_Settings.defaultFontAsset != null) return TMP_Settings.defaultFontAsset;
            TMP_FontAsset[] all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return all != null && all.Length > 0 ? all[0] : null;
        }
    }
}
