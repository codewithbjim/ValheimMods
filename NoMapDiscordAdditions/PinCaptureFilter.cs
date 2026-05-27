using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Prepares Valheim's live pin UI for a SEND / COPY / texture capture so
    /// the output matches what the compile path stamps on its composite:
    /// <list type="bullet">
    ///   <item>Hides <c>PinType.Player</c> and <c>PinType.Death</c> pins for
    ///   the capture frame — they're session-scoped and we exclude them from
    ///   compile too (see <see cref="MapCompile.MapCompilePinSnapshot"/>).</item>
    ///   <item>Strips TMP rich-text tags from pin name captions
    ///   (e.g. "&lt;color=orange&gt;THE ELDER&lt;/color&gt;" → "The Elder")
    ///   so boss / location names render uniformly instead of inheriting
    ///   localization styling.</item>
    /// </list>
    /// All mutations are reverted in <see cref="Restore"/> so the live map is
    /// untouched once the capture frame is done.
    ///
    /// Compile mode already hides ALL pins (<c>hidePinIcons: true</c>) so
    /// these filters are a no-op there; the helper is a SEND / COPY /
    /// texture-path concern.
    /// </summary>
    internal static class PinCaptureFilter
    {
        // Same regex MapCompilePinSnapshot uses for compile-stamped pins —
        // keep them in lockstep so a localization tweak that affects compile
        // affects SEND identically.
        private static readonly Regex s_richText =
            new Regex("<[^>]+>", RegexOptions.Compiled);

        // Set at SEND / COPY click time by the button handler so the capture
        // coroutine can read the LEFT CTRL state deterministically. Sampling
        // Input.GetKey inside the capture coroutine instead is unreliable — by
        // the time the texture-capture path reaches Apply, multiple frames
        // (WaitForEndOfFrame + MapStyleRender.BuildAsync) may have elapsed
        // since the click, and the player may have already released the key.
        internal static bool s_armedIncludeFiltered;

        // True between Apply and Restore. ZenMap's UpdateDynamicPins postfix
        // (ZenMap.cs:3173) calls ShowLabel(IsCursorOver || IsLabelTogglePressed)
        // on every visible pin every frame, hiding any caption not currently
        // hovered or LeftShift-held. That clobbers our one-shot SetActive(true)
        // before the back buffer is sampled. MinimapPatch.UpdateDynamicPins_
        // ForceCaptionsDuringCapture watches this flag and re-activates the
        // caption GOs AFTER ZenMap's postfix runs, so captions are visible at
        // capture time without us having to fight a per-frame loop.
        internal static bool s_forceShowLabels;

        /// <summary>
        /// Read at the click moment and stash for the next capture. Sample
        /// here (not deep inside the coroutine) because the actual capture
        /// can be many frames after the click.
        /// </summary>
        internal static void ArmFromCurrentInput()
        {
            s_armedIncludeFiltered = Input.GetKey(KeyCode.LeftControl)
                                  || Input.GetKey(KeyCode.RightControl);
        }

        /// <summary>
        /// Consume + clear the armed flag. Capture methods call this once at
        /// the start of <see cref="Apply"/>; the next capture starts unarmed
        /// unless the click handler re-arms it.
        /// </summary>
        private static bool ConsumeArmedIncludeFiltered()
        {
            bool v = s_armedIncludeFiltered;
            s_armedIncludeFiltered = false;
            return v;
        }

        internal struct State
        {
            // Per-pin Image / TMP component .enabled flags we toggled off.
            // We disable components (not GameObjects) because mods like
            // ZenMap re-call gameObject.SetActive(true) on pin elements every
            // frame in their own UpdatePins loop, which would clobber a
            // SetActive(false). Component .enabled isn't touched by those
            // mods, so disabling it survives until our Restore.
            public List<(Image img, bool wasEnabled)> DisabledImages;
            public List<(TMP_Text tmp, bool wasEnabled)> DisabledTexts;
            public List<(TMP_Text tmp, string original)> StrippedTexts;
            // Caption GameObjects we force-activated (vanilla UpdatePins hides
            // them via SetActive(false) when m_largeZoom >= m_showNamesZoom).
            // For a one-shot capture the player expects all named pins to
            // appear regardless of how zoomed-out the live view happens to be.
            public List<(GameObject go, bool wasActive)> ForcedCaptionGOs;
        }

        /// <summary>
        /// Hide Player/Death pin UI elements + strip rich-text from remaining
        /// pin captions. Returns the saved state for <see cref="Restore"/>.
        /// Safe to call even when there are no pins (returns empty state).
        ///
        /// <paramref name="includeFilteredPins"/> opts OUT of the Player/Death
        /// exclusion for SEND/COPY captures — the user holds LEFT CTRL at
        /// click time to "include all pins" for one-off shares (live deaths,
        /// other-player positions). Caption rich-text is still stripped so
        /// styling stays uniform regardless. Compile never engages this
        /// helper — it uses its own snapshot which always excludes Player /
        /// Death pins.
        /// </summary>
        internal static State Apply(bool includeFilteredPins = false)
        {
            // Click handler armed the flag if LEFT/RIGHT CTRL was held — that
            // wins over the parameter default, since the click handler ran on
            // the exact frame of the click and the param default never
            // captures that intent. Callers can still pass true explicitly
            // (e.g. for tests / future modes that don't gate on CTRL).
            includeFilteredPins = includeFilteredPins || ConsumeArmedIncludeFiltered();

            var state = new State
            {
                DisabledImages = new List<(Image, bool)>(),
                DisabledTexts = new List<(TMP_Text, bool)>(),
                StrippedTexts = new List<(TMP_Text, string)>(),
                ForcedCaptionGOs = new List<(GameObject, bool)>(),
            };

            // Arm the postfix watcher so any per-frame "hide labels" loop
            // (vanilla zoom gate or ZenMap's hover/key gate) gets undone
            // every frame until Restore. Without this, our SetActive(true)
            // below survives this frame but is reverted before the next
            // capture's back-buffer read.
            s_forceShowLabels = true;

            var minimap = Minimap.instance;
            var pins = minimap?.m_pins;
            if (pins == null) return state;

            int count = pins.Count;
            for (int i = 0; i < count; i++)
            {
                var pin = pins[i];
                if (pin == null) continue;

                bool excluded = !includeFilteredPins
                             && (pin.m_type == Minimap.PinType.Player
                              || pin.m_type == Minimap.PinType.Death);

                if (excluded)
                {
                    DisableImage(state, pin.m_iconElement);
                    DisableText(state, pin.m_NamePinData?.PinNameText);
                    continue;
                }

                var tmp = pin.m_NamePinData?.PinNameText;
                if (tmp != null && !string.IsNullOrEmpty(tmp.text)
                    && tmp.text.IndexOf('<') >= 0)
                {
                    state.StrippedTexts.Add((tmp, tmp.text));
                    tmp.text = s_richText.Replace(tmp.text, string.Empty).Trim();
                }

                // Force-activate the caption GameObject. Vanilla UpdatePins
                // toggles this off when m_largeZoom >= m_showNamesZoom, so a
                // zoomed-out SEND/COPY screenshot would otherwise miss every
                // pin name. Skip pins with no name (their caption GO is
                // legitimately inactive and we shouldn't reveal a blank).
                var captionGo = pin.m_NamePinData?.PinNameGameObject;
                if (captionGo != null && !string.IsNullOrEmpty(pin.m_name))
                {
                    bool wasActive = captionGo.activeSelf;
                    if (!wasActive) captionGo.SetActive(true);
                    state.ForcedCaptionGOs.Add((captionGo, wasActive));
                }
            }

            return state;
        }

        internal static void Restore(State state)
        {
            // Disarm the postfix watcher BEFORE restoring per-pin state so we
            // don't fight ZenMap's next-frame ShowLabel call after we hand
            // control back.
            s_forceShowLabels = false;

            if (state.DisabledImages != null)
            {
                for (int i = 0; i < state.DisabledImages.Count; i++)
                {
                    var (img, wasEnabled) = state.DisabledImages[i];
                    if (img != null) img.enabled = wasEnabled;
                }
            }
            if (state.DisabledTexts != null)
            {
                for (int i = 0; i < state.DisabledTexts.Count; i++)
                {
                    var (tmp, wasEnabled) = state.DisabledTexts[i];
                    if (tmp != null) tmp.enabled = wasEnabled;
                }
            }
            if (state.StrippedTexts != null)
            {
                for (int i = 0; i < state.StrippedTexts.Count; i++)
                {
                    var (tmp, original) = state.StrippedTexts[i];
                    if (tmp != null) tmp.text = original;
                }
            }
            if (state.ForcedCaptionGOs != null)
            {
                for (int i = 0; i < state.ForcedCaptionGOs.Count; i++)
                {
                    var (go, wasActive) = state.ForcedCaptionGOs[i];
                    // Only restore the OFF state we overrode — leave the GO
                    // active if it was already active so vanilla UpdatePins'
                    // next pass can pick it up without a one-frame flicker.
                    if (go != null && !wasActive) go.SetActive(false);
                }
            }
        }

        private static void DisableImage(State state, Image img)
        {
            if (img == null) return;
            bool was = img.enabled;
            if (was) img.enabled = false;
            state.DisabledImages.Add((img, was));
        }

        private static void DisableText(State state, TMP_Text tmp)
        {
            if (tmp == null) return;
            bool was = tmp.enabled;
            if (was) tmp.enabled = false;
            state.DisabledTexts.Add((tmp, was));
        }
    }
}
