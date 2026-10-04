using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Collections.Generic;
using System.Linq;
using System.IO;

#if UNITY_VISUALSCRIPTING
using Unity.VisualScripting;
#endif

namespace U3D.Editor
{
    /// <summary>
    /// Asset cleanup utilities for the U3D SDK.
    /// Handles missing script detection, replacement, and cleanup.
    /// Also handles missing object reference detection and placeholder management.
    /// Also handles broken Visual Scripting graph cleanup.
    /// </summary>
    public static class AssetCleanupTools
    {
        public static void ReplaceMissingScriptsWithPlaceholders()
        {
            int replacedCount = 0;
            int suggestionsFound = 0;
            string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            GameObject[] allGameObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject go in allGameObjects)
            {
                int numComponents = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (numComponents > 0)
                {
                    Undo.RegisterCompleteObjectUndo(go, "Replace missing scripts with placeholders");

                    string searchTerm = ComponentSuggestions.GetSearchTermForGameObject(go.name);
                    if (!string.IsNullOrEmpty(searchTerm))
                    {
                        suggestionsFound++;
                    }

                    for (int i = 0; i < numComponents; i++)
                    {
                        var placeholder = go.AddComponent<U3D.MissingScriptPlaceholder>();
                        placeholder.SetReplacementDateTime(timestamp);
                        if (!string.IsNullOrEmpty(searchTerm))
                        {
                            placeholder.SetSuggestedSearchTerm(searchTerm);
                        }
                    }

                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                    replacedCount += numComponents;

                    EditorUtility.SetDirty(go);
                }
            }

            if (replacedCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"🔧 U3D SDK: Replaced {replacedCount} missing scripts with placeholder components.");

                if (suggestionsFound > 0)
                {
                    Debug.Log($"💡 U3D SDK: Found {suggestionsFound} objects with replacement suggestions. Check placeholder components in Inspector for details.");
                }
            }
            else
            {
                Debug.Log("✅ U3D SDK: No missing scripts found in scene.");
            }
        }

        /// <summary>
        /// Remove all MissingScriptPlaceholder components from the scene
        /// </summary>
        public static void RemovePlaceholderComponents()
        {
            int removedCount = 0;
            GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject go in allObjects)
            {
                var placeholders = go.GetComponents<U3D.MissingScriptPlaceholder>();
                foreach (var placeholder in placeholders)
                {
                    Undo.DestroyObjectImmediate(placeholder);
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"🧼 U3D SDK: Removed {removedCount} placeholder component(s) from scene.");
            }
            else
            {
                Debug.Log("✅ U3D SDK: No placeholder components found in scene.");
            }
        }

        /// <summary>
        /// Detect missing object references in components and add placeholder tracking components
        /// </summary>
        public static void ReplaceMissingReferencesWithPlaceholders()
        {
            int gameObjectsProcessed = 0;
            int missingReferencesFound = 0;
            int placeholdersAdded = 0;

            GameObject[] allGameObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject go in allGameObjects)
            {
                gameObjectsProcessed++;
                var missingRefsOnThisObject = new List<U3D.MissingReferenceInfo>();

                Component[] components = go.GetComponents<Component>();

                foreach (Component component in components)
                {
                    if (component == null) continue;
                    if (component is U3D.MissingReferencePlaceholder) continue;

                    SerializedObject serializedObject = new SerializedObject(component);
                    SerializedProperty property = serializedObject.GetIterator();

                    bool enterChildren = true;
                    while (property.NextVisible(enterChildren))
                    {
                        enterChildren = false;

                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue == null &&
                            property.objectReferenceInstanceIDValue != 0)
                        {
                            missingReferencesFound++;
                            missingRefsOnThisObject.Add(new U3D.MissingReferenceInfo(
                                component.GetType().Name,
                                property.displayName,
                                property.type,
                                property.propertyPath,
                                GetGameObjectPath(go)
                            ));
                        }
                    }
                }

                if (missingRefsOnThisObject.Count > 0)
                {
                    Undo.RegisterCompleteObjectUndo(go, "Add missing reference placeholder");

                    U3D.MissingReferencePlaceholder existingPlaceholder = go.GetComponent<U3D.MissingReferencePlaceholder>();

                    if (existingPlaceholder == null)
                    {
                        existingPlaceholder = go.AddComponent<U3D.MissingReferencePlaceholder>();
                        placeholdersAdded++;
                    }

                    foreach (var missingRef in missingRefsOnThisObject)
                    {
                        existingPlaceholder.AddMissingReference(
                            missingRef.componentType,
                            missingRef.propertyName,
                            missingRef.expectedType,
                            missingRef.propertyPath,
                            missingRef.gameObjectPath
                        );
                    }

                    EditorUtility.SetDirty(go);
                }
            }

            if (missingReferencesFound > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"🔧 U3D SDK: Found {missingReferencesFound} missing references across {gameObjectsProcessed} GameObjects.");
                Debug.Log($"💡 U3D SDK: Added {placeholdersAdded} MissingReferencePlaceholder components. Use 'Find Reference Placeholders' to locate them.");
            }
            else
            {
                Debug.Log($"✅ U3D SDK: No missing references found in {gameObjectsProcessed} GameObjects.");
            }
        }

        /// <summary>
        /// Find all GameObjects with MissingReferencePlaceholder components for easy identification
        /// </summary>
        public static void FindMissingReferencePlaceholders()
        {
            var placeholderObjects = new List<GameObject>();
            int totalMissingReferences = 0;

            GameObject[] allGameObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject go in allGameObjects)
            {
                U3D.MissingReferencePlaceholder placeholder = go.GetComponent<U3D.MissingReferencePlaceholder>();
                if (placeholder != null && placeholder.HasMissingReferences())
                {
                    placeholderObjects.Add(go);
                    totalMissingReferences += placeholder.GetMissingReferences().Count;
                }
            }

            if (placeholderObjects.Count > 0)
            {
                Debug.Log($"🔍 U3D SDK: Found {placeholderObjects.Count} GameObjects with missing reference placeholders tracking {totalMissingReferences} missing references:");

                foreach (GameObject go in placeholderObjects)
                {
                    U3D.MissingReferencePlaceholder placeholder = go.GetComponent<U3D.MissingReferencePlaceholder>();
                    var missingRefs = placeholder.GetMissingReferences();
                    string gameObjectPath = GetGameObjectPath(go);
                    Debug.Log($"📍 {gameObjectPath} - {missingRefs.Count} missing references", go);

                    foreach (var missingRef in missingRefs)
                    {
                        Debug.Log($"   • {missingRef.componentType}.{missingRef.propertyName} (expecting {missingRef.expectedType})", go);
                    }
                }

                Selection.objects = placeholderObjects.ToArray();
                Debug.Log($"🎯 U3D SDK: Selected all {placeholderObjects.Count} GameObjects with missing reference placeholders in Hierarchy.");
            }
            else
            {
                Debug.Log("✅ U3D SDK: No missing reference placeholders found in scene.");
            }
        }

        /// <summary>
        /// Remove all MissingReferencePlaceholder components from the scene
        /// </summary>
        public static void RemoveMissingReferencePlaceholders()
        {
            int removedCount = 0;
            GameObject[] allObjects = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject go in allObjects)
            {
                var placeholders = go.GetComponents<U3D.MissingReferencePlaceholder>();
                foreach (var placeholder in placeholders)
                {
                    Undo.DestroyObjectImmediate(placeholder);
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                Debug.Log($"🧼 U3D SDK: Removed {removedCount} missing reference placeholder component(s) from scene.");
            }
            else
            {
                Debug.Log("✅ U3D SDK: No missing reference placeholder components found in scene.");
            }
        }

        /// <summary>
        /// Remove missing scripts directly from scene GameObjects
        /// </summary>
        public static void RemoveMissingScriptsFromScene()
        {
            int removedCount = 0;
            int gameObjectCount = 0;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                GameObject[] rootObjects = scene.GetRootGameObjects();
                foreach (GameObject root in rootObjects)
                {
                    GameObject[] allObjects = root.GetComponentsInChildren<Transform>(true)
                                                  .Select(t => t.gameObject)
                                                  .ToArray();

                    foreach (GameObject go in allObjects)
                    {
                        gameObjectCount++;
                        int before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                        if (before > 0)
                        {
                            Undo.RegisterCompleteObjectUndo(go, "Remove missing scripts");
                            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                            removedCount += before;
                            EditorUtility.SetDirty(go);
                        }
                    }
                }
            }

            if (removedCount > 0)
            {
                EditorSceneManager.MarkAllScenesDirty();
                Debug.Log($"🗑️ U3D SDK: Removed {removedCount} missing script(s) from {gameObjectCount} GameObject(s) across all open scenes.");
            }
            else
            {
                Debug.Log($"✅ U3D SDK: No missing scripts found in {gameObjectCount} GameObject(s) across all open scenes.");
            }
        }

        /// <summary>
        /// Clean prefabs in a selected folder by removing missing script components
        /// </summary>
        public static void CleanPrefabsInFolder()
        {
            string folderPath = EditorUtility.OpenFolderPanel("Select Folder Containing Prefabs", "Assets", "");

            if (string.IsNullOrEmpty(folderPath)) return;

            if (folderPath.StartsWith(Application.dataPath))
            {
                folderPath = "Assets" + folderPath.Substring(Application.dataPath.Length);
            }

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { folderPath });
            int cleanedCount = 0;
            int prefabCount = prefabGuids.Length;

            foreach (string guid in prefabGuids)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = PrefabUtility.LoadPrefabContents(prefabPath);

                if (prefab == null) continue;

                bool prefabModified = false;
                GameObject[] allObjects = prefab.GetComponentsInChildren<Transform>(true)
                                               .Select(t => t.gameObject)
                                               .ToArray();

                foreach (GameObject go in allObjects)
                {
                    int before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (before > 0)
                    {
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                        cleanedCount += before;
                        prefabModified = true;
                    }
                }

                if (prefabModified)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
                }

                PrefabUtility.UnloadPrefabContents(prefab);
            }

            AssetDatabase.Refresh();
            Debug.Log($"🧼 U3D SDK: Cleaned {cleanedCount} missing script(s) from {prefabCount} prefab(s) in {folderPath}.");
        }

        /// <summary>
        /// Detect ScriptMachine and StateMachine components in the active scene whose embedded
        /// or external graphs contain third party SDK node types, and clear them.
        /// </summary>
        public static void CleanVisualScriptingGraphs()
        {
#if UNITY_VISUALSCRIPTING
            int machinesFound = 0;
            var candidates = new List<(Component component, bool isEmbedded, string graphPath)>();

            const string emptyEmbeddedGraph = "{\"nest\":{\"source\":\"Embed\",\"macro\":null,\"embed\":{\"variables\":{\"Kind\":\"Flow\",\"collection\":{\"$content\":[],\"$version\":\"A\"},\"$version\":\"A\"},\"controlInputDefinitions\":[],\"controlOutputDefinitions\":[],\"valueInputDefinitions\":[],\"valueOutputDefinitions\":[],\"title\":null,\"summary\":null,\"pan\":{\"x\":0.0,\"y\":0.0},\"zoom\":1.0,\"elements\":[],\"$version\":\"A\"}}}";

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        foreach (Component component in t.gameObject.GetComponents<Component>())
                        {
                            if (component == null) continue;
                            if (!(component is ScriptMachine) && !(component is StateMachine)) continue;

                            machinesFound++;

                            SerializedObject so = new SerializedObject(component);
                            SerializedProperty dataProp = so.FindProperty("_data");
                            SerializedProperty jsonProp = dataProp?.FindPropertyRelative("_json");
                            SerializedProperty graphProp = so.FindProperty("_graph");

                            if (jsonProp != null && !string.IsNullOrEmpty(jsonProp.stringValue)
                                && jsonProp.stringValue.Contains("SpatialSys"))
                            {
                                candidates.Add((component, true, null));
                            }
                            else if (graphProp != null && graphProp.objectReferenceValue != null)
                            {
                                string graphPath = AssetDatabase.GetAssetPath(graphProp.objectReferenceValue);
                                if (!string.IsNullOrEmpty(graphPath) && File.Exists(graphPath))
                                {
                                    string graphText = File.ReadAllText(graphPath);
                                    if (graphText.Contains("SpatialSys"))
                                        candidates.Add((component, false, graphPath));
                                }
                            }
                        }
                    }
                }
            }

            if (machinesFound == 0)
            {
                Debug.Log("✅ U3D SDK: No Script Machine or State Machine components found in scene.");
                return;
            }

            if (candidates.Count == 0)
            {
                Debug.Log($"✅ U3D SDK: Checked {machinesFound} VS machine(s) - no third-party graphs detected.");
                return;
            }

            bool confirm = EditorUtility.DisplayDialog(
                "Clear Third-Party VS Graphs",
                $"Found {candidates.Count} VS graph(s) containing third-party node types across {machinesFound} machine(s) checked.\n\n" +
                "Entire graphs will be cleared - all nodes including any valid Unity VS logic in the same graph will be lost.\n\n" +
                "For graphs with mixed content, consider manually deleting only the red 'Script Missing' nodes in the VS Graph editor instead.\n\n" +
                "This action can be undone with Ctrl+Z.",
                "Clear All", "Cancel");

            if (!confirm) return;

            int graphsNulled = 0;
            var affectedObjects = new List<GameObject>();

            foreach (var (component, isEmbedded, graphPath) in candidates)
            {
                SerializedObject so = new SerializedObject(component);

                if (isEmbedded)
                {
                    SerializedProperty dataProp = so.FindProperty("_data");
                    SerializedProperty jsonProp = dataProp?.FindPropertyRelative("_json");
                    if (jsonProp != null)
                    {
                        Undo.RegisterCompleteObjectUndo(component, "Clear broken embedded VS graph");
                        jsonProp.stringValue = emptyEmbeddedGraph;
                        so.ApplyModifiedProperties();
                    }
                }
                else
                {
                    SerializedProperty graphProp = so.FindProperty("_graph");
                    if (graphProp != null)
                    {
                        Undo.RegisterCompleteObjectUndo(component, "Null broken external VS graph");
                        graphProp.objectReferenceValue = null;
                        so.ApplyModifiedProperties();
                    }
                }

                GameObject go = (component as MonoBehaviour)?.gameObject;
                if (go != null)
                {
                    EditorUtility.SetDirty(go);
                    affectedObjects.Add(go);
                    Debug.LogWarning($"⚠️ U3D SDK: Cleared third-party VS graph on '{GetGameObjectPath(go)}'", go);
                }

                graphsNulled++;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"🔧 U3D SDK: Cleared {graphsNulled} third-party VS graph(s). {machinesFound - graphsNulled} machine(s) were clean.");
            Debug.Log($"📋 U3D SDK: Affected GameObjects selected in Hierarchy.");
            Selection.objects = affectedObjects.ToArray();
#else
    EditorUtility.DisplayDialog(
        "Visual Scripting Not Available",
        "Unity Visual Scripting package is not installed in this project. Add 'com.unity.visualscripting' via Package Manager to use this tool.",
        "OK");
#endif
        }

        /// <summary>
        /// Scan a selected folder (including subfolders) for .asset and .prefab files containing
        /// third party SDK Visual Scripting node types and clean them. Also removes missing script
        /// components from prefabs in the same pass. Targets project assets rather than scene objects.
        /// </summary>
        public static void CleanThirdPartyVSProjectAssets()
        {
#if UNITY_VISUALSCRIPTING
            string folderPath = EditorUtility.OpenFolderPanel("Select Folder Containing Imported Assets", "Assets", "");

            if (string.IsNullOrEmpty(folderPath)) return;

            if (folderPath.StartsWith(Application.dataPath))
                folderPath = "Assets" + folderPath.Substring(Application.dataPath.Length);

            const string emptyEmbeddedGraph = "{\"nest\":{\"source\":\"Embed\",\"macro\":null,\"embed\":{\"variables\":{\"Kind\":\"Flow\",\"collection\":{\"$content\":[],\"$version\":\"A\"},\"$version\":\"A\"},\"controlInputDefinitions\":[],\"controlOutputDefinitions\":[],\"valueInputDefinitions\":[],\"valueOutputDefinitions\":[],\"title\":null,\"summary\":null,\"pan\":{\"x\":0.0,\"y\":0.0},\"zoom\":1.0,\"elements\":[],\"$version\":\"A\"}}}";

            bool confirmed = EditorUtility.DisplayDialog(
                "Clear Third-Party VS Graphs",
                $"This will scan all .asset and .prefab files in '{folderPath}' and its subfolders.\n\n" +
                "Any VS graph containing third-party node types will be entirely cleared - all nodes including valid Unity VS logic in the same graph will be lost.\n\n" +
                "For graphs with mixed content, consider manually deleting only the red 'Script Missing' nodes in the VS Graph editor instead.\n\n" +
                "Missing scripts will also be removed from prefabs in the same pass.",
                "Continue", "Cancel");

            if (!confirmed) return;

            string absoluteFolder = Path.Combine(Application.dataPath, folderPath.Substring("Assets".Length).TrimStart('/', '\\'));
            string[] assetFiles = Directory.GetFiles(absoluteFolder, "*.asset", SearchOption.AllDirectories);
            string[] prefabFiles = Directory.GetFiles(absoluteFolder, "*.prefab", SearchOption.AllDirectories);

            int totalFiles = assetFiles.Length + prefabFiles.Length;
            int graphsCleared = 0;
            int missingScriptsRemoved = 0;
            int filesModified = 0;
            int fileIndex = 0;

            try
            {
                // --- Pass 1: .asset files - write directly to disk to bypass VS re-serialization ---
                foreach (string absolutePath in assetFiles)
                {
                    string relativePath = "Assets" + absolutePath.Substring(Application.dataPath.Length).Replace('\\', '/');
                    fileIndex++;

                    EditorUtility.DisplayProgressBar(
                        "Cleaning Third-Party VS Project Assets",
                        $"Checking: {Path.GetFileName(relativePath)}",
                        (float)fileIndex / totalFiles);

                    string fileText = File.ReadAllText(absolutePath);
                    if (!fileText.Contains("SpatialSys")) continue;

                    string modifiedText = System.Text.RegularExpressions.Regex.Replace(
                        fileText,
                        @"(_json: ')(.*?SpatialSys.*?)('(\s*\n|\s*$))",
                        m => m.Groups[1].Value + emptyEmbeddedGraph + m.Groups[3].Value,
                        System.Text.RegularExpressions.RegexOptions.Singleline
                    );

                    if (modifiedText != fileText)
                    {
                        File.WriteAllText(absolutePath, modifiedText);
                        graphsCleared++;
                        filesModified++;
                        Debug.LogWarning($"⚠️ U3D SDK: Cleared third-party VS graph in asset: {relativePath}");
                    }
                }

                // --- Pass 2: .prefab files (missing scripts + embedded VS graphs) ---
                foreach (string absolutePath in prefabFiles)
                {
                    string relativePath = "Assets" + absolutePath.Substring(Application.dataPath.Length).Replace('\\', '/');
                    fileIndex++;

                    EditorUtility.DisplayProgressBar(
                        "Cleaning Third-Party VS Project Assets",
                        $"Checking: {Path.GetFileName(relativePath)}",
                        (float)fileIndex / totalFiles);

                    string fileText = File.ReadAllText(absolutePath);
                    bool hasSpatial = fileText.Contains("SpatialSys");
                    bool hasMissingScripts = fileText.Contains("m_Script: {fileID: 0}");

                    if (!hasSpatial && !hasMissingScripts) continue;

                    GameObject prefab = PrefabUtility.LoadPrefabContents(relativePath);
                    if (prefab == null) continue;

                    bool prefabModified = false;

                    foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                    {
                        GameObject go = t.gameObject;

                        int before = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                        if (before > 0)
                        {
                            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                            missingScriptsRemoved += before;
                            prefabModified = true;
                        }

                        if (hasSpatial)
                        {
                            foreach (Component component in go.GetComponents<Component>())
                            {
                                if (component == null) continue;
                                if (!(component is ScriptMachine) && !(component is StateMachine)) continue;

                                SerializedObject so = new SerializedObject(component);
                                SerializedProperty dataProp = so.FindProperty("_data");
                                SerializedProperty jsonProp = dataProp?.FindPropertyRelative("_json");

                                if (jsonProp != null && !string.IsNullOrEmpty(jsonProp.stringValue)
                                    && jsonProp.stringValue.Contains("SpatialSys"))
                                {
                                    jsonProp.stringValue = emptyEmbeddedGraph;
                                    so.ApplyModifiedProperties();
                                    graphsCleared++;
                                    prefabModified = true;
                                    Debug.LogWarning($"⚠️ U3D SDK: Cleared third-party VS graph in prefab: {relativePath} on '{go.name}'");
                                }
                            }
                        }
                    }

                    if (prefabModified)
                    {
                        PrefabUtility.SaveAsPrefabAsset(prefab, relativePath);
                        filesModified++;
                    }

                    PrefabUtility.UnloadPrefabContents(prefab);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.Refresh();

            if (graphsCleared > 0 || missingScriptsRemoved > 0)
            {
                Debug.Log($"🔧 U3D SDK: Cleaned {filesModified} file(s) in '{folderPath}'.");
                if (graphsCleared > 0)
                    Debug.Log($"   • {graphsCleared} third-party VS graph(s) cleared.");
                if (missingScriptsRemoved > 0)
                    Debug.Log($"   • {missingScriptsRemoved} missing script(s) removed from prefabs.");
            }
            else
            {
                Debug.Log($"✅ U3D SDK: No third-party VS content or missing scripts found in '{folderPath}'.");
            }
#else
    EditorUtility.DisplayDialog(
        "Visual Scripting Not Available",
        "Unity Visual Scripting package is not installed in this project. Add 'com.unity.visualscripting' via Package Manager to use this tool.",
        "OK");
#endif
        }

        private static string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}