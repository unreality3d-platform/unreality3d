using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    public class U3DAudioOptimizationWindow : EditorWindow
    {
        private class AudioFileData
        {
            public string path;
            public string fileName;
            public AudioImporter importer;
            public long fileSize;
            public bool isSelected;
            public string currentFormat;
            public AudioClipLoadType currentLoadType;
            public bool hasWebGLOverride;

            public AudioFileData(string audioPath)
            {
                path = audioPath;
                fileName = Path.GetFileName(audioPath);
                importer = AssetImporter.GetAtPath(audioPath) as AudioImporter;

                var fileInfo = new FileInfo(audioPath);
                fileSize = fileInfo.Exists ? fileInfo.Length : 0;

                isSelected = true;

                if (importer != null)
                {
                    hasWebGLOverride = importer.ContainsSampleSettingsOverride("WebGL");

                    if (hasWebGLOverride)
                    {
                        var webglSettings = importer.GetOverrideSampleSettings("WebGL");
                        currentFormat = webglSettings.compressionFormat.ToString();
                        currentLoadType = webglSettings.loadType;
                    }
                    else
                    {
                        var defaultSettings = importer.defaultSampleSettings;
                        currentFormat = defaultSettings.compressionFormat.ToString();
                        currentLoadType = defaultSettings.loadType;
                    }
                }
                else
                {
                    currentFormat = "Unknown";
                    currentLoadType = AudioClipLoadType.DecompressOnLoad;
                    hasWebGLOverride = false;
                }
            }
        }

        private List<AudioFileData> audioFiles = new List<AudioFileData>();
        private Vector2 scrollPosition;

        private List<string> availableFolders = new List<string>();
        private Dictionary<string, bool> folderExclusions = new Dictionary<string, bool>();

        public static void ShowWindow()
        {
            var window = GetWindow<U3DAudioOptimizationWindow>("Audio Optimization");
            window.minSize = new Vector2(500, 400);
            window.RefreshAudioList();
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Audio Optimization for WebGL", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select audio files and apply WebGL optimization presets. This sets WebGL-specific platform overrides.", MessageType.Info);
            EditorGUILayout.Space(10);

            DrawControls();
            EditorGUILayout.Space(5);
            DrawFilters();
            EditorGUILayout.Space(5);
            DrawStatusLine();
            EditorGUILayout.Space(5);
            DrawAudioList();
            EditorGUILayout.Space(10);
            DrawActions();
        }

        private void DrawControls()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh List"))
            {
                RefreshAudioList();
            }

            if (GUILayout.Button("Select All"))
            {
                GetFilteredAudio().ForEach(a => a.isSelected = true);
            }

            if (GUILayout.Button("Select None"))
            {
                audioFiles.ForEach(a => a.isSelected = false);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilters()
        {
            if (!availableFolders.Any()) return;

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

        private void DrawStatusLine()
        {
            var filtered = GetFilteredAudio();
            var selectedCount = filtered.Count(a => a.isSelected);
            EditorGUILayout.LabelField($"{filtered.Count} audio files shown, {selectedCount} selected", EditorStyles.miniLabel);
        }

        private void DrawAudioList()
        {
            var filtered = GetFilteredAudio();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            foreach (var audioFile in filtered)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                audioFile.isSelected = EditorGUILayout.Toggle(audioFile.isSelected, GUILayout.Width(20));

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(audioFile.fileName, EditorStyles.boldLabel);

                string status = audioFile.hasWebGLOverride ? $"WebGL: {audioFile.currentFormat}" : "No WebGL override";
                EditorGUILayout.LabelField(
                    $"{audioFile.fileSize / 1024:F0} KB | {status} | Load: {audioFile.currentLoadType}",
                    EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();

                if (GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(30)))
                {
                    var audioClip = AssetDatabase.LoadAssetAtPath<AudioClip>(audioFile.path);
                    Selection.activeObject = audioClip;
                    EditorGUIUtility.PingObject(audioClip);
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawActions()
        {
            var filtered = GetFilteredAudio();
            var selected = filtered.Where(a => a.isSelected).ToList();

            if (!selected.Any())
            {
                EditorGUILayout.HelpBox("Select audio files above to enable actions.", MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField($"Actions ({selected.Count} selected)", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Ambient & Music", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Streaming, low quality, size priority", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Apply to Selected", GUILayout.Height(28)))
            {
                ApplyPreset(new AudioImporterSampleSettings
                {
                    loadType = AudioClipLoadType.Streaming,
                    compressionFormat = AudioCompressionFormat.Vorbis,
                    quality = 0.3f,
                    sampleRateSetting = AudioSampleRateSetting.OptimizeSampleRate
                }, "Ambient/Music");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Instant Load", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Decompress on load, medium quality", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Apply to Selected", GUILayout.Height(28)))
            {
                ApplyPreset(new AudioImporterSampleSettings
                {
                    loadType = AudioClipLoadType.DecompressOnLoad,
                    compressionFormat = AudioCompressionFormat.Vorbis,
                    quality = 0.6f,
                    sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate
                }, "Instant Load");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("UI & One-Shot", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Compressed in memory, high quality", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Apply to Selected", GUILayout.Height(28)))
            {
                ApplyPreset(new AudioImporterSampleSettings
                {
                    loadType = AudioClipLoadType.CompressedInMemory,
                    compressionFormat = AudioCompressionFormat.Vorbis,
                    quality = 0.8f,
                    sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate
                }, "UI/One-Shot");
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private List<AudioFileData> GetFilteredAudio()
        {
            var filtered = audioFiles.AsEnumerable();

            foreach (var exclusion in folderExclusions.Where(kv => kv.Value))
            {
                filtered = filtered.Where(a => !a.path.ToLower().Contains(exclusion.Key.ToLower()));
            }

            return filtered.ToList();
        }

        private void RefreshAudioList()
        {
            audioFiles.Clear();
            var audioGuids = AssetDatabase.FindAssets("t:AudioClip");

            foreach (var guid in audioGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                if (ShouldExcludeSystemAsset(path))
                    continue;

                audioFiles.Add(new AudioFileData(path));
            }

            audioFiles = audioFiles.OrderByDescending(a => a.fileSize).ToList();
            RefreshFolderList();
        }

        private void RefreshFolderList()
        {
            var folders = new HashSet<string>();

            foreach (var audioFile in audioFiles)
            {
                var path = audioFile.path;
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

            if (pathLower.Contains("packages/") ||
                pathLower.Contains("library/"))
                return true;

            if (pathLower.Contains("unity_builtin_extra") ||
                pathLower.Contains("default-") ||
                pathLower.Contains("builtin_"))
                return true;

            return false;
        }

        private void ApplyPreset(AudioImporterSampleSettings settings, string presetName)
        {
            var selected = GetFilteredAudio().Where(a => a.isSelected).ToList();

            if (!selected.Any())
            {
                EditorUtility.DisplayDialog("No Selection", "Please select at least one audio file to optimize.", "OK");
                return;
            }

            int applied = 0;

            foreach (var audioFile in selected)
            {
                if (audioFile.importer != null)
                {
                    audioFile.importer.SetOverrideSampleSettings("WebGL", settings);
                    audioFile.importer.forceToMono = false;

                    EditorUtility.SetDirty(audioFile.importer);
                    audioFile.importer.SaveAndReimport();
                    applied++;
                }
            }

            AssetDatabase.Refresh();
            RefreshAudioList();

            EditorUtility.DisplayDialog("Optimization Complete",
                $"Applied '{presetName}' preset to {applied} audio files.", "OK");
        }
    }
}