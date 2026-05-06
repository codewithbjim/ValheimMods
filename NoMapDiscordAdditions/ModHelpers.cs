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
        public static class EffectiveConfig
        {
            public static string WebhookUrl => ServerSyncCompat.IsActive
                ? Plugin.WebhookUrl?.Value
                : NetworkConfigSync.EffectiveWebhookUrl;

            public static bool UseTextureCapture => ServerSyncCompat.IsActive
                ? Plugin.CaptureMethod.Value == Plugin.CaptureMethodMode.TextureCapture
                : NetworkConfigSync.EffectiveUseTextureCapture;

            public static int CaptureSuperSize => ServerSyncCompat.IsActive
                ? Plugin.CaptureSuperSize.Value
                : NetworkConfigSync.EffectiveCaptureSuperSize;

            public static bool SpoilerImageData => ServerSyncCompat.IsActive
                ? Plugin.SpoilerImageData.Value
                : NetworkConfigSync.EffectiveSpoilerImageData;

            public static bool HideClouds => ServerSyncCompat.IsActive
                ? Plugin.HideClouds.Value
                : NetworkConfigSync.EffectiveHideClouds;

            public static bool EnableCartographyTableLabels => ServerSyncCompat.IsActive
                ? (Plugin.EnableCartographyTableLabels?.Value ?? true)
                : NetworkConfigSync.EffectiveEnableCartographyTableLabels;

            public static int SendMaxDimension => ServerSyncCompat.IsActive
                ? (Plugin.SendMaxDimension?.Value ?? 2560)
                : NetworkConfigSync.EffectiveSendMaxDimension;

            public static bool SpawnLabelIncludeDistance => ServerSyncCompat.IsActive
                ? (Plugin.SpawnLabelIncludeDistance?.Value ?? true)
                : NetworkConfigSync.EffectiveSpawnLabelIncludeDistance;

            public static bool SpawnLabelIncludeMapItemSources => ServerSyncCompat.IsActive
                ? (Plugin.SpawnLabelIncludeMapItemSources?.Value ?? false)
                : NetworkConfigSync.EffectiveSpawnLabelIncludeMapItemSources;

            public static int CompileMaxDimension => ServerSyncCompat.IsActive
                ? (Plugin.CompileMaxDimension?.Value ?? 2560)
                : NetworkConfigSync.EffectiveCompileMaxDimension;

            public static string CompileMessageTemplate => ServerSyncCompat.IsActive
                ? (Plugin.CompileMessageTemplate?.Value
                    ?? "{player} compiled a map from {tileCount} cartography tables.")
                : NetworkConfigSync.EffectiveCompileMessageTemplate;

            public static string MessageTemplate => ServerSyncCompat.IsActive
                ? (Plugin.MessageTemplate?.Value
                    ?? "{player} shared a map update from {biome}{spawnDir}")
                : NetworkConfigSync.EffectiveMessageTemplate;
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
