using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using U3D.Net;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Editor
{
    /// <summary>
    /// Gives every prefab a build could spawn a prefab-table index, before the build runs.
    /// Finds them by field type rather than by component type, so a prefab assigned on a
    /// creator's own component is found without this file naming any component.
    /// Registration is permanent per Working Rules 8.8, so scope is the build's scenes
    /// and the prefabs reachable from them, never the whole project.
    /// </summary>
    public static class NetPrefabSweep
    {
        private const int MaxFieldDepth = 6;

        public struct Result
        {
            public bool Succeeded;
            public string Error;
            public int CountBefore;
            public int CountAfter;
            public List<string> NewlyRegistered;
            public List<string> AlreadyHeld;
        }

        public static Result Run(string[] selectedScenePaths, U3DObjectIdRegistry registry, bool commit)
        {
            var result = new Result
            {
                Succeeded = false,
                NewlyRegistered = new List<string>(),
                AlreadyHeld = new List<string>()
            };

            if (registry == null)
            {
                result.Error = "The object registry was not available, so spawnable prefabs " +
                               "could not be given their numbers.";
                return result;
            }

            result.CountBefore = registry.PrefabTableCount;

            var roots = new List<GameObject>();
            if (!TryCollectFromScenes(selectedScenePaths, roots, out string sceneError))
            {
                result.Error = sceneError;
                return result;
            }

            var seen = new HashSet<GameObject>();
            var pending = new Queue<GameObject>();

            foreach (GameObject candidate in roots)
            {
                if (candidate != null) pending.Enqueue(candidate);
            }

            while (pending.Count > 0)
            {
                GameObject prefab = pending.Dequeue();

                if (prefab == null) continue;
                if (!seen.Add(prefab)) continue;

                if (!AssetDatabase.Contains(prefab))
                {
                    result.Error = "\"" + prefab.name + "\" is assigned to a spawnable prefab field, " +
                                   "but it is an object in a scene rather than a prefab in your Project " +
                                   "window. It would never appear for other players. Drag it into the " +
                                   "Project window to make a prefab, then assign that prefab instead.";
                    return result;
                }

                bool held = registry.IsPrefabRegistered(prefab);

                if (commit)
                {
                    if (!registry.TryRegisterPrefab(prefab, out ushort index))
                    {
                        result.Error = "\"" + prefab.name + "\" could not be added to the list of spawnable " +
                                       "prefabs, so it would never appear for other players.";
                        return result;
                    }

                    if (held) result.AlreadyHeld.Add(prefab.name + "  (already number " + index + ")");
                    else result.NewlyRegistered.Add(prefab.name + "  (given number " + index + ")");
                }
                else
                {
                    if (held) result.AlreadyHeld.Add(prefab.name + "  (already registered)");
                    else result.NewlyRegistered.Add(prefab.name + "  (would be registered)");
                }

                var nested = new List<GameObject>();
                CollectFromHierarchy(prefab, nested);

                foreach (GameObject child in nested)
                {
                    if (child != null) pending.Enqueue(child);
                }
            }

            if (commit) AssetDatabase.SaveAssets();

            result.CountAfter = registry.PrefabTableCount;
            result.Succeeded = true;
            return result;
        }

        private static bool TryCollectFromScenes(string[] scenePaths, List<GameObject> found, out string error)
        {
            error = null;

            if (scenePaths == null) return true;

            var openedHere = new List<Scene>();

            try
            {
                foreach (string path in scenePaths)
                {
                    if (string.IsNullOrEmpty(path)) continue;

                    Scene scene = SceneManager.GetSceneByPath(path);
                    bool alreadyOpen = scene.IsValid() && scene.isLoaded;

                    if (!alreadyOpen)
                    {
                        try
                        {
                            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                        }
                        catch (Exception e)
                        {
                            error = path + " could not be opened to check its spawnable prefabs. " + e.Message;
                            return false;
                        }

                        if (!scene.IsValid() || !scene.isLoaded)
                        {
                            error = path + " could not be opened to check its spawnable prefabs.";
                            return false;
                        }

                        openedHere.Add(scene);
                    }

                    CollectFromScene(scene, found);
                }
            }
            finally
            {
                foreach (Scene scene in openedHere)
                {
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }

            return true;
        }

        private static void CollectFromScene(Scene scene, List<GameObject> found)
        {
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);

            for (int i = 0; i < roots.Count; i++)
            {
                CollectFromHierarchy(roots[i], found);
            }
        }

        private static void CollectFromHierarchy(GameObject root, List<GameObject> found)
        {
            if (root == null) return;

            var behaviours = new List<MonoBehaviour>();
            root.GetComponentsInChildren(true, behaviours);

            for (int i = 0; i < behaviours.Count; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                CollectFromContainer(behaviour, 0, found);
            }
        }

        private static void CollectFromContainer(object container, int depth, List<GameObject> found)
        {
            if (container == null) return;
            if (depth > MaxFieldDepth) return;

            foreach (FieldInfo field in SerializedFields(container.GetType()))
            {
                object value;

                try
                {
                    value = field.GetValue(container);
                }
                catch
                {
                    continue;
                }

                CollectFromValue(field.FieldType, value, depth, found);
            }
        }

        private static void CollectFromValue(Type declared, object value, int depth, List<GameObject> found)
        {
            if (value == null) return;
            if (depth > MaxFieldDepth) return;

            if (declared == typeof(NetPrefab))
            {
                var prefab = (NetPrefab)value;
                if (prefab.IsValid) found.Add(prefab.Prefab);
                return;
            }

            if (declared.IsArray)
            {
                Type element = declared.GetElementType();
                if (element == null) return;

                var array = value as Array;
                if (array == null) return;

                for (int i = 0; i < array.Length; i++)
                {
                    CollectFromValue(element, array.GetValue(i), depth + 1, found);
                }

                return;
            }

            if (declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = declared.GetGenericArguments()[0];
                var list = value as IList;
                if (list == null) return;

                for (int i = 0; i < list.Count; i++)
                {
                    CollectFromValue(element, list[i], depth + 1, found);
                }

                return;
            }

            if (declared.IsPrimitive) return;
            if (declared.IsEnum) return;
            if (declared == typeof(string)) return;
            if (typeof(UnityEngine.Object).IsAssignableFrom(declared)) return;
            if (declared.Namespace != null && declared.Namespace.StartsWith("UnityEngine")) return;
            if (!declared.IsDefined(typeof(SerializableAttribute), false)) return;

            CollectFromContainer(value, depth + 1, found);
        }

        private static IEnumerable<FieldInfo> SerializedFields(Type type)
        {
            while (type != null
                   && type != typeof(MonoBehaviour)
                   && type != typeof(Behaviour)
                   && type != typeof(Component)
                   && type != typeof(UnityEngine.Object)
                   && type != typeof(object))
            {
                FieldInfo[] fields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];

                    if (field.IsStatic) continue;
                    if (field.IsInitOnly) continue;
                    if (field.IsNotSerialized) continue;

                    if (field.IsPublic)
                    {
                        if (field.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                    }
                    else
                    {
                        if (!field.IsDefined(typeof(SerializeField), false)) continue;
                    }

                    yield return field;
                }

                type = type.BaseType;
            }
        }
    }
}