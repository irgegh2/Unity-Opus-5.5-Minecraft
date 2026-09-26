using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MCR.EditorTools
{
    /// <summary>
    /// Imports vanilla Minecraft textures from the user's own installed Java Edition
    /// version jar. No Mojang texture binaries are stored in source control.
    /// </summary>
    [InitializeOnLoad]
    public static class VanillaTextureImporter
    {
        const string TargetRoot = "Assets/_Game/Generated/Resources/Vanilla";
        const string MarkerPath = TargetRoot + "/.source.txt";

        static VanillaTextureImporter()
        {
            EditorApplication.delayCall += TryAutoImport;
        }

        static void TryAutoImport()
        {
            if (Application.isPlaying || File.Exists(MarkerPath)) return;
            try { EnsureImported(); }
            catch (Exception e) { Debug.LogWarning("[VanillaTextures] Auto import skipped: " + e.Message); }
        }

        [MenuItem("Tools/Opus 5.5 Minecraft/Import Vanilla Textures")]
        public static void ImportFromInstalledMinecraft()
        {
            if (!EnsureImported(true))
                EditorUtility.DisplayDialog("Vanilla textures", "Minecraft Java version jar was not found. Install/launch Minecraft Java once, or set MCR_MINECRAFT_JAR to a version jar.", "OK");
        }

        public static bool EnsureImported(bool force = false)
        {
            string jar = FindVersionJar();
            if (string.IsNullOrEmpty(jar) || !File.Exists(jar))
            {
                Debug.LogWarning("[VanillaTextures] Minecraft Java version jar not found; keeping procedural texture fallback.");
                return false;
            }

            string stamp = jar + "|" + File.GetLastWriteTimeUtc(jar).Ticks;
            if (!force && File.Exists(MarkerPath) && File.ReadAllText(MarkerPath) == stamp)
                return true;

            Directory.CreateDirectory(TargetRoot);
            int count = 0;
            using (var zip = ZipFile.OpenRead(jar))
            {
                const string prefix = "assets/minecraft/textures/";
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || !entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string rel = entry.FullName.Substring(prefix.Length);
                    // Categories actually consumed by this project now; entity textures are
                    // imported too for the ongoing vanilla-UV model conversion.
                    if (!(rel.StartsWith("block/", StringComparison.Ordinal) ||
                          rel.StartsWith("item/", StringComparison.Ordinal) ||
                          rel.StartsWith("particle/", StringComparison.Ordinal) ||
                          rel.StartsWith("entity/", StringComparison.Ordinal) ||
                          rel.StartsWith("environment/", StringComparison.Ordinal)))
                        continue;

                    string dst = Path.Combine(TargetRoot, rel).Replace('\\', '/');
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    using (var input = entry.Open())
                    using (var output = File.Create(dst))
                        input.CopyTo(output);
                    count++;
                }
            }

            File.WriteAllText(MarkerPath, stamp);
            AssetDatabase.Refresh();
            ConfigureImporters();
            AssetDatabase.Refresh();
            Debug.Log("[VanillaTextures] Imported " + count + " textures from " + Path.GetFileName(jar));
            return count > 0;
        }

        static void ConfigureImporters()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TargetRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;
                bool changed = !imp.isReadable || imp.mipmapEnabled ||
                               imp.textureCompression != TextureImporterCompression.Uncompressed ||
                               imp.filterMode != FilterMode.Point;
                imp.isReadable = true;
                imp.mipmapEnabled = false;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.filterMode = FilterMode.Point;
                imp.wrapMode = path.Contains("/block/") ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                imp.npotScale = TextureImporterNPOTScale.None;
                if (changed) imp.SaveAndReimport();
            }
        }

        static string FindVersionJar()
        {
            string env = Environment.GetEnvironmentVariable("MCR_MINECRAFT_JAR");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] roots =
            {
                Path.Combine(home, "Library/Application Support/minecraft/versions"),
                Path.Combine(home, ".minecraft/versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft/versions")
            };

            return roots.Where(Directory.Exists)
                .SelectMany(root =>
                {
                    try { return Directory.GetFiles(root, "*.jar", SearchOption.AllDirectories); }
                    catch { return Array.Empty<string>(); }
                })
                .Where(path => !path.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
