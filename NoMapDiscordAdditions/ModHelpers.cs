using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NoMapDiscordAdditions
{
    /// <summary>
    /// Centralizes patterns that were previously duplicated across files:
    ///
    /// 1. <see cref="EffectiveConfig"/> — single source of truth for "what value
    ///    should I actually use right now?". ServerSync (when present) is
    ///    authoritative; otherwise we fall back to the lightweight RPC sync
    ///    layer's snapshot, which itself falls back to the local user config.
    ///    This is the same select that used to live inline at every call site.
    ///
    /// 2. <see cref="ShaderPropSuppressor"/> — temporarily zero shader properties
    ///    matching a substring (used to hide the cloud overlay during capture).
    ///    Both capture paths previously had byte-identical copies of this.
    /// </summary>
    internal static class ModHelpers
    {
        // ── Effective config ────────────────────────────────────────────────
        // Jotunn's SynchronizationManager makes ConfigEntry.Value itself return
        // the server's value on clients (the local value is cached and the
        // getter is patched) for every entry bound with synced: true. So the
        // "effective" value is simply .Value everywhere — no dual-path select.
        // This wrapper is kept so existing call sites stay unchanged.
        public static class EffectiveConfig
        {
            public static string WebhookUrl => Plugin.WebhookUrl?.Value;

            public static bool SpoilerImageData => Plugin.SpoilerImageData.Value;

            public static bool HideClouds => Plugin.HideClouds.Value;

            public static bool AllowCompileFromMapItems =>
                Plugin.AllowCompileFromMapItems?.Value ?? true;

            public static string CompileMessageTemplate =>
                Plugin.CompileMessageTemplate?.Value
                ?? "{player} compiled a map from {tileCount} cartography tables.";

            public static string MessageTemplate =>
                Plugin.MessageTemplate?.Value
                ?? "{player} shared a map update from {biome}{table}";

            public static bool EnableCompileMapSharing =>
                Plugin.EnableCompileMapSharing?.Value ?? true;

            public static string CompileShareMessageTemplate =>
                Plugin.CompileShareMessageTemplate?.Value
                ?? "{player} shared {tileCount} map tile(s) for compile mode.";
        }

        // ── Capture lighting normalization ──────────────────────────────────
        // Valheim's map shader darkens with time of day: EnvMan.SetEnv writes
        // the GLOBAL shader props _SunColor/_AmbientColor/_SunDir/_SunFogColor
        // every frame from the current environment interpolated by day fraction
        // (assembly_valheim EnvMan.cs ~753-758). A tile captured at night is
        // therefore much darker than one captured at noon, which shows up as
        // blocky brightness seams in a compiled multi-tile map. We temporarily
        // override those globals with the *day* palette of the current
        // environment (so every tile renders as if at noon, regardless of when
        // it was actually captured) for the single offscreen render pass, then
        // restore — EnvMan rewrites them next frame anyway, but restoring keeps
        // any same-frame world rendering correct.

        private static readonly int _idSunDir      = Shader.PropertyToID("_SunDir");
        private static readonly int _idSunColor    = Shader.PropertyToID("_SunColor");
        private static readonly int _idAmbient     = Shader.PropertyToID("_AmbientColor");
        private static readonly int _idSunFogColor = Shader.PropertyToID("_SunFogColor");

        public struct SavedLighting
        {
            public bool Active;
            public Vector4 SunDir;
            public Color SunColor;
            public Color Ambient;
            public Color SunFogColor;
        }

        /// <summary>
        /// Saves the four time-of-day globals and overwrites them with FIXED
        /// neutral-noon values. Pass the result to <see cref="RestoreLighting"/>
        /// in a finally block.
        ///
        /// We deliberately ignore <c>EnvMan.instance.m_currentEnv</c> here —
        /// it varies per biome (Meadows is brighter than Mountain, Plains has
        /// warmer sun, etc.) so reading "noon" from it makes consecutive
        /// compile tiles end up at different brightness depending on which
        /// biome the player was standing in when each tile was captured.
        /// Fixed values give every tile the same lighting, which is what the
        /// "normalize" setting is supposed to do.
        /// </summary>
        public static SavedLighting OverrideLightingToNoon()
        {
            var saved = new SavedLighting
            {
                Active      = true,
                SunDir      = Shader.GetGlobalVector(_idSunDir),
                SunColor    = Shader.GetGlobalColor(_idSunColor),
                Ambient     = Shader.GetGlobalColor(_idAmbient),
                SunFogColor = Shader.GetGlobalColor(_idSunFogColor),
            };

            // Neutral bright daylight — tuned by eye to give Meadows-ish
            // brightness regardless of which biome the capture happened in.
            Color ambient  = new Color(0.80f, 0.80f, 0.80f, 1f);
            Color sunColor = Color.white * 1.2f;
            Color sunFog   = Color.white;
            const float sunAngle = 60f;

            // Noon sun direction — EnvMan.cs:675 rotation at day fraction 0.5.
            Quaternion rot =
                Quaternion.Euler(-90f + sunAngle, 0f, 0f) *
                Quaternion.Euler(0f, -90f, 0f) *
                Quaternion.Euler(-90f + 360f * 0.5f, 0f, 0f);
            Vector3 sunDir = -(rot * Vector3.forward);

            Shader.SetGlobalVector(_idSunDir, new Vector4(sunDir.x, sunDir.y, sunDir.z, 0f));
            Shader.SetGlobalColor(_idSunColor, sunColor);
            Shader.SetGlobalColor(_idAmbient, ambient);
            Shader.SetGlobalColor(_idSunFogColor, sunFog);
            return saved;
        }

        public static void RestoreLighting(SavedLighting saved)
        {
            if (!saved.Active) return;
            Shader.SetGlobalVector(_idSunDir, saved.SunDir);
            Shader.SetGlobalColor(_idSunColor, saved.SunColor);
            Shader.SetGlobalColor(_idAmbient, saved.Ambient);
            Shader.SetGlobalColor(_idSunFogColor, saved.SunFogColor);
        }

        // ── Shader property suppression ─────────────────────────────────────
        // The map material's shader has cloud-related properties (texture/colors/
        // floats) that produce the animated cloud overlay. Names vary across
        // Valheim versions, so we enumerate properties and zero anything
        // containing a given substring. Caller passes the saved list back to
        // RestoreShaderProps in a finally block.

        public struct SavedShaderProp
        {
            public string Name;
            public ShaderPropertyType Type;
            public Texture Tex;
            public float Float;
            public Color Color;
            public Vector4 Vec;
        }

        public static List<SavedShaderProp> SuppressShaderPropsContaining(Material mat, string substring)
        {
            var saved = new List<SavedShaderProp>();
            if (mat == null || mat.shader == null) return saved;

            var shader = mat.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf(substring, System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                var type = shader.GetPropertyType(i);
                var prop = new SavedShaderProp { Name = name, Type = type };
                switch (type)
                {
                    case ShaderPropertyType.Texture:
                        prop.Tex = mat.GetTexture(name);
                        mat.SetTexture(name, Texture2D.blackTexture);
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        prop.Float = mat.GetFloat(name);
                        mat.SetFloat(name, 0f);
                        break;
                    case ShaderPropertyType.Color:
                        prop.Color = mat.GetColor(name);
                        mat.SetColor(name, new Color(0f, 0f, 0f, 0f));
                        break;
                    case ShaderPropertyType.Vector:
                        prop.Vec = mat.GetVector(name);
                        mat.SetVector(name, Vector4.zero);
                        break;
                }
                saved.Add(prop);
            }
            return saved;
        }

        public static void RestoreShaderProps(Material mat, List<SavedShaderProp> saved)
        {
            if (mat == null || saved == null) return;
            foreach (var p in saved)
            {
                switch (p.Type)
                {
                    case ShaderPropertyType.Texture: mat.SetTexture(p.Name, p.Tex); break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:   mat.SetFloat(p.Name, p.Float); break;
                    case ShaderPropertyType.Color:   mat.SetColor(p.Name, p.Color); break;
                    case ShaderPropertyType.Vector:  mat.SetVector(p.Name, p.Vec); break;
                }
            }
        }
    }
}
