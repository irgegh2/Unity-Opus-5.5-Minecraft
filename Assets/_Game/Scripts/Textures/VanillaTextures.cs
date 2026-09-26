using System;
using System.Collections.Generic;
using UnityEngine;

namespace MCR
{
    /// <summary>
    /// Reads vanilla Minecraft block/item textures imported by the editor tool under
    /// Resources/Vanilla. The game still has its procedural generator as a fallback,
    /// so a checkout without a local Minecraft installation remains playable.
    /// </summary>
    public static class VanillaTextures
    {
        // Bump whenever the vanilla texture lookup/baking rules change. Res.NameHash
        // includes this so an old procedural Texture2DArray cannot silently win.
        public const int Revision = 0x56414E32; // "VAN2"

        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetRuntimeState() => cache.Clear();

        public static bool TryPixels(string registeredName, out Color32[] pixels)
        {
            pixels = null;
            if (string.IsNullOrEmpty(registeredName)) return false;

            string category;
            string name;
            if (registeredName.StartsWith("item/", StringComparison.Ordinal))
            {
                category = "item";
                name = registeredName.Substring(5);
            }
            else if (registeredName.StartsWith("particle/", StringComparison.Ordinal))
            {
                category = "particle";
                name = registeredName.Substring(9);
            }
            else
            {
                category = "block";
                name = registeredName;
            }

            int frame = 0;
            int hash = name.LastIndexOf('#');
            if (hash >= 0 && hash + 1 < name.Length)
            {
                int.TryParse(name.Substring(hash + 1), out frame);
                name = name.Substring(0, hash);
            }

            // A handful of internal names predate Mojang's modern resource names.
            name = Alias(category, name);

            string key = category + "/" + name;
            if (!cache.TryGetValue(key, out var tex) || tex == null)
            {
                tex = Resources.Load<Texture2D>("Vanilla/" + key);
                cache[key] = tex;
            }
            if (tex == null || !tex.isReadable) return false;

            pixels = Read16(tex, frame);
            return pixels != null;
        }

        static string Alias(string category, string name)
        {
            if (category != "block") return name;
            switch (name)
            {
                case "grass_top": return "grass_block_top";
                case "grass_side": return "grass_block_side";
                case "grass_side_overlay": return "grass_block_side_overlay";
                case "snow_side": return "grass_block_snow";
                case "water": return "water_still";
                case "lava": return "lava_still";
                default: return name;
            }
        }

        static Color32[] Read16(Texture2D tex, int frame)
        {
            int w = tex.width, h = tex.height;
            if (w < 1 || h < 1) return null;

            var src = tex.GetPixels32();
            var dst = new Color32[16 * 16];

            // Vanilla animated textures are normally vertical strips of square frames.
            int frameSize = Math.Min(w, 16);
            int availableFrames = Math.Max(1, h / Math.Max(1, frameSize));
            int f = ((frame % availableFrames) + availableFrames) % availableFrames;
            int fileTop = f * frameSize;

            for (int y = 0; y < 16; y++)
            {
                int syTop = (y * frameSize) / 16;
                int pngYFromTop = fileTop + syTop;
                int unityY = h - 1 - Mathf.Clamp(pngYFromTop, 0, h - 1);
                for (int x = 0; x < 16; x++)
                {
                    int sx = Mathf.Clamp((x * frameSize) / 16, 0, w - 1);
                    dst[y * 16 + x] = src[unityY * w + sx];
                }
            }
            return dst;
        }
    }
}
