using System.Collections.Generic;
using System.Text;
using U3D.Net;
using U3D.Networking;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace U3D.Editor
{
    /// <summary>
    /// Runs before a publish builds anything. Verifies the project can produce a
    /// build whose objects will take part in the session, corrects the authored-ID
    /// counter against every scene in the project, refuses the publish when a scene
    /// in the build carries an ID that would fail silently at runtime, and writes
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

        private static void Report(string title, string full, string summary)
        {
            EditorGUIUtility.systemCopyBuffer = full;
            EditorUtility.DisplayDialog(title, summary, "OK");
        }
    }
}
