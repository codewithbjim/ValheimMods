using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Loose, reflection-only bridge to ZenMap's internal <c>ZenMap.MapLocation</c>.
    /// We can't reference the type directly — it's <c>internal</c> in ZenMap's
    /// assembly — so everything here is best-effort and degrades to "unavailable"
    /// (callers then fall back to their own logic) if ZenMap is absent or its
    /// shape changed.
    ///
    /// Why this exists: in a ZenMap nomap world, exploration is written into
    /// <see cref="Minimap.m_explored"/> as a single CIRCULAR disc per table read
    /// (<c>MapLocation.Show()</c> → <c>Minimap.Explore(pos, radius)</c>). A
    /// rectangular capture viewport always has unexplored corners outside that
    /// disc, so a strict "every pixel explored" test (see
    /// <see cref="MapCompileCapture"/>) reports even a maxed-out table as
    /// partial. ZenMap's own <c>MapLocation.Percent</c> (revealed radius ÷ the
    /// configured full radius) is the correct, fog-independent measure of how
    /// complete the current reveal is.
    /// </summary>
    public static class ZenMapInterop
    {
        // A table is treated as a "complete" tile only at (essentially) full
        // reveal. Percent is clamp01(Radius / MapTableFullRadius); an aged-out
        // table sits at 1.0. Anything short of full stays "partial" so the
        // compositor's explored-priority tier still ranks a maxed table above a
        // younger, smaller reveal in overlap regions.
        public const float FullRevealThreshold = 0.999f;

        private static bool _resolved;
        private static PropertyInfo _inUseProp;   // static bool MapLocation.InUse
        private static PropertyInfo _activeProp;   // static MapLocation MapLocation.Active
        private static FieldInfo _percentField;   // float MapLocation.Percent (readonly)

        // static Color AdjustPinColor(Minimap.PinData pin, Color defaultColor) —
        // ZenMap's UpdatePins-transpiler hook that decides each pin's icon
        // color (boss orange, private peach, shared, death, special, …) from
        // its PinColor config. See ZenMap.cs:1390.
        private static bool _colorResolved;
        private static MethodInfo _adjustPinColor;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                Type t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType("ZenMap.MapLocation", false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(x => x != null);

                if (t == null) return; // ZenMap not loaded — caller falls back.

                _inUseProp = t.GetProperty("InUse",
                    BindingFlags.Public | BindingFlags.Static);
                _activeProp = t.GetProperty("Active",
                    BindingFlags.Public | BindingFlags.Static);
                _percentField = t.GetField("Percent",
                    BindingFlags.Public | BindingFlags.Instance);

                if (_inUseProp == null || _activeProp == null || _percentField == null)
                {
                    ModLog.Warn("[NoMapDiscordAdditions] ZenMap MapLocation shape " +
                                "changed; reveal-percent detection disabled.");
                    _inUseProp = null;
                    _activeProp = null;
                    _percentField = null;
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] ZenMap interop probe failed: {ex.Message}");
            }
        }

        /// <summary>
        /// When a ZenMap map location (cartography table or map item) is the
        /// active reveal, returns its completeness in [0,1] via
        /// <paramref name="percent"/> and true. Returns false when ZenMap is not
        /// present, no location is active, or reflection failed — the caller
        /// should then fall back to its own explored-pixel test.
        /// </summary>
        public static bool TryGetActiveRevealPercent(out float percent)
        {
            percent = 0f;
            Resolve();
            if (_inUseProp == null) return false;

            try
            {
                if (!(_inUseProp.GetValue(null) is bool inUse) || !inUse)
                    return false;

                object active = _activeProp.GetValue(null);
                if (active == null) return false;

                percent = Convert.ToSingle(_percentField.GetValue(active));
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] ZenMap reveal-percent read failed: {ex.Message}");
                return false;
            }
        }

        // Locate ZenMap's private static AdjustPinColor(PinData, Color) once.
        // It lives on an internal patch class we can't name directly, so we
        // scan ZenMap's assembly for a static method with the exact
        // (PinData, Color) → Color signature. Reuses the MapLocation type found
        // by Resolve() purely to get a handle on ZenMap's assembly.
        private static void ResolveColor()
        {
            if (_colorResolved) return;
            _colorResolved = true;
            try
            {
                Type anchor = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a =>
                    {
                        try { return a.GetType("ZenMap.MapLocation", false); }
                        catch { return null; }
                    })
                    .FirstOrDefault(x => x != null);

                if (anchor == null) return; // ZenMap not loaded — caller falls back.

                foreach (Type t in anchor.Assembly.GetTypes())
                {
                    MethodInfo m;
                    try
                    {
                        m = t.GetMethod("AdjustPinColor",
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                    }
                    catch { continue; }
                    if (m == null || m.ReturnType != typeof(Color)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[1].ParameterType == typeof(Color))
                    {
                        _adjustPinColor = m;
                        return;
                    }
                }

                ModLog.Warn("[NoMapDiscordAdditions] ZenMap AdjustPinColor not found; " +
                            "off-screen pins won't inherit ZenMap pin colors.");
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] ZenMap pin-color probe failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Compute the icon color ZenMap would assign to <paramref name="pin"/>,
        /// independent of whether the pin currently has a live UI element. Used
        /// to color pins that sit on already-captured-but-off-screen tiles in
        /// the compile composite: their on-screen <c>m_iconElement</c> has been
        /// destroyed, so reading its color yields white. This asks ZenMap's own
        /// <c>AdjustPinColor</c> for the right hue instead (boss orange, private
        /// peach, …). Returns false — leaving <paramref name="color"/> at
        /// <paramref name="defaultColor"/> — when ZenMap is absent, its pin
        /// coloring is disabled (it returns the default), or reflection failed.
        /// </summary>
        public static bool TryGetPinColor(Minimap.PinData pin, Color defaultColor, out Color color)
        {
            color = defaultColor;
            if (pin == null) return false;
            ResolveColor();
            if (_adjustPinColor == null) return false;

            try
            {
                color = (Color)_adjustPinColor.Invoke(null, new object[] { pin, defaultColor });
                return true;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] ZenMap pin-color read failed: {ex.Message}");
                color = defaultColor;
                return false;
            }
        }
    }
}
