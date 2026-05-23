using System.Collections;
using System.Threading;
using UnityEngine;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Renders a stylized "Old Map" version of the current world map — an
    /// in-mod, faithful reimplementation of ASpy's MapPrinter GenerateOldMap
    /// pipeline (aged-parchment chart: biome wash, Perlin grain, height
    /// contour lines, stylized fog).
    ///
    /// Unlike MapPrinter it does NOT scan WorldGenerator. The four data layers
    /// MapPrinter builds via DIYMapQuery already exist in Minimap.instance for
    /// the whole world, so we read those instead:
    ///   biome   <- Minimap.m_mapTexture   (RGB24 biome colours)
    ///   height  <- Minimap.m_heightTexture (RHalf, raw biome height)
    ///   fog     <- Minimap.m_explored      (per-pixel exploration)
    /// Exploration is still respected — unexplored pixels render as fogged
    /// parchment, exactly as MapPrinter does.
    ///
    /// The heavy per-pixel pipeline runs on a background thread (Valheim's map
    /// textures are large — m_textureSize is typically 2048 — so a synchronous
    /// render would freeze the game for a noticeable fraction of a second).
    /// Layer read-back and the final Texture2D upload happen on the main thread.
    /// </summary>
    public static class MapStyleRender
    {
        /// <summary>True when a non-None Map Style is selected.</summary>
        public static bool IsStyleActive()
            => Plugin.MapStyle != null
               && Plugin.MapStyle.Value != Plugin.MapStyleMode.None;

        /// <summary>
        /// Coroutine: builds the stylized map texture for the active style and
        /// hands it to <paramref name="onDone"/> (null on any failure — the
        /// caller then falls back to a normal capture). The Minimap layers are
        /// read on the main thread, the pixel pipeline runs on a worker thread,
        /// and the Texture2D is created on the main thread once it finishes.
        /// The caller owns the returned texture and must Destroy it.
        /// <paramref name="uvRect"/> is the large map's live uvRect and
        /// <paramref name="width"/> × <paramref name="height"/> the capture
        /// resolution: the styled texture covers exactly that viewport at that
        /// resolution, so it is as sharp as the screenshot it replaces.
        /// </summary>
        public static IEnumerator BuildAsync(Rect uvRect, int width, int height,
            System.Action<Texture2D> onDone)
        {
            if (!IsStyleActive())
            {
                onDone(null);
                yield break;
            }

            MapStyleContext ctx = null;
            try
            {
                ctx = MapStyleContext.CaptureLayers(uvRect, width, height);
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Map style: layer capture failed: {ex.Message}");
                ctx = null;
            }

            if (ctx == null)
            {
                onDone(null);
                yield break;
            }

            var thread = new Thread(ctx.RunPipeline) { IsBackground = true, Name = "NMDA-MapStyle" };
            thread.Start();
            while (thread.IsAlive)
                yield return null;

            if (ctx.Failed || ctx.Result == null)
            {
                ModLog.Warn("[NoMapDiscordAdditions] Map style render failed, using normal capture"
                    + (string.IsNullOrEmpty(ctx.Error) ? "." : $": {ctx.Error}"));
                onDone(null);
                yield break;
            }

            Texture2D result = null;
            try
            {
                var tex = new Texture2D(ctx.OutWidth, ctx.OutHeight, TextureFormat.RGB24, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                };
                tex.SetPixels32(ctx.Result);
                tex.Apply(false);
                result = tex;
            }
            catch (System.Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Map style: texture upload failed: {ex.Message}");
                result = null;
            }

            onDone(result);
        }
    }
}
