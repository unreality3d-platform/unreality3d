using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    public class U3DTextureOptimizationWindow : EditorWindow
    {
        private class TextureFileData
        {
            public string path;
            public string fileName;
            public TextureImporter importer;
            public long fileSize;
            public bool isSelected;
            public TextureImporterType textureType;
            public TextureImporterShape textureShape;
            public int currentMaxSize;
            public bool hasCrunchCompression;
            public bool generateMipMaps;
            public bool hasWebGLOverride;

            public TextureFileData(string texturePath)
            {
                path = texturePath;
                fileName = Path.GetFileName(texturePath);
                importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;

                var fileInfo = new FileInfo(texturePath);
                fileSize = fileInfo.Exists ? fileInfo.Length : 0;

                isSelected = true;

                if (importer != null)
                {
                    textureType = importer.textureType;
                    textureShape = importer.textureShape;
                    generateMipMaps = importer.mipmapEnabled;

                    var webglSettings = importer.GetPlatformTextureSettings("WebGL");
                    hasWebGLOverride = webglSettings.overridden;

                    if (hasWebGLOverride)
                    {
                        currentMaxSize = webglSettings.maxTextureSize;
                        hasCrunchCompression = webglSettings.crunchedCompression;
                    }
                    else
                    {
                        var defaultSettings = importer.GetDefaultPlatformTextureSettings();
                        currentMaxSize = defaultSettings.maxTextureSize;
                        hasCrunchCompression = defaultSettings.crunchedCompression;
                    }
                }
                else
                {
                    textureType = TextureImporterType.Default;
                    textureShape = TextureImporterShape.Texture2D;
                    currentMaxSize = 2048;
                    hasCrunchCompression = false;
                    generateMipMaps = true;
                    hasWebGLOverride = false;
                }
            }

            public bool ShouldExcludeFromSizeOptimization()
            {
                return textureType == TextureImporterType.NormalMap ||
                       textureShape == TextureImporterShape.TextureCube;
            }

            public bool ShouldExcludeFromCompression()
            {
                return textureType == TextureImporterType.NormalMap ||
                       textureType == TextureImporterType.Lightmap ||
                       textureShape == TextureImporterShape.TextureCube;
            }
        }

        private List<TextureFileData> textureFiles = new List<TextureFileData>();
        private Vector2 scrollPosition;

        private bool excludeNormalMaps = true;
        private bool excludeSkyboxes = true;

        private List<string> availableFolders = new List<string>();
        private Dictionary<string, bool> folderExclusions = new Dictionary<string, bool>();

        public static void ShowWindow()
        {
            var window = GetWindow<U3DTextureOptimizationWindow>("Texture Optimization");
            window.minSize = new Vector2(500, 400);
            window.RefreshTextureList();
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Texture Optimization for WebGL", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select textures and apply WebGL settings. Normal maps and skyboxes are automatically protected from size and compression changes.", MessageType.Info);
            EditorGUILayout.Space(10);

            DrawControls();
            EditorGUILayout.Space(5);
            DrawFilters();
            EditorGUILayout.Space(5);
            DrawStatusLine();
            EditorGUILayout.Space(5);
            DrawTextureList();
            EditorGUILayout.Space(10);
            DrawActions();
        }

        private void DrawControls()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh List"))
            {
                RefreshTextureList();
            }

            if (GUILayout.Button("Select All"))
            {
                GetFilteredTextures().ForEach(t => t.isSelected = true);
            }

            if (GUILayout.Button("Select None"))
            {
                textureFiles.ForEach(t => t.isSelected = false);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilters()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Exclude types:", GUILayout.Width(95));
            excludeNormalMaps = GUILayout.Toggle(excludeNormalMaps, "Normal Maps", EditorStyles.miniButton);
            excludeSkyboxes = GUILayout.Toggle(excludeSkyboxes, "Skyboxes", EditorStyles.miniButton);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (availableFolders.Any())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Exclude folders:", GUILayout.Width(95));

                foreach (var folder in availableFolders)
                {
                    bool currentState = folderExclusions.ContainsKey(folder) && folderExclusions[folder];
                    bool newState = GUILayout.Toggle(currentState, folder, EditorStyles.miniButton);
                    folderExclusions[folder] = newState;
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawStatusLine()
        {
            var filtered = GetFilteredTextures();
            var selectedCount = filtered.Count(t => t.isSelected);
            EditorGUILayout.LabelField($"{filtered.Count} textures shown, {selectedCount} selected", EditorStyles.miniLabel);
        }

        private void DrawTextureList()
        {
            var filtered = GetFilteredTextures();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            foreach (var textureFile in filtered)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                textureFile.isSelected = EditorGUILayout.Toggle(textureFile.isSelected, GUILayout.Width(20));

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(textureFile.fileName, EditorStyles.boldLabel);

                string status = textureFile.hasWebGLOverride ? $"WebGL: {textureFile.currentMaxSize}px" : "No WebGL override";
                string typeInfo = "";
                if (textureFile.textureType == TextureImporterType.NormalMap) typeInfo = " | Normal Map";
                if (textureFile.textureShape == TextureImporterShape.TextureCube) typeInfo = " | Cubemap";

                EditorGUILayout.LabelField(
                    $"{textureFile.fileSize / 1024:F0} KB | {status} | Mips: {(textureFile.generateMipMaps ? "On" : "Off")} | Crunch: {(textureFile.hasCrunchCompression ? "On" : "Off")}{typeInfo}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();

                if (GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(30)))
                {
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureFile.path);
                    Selection.activeObject = texture;
                    EditorGUIUtility.PingObject(texture);
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawActions()
        {
            var filtered = GetFilteredTextures();
            var selected = filtered.Where(t => t.isSelected).ToList();

            if (!selected.Any())
            {
                EditorGUILayout.HelpBox("Select textures above to enable actions.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"Actions ({selected.Count} selected)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Max Texture Size", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Set 512px", GUILayout.Height(28)))
            {
                ApplyMaxSize(512);
            }
            if (GUILayout.Button("Set 1024px", GUILayout.Height(28)))
            {
                ApplyMaxSize(1024);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Crunch Compression", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Enable", GUILayout.Height(28)))
            {
                ApplyCrunchCompression(true);
            }
            if (GUILayout.Button("Disable", GUILayout.Height(28)))
            {
                ApplyCrunchCompression(false);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Mip Maps", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Enable", GUILayout.Height(28)))
            {
                ApplyMipMaps(true);
            }
            if (GUILayout.Button("Disable", GUILayout.Height(28)))
            {
                ApplyMipMaps(false);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private List<TextureFileData> GetFilteredTextures()
        {
            var filtered = textureFiles.AsEnumerable();

            if (excludeNormalMaps)
                filtered = filtered.Where(t => t.textureType != TextureImporterType.NormalMap);

            if (excludeSkyboxes)
                filtered = filtered.Where(t => t.textureShape != TextureImporterShape.TextureCube);

            foreach (var exclusion in folderExclusions.Where(kv => kv.Value))
            {
                filtered = filtered.Where(t => !t.path.ToLower().Contains(exclusion.Key.ToLower()));
            }

            return filtered.ToList();
        }

        private void RefreshTextureList()
        {
            textureFiles.Clear();
            var textureGuids = AssetDatabase.FindAssets("t:Texture2D");

            foreach (var guid in textureGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                if (ShouldExcludeSystemAsset(path))
                    continue;

                textureFiles.Add(new TextureFileData(path));
            }

            textureFiles = textureFiles.OrderByDescending(t => t.fileSize).ToList();
            RefreshFolderList();
        }

        private void RefreshFolderList()
        {
            var folders = new HashSet<string>();

            foreach (var textureFile in textureFiles)
            {
                var path = textureFile.path;
                if (path.StartsWith("Assets/"))
                {
                    var pathParts = path.Substring(7).Split('/');
                    if (pathParts.Length > 1)
                    {
                        folders.Add(pathParts[0]);
                    }
                }
            }

            availableFolders = folders.OrderBy(f => f).ToList();

            var newExclusions = new Dictionary<string, bool>();
            foreach (var folder in availableFolders)
            {
                newExclusions[folder] = folderExclusions.ContainsKey(folder) && folderExclusions[folder];
            }
            folderExclusions = newExclusions;
        }

        private bool ShouldExcludeSystemAsset(string path)
        {
            var pathLower = path.ToLower();
            var fileName = Path.GetFileName(pathLower);

            if (pathLower.Contains("packages/") ||
                pathLower.Contains("library/"))
                return true;

            if (pathLower.EndsWith(".ttf") || pathLower.EndsWith(".otf"))
                return true;

            if (IsFontRelatedTexture(fileName, pathLower))
                return true;

            if (pathLower.Contains("unity_builtin_extra") ||
                pathLower.Contains("default-") ||
                pathLower.Contains("builtin_"))
                return true;

            if (pathLower.Contains("preview") &&
                (pathLower.Contains("material") || pathLower.Contains("shader")))
                return true;

            return false;
        }

        private bool IsFontRelatedTexture(string fileName, string pathLower)
        {
            if (pathLower.Contains("textmeshpro") ||
                fileName.Contains("tmp") ||
                fileName.Contains("sdf"))
                return true;

            if (fileName.Contains("atlas") && (
                fileName.Contains("font") ||
                fileName.Contains("liberation") ||
                fileName.Contains("arial") ||
                fileName.Contains("opensans")))
                return true;

            string[] fontKeywords = {
                "liberation", "arial", "opensans", "roboto", "ubuntu",
                "calibri", "times", "helvetica", "verdana", "georgia",
                "trebuchet", "impact", "comic", "courier"
            };

            foreach (var keyword in fontKeywords)
            {
                if (fileName.Contains(keyword) && (
                    fileName.Contains("sdf") ||
                    fileName.Contains("atlas") ||
                    fileName.Contains("font")))
                    return true;
            }

            if (fileName.Contains(" sdf") ||
                fileName.EndsWith("_atlas") ||
                fileName.EndsWith(" atlas") ||
                fileName.Contains("lut"))
                return true;

            return false;
        }

        private void ApplyMaxSize(int maxSize)
        {
            var selected = GetFilteredTextures().Where(t => t.isSelected).ToList();
            int applied = 0;
            int skipped = 0;

            foreach (var textureFile in selected)
            {
                if (textureFile.importer == null) continue;

                if (textureFile.ShouldExcludeFromSizeOptimization())
                {
                    skipped++;
                    continue;
                }

                var webglSettings = textureFile.importer.GetPlatformTextureSettings("WebGL");
                webglSettings.overridden = true;
                webglSettings.maxTextureSize = maxSize;
                textureFile.importer.SetPlatformTextureSettings(webglSettings);
                EditorUtility.SetDirty(textureFile.importer);
                textureFile.importer.SaveAndReimport();
                applied++;
            }

            AssetDatabase.Refresh();
            RefreshTextureList();

            string message = $"Set max size to {maxSize}px on {applied} textures.";
            if (skipped > 0) message += $"\nSkipped {skipped} (normal maps/cubemaps preserved).";
            EditorUtility.DisplayDialog("Max Size Applied", message, "OK");
        }

        private void ApplyCrunchCompression(bool enable)
        {
            var selected = GetFilteredTextures().Where(t => t.isSelected).ToList();
            int applied = 0;
            int skipped = 0;

            foreach (var textureFile in selected)
            {
                if (textureFile.importer == null) continue;

                if (enable && textureFile.ShouldExcludeFromCompression())
                {
                    skipped++;
                    continue;
                }

                var webglSettings = textureFile.importer.GetPlatformTextureSettings("WebGL");
                webglSettings.overridden = true;
                webglSettings.crunchedCompression = enable;

                if (enable)
                {
                    webglSettings.compressionQuality = 50;
                    webglSettings.format = textureFile.importer.DoesSourceTextureHaveAlpha()
                        ? TextureImporterFormat.DXT5Crunched
                        : TextureImporterFormat.DXT1Crunched;
                }

                textureFile.importer.SetPlatformTextureSettings(webglSettings);
                EditorUtility.SetDirty(textureFile.importer);
                textureFile.importer.SaveAndReimport();
                applied++;
            }

            AssetDatabase.Refresh();
            RefreshTextureList();

            string action = enable ? "Enabled" : "Disabled";
            string message = $"{action} crunch compression on {applied} textures.";
            if (skipped > 0) message += $"\nSkipped {skipped} (normal maps/lightmaps/cubemaps preserved).";
            EditorUtility.DisplayDialog("Crunch Compression", message, "OK");
        }

        private void ApplyMipMaps(bool enable)
        {
            var selected = GetFilteredTextures().Where(t => t.isSelected).ToList();
            int modified = 0;

            foreach (var textureFile in selected)
            {
                if (textureFile.importer != null && textureFile.importer.mipmapEnabled != enable)
                {
                    textureFile.importer.mipmapEnabled = enable;
                    EditorUtility.SetDirty(textureFile.importer);
                    textureFile.importer.SaveAndReimport();
                    modified++;
                }
            }

            AssetDatabase.Refresh();
            RefreshTextureList();

            string action = enable ? "Enabled" : "Disabled";
            EditorUtility.DisplayDialog("Mip Maps", $"{action} mip maps on {modified} textures.", "OK");
        }
    }
}