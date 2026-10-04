using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor.Tools
{
    /// <summary>
    /// Creates selective update packages for existing Unreality3D template users
    /// Excludes user content while updating core U3D systems
    /// </summary>
    public static class TemplatePackageExporter
    {
        private const string EXPORT_PATH = "Build/Updates";

        // CORE U3D SYSTEMS - Always include in updates
        // Only paths under Assets/ can ship. AssetDatabase.ExportPackage ignores
        // anything outside it, including .github/workflows.
        private static readonly string[] CORE_UPDATE_PATHS = {
            "Assets/U3D",                              // Core U3D SDK and tools
            "Assets/U3D_SDK",                          // Publishing and monetization systems  
            "Assets/Plugins/U3D",                      // U3D-specific plugins
            "Assets/StreamingAssets/U3D",              // U3D streaming assets
            "Assets/Settings",                         // URP/renderer/quality/post-process assets
        };

        // USER CONTENT - NEVER include in updates (preserve user work)
        private static readonly string[] PRESERVE_USER_CONTENT = {
            "Assets/Scenes",           // User scenes
            "Assets/Materials",        // User materials
            "Assets/Textures",         // User textures  
            "Assets/Models",           // User models
            "Assets/Scripts",          // User scripts
            "Assets/Audio",            // User audio
            "Assets/Animations",       // User animations
            "Assets/Prefabs",          // User prefabs (not U3D prefabs)
            "Assets/Resources",        // User resources
            "Assets/StreamingAssets/Audio", // User streaming audio
            "Assets/StreamingAssets/Video", // User streaming video
        };

        // CREATOR-SPECIFIC FILES - Exclude from updates (contains per-creator data)
        // Assets/U3D_ProjectData must never be added to CORE_UPDATE_PATHS. The object ID
        // registry there holds the creator's own ID counter and prefab table; overwriting it
        // reissues numbers already held by published objects and repoints every spawn index.
        private static readonly string[] EXCLUDE_FROM_UPDATES = {
            "Assets/U3D/Resources/U3DCreatorData.asset",
            "Assets/U3D_ProjectData/Resources/U3DObjectIdRegistry.asset",
        };

        /// <summary>
        /// Main export method - can be called from command line automation
        /// </summary>
        public static void ExportUpdatePackage(string customVersion = null)
        {
            try
            {
                string version = customVersion ?? GetVersionFromCommandLine() ?? GetDefaultVersion();
                string fileName = $"{version}.unitypackage";
                string fullPath = Path.Combine(EXPORT_PATH, fileName);

                // Ensure export directory exists
                Directory.CreateDirectory(EXPORT_PATH);

                Debug.Log($"🚀 Exporting U3D Template Update Package");
                Debug.Log($"📋 Version: {version}");
                Debug.Log($"📦 Output: {fullPath}");

                // Build comprehensive asset list for update
                var assetsToExport = new List<string>();
                int coreAssets = 0;

                // Include all core U3D systems
                foreach (string corePath in CORE_UPDATE_PATHS)
                {
                    if (AssetDatabase.IsValidFolder(corePath) || File.Exists(corePath))
                    {
                        assetsToExport.Add(corePath);
                        coreAssets++;
                        Debug.Log($"✅ Core: {corePath}");
                    }
                    else
                    {
                        Debug.LogWarning($"⚠️ Core path missing: {corePath}");
                    }
                }

                // Validate we have assets to export
                if (assetsToExport.Count == 0)
                {
                    Debug.LogError("❌ No valid assets found to export!");
                    return;
                }

                Debug.Log($"📊 Export Summary: {coreAssets} core systems");

                // Filter out creator-specific files
                var excludeSet = new HashSet<string>(EXCLUDE_FROM_UPDATES, StringComparer.OrdinalIgnoreCase);

                // Expand folders into individual asset paths so we can filter
                var expandedAssets = new List<string>();
                foreach (string path in assetsToExport)
                {
                    if (AssetDatabase.IsValidFolder(path))
                    {
                        string[] guids = AssetDatabase.FindAssets("", new[] { path });
                        foreach (string guid in guids)
                        {
                            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                            if (!excludeSet.Contains(assetPath))
                            {
                                expandedAssets.Add(assetPath);
                            }
                        }
                    }
                    else if (!excludeSet.Contains(path))
                    {
                        expandedAssets.Add(path);
                    }
                }

                // Export the update package
                AssetDatabase.ExportPackage(
                    expandedAssets.ToArray(),
                    fullPath,
                    ExportPackageOptions.Default
                );

                // Verify and report success
                if (File.Exists(fullPath))
                {
                    var fileInfo = new FileInfo(fullPath);
                    Debug.Log($"✅ Update package exported successfully!");
                    Debug.Log($"📄 File: {Path.GetFullPath(fullPath)}");
                    Debug.Log($"📊 Size: {GetFileSizeString(fileInfo.Length)}");
                    Debug.Log($"🎯 Ready for distribution to existing U3D template users");

                    // Create version info file for automation
                    CreateVersionInfoFile(version, fullPath, assetsToExport.Count);

                    // Optional: Reveal in finder/explorer
                    if (Application.isBatchMode == false)
                    {
                        EditorUtility.RevealInFinder(fullPath);
                    }
                }
                else
                {
                    Debug.LogError("❌ Package export failed - file not created");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Template update export failed: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Command line export method for GitHub Actions
        /// </summary>
        public static void ExportUpdatePackageCommandLine()
        {
            Debug.Log("🤖 Command line template update export started");

            try
            {
                ExportUpdatePackage();
                Debug.Log("✅ Command line export completed successfully");
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ Command line export failed: {ex.Message}");
                EditorApplication.Exit(1);
                return;
            }

            // Clean exit for automation
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }

        /// <summary>
        /// Validates the template structure before export
        /// </summary>

        public static void ValidateTemplateStructure()
        {
            Debug.Log("🔍 Validating U3D Template structure for updates...");

            bool isValid = true;
            int coreFound = 0, userContentProtected = 0;

            // Check core systems
            Debug.Log("=== Core Systems ===");
            foreach (string path in CORE_UPDATE_PATHS)
            {
                if (AssetDatabase.IsValidFolder(path) || File.Exists(path))
                {
                    Debug.Log($"✅ {path}");
                    coreFound++;
                }
                else
                {
                    Debug.LogError($"❌ Missing core: {path}");
                    isValid = false;
                }
            }

            // Verify user content protection
            Debug.Log("=== User Content Protection ===");
            foreach (string path in PRESERVE_USER_CONTENT)
            {
                if (AssetDatabase.IsValidFolder(path))
                {
                    Debug.Log($"🛡️ Protected: {path} (will be preserved during updates)");
                    userContentProtected++;
                }
            }

            // Summary
            Debug.Log($"📊 Validation Summary:");
            Debug.Log($"   Core systems: {coreFound}/{CORE_UPDATE_PATHS.Length}");
            Debug.Log($"   Protected paths: {userContentProtected}");

            if (isValid && coreFound >= CORE_UPDATE_PATHS.Length - 1) // Allow 1 missing for flexibility
            {
                Debug.Log("✅ Template structure is valid for update package creation");
            }
            else
            {
                Debug.LogError("❌ Template structure issues detected - fix before creating update packages");
            }
        }

        private static string GetVersionFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-packageVersion")
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static string GetDefaultVersion()
        {
            string versionFilePath = "Assets/U3D/Resources/version.txt";
            if (File.Exists(versionFilePath))
            {
                string version = File.ReadAllText(versionFilePath).Trim();
                if (!string.IsNullOrEmpty(version))
                {
                    return $"u3d-update-{version}";
                }
            }

            // Fallback if version.txt is missing
            return $"u3d-update-{DateTime.Now:MM-dd-yy-HH-mm-ss}";
        }

        private static string GetFileSizeString(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
            public string timestamp;
            public string packagePath;
            public int assetCount;
            public string type;
        }

        private static void CreateVersionInfoFile(string version, string packagePath, int assetCount)
        {
            var versionInfo = new VersionInfo
            {
                version = version,
                timestamp = DateTime.UtcNow.ToString("O"),
                packagePath = packagePath,
                assetCount = assetCount,
                type = "u3d-template-update"
            };

            string jsonPath = Path.Combine(EXPORT_PATH, $"version-info-{version}.json");
            File.WriteAllText(jsonPath, JsonUtility.ToJson(versionInfo, true));

            Debug.Log($"📋 Version info saved: {jsonPath}");
        }
    }
}