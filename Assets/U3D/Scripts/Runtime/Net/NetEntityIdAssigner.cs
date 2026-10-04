#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Net
{
    /// <summary>
    /// Assigns authored IDs to NetEntity components on scene objects, per Object Identity
    /// Spec section 3. Work is deferred out of OnValidate for two reasons: OnValidate fires
    /// per component during scene load, so an inline duplicate scan would read a
    /// half-loaded scene and miss collisions; and one coalesced pass replaces one scan per
    /// component.
    /// </summary>
    public static class NetEntityIdAssigner
    {
        private const string RegistryFolder = "Assets/U3D_ProjectData";
        private const string RegistryFolderName = "U3D_ProjectData";
        private const string RegistryResourcesFolder = RegistryFolder + "/Resources";
        private const string RegistryPath = RegistryResourcesFolder + "/" + U3DObjectIdRegistry.ResourceName + ".asset";
        private const string LegacyRegistryPath = RegistryFolder + "/U3DObjectIdRegistry.asset";

        private static bool _passPending;
        private static readonly HashSet<NetEntity> _pendingClears = new HashSet<NetEntity>();

        private static readonly List<GameObject> _rootScratch = new List<GameObject>();
        private static readonly List<NetEntity> _childScratch = new List<NetEntity>();
        private static readonly List<NetEntity> _sceneEntities = new List<NetEntity>();
        private static readonly List<NetEntity> _needsId = new List<NetEntity>();
        private static readonly Dictionary<ushort, NetEntity> _byId = new Dictionary<ushort, NetEntity>();
        private static readonly HashSet<Scene> _dirtyScenes = new HashSet<Scene>();

        // Which instance held each ID as of the last pass. On a collision the remembered
        // holder keeps the number and the newcomer yields, so duplicating an object
        // renumbers the copy rather than the original. Section 3's GetInstanceID comparison
        // remains the fallback when there is no prior knowledge, which is a fresh scene open
        // or a script recompile.
        private static readonly Dictionary<ushort, int> _knownHolders = new Dictionary<ushort, int>();

        /// <summary>
        /// Requests a pass over every loaded scene. Safe to call repeatedly; passes coalesce.
        /// </summary>
        public static void Schedule()
        {
            if (_passPending) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            _passPending = true;
            EditorApplication.delayCall += RunPass;
        }

        /// <summary>
        /// Runs one pass when the editor loads and after every script recompile. Recreates a
        /// missing registry at a moment the warning is legible, and rebuilds the duplicate
        /// memory, which is a static field and therefore wiped by every domain reload.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void ScheduleOnLoad()
        {
            Schedule();
        }

        /// <summary>
        /// Requests that a stale ID be cleared from a prefab asset. A prefab asset must hold
        /// zero, per spec sections 3 and 5, or every spawned copy arrives with the same
        /// baked number.
        /// </summary>
        public static void ScheduleClear(NetEntity entity)
        {
            if (entity == null) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (_pendingClears.Add(entity) && !_passPending)
            {
                _passPending = true;
                EditorApplication.delayCall += RunPass;
            }
        }

        private static void RunPass()
        {
            _passPending = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _pendingClears.Clear();
                return;
            }

            ClearPendingAssets();
            ClearPrefabStage();
            AssignSceneIds();
        }

        private static void ClearPendingAssets()
        {
            if (_pendingClears.Count == 0) return;

            bool changed = false;
            foreach (NetEntity entity in _pendingClears)
            {
                if (entity == null) continue;
                if (!EditorUtility.IsPersistent(entity)) continue;
                if (entity.EditorId == 0) continue;

                Debug.LogWarning($"Prefab asset '{entity.name}' carried authored ID {entity.EditorId}. Cleared it: a spawnable prefab receives its ID when it spawns, and a baked one would be handed to every copy.", entity);
                entity.EditorSetId(0);
                EditorUtility.SetDirty(entity);
                changed = true;
            }

            _pendingClears.Clear();
            if (changed) AssetDatabase.SaveAssets();
        }

        private static void ClearPrefabStage()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null) return;

            _childScratch.Clear();
            stage.prefabContentsRoot.GetComponentsInChildren(true, _childScratch);

            bool changed = false;
            for (int i = 0; i < _childScratch.Count; i++)
            {
                NetEntity entity = _childScratch[i];
                if (entity == null || entity.EditorId == 0) continue;

                Debug.LogWarning($"'{entity.name}' in this prefab carried authored ID {entity.EditorId}. Cleared it: a spawnable prefab receives its ID when it spawns.", entity);
                entity.EditorSetId(0);
                EditorUtility.SetDirty(entity);
                changed = true;
            }

            if (changed) EditorSceneManager.MarkSceneDirty(stage.scene);
        }

        private static void AssignSceneIds()
        {
            GatherSceneEntities();
            if (_sceneEntities.Count == 0)
            {
                _knownHolders.Clear();
                return;
            }

            _byId.Clear();
            _needsId.Clear();
            ushort highest = 0;

            for (int i = 0; i < _sceneEntities.Count; i++)
            {
                NetEntity entity = _sceneEntities[i];
                ushort id = entity.EditorId;

                if (!NetEntity.IsAuthoredId(id))
                {
                    _needsId.Add(entity);
                    continue;
                }

                if (id > highest) highest = id;

                if (!_byId.TryGetValue(id, out NetEntity held))
                {
                    _byId[id] = entity;
                    continue;
                }

                NetEntity keeper = ChooseKeeper(id, held, entity);
                _byId[id] = keeper;
                _needsId.Add(keeper == held ? entity : held);
            }

            U3DObjectIdRegistry registry = LoadOrCreateRegistry();
            if (registry == null) return;

            registry.RaiseFloorAbove(highest);

            if (_needsId.Count > 0)
            {
                _dirtyScenes.Clear();
                for (int i = 0; i < _needsId.Count; i++)
                {
                    NetEntity entity = _needsId[i];
                    if (entity == null) continue;

                    if (!registry.TryTakeNextAuthoredId(out ushort fresh))
                    {
                        Debug.LogError($"Authored ID range is exhausted, so '{entity.name}' could not be given one. The range holds {NetEntity.AuthoredIdMax} objects.", entity);
                        break;
                    }

                    entity.EditorSetId(fresh);
                    EditorUtility.SetDirty(entity);
                    _dirtyScenes.Add(entity.gameObject.scene);
                }

                foreach (Scene scene in _dirtyScenes)
                {
                    if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
                }
                _dirtyScenes.Clear();
            }

            RememberHolders();
        }

        /// <summary>
        /// Decides which of two objects sharing an ID keeps it. The object we already knew
        /// held that number wins, so a duplicate yields rather than displacing its source.
        /// With no prior knowledge, falls back to section 3's rule: the higher
        /// GetInstanceID yields.
        /// </summary>
        private static NetEntity ChooseKeeper(ushort id, NetEntity a, NetEntity b)
        {
            if (_knownHolders.TryGetValue(id, out int remembered))
            {
                if (a.GetInstanceID() == remembered) return a;
                if (b.GetInstanceID() == remembered) return b;
            }

            return a.GetInstanceID() <= b.GetInstanceID() ? a : b;
        }

        private static void RememberHolders()
        {
            _knownHolders.Clear();
            for (int i = 0; i < _sceneEntities.Count; i++)
            {
                NetEntity entity = _sceneEntities[i];
                if (entity == null) continue;

                ushort id = entity.EditorId;
                if (!NetEntity.IsAuthoredId(id)) continue;

                _knownHolders[id] = entity.GetInstanceID();
            }
        }

        private static void GatherSceneEntities()
        {
            _sceneEntities.Clear();

            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.IsValid() || !scene.isLoaded) continue;

                _rootScratch.Clear();
                scene.GetRootGameObjects(_rootScratch);

                for (int r = 0; r < _rootScratch.Count; r++)
                {
                    _childScratch.Clear();
                    _rootScratch[r].GetComponentsInChildren(true, _childScratch);

                    for (int c = 0; c < _childScratch.Count; c++)
                    {
                        NetEntity entity = _childScratch[c];
                        if (entity == null) continue;
                        if (EditorUtility.IsPersistent(entity)) continue;
                        _sceneEntities.Add(entity);
                    }
                }
            }
        }

        public static U3DObjectIdRegistry LoadOrCreateRegistry()
        {
            var registry = AssetDatabase.LoadAssetAtPath<U3DObjectIdRegistry>(RegistryPath);
            if (registry != null) return registry;

            EnsureRegistryFolders();

            var legacy = AssetDatabase.LoadAssetAtPath<U3DObjectIdRegistry>(LegacyRegistryPath);
            if (legacy != null)
            {
                string moveError = AssetDatabase.MoveAsset(LegacyRegistryPath, RegistryPath);
                if (string.IsNullOrEmpty(moveError))
                {
                    AssetDatabase.SaveAssets();
                    Debug.Log($"Moved the object ID registry to {RegistryPath}. It has to sit in a Resources folder so the prefab table reaches a published build.");
                    return AssetDatabase.LoadAssetAtPath<U3DObjectIdRegistry>(RegistryPath);
                }

                Debug.LogError($"Could not move the object ID registry from {LegacyRegistryPath} to {RegistryPath}: {moveError}. Move it by hand in the Project window before publishing, or spawned objects will not resolve for other players.");
                return legacy;
            }

            registry = ScriptableObject.CreateInstance<U3DObjectIdRegistry>();
            AssetDatabase.CreateAsset(registry, RegistryPath);
            AssetDatabase.SaveAssets();

            Debug.LogWarning($"Created a new object ID registry at {RegistryPath}. If a previous one was deleted, numbers belonging to objects removed since your last publish may be handed out again, and every spawnable prefab has to be registered again. The counter has been raised above every ID in your open scenes, so anything you can currently see is safe.");
            return registry;
        }

        private static void EnsureRegistryFolders()
        {
            if (!AssetDatabase.IsValidFolder(RegistryFolder))
                AssetDatabase.CreateFolder("Assets", RegistryFolderName);

            if (!AssetDatabase.IsValidFolder(RegistryResourcesFolder))
                AssetDatabase.CreateFolder(RegistryFolder, "Resources");
        }
    }
}
#endif