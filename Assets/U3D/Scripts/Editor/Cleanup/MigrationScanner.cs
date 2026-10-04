using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Editor
{
    /// <summary>
    /// Per-category lists of GameObjects with migration issues. A single object can
    /// appear in more than one list (for example a missing script and a placeholder).
    /// </summary>
    public struct MigrationScanResult
    {
        public List<GameObject> missingScripts;
        public List<GameObject> missingReferences;
        public List<GameObject> placeholders;
    }

    /// <summary>
    /// Read-only detection of migration issues across all loaded scenes. Changes nothing;
    /// it only finds and categorizes objects so they can be highlighted and selected before
    /// any cleanup tool is run.
    /// </summary>
    public static class MigrationScanner
    {
        public static MigrationScanResult ScanLoadedScenes()
        {
            var result = new MigrationScanResult
            {
                missingScripts = new List<GameObject>(),
                missingReferences = new List<GameObject>(),
                placeholders = new List<GameObject>()
            };

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        GameObject go = t.gameObject;

                        if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0)
                            result.missingScripts.Add(go);

                        if (go.GetComponent<MissingScriptPlaceholder>() != null ||
                            go.GetComponent<MissingReferencePlaceholder>() != null)
                            result.placeholders.Add(go);

                        if (HasMissingReferences(go))
                            result.missingReferences.Add(go);
                    }
                }
            }

            return result;
        }

        private static bool HasMissingReferences(GameObject go)
        {
            Component[] components = go.GetComponents<Component>();

            foreach (Component component in components)
            {
                if (component == null) continue;
                if (component is MissingReferencePlaceholder) continue;
                if (component is MissingScriptPlaceholder) continue;

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
                        return true;
                    }
                }
            }

            return false;
        }
    }
}