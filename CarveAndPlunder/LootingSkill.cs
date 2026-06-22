using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace CarveAndPlunder
{
    // The custom "Looting" skill.
    //
    // SCAFFOLD NOTE: this is a self-contained implementation against Valheim's
    // native Skills system so the project compiles and the skill works (XP
    // accumulates, GetSkillFactor scales, it persists by enum-hash for free).
    // The display name is registered as a localization word (see SkillPatches),
    // the icon is a PNG embedded in the DLL, and the description is real. The
    // remaining planned path is to replace this file + SkillPatches.cs with
    // Smoothbrain SkillManager for per-skill XP-rate config. All callers go
    // through Raise()/GetFactor() so that swap stays isolated to these two files.
    internal static class LootingSkill
    {
        public const string InternalName = "CarvePlunder_Looting";

        // What the skills UI shows for the skill name.
        public const string DisplayName = "Looting";

        // Embedded icon resource (csproj <EmbeddedResource>); matched by suffix so
        // the exact manifest-name prefix doesn't matter.
        private const string IconResourceSuffix = "Looting.png";

        // A stable hashed enum value that won't collide with vanilla SkillTypes.
        public static readonly Skills.SkillType SkillType =
            (Skills.SkillType)InternalName.GetStableHashCode();

        // Valheim builds the skill name as "$skill_" + m_skill.ToString().ToLower()
        // (SkillsDialog / Skills.RaiseSkill). For our hashed enum value that token
        // is "skill_<hash>", so we register a word under exactly that key.
        public static string LocalizationKey => "skill_" + SkillType.ToString().ToLower();

        public static Skills.SkillDef Def { get; private set; }

        public static void Init()
        {
            if (Def != null) return;

            Def = new Skills.SkillDef
            {
                m_skill = SkillType,
                m_description = BuildDescription(),
                m_increseStep = 1f,
                m_icon = LoadEmbeddedIcon(),
            };
        }

        // Reads the configured loot bonus so the in-game tooltip always matches
        // what Looting actually does. LootExtraMax is the number of extra items
        // added per drop stack at level 100 (scales down with skill).
        private static string BuildDescription()
        {
            int extra = ModConfig.LootExtraMax.Value;
            return "Looting humanoid corpses faster and for greater reward.\n" +
                   $"At level 100, you recover up to {extra} extra item(s) per drop.";
        }

        // Registers the localized skill name. Called from the Localization
        // SetupLanguage patch so it survives every language (re)load.
        public static void RegisterLocalization(Localization loc)
        {
            loc?.AddWord(LocalizationKey, DisplayName);
        }

        public static void Raise(Player player, float amount)
        {
            if (player == null || amount <= 0f) return;
            player.RaiseSkill(SkillType, amount);
        }

        // 0..1 over levels 0..100.
        public static float GetFactor(Player player)
        {
            if (player == null) return 0f;
            return player.GetSkillFactor(SkillType);
        }

        // Loads the icon PNG embedded in the DLL into a Sprite. Falls back to a
        // 1x1 stand-in if the resource is missing or unreadable, so the skills UI
        // never dereferences a null icon.
        private static Sprite LoadEmbeddedIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith(IconResourceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        resName = n;
                        break;
                    }
                }
                if (resName == null) { ModLog.Diag("Looting icon resource not found; using fallback."); return MakeFallbackIcon(); }

                byte[] bytes;
                using (Stream s = asm.GetManifestResourceStream(resName))
                using (var ms = new MemoryStream())
                {
                    if (s == null) return MakeFallbackIcon();
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                Texture2D tex = DecodePngToTexture(bytes);
                if (tex == null) { ModLog.Diag("Looting icon failed to decode; using fallback."); return MakeFallbackIcon(); }
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;

                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                     new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e)
            {
                ModLog.Diag($"Looting icon load error: {e.Message}; using fallback.");
                return MakeFallbackIcon();
            }
        }

        // Decode PNG bytes to a Texture2D via System.Drawing instead of
        // UnityEngine.ImageConversion. The latter has a ReadOnlySpan<byte>
        // overload that drags in System.ReadOnlySpan during overload
        // resolution, which net481's mscorlib doesn't expose (CS7069).
        // Mirrors the sibling NoMapDiscordAdditions decoder.
        private static Texture2D DecodePngToTexture(byte[] pngBytes)
        {
            using (var ms = new MemoryStream(pngBytes))
            using (var src = new System.Drawing.Bitmap(ms))
            {
                int w = src.Width, h = src.Height;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var pixels = new Color32[w * h];
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var bd = src.LockBits(rect,
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int srcStride = bd.Stride;
                    byte[] row = new byte[srcStride];
                    for (int y = 0; y < h; y++)
                    {
                        var rowPtr = new IntPtr(bd.Scan0.ToInt64() + (long)y * srcStride);
                        System.Runtime.InteropServices.Marshal.Copy(rowPtr, row, 0, srcStride);

                        // GDI gives BGRA top-down; Texture2D wants RGBA bottom-up.
                        int dstY = h - 1 - y;
                        int dstRow = dstY * w;
                        for (int x = 0; x < w; x++)
                        {
                            int si = x * 4;
                            pixels[dstRow + x] = new Color32(
                                row[si + 2], row[si + 1], row[si], row[si + 3]);
                        }
                    }
                }
                finally
                {
                    src.UnlockBits(bd);
                }
                tex.SetPixels32(pixels);
                tex.Apply();
                return tex;
            }
        }

        // A 1x1 sprite stand-in so the skills UI never dereferences a null icon.
        private static Sprite MakeFallbackIcon()
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, new Color(0.78f, 0.66f, 0.39f));
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f));
        }
    }
}
