using System.Collections.Generic;
using System.Text;
using U3D.Net;
using U3D.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Editor
{
    /// <summary>
    /// Runs before a publish builds anything. Verifies the project can produce a
    /// build whose objects will take part in the session, corrects the authored-ID
    /// counter against every scene in the project, refuses the publish when a scene
    /// in the build is unsaved, carries an ID that would fail silently at runtime, or
    /// holds a U3D CORE that is missing or no longer the prefab as shipped, and writes
    /// the room identity the build composes its room name from.
    /// </summary>
    public static class NetPublishWalk
    {
        /// <param name="creatorName">The creator's lookup-form username.</param>
        /// <param name="productName">The repository name this publish deploys to.</param>
        public static bool Run(string[] selectedScenePaths, string creatorName, string productName)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return false;
            }

            var unsaved = CollectUnsavedSelected(selectedScenePaths);

            if (unsaved.Count > 0)
            {
                var unsavedMessage = BuildUnsavedRefusal(unsaved);
                Report("Publish stopped", unsavedMessage, unsavedMessage);
                return false;
            }

            if (!NetEntitySceneFinder.CheckPreconditions(out string finderError))
            {
                Report("Publish stopped", finderError, finderError);
                return false;
            }

            if (!TryLocateRegistry(out U3DObjectIdRegistry registry, out string registryError))
            {
                Report("Publish stopped", registryError, registryError);
                return false;
            }

            var netEntityGuid = NetEntitySceneFinder.ResolveNetEntityGuid();
            var projectScenes = ScanProject(netEntityGuid);

            RaiseFloorAcrossProject(registry, projectScenes);

            var selectedResults = ResolveSelected(selectedScenePaths, projectScenes, netEntityGuid);

            var problems = new List<string>();
            CollectUnreadable(selectedResults, problems);
            CollectOutOfRange(selectedResults, problems);
            CollectDuplicates(selectedResults, problems);

            if (problems.Count > 0)
            {
                Report("Publish stopped", BuildRefusal(problems), BuildSummary(problems));
                return false;
            }

            var coreProblems = CollectCoreProblems(selectedScenePaths);

            if (coreProblems.Count > 0)
            {
                Report("Publish stopped", BuildCoreRefusal(coreProblems), BuildCoreSummary(coreProblems));
                return false;
            }

            if (!TryPrepareRoomIdentity(
                    selectedScenePaths,
                    registry,
                    creatorName,
                    productName,
                    out string identityPath,
                    out U3DRoomIdentityData.Entry[] identityEntries,
                    out string identityError))
            {
                Report("Publish stopped", identityError, identityError);
                return false;
            }

            var registration = NetPrefabSweep.Run(selectedScenePaths, registry, true);

            if (!registration.Succeeded)
            {
                Report("Publish stopped", registration.Error, registration.Error);
                return false;
            }

            WriteRoomIdentity(identityPath, creatorName, productName, identityEntries);

            return true;
        }

        private static bool TryLocateRegistry(out U3DObjectIdRegistry registry, out string error)
        {
            registry = null;
            error = null;

            var found = AssetDatabase.FindAssets("t:U3DObjectIdRegistry");

            if (found.Length == 0)
            {
                error = "The object registry is missing from this project. Publishing would produce " +
                        "objects that do not appear for other players. It should sit at " +
                        "Assets/U3D_ProjectData/Resources/U3DObjectIdRegistry.asset.";
                return false;
            }

            if (found.Length > 1)
            {
                var paths = new StringBuilder();
                foreach (var guid in found)
                {
                    paths.AppendLine("  " + AssetDatabase.GUIDToAssetPath(guid));
                }

                error = "This project contains more than one object registry, and only one of them " +
                        "would be used. Delete all but the correct one before publishing.\n\n" + paths;
                return false;
            }

            registry = Resources.Load<U3DObjectIdRegistry>(U3DObjectIdRegistry.ResourceName);

            if (registry == null)
            {
                error = "The object registry exists at " + AssetDatabase.GUIDToAssetPath(found[0]) +
                        " but it is not inside a folder named Resources, so it would be left out of " +
                        "the build. Move it into Assets/U3D_ProjectData/Resources/.";
                return false;
            }

            return true;
        }

        private static Dictionary<string, SceneScanResult> ScanProject(string netEntityGuid)
        {
            var results = new Dictionary<string, SceneScanResult>();

            foreach (var guid in AssetDatabase.FindAssets("t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/")) continue;
                if (results.ContainsKey(path)) continue;

                results[path] = NetEntitySceneFinder.Scan(path, netEntityGuid);
            }

            return results;
        }

        private static void RaiseFloorAcrossProject(
            U3DObjectIdRegistry registry,
            Dictionary<string, SceneScanResult> projectScenes)
        {
            ushort highest = 0;

            foreach (var result in projectScenes.Values)
            {
                if (!result.Succeeded) continue;

                foreach (var record in result.Records)
                {
                    if (record.Id < NetEntity.AuthoredIdMin) continue;
                    if (record.Id > NetEntity.AuthoredIdMax) continue;
                    if (record.Id <= highest) continue;

                    highest = (ushort)record.Id;
                }
            }

            if (highest == 0) return;

            registry.RaiseFloorAbove(highest);
            AssetDatabase.SaveAssets();
        }

        private static List<SceneScanResult> ResolveSelected(
            string[] selectedScenePaths,
            Dictionary<string, SceneScanResult> projectScenes,
            string netEntityGuid)
        {
            var selected = new List<SceneScanResult>();

            if (selectedScenePaths == null) return selected;

            foreach (var path in selectedScenePaths)
            {
                if (projectScenes.TryGetValue(path, out SceneScanResult existing))
                {
                    selected.Add(existing);
                    continue;
                }

                selected.Add(NetEntitySceneFinder.Scan(path, netEntityGuid));
            }

            return selected;
        }

        private static void CollectUnreadable(List<SceneScanResult> selected, List<string> problems)
        {
            foreach (var result in selected)
            {
                if (result.Succeeded) continue;

                var name = string.IsNullOrEmpty(result.ScenePath) ? "A scene in this build" : result.ScenePath;
                problems.Add(name + " could not be checked. " + result.Error);
            }
        }

        private static void CollectOutOfRange(List<SceneScanResult> selected, List<string> problems)
        {
            foreach (var result in selected)
            {
                if (!result.Succeeded) continue;

                foreach (var record in result.Records)
                {
                    if (record.Id >= NetEntity.AuthoredIdMin && record.Id <= NetEntity.AuthoredIdMax)
                    {
                        continue;
                    }

                    if (record.Id == 0)
                    {
                        problems.Add("\"" + record.ObjectName + "\" has not been given a number yet. " +
                                     result.ScenePath + ", line " + record.ValueLine + ".");
                        continue;
                    }

                    problems.Add("\"" + record.ObjectName + "\" has the number " + record.Id +
                                 ", which is outside the range authored objects may use (" +
                                 NetEntity.AuthoredIdMin + " to " + NetEntity.AuthoredIdMax + "). " +
                                 result.ScenePath + ", line " + record.ValueLine + ".");
                }
            }
        }

        /// <summary>
        /// Scoped to one scene. Under G304 the product segment carries the entry
        /// scene's GUID, so two scenes are two rooms and an ID repeated across them
        /// cannot collide. Pooling across the set would refuse the ordinary way a
        /// creator makes a second level, which is duplicating the first.
        /// </summary>
        private static void CollectDuplicates(List<SceneScanResult> selected, List<string> problems)
        {
            foreach (var result in selected)
            {
                if (!result.Succeeded) continue;

                var byId = new Dictionary<int, List<NetEntityRecord>>();

                foreach (var record in result.Records)
                {
                    if (record.Id == 0) continue;

                    if (!byId.TryGetValue(record.Id, out List<NetEntityRecord> sharing))
                    {
                        sharing = new List<NetEntityRecord>();
                        byId[record.Id] = sharing;
                    }

                    sharing.Add(record);
                }

                foreach (var pair in byId)
                {
                    if (pair.Value.Count < 2) continue;

                    var detail = new StringBuilder();
                    detail.Append("The number ").Append(pair.Key).Append(" is used by ")
                          .Append(pair.Value.Count).Append(" objects in ")
                          .Append(result.ScenePath).Append(":");

                    foreach (var record in pair.Value)
                    {
                        detail.AppendLine();
                        detail.Append("    \"").Append(record.ObjectName)
                              .Append("\" at line ").Append(record.ValueLine);
                    }

                    problems.Add(detail.ToString());
                }
            }
        }

        /// <summary>
        /// Checks everything the room identity write needs. Runs before registration,
        /// because registration writes permanent indices and every refusal must precede
        /// both permanent writes. Runs the same composition the build runs, once per
        /// selected scene, so names the build could not form a room from refuse here
        /// rather than running alone after publishing. The asset sits beside the registry,
        /// whose folder Resources.Load has already proven is a Resources folder.
        /// </summary>
        private static bool TryPrepareRoomIdentity(
            string[] selectedScenePaths,
            U3DObjectIdRegistry registry,
            string creatorName,
            string productName,
            out string assetPath,
            out U3DRoomIdentityData.Entry[] entries,
            out string error)
        {
            assetPath = null;
            entries = null;
            error = null;

            if (string.IsNullOrWhiteSpace(creatorName))
            {
                error = "This account has no creator username on record, so the build cannot be given " +
                        "a multiplayer room. Log out and back in from the Setup tab, then publish again.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(productName))
            {
                error = "This publish has no product name, so the build cannot be given a multiplayer " +
                        "room. Set a Product Name in the Publish tab and publish again.";
                return false;
            }

            var registryPath = AssetDatabase.GetAssetPath(registry);
            int lastSlash = string.IsNullOrEmpty(registryPath) ? -1 : registryPath.LastIndexOf('/');

            if (lastSlash < 0)
            {
                error = "The object registry has no folder on record, so the room identity cannot be " +
                        "written beside it.";
                return false;
            }

            var folder = registryPath.Substring(0, lastSlash);
            assetPath = folder + "/" + U3DRoomIdentityData.ResourceName + ".asset";

            var strays = new StringBuilder();
            foreach (var guid in AssetDatabase.FindAssets("t:U3DRoomIdentityData"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(path, assetPath, System.StringComparison.Ordinal)) continue;
                strays.AppendLine("  " + path);
            }

            if (strays.Length > 0)
            {
                error = "This project contains more than one room identity asset. Only " + assetPath +
                        " is used. Delete the others and publish again.\n\n" + strays;
                return false;
            }

            var collected = new List<U3DRoomIdentityData.Entry>();

            if (selectedScenePaths != null)
            {
                foreach (var path in selectedScenePaths)
                {
                    var sceneGuid = AssetDatabase.AssetPathToGUID(path);

                    if (string.IsNullOrEmpty(sceneGuid))
                    {
                        error = "Unity has no identifier on record for " + path + ", so multiplayer in " +
                                "that scene would run single-player. Save the scene into this project " +
                                "and publish again.";
                        return false;
                    }

                    if (!U3DRoomIdentity.TryBuildRoomPath(creatorName, productName, sceneGuid, out _))
                    {
                        error = "The creator name \"" + creatorName + "\" and product name \"" + productName +
                                "\" cannot form a multiplayer room name for " + path + ", so that scene " +
                                "would run single-player. Shorten the Product Name and publish again.";
                        return false;
                    }

                    collected.Add(new U3DRoomIdentityData.Entry { ScenePath = path, Guid = sceneGuid });
                }
            }

            entries = collected.ToArray();
            return true;
        }

        /// <summary>
        /// A selected scene that is open with unsaved changes after the save prompt. The
        /// ID checks and the build read the file on disk, while the CORE check and
        /// registration read the open scene, so a declined save would let them disagree.
        /// </summary>
        private static List<string> CollectUnsavedSelected(string[] selectedScenePaths)
        {
            var unsaved = new List<string>();

            if (selectedScenePaths == null) return unsaved;

            foreach (var path in selectedScenePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                Scene scene = SceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) unsaved.Add(path);
            }

            return unsaved;
        }

        /// <summary>
        /// Refuses a build unless at least one selected scene holds U3D CORE, and every
        /// selected scene that holds one holds a single, linked, intact instance of the
        /// template's prefab. Intact is judged against the prefab asset itself rather than
        /// a list kept here, so a part added to CORE in a later release is covered without
        /// an edit to this file. A selected scene with no CORE passes when another one has
        /// it, because a scene loaded additively runs on the entry scene's CORE (§8.9).
        /// Loads scenes the way NetPrefabSweep does, because an override on a CORE instance
        /// is invisible to a text read of the scene file.
        /// </summary>
        private static List<string> CollectCoreProblems(string[] selectedScenePaths)
        {
            var problems = new List<string>();

            if (selectedScenePaths == null || selectedScenePaths.Length == 0) return problems;

            var openedHere = new List<Scene>();
            var checkedAssets = new Dictionary<GameObject, bool>();
            int scenesWithCore = 0;

            try
            {
                foreach (var path in selectedScenePaths)
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
                        catch (System.Exception e)
                        {
                            problems.Add(path + " could not be opened to check U3D CORE. " + e.Message);
                            continue;
                        }

                        if (!scene.IsValid() || !scene.isLoaded)
                        {
                            problems.Add(path + " could not be opened to check U3D CORE.");
                            continue;
                        }

                        openedHere.Add(scene);
                    }

                    if (CheckCore(scene, path, checkedAssets, problems)) scenesWithCore++;
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

            if (scenesWithCore == 0 && problems.Count == 0)
            {
                problems.Add("None of the scenes selected for this build holds U3D CORE - DO NOT DELETE, so " +
                             "nobody would be connected to the room or given a player. Open your starting " +
                             "scene, add it from the Creator Dashboard (Project Tools, Game Systems, Add U3D " +
                             "Core Prefab), and save the scene.");
            }

            return problems;
        }

        /// <summary>
        /// Checks one scene and returns whether it holds a CORE at all. The CORE is found
        /// from its network bootstrap and traced to the prefab asset that bootstrap came
        /// from, so a CORE nested inside a creator's own prefab is still found.
        /// </summary>
        private static bool CheckCore(
            Scene scene,
            string path,
            Dictionary<GameObject, bool> checkedAssets,
            List<string> problems)
        {
            var bootstraps = new List<U3DNetworkBootstrap>();
            var roots = new List<GameObject>();
            scene.GetRootGameObjects(roots);

            foreach (GameObject root in roots)
            {
                bootstraps.AddRange(root.GetComponentsInChildren<U3DNetworkBootstrap>(true));
            }

            if (bootstraps.Count == 0) return false;

            if (bootstraps.Count > 1)
            {
                problems.Add(path + ": this scene holds " + bootstraps.Count + " copies of U3D CORE - DO NOT " +
                             "DELETE. Each would connect its own session and spawn its own player. Delete all " +
                             "but one and save the scene.");
                return true;
            }

            U3DNetworkBootstrap bootstrap = bootstraps[0];
            var sourceBootstrap = PrefabUtility.GetCorrespondingObjectFromOriginalSource(bootstrap);
            GameObject assetRoot = sourceBootstrap != null ? sourceBootstrap.transform.root.gameObject : null;
            GameObject instanceRoot = assetRoot != null ? FindInstanceRoot(bootstrap.transform, assetRoot) : null;

            if (instanceRoot == null)
            {
                problems.Add(path + ": U3D CORE - DO NOT DELETE is no longer linked to the template's prefab, " +
                             "because it was unpacked or rebuilt by hand, so it cannot be checked or updated. " +
                             "Delete it from the Hierarchy, add it again from the Creator Dashboard (Project " +
                             "Tools, Game Systems, Add U3D Core Prefab), move its spawn point back where you " +
                             "want it, and save the scene.");
                return true;
            }

            var assetPath = AssetDatabase.GetAssetPath(assetRoot);

            if (!assetPath.StartsWith("Assets/U3D/", System.StringComparison.Ordinal))
            {
                problems.Add(path + ": the CORE in this scene comes from " + assetPath + ", a copy of the " +
                             "template's prefab. Template updates never reach a copy. Delete it from the " +
                             "Hierarchy, add the template's own from the Creator Dashboard (Project Tools, Game " +
                             "Systems, Add U3D Core Prefab), and save the scene.");
                return true;
            }

            if (!checkedAssets.TryGetValue(assetRoot, out bool assetIntact))
            {
                assetIntact = CheckCoreAsset(assetRoot, assetPath, problems);
                checkedAssets[assetRoot] = assetIntact;
            }

            if (!assetIntact) return true;

            CheckCoreInstance(instanceRoot, assetRoot, path, problems);
            return true;
        }

        private static GameObject FindInstanceRoot(Transform from, GameObject assetRoot)
        {
            for (Transform t = from; t != null; t = t.parent)
            {
                if (PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject) == assetRoot)
                {
                    return t.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// The prefab asset must itself carry what the runtime needs, because every
        /// instance check below measures against it.
        /// </summary>
        private static bool CheckCoreAsset(GameObject assetRoot, string assetPath, List<string> problems)
        {
            var missing = new List<string>();
            Transform top = assetRoot.transform;

            if (!HasRunning<U3DNetworkBootstrap>(assetRoot)) missing.Add("the network bootstrap");
            if (!HasRunning<RosterSession>(assetRoot)) missing.Add("the multiplayer session");
            if (!HasRunning<U3DPlayerSpawner>(assetRoot)) missing.Add("the player spawner");

            bool hasSpawnPoint = false;
            foreach (var t in assetRoot.GetComponentsInChildren<Transform>(true))
            {
                if (IsActiveWithin(t, top) &&
                    string.Equals(t.gameObject.tag, SpawnTag, System.StringComparison.Ordinal))
                {
                    hasSpawnPoint = true;
                    break;
                }
            }
            if (!hasSpawnPoint) missing.Add("a player spawn point");

            foreach (var bootstrap in assetRoot.GetComponentsInChildren<U3DNetworkBootstrap>(true))
            {
                var serialized = new SerializedObject(bootstrap);
                foreach (var field in LockedBootstrapFields)
                {
                    var prefab = serialized.FindProperty(field + ".prefab");
                    if (prefab == null || prefab.objectReferenceValue == null)
                    {
                        missing.Add("its " + ObjectNames.NicifyVariableName(field));
                    }
                }
            }

            if (missing.Count == 0) return true;

            problems.Add("The U3D CORE - DO NOT DELETE prefab at " + assetPath + " has been changed and is " +
                         "missing " + string.Join(", ", missing) + ". Restore it from the latest U3D update: " +
                         "install it from the Creator Dashboard if one is offered, or download the newest " +
                         ".unitypackage from https://github.com/unreality3d-platform/unreality3d/releases and " +
                         "import it with Assets/U3D checked.");
            return false;
        }

        /// <summary>
        /// Every object and component the prefab asset carries must still be in the
        /// instance, switched on wherever the asset has it on, and every object keeps its
        /// tag. The session's settings and the two player prefabs must match the asset.
        /// Anything else the creator changes is theirs: transforms, spawn behaviour, the
        /// loading camera, and the cursor manager, which the docs call optional.
        /// </summary>
        private static void CheckCoreInstance(GameObject instanceRoot, GameObject assetRoot, string path, List<string> problems)
        {
            var issues = new List<string>();
            Transform assetTop = assetRoot.transform;
            var presentObjects = new HashSet<GameObject>();
            var presentComponents = new HashSet<Component>();

            if (instanceRoot.activeSelf && !instanceRoot.activeInHierarchy)
            {
                issues.Add("an object it sits inside is switched off");
            }

            foreach (var t in instanceRoot.GetComponentsInChildren<Transform>(true))
            {
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject);
                if (source == null || source.transform.root != assetTop) continue;

                presentObjects.Add(source);

                if (!IsOptional(source))
                {
                    if (source.activeSelf && !t.gameObject.activeSelf)
                    {
                        issues.Add("\"" + t.gameObject.name + "\" is switched off");
                    }

                    if (!string.Equals(source.tag, t.gameObject.tag, System.StringComparison.Ordinal))
                    {
                        issues.Add("\"" + t.gameObject.name + "\" has its tag changed from " + source.tag +
                                   " to " + t.gameObject.tag);
                    }
                }

                foreach (var component in t.GetComponents<Component>())
                {
                    if (component == null) continue;

                    var sourceComponent = PrefabUtility.GetCorrespondingObjectFromOriginalSource(component);
                    if (sourceComponent == null || sourceComponent.transform.root != assetTop) continue;

                    presentComponents.Add(sourceComponent);

                    if (component is U3DWebGLCursorManager) continue;

                    if (component is Behaviour behaviour && sourceComponent is Behaviour sourceBehaviour &&
                        sourceBehaviour.enabled && !behaviour.enabled)
                    {
                        issues.Add("the " + ComponentName(component) + " on \"" + t.gameObject.name +
                                   "\" is switched off");
                    }

                    if (component is RosterSession)
                    {
                        CompareLocked(component, sourceComponent, null, t.gameObject.name, issues);
                    }
                    else if (component is U3DNetworkBootstrap)
                    {
                        CompareLocked(component, sourceComponent, LockedBootstrapFields, t.gameObject.name, issues);
                    }
                }
            }

            foreach (var t in assetRoot.GetComponentsInChildren<Transform>(true))
            {
                if (IsOptional(t.gameObject)) continue;

                if (!presentObjects.Contains(t.gameObject))
                {
                    if (t.parent == null || presentObjects.Contains(t.parent.gameObject))
                    {
                        issues.Add("\"" + t.gameObject.name + "\" was removed");
                    }
                    continue;
                }

                foreach (var component in t.GetComponents<Component>())
                {
                    if (component == null || component is Transform) continue;
                    if (component is U3DWebGLCursorManager) continue;

                    if (!presentComponents.Contains(component))
                    {
                        issues.Add("the " + ComponentName(component) + " on \"" + t.gameObject.name +
                                   "\" was removed");
                    }
                }
            }

            if (issues.Count == 0) return;

            var detail = new StringBuilder();
            detail.Append(path).Append(": U3D CORE - DO NOT DELETE has been changed in ways the experience ")
                  .Append("cannot run with:");

            foreach (var issue in issues)
            {
                detail.AppendLine();
                detail.Append("    ").Append(issue);
            }

            detail.AppendLine();
            detail.Append("    To fix: select U3D CORE - DO NOT DELETE in the Hierarchy, open Overrides at the top ")
                  .Append("of the Inspector, and revert each change listed above. Revert All also undoes changes ")
                  .Append("you meant to keep, such as a moved spawn point. Then save the scene.");

            problems.Add(detail.ToString());
        }

        private static readonly string[] LockedBootstrapFields = { "localPlayerPrefab", "remoteAvatarPrefab" };

        private const string SpawnTag = "PlayerSpawnPoint";

        private static void CompareLocked(
            Component instance,
            Component source,
            string[] only,
            string objectName,
            List<string> issues)
        {
            var sourceSerialized = new SerializedObject(source);
            var instanceSerialized = new SerializedObject(instance);
            var changed = new List<string>();

            if (only != null)
            {
                foreach (var field in only)
                {
                    var a = sourceSerialized.FindProperty(field);
                    var b = instanceSerialized.FindProperty(field);
                    if (a == null || b == null) continue;
                    if (!SerializedProperty.DataEquals(a, b)) changed.Add(a.displayName);
                }
            }
            else
            {
                var iterator = sourceSerialized.GetIterator();
                bool enterChildren = true;

                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (iterator.propertyPath == "m_Script") continue;

                    var b = instanceSerialized.FindProperty(iterator.propertyPath);
                    if (b == null || !SerializedProperty.DataEquals(iterator, b)) changed.Add(iterator.displayName);
                }
            }

            if (changed.Count == 0) return;

            issues.Add("the " + ComponentName(instance) + " on \"" + objectName + "\" has settings changed that " +
                       "must stay as shipped: " + string.Join(", ", changed));
        }

        private static bool HasRunning<T>(GameObject assetRoot) where T : Behaviour
        {
            foreach (var component in assetRoot.GetComponentsInChildren<T>(true))
            {
                if (component.enabled && IsActiveWithin(component.transform, assetRoot.transform)) return true;
            }

            return false;
        }

        private static bool IsActiveWithin(Transform t, Transform top)
        {
            for (Transform current = t; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf) return false;
                if (current == top) break;
            }

            return true;
        }

        /// <summary>
        /// The cursor manager's object holds nothing else, and the docs call it optional,
        /// so removing or switching off that object is the creator's choice.
        /// </summary>
        private static bool IsOptional(GameObject source)
        {
            bool hasCursorManager = false;

            foreach (var component in source.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                if (component is U3DWebGLCursorManager)
                {
                    hasCursorManager = true;
                    continue;
                }
                return false;
            }

            return hasCursorManager;
        }

        private static string ComponentName(Component component)
        {
            return ObjectNames.NicifyVariableName(component.GetType().Name);
        }

        /// <summary>
        /// Writes the room identity whole, so the names and the scene entries always come
        /// from the same publish. Runs after registration and has nothing left to refuse.
        /// </summary>
        private static void WriteRoomIdentity(
            string assetPath,
            string creatorName,
            string productName,
            U3DRoomIdentityData.Entry[] entries)
        {
            var data = AssetDatabase.LoadAssetAtPath<U3DRoomIdentityData>(assetPath);

            if (data == null)
            {
                data = ScriptableObject.CreateInstance<U3DRoomIdentityData>();
                data.SetContents(creatorName, productName, entries);
                AssetDatabase.CreateAsset(data, assetPath);
            }
            else
            {
                data.SetContents(creatorName, productName, entries);
                EditorUtility.SetDirty(data);
            }

            AssetDatabase.SaveAssets();
        }

        private static string BuildRefusal(List<string> problems)
        {
            var message = new StringBuilder();

            message.AppendLine("Some objects in this build would be invisible to other players.");
            message.AppendLine();
            message.AppendLine("Every networked object needs its own number, and these do not have one:");
            message.AppendLine();

            foreach (var problem in problems)
            {
                message.Append("  ").AppendLine(problem);
            }

            message.AppendLine();
            message.AppendLine("To fix a repeated number: open the scene named above, save it, and publish again.");
            message.AppendLine("To fix a missing or out-of-range number: open the scene, select the object, and set");
            message.AppendLine("a different number in its Net Entity component. Then publish again.");

            return message.ToString();
        }

        private static string BuildSummary(List<string> problems)
        {
            var summary = new StringBuilder();

            summary.AppendLine("Some objects in this build would be invisible to other players.");
            summary.AppendLine();

            if (problems.Count == 1)
            {
                summary.AppendLine("There is 1 problem to fix.");
            }
            else
            {
                summary.AppendLine("There are " + problems.Count + " problems to fix.");
            }

            summary.AppendLine();
            summary.AppendLine("The full list has been copied to your clipboard. Paste it somewhere you can");
            summary.AppendLine("read it — a text file, or a chat window — then fix the objects it names and");
            summary.AppendLine("publish again.");

            return summary.ToString();
        }

        private static string BuildUnsavedRefusal(List<string> unsaved)
        {
            var message = new StringBuilder();

            message.AppendLine("These scenes have unsaved changes, and a publish ships only what is saved:");
            message.AppendLine();

            foreach (var path in unsaved)
            {
                message.Append("  ").AppendLine(path);
            }

            message.AppendLine();
            message.AppendLine("Save them (File, Save), then press Make It Live! again.");

            return message.ToString();
        }

        private static string BuildCoreRefusal(List<string> problems)
        {
            var message = new StringBuilder();

            message.AppendLine("U3D CORE - DO NOT DELETE is missing or changed, so this experience would not run");
            message.AppendLine("for visitors. Every experience needs one unchanged copy of it.");
            message.AppendLine();

            foreach (var problem in problems)
            {
                message.Append("  ").AppendLine(problem);
                message.AppendLine();
            }

            message.AppendLine("Save each scene after fixing it, then publish again.");

            return message.ToString();
        }

        private static string BuildCoreSummary(List<string> problems)
        {
            var summary = new StringBuilder();

            summary.AppendLine("U3D CORE - DO NOT DELETE is missing or changed, so this experience would not run");
            summary.AppendLine("for visitors.");
            summary.AppendLine();
            summary.AppendLine(problems.Count == 1
                ? "There is 1 problem to fix."
                : "There are " + problems.Count + " problems to fix.");
            summary.AppendLine();
            summary.AppendLine("The full list, with how to fix each one, has been copied to your clipboard.");
            summary.AppendLine("Paste it somewhere you can read it, fix what it names, and publish again.");

            return summary.ToString();
        }

        private static void Report(string title, string full, string summary)
        {
            EditorGUIUtility.systemCopyBuffer = full;
            EditorUtility.DisplayDialog(title, summary, "OK");
        }
    }
}