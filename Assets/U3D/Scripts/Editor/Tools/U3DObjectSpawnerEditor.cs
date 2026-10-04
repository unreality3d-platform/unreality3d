using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using U3D.Net;

namespace U3D.Editor
{
    [CustomEditor(typeof(U3DObjectSpawner))]
    public class U3DObjectSpawnerEditor : UnityEditor.Editor
    {
        private U3DObjectIdRegistry _registry;
        private readonly List<GameObject> _prefabScratch = new List<GameObject>();

        private void OnEnable()
        {
            _registry = NetEntityIdAssigner.LoadOrCreateRegistry();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var spawner = (U3DObjectSpawner)target;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Multiplayer Setup", EditorStyles.boldLabel);

            if (!spawner.networkedSpawn)
            {
                EditorGUILayout.HelpBox(
                    "Networked Spawn is off, so each player builds these objects on their own machine and nobody else sees them. No setup needed.",
                    MessageType.None);
                return;
            }

            CollectPrefabs(spawner, _prefabScratch);

            if (_prefabScratch.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No prefabs assigned yet. Drop one into 'Prefab To Spawn', or add entries to 'Prefab List'.",
                    MessageType.Info);
                return;
            }

            int unregistered = CountUnregistered(_prefabScratch);

            if (unregistered > 0)
            {
                EditorGUILayout.HelpBox(
                    unregistered == 1
                        ? "One prefab still needs registering. Until you do, it will not appear for other players."
                        : $"{unregistered} prefabs still need registering. Until you do, they will not appear for other players.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    _prefabScratch.Count == 1
                        ? "Prefab is registered and ready."
                        : $"All {_prefabScratch.Count} prefabs are registered and ready.",
                    MessageType.None);
            }

            if (GUILayout.Button("Register Prefab(s) for Multiplayer", GUILayout.Height(28)))
            {
                RegisterPrefabs(spawner);
            }
        }

        private int CountUnregistered(List<GameObject> prefabs)
        {
            if (_registry == null) return prefabs.Count;

            int count = 0;
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (!_registry.IsPrefabRegistered(prefabs[i])) count++;
            }
            return count;
        }

        private void RegisterPrefabs(U3DObjectSpawner spawner)
        {
            if (_registry == null) _registry = NetEntityIdAssigner.LoadOrCreateRegistry();
            if (_registry == null)
            {
                EditorUtility.DisplayDialog(
                    "Register Prefab(s) for Multiplayer",
                    "Could not open the project's object registry. See the Console.",
                    "OK");
                return;
            }

            var prefabs = new List<GameObject>();
            CollectPrefabs(spawner, prefabs);

            if (prefabs.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Register Prefab(s) for Multiplayer",
                    "No prefabs are assigned to this spawner. Assign one to 'Prefab To Spawn' or add entries to 'Prefab List' first.",
                    "OK");
                return;
            }

            var report = new List<string>();
            int prepared = 0;

            for (int i = 0; i < prefabs.Count; i++)
            {
                if (PreparePrefab(prefabs[i], report)) prepared++;
            }

            for (int i = 0; i < prefabs.Count; i++)
            {
                if (_registry.TryRegisterPrefab(prefabs[i], out ushort index))
                    report.Add($"• {prefabs[i].name}: registered as #{index}.");
                else
                    report.Add($"• {prefabs[i].name}: could not be registered. It may not be a prefab asset.");
            }

            EditorUtility.SetDirty(_registry);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string summary = prepared == 0
                ? "Prefabs were already set up. Registration is up to date."
                : $"Set up {prepared} prefab(s) and registered them.";

            Debug.Log($"U3DObjectSpawner: {summary}\n{string.Join("\n", report)}");

            EditorUtility.DisplayDialog(
                "Register Prefab(s) for Multiplayer",
                $"{summary}\n\nSee the Console for details.",
                "OK");
        }

        /// <summary>
        /// Puts the two components a spawned object needs onto the prefab: a NetEntity so
        /// every player can name it, and a U3DSpawnTracker so the spawner learns when it
        /// arrives and when it goes. Returns true if anything was added.
        /// </summary>
        private static bool PreparePrefab(GameObject prefab, List<string> report)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path))
            {
                report.Add($"• Skipped '{prefab.name}': not a prefab asset.");
                return false;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool changed = false;
            var added = new List<string>();

            try
            {
                if (root.GetComponent<NetEntity>() == null)
                {
                    root.AddComponent<NetEntity>();
                    added.Add("NetEntity");
                    changed = true;
                }

                if (root.GetComponent<U3DSpawnTracker>() == null)
                {
                    root.AddComponent<U3DSpawnTracker>();
                    added.Add("U3D Spawn Tracker");
                    changed = true;
                }

                if (changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    report.Add($"• {prefab.name}: added {string.Join(" and ", added)}.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return changed;
        }

        /// <summary>
        /// Every prefab this spawner can produce. Section 6's spawner half of the publish
        /// walk; the collectable half lives with the collectable.
        /// </summary>
        public static void CollectPrefabs(U3DObjectSpawner spawner, List<GameObject> into)
        {
            into.Clear();
            if (spawner == null) return;

            var seen = new HashSet<GameObject>();

            if (spawner.prefabToSpawn.IsValid && seen.Add(spawner.prefabToSpawn.Prefab))
                into.Add(spawner.prefabToSpawn.Prefab);

            if (spawner.prefabList == null) return;

            for (int i = 0; i < spawner.prefabList.Length; i++)
            {
                NetPrefab entry = spawner.prefabList[i].prefab;
                if (!entry.IsValid) continue;
                if (seen.Add(entry.Prefab)) into.Add(entry.Prefab);
            }
        }
    }
}