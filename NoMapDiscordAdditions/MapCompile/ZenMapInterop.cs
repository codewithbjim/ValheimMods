using System;
using System.Linq;
using System.Reflection;

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
    }
}
