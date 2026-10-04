using System.Collections.Generic;
using System.Text;
using U3D.Net;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Editor
{
    public static class NetEntitySceneFinderProbe
    {
        [MenuItem("Tools/U3D/Probe - Scan Demo Scene For Net Entities")]
        public static void Run()
        {
            if (!NetEntitySceneFinder.CheckPreconditions(out string error))
            {
                EditorUtility.DisplayDialog("Finder probe", error, "OK");
                return;
            }

            var netEntityGuid = NetEntitySceneFinder.ResolveNetEntityGuid();

            var sceneGuids = AssetDatabase.FindAssets("U3DDemoScene t:Scene");
            if (sceneGuids.Length == 0)
            {
                EditorUtility.DisplayDialog("Finder probe", "Could not find U3DDemoScene.", "OK");
                return;
            }

            var scenePath = AssetDatabase.GUIDToAssetPath(sceneGuids[0]);
            var result = NetEntitySceneFinder.Scan(scenePath, netEntityGuid);

            var report = new StringBuilder();
            report.AppendLine("Scene: " + scenePath);
            report.AppendLine("NetEntity GUID: " + netEntityGuid);

            if (!result.Succeeded)
            {
                report.AppendLine("FAILED: " + result.Error);
            }
            else
            {
                int fullBlocks = 0;
                int overrides = 0;
                foreach (var record in result.Records)
                {
                    if (record.Form == NetEntityRecordForm.FullBlock)
                    {
                        fullBlocks++;
                    }
                    else
                    {
                        overrides++;
                    }
                }

                report.AppendLine("Total found: " + result.Records.Count);
                report.AppendLine("Full blocks: " + fullBlocks);
                report.AppendLine("Prefab overrides: " + overrides);
                report.AppendLine();

                foreach (var record in result.Records)
                {
                    report.AppendLine(record.Form + "  id " + record.Id + "  line " + record.ValueLine +
                                      "  " + record.ObjectName);
                }

                if (result.Anomalies.Count > 0)
                {
                    report.AppendLine();
                    report.AppendLine("Anomalies:");
                    foreach (var anomaly in result.Anomalies)
                    {
                        report.AppendLine("  " + anomaly);
                    }
                }
            }

            Deliver(report.ToString());
        }

        [MenuItem("Tools/U3D/Probe - Sweep Whole Project For Net Entities")]
        public static void RunProjectSweep()
        {
            if (!NetEntitySceneFinder.CheckPreconditions(out string error))
            {
                EditorUtility.DisplayDialog("Sweep probe", error, "OK");
                return;
            }

            var netEntityGuid = NetEntitySceneFinder.ResolveNetEntityGuid();

            var report = new StringBuilder();
            int sceneCount = 0;
            int recordCount = 0;
            int highest = 0;

            foreach (var sceneGuid in AssetDatabase.FindAssets("t:Scene"))
            {
                var path = AssetDatabase.GUIDToAssetPath(sceneGuid);

                if (string.IsNullOrEmpty(path)) continue;
                if (!path.StartsWith("Assets/")) continue;

                sceneCount++;
                var result = NetEntitySceneFinder.Scan(path, netEntityGuid);

                if (!result.Succeeded)
                {
                    report.AppendLine(path + "  UNREADABLE: " + result.Error);
                    continue;
                }

                int sceneHighest = 0;
                foreach (var record in result.Records)
                {
                    if (record.Id > sceneHighest) sceneHighest = record.Id;
                    if (record.Id > highest) highest = record.Id;
                }

                recordCount += result.Records.Count;
                report.AppendLine(path + "  found " + result.Records.Count + "  highest " + sceneHighest);
            }

            var registry = Resources.Load<U3DObjectIdRegistry>(U3DObjectIdRegistry.ResourceName);
            var counter = registry != null ? registry.NextAuthoredId.ToString() : "REGISTRY NOT FOUND";

            var header = new StringBuilder();
            header.AppendLine("Scenes swept: " + sceneCount);
            header.AppendLine("Net entities found: " + recordCount);
            header.AppendLine("Highest id anywhere: " + highest);
            header.AppendLine("Registry next authored id: " + counter);
            header.AppendLine();

            Deliver(header.ToString() + report.ToString());
        }

        [MenuItem("Tools/U3D/Probe - Registration Dry Run (writes nothing)")]
        public static void RunRegistrationDryRun()
        {
            RunRegistrationProbe(false);
        }

        [MenuItem("Tools/U3D/Probe - Registration Commit (permanent)")]
        public static void RunRegistrationCommit()
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Registration commit",
                "This gives every spawnable prefab in the selected scenes a permanent number.\n\n" +
                "Numbers are never removed and never reissued, so this cannot be undone.\n\n" +
                "Run the dry run first and check its list.",
                "Register them",
                "Cancel");

            if (!proceed) return;

            RunRegistrationProbe(true);
        }

        private static void RunRegistrationProbe(bool commit)
        {
            var registry = Resources.Load<U3DObjectIdRegistry>(U3DObjectIdRegistry.ResourceName);

            if (registry == null)
            {
                EditorUtility.DisplayDialog("Registration probe",
                    "The registry was not found in a Resources folder.", "OK");
                return;
            }

            var scenePaths = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid()) continue;
                if (string.IsNullOrEmpty(scene.path)) continue;
                scenePaths.Add(scene.path);
            }

            if (scenePaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Registration probe",
                    "No saved scene is open. Open the scene you want to check and run this again.", "OK");
                return;
            }

            var result = NetPrefabSweep.Run(scenePaths.ToArray(), registry, commit);

            var report = new StringBuilder();
            report.AppendLine(commit ? "MODE: commit (permanent)" : "MODE: dry run (nothing written)");
            report.AppendLine();
            report.AppendLine("Scenes read:");
            foreach (string path in scenePaths)
            {
                report.AppendLine("  " + path);
            }
            report.AppendLine();

            if (!result.Succeeded)
            {
                report.AppendLine("REFUSED: " + result.Error);
                Deliver(report.ToString());
                return;
            }

            report.AppendLine("Prefab table count before: " + result.CountBefore);
            report.AppendLine("Prefab table count after:  " + result.CountAfter);
            report.AppendLine("Prefabs found: " + (result.NewlyRegistered.Count + result.AlreadyHeld.Count));
            report.AppendLine();

            report.AppendLine("New (" + result.NewlyRegistered.Count + "):");
            foreach (string line in result.NewlyRegistered)
            {
                report.AppendLine("  " + line);
            }

            report.AppendLine();
            report.AppendLine("Already held (" + result.AlreadyHeld.Count + "):");
            foreach (string line in result.AlreadyHeld)
            {
                report.AppendLine("  " + line);
            }

            Deliver(report.ToString());
        }

        private static void Deliver(string text)
        {
            EditorGUIUtility.systemCopyBuffer = text;
            EditorUtility.DisplayDialog("Probe", text + "\n(copied to clipboard)", "OK");
        }
    }
}