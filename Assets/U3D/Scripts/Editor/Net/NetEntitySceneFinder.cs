using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace U3D.Editor
{
    public enum NetEntityRecordForm
    {
        FullBlock,
        PrefabOverride
    }

    public class NetEntityRecord
    {
        public string ScenePath;
        public string ObjectName;
        public int Id;
        public NetEntityRecordForm Form;
        public int ValueLine;
    }

    public class SceneScanResult
    {
        public string ScenePath;
        public string Error;
        public readonly List<NetEntityRecord> Records = new List<NetEntityRecord>();
        public readonly List<string> Anomalies = new List<string>();

        public bool Succeeded => string.IsNullOrEmpty(Error);
    }

    public static class NetEntitySceneFinder
    {
        private static readonly Regex DocumentHeader =
            new Regex(@"^--- !u!(\d+) &(-?\d+)");
        private static readonly Regex ScriptGuidField =
            new Regex(@"^  m_Script: \{fileID: -?\d+, guid: ([0-9a-fA-F]+)");
        private static readonly Regex GameObjectField =
            new Regex(@"^  m_GameObject: \{fileID: (-?\d+)\}");
        private static readonly Regex IdField =
            new Regex(@"^  _id: (-?\d+)\s*$");
        private static readonly Regex NameField =
            new Regex(@"^  m_Name: (.*)$");
        private static readonly Regex ModificationTarget =
            new Regex(@"^\s*- target: \{fileID: -?\d+, guid: ([0-9a-fA-F]+)");
        private static readonly Regex ModificationPath =
            new Regex(@"^\s*propertyPath: (.*)$");
        private static readonly Regex ModificationValue =
            new Regex(@"^\s*value: (.*)$");

        private class Document
        {
            public int ClassId;
            public string Anchor;
            public int Start;
            public int End;
        }

        private class Modification
        {
            public string TargetGuid;
            public string PropertyPath;
            public string Value;
            public int ValueLine;
        }

        /// <summary>
        /// Checks the two project-level facts the finder cannot work without.
        /// A Force Binary project would produce a finder that reports every
        /// scene clean, which is indistinguishable from a scene that is clean.
        /// </summary>
        public static bool CheckPreconditions(out string error)
        {
            error = null;

            if (EditorSettings.serializationMode != SerializationMode.ForceText)
            {
                error = "Asset Serialization is set to " + EditorSettings.serializationMode +
                        ". It must be Force Text. Change it in Edit > Project Settings > Editor, " +
                        "under Asset Serialization, then try again.";
                return false;
            }

            if (string.IsNullOrEmpty(ResolveNetEntityGuid()))
            {
                error = "Could not locate the NetEntity script asset in this project.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves the NetEntity script's GUID through the AssetDatabase.
        /// Never hard-coded: a regenerated .meta would leave a constant matching
        /// nothing, and the finder would then report every scene clean.
        /// </summary>
        public static string ResolveNetEntityGuid()
        {
            var candidates = AssetDatabase.FindAssets("NetEntity t:MonoScript");
            foreach (var guid in candidates)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (script == null)
                {
                    continue;
                }

                if (script.GetClass() == typeof(U3D.Net.NetEntity))
                {
                    return guid;
                }
            }

            return null;
        }

        public static List<SceneScanResult> ScanAll(IEnumerable<string> scenePaths, string netEntityGuid)
        {
            var results = new List<SceneScanResult>();
            foreach (var scenePath in scenePaths)
            {
                results.Add(Scan(scenePath, netEntityGuid));
            }
            return results;
        }

        /// <summary>
        /// Reads one scene file as text and returns every NetEntity it can see,
        /// in both of the forms a scene file uses. The scene is never opened.
        /// </summary>
        public static SceneScanResult Scan(string scenePath, string netEntityGuid)
        {
            var result = new SceneScanResult { ScenePath = scenePath };

            if (string.IsNullOrEmpty(scenePath))
            {
                result.Error = "This scene has never been saved, so there is no file to read.";
                return result;
            }

            if (!File.Exists(scenePath))
            {
                result.Error = "Scene file not found on disk: " + scenePath;
                return result;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(scenePath);
            }
            catch (System.Exception ex)
            {
                result.Error = "Could not read the scene file: " + ex.Message;
                return result;
            }

            var documents = SplitDocuments(lines);
            var namesByAnchor = CollectGameObjectNames(lines, documents);

            foreach (var document in documents)
            {
                if (document.ClassId == 114)
                {
                    ReadFullBlock(lines, document, namesByAnchor, netEntityGuid, result);
                }
                else if (document.ClassId == 1001)
                {
                    ReadPrefabInstance(lines, document, result);
                }
            }

            return result;
        }

        private static List<Document> SplitDocuments(string[] lines)
        {
            var documents = new List<Document>();

            for (int i = 0; i < lines.Length; i++)
            {
                var header = DocumentHeader.Match(lines[i]);
                if (!header.Success)
                {
                    continue;
                }

                if (documents.Count > 0)
                {
                    documents[documents.Count - 1].End = i;
                }

                documents.Add(new Document
                {
                    ClassId = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture),
                    Anchor = header.Groups[2].Value,
                    Start = i + 1,
                    End = lines.Length
                });
            }

            return documents;
        }

        private static Dictionary<string, string> CollectGameObjectNames(string[] lines, List<Document> documents)
        {
            var names = new Dictionary<string, string>();

            foreach (var document in documents)
            {
                if (document.ClassId != 1)
                {
                    continue;
                }

                for (int i = document.Start; i < document.End; i++)
                {
                    var match = NameField.Match(lines[i]);
                    if (!match.Success)
                    {
                        continue;
                    }

                    names[document.Anchor] = match.Groups[1].Value.Trim();
                    break;
                }
            }

            return names;
        }

        private static void ReadFullBlock(
            string[] lines,
            Document document,
            Dictionary<string, string> namesByAnchor,
            string netEntityGuid,
            SceneScanResult result)
        {
            string scriptGuid = null;
            string gameObjectAnchor = null;
            int id = 0;
            int idLine = -1;
            bool hasId = false;

            for (int i = document.Start; i < document.End; i++)
            {
                var line = lines[i];

                if (scriptGuid == null)
                {
                    var scriptMatch = ScriptGuidField.Match(line);
                    if (scriptMatch.Success)
                    {
                        scriptGuid = scriptMatch.Groups[1].Value;
                        continue;
                    }
                }

                if (gameObjectAnchor == null)
                {
                    var gameObjectMatch = GameObjectField.Match(line);
                    if (gameObjectMatch.Success)
                    {
                        gameObjectAnchor = gameObjectMatch.Groups[1].Value;
                        continue;
                    }
                }

                if (!hasId)
                {
                    var idMatch = IdField.Match(line);
                    if (idMatch.Success)
                    {
                        hasId = int.TryParse(idMatch.Groups[1].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out id);
                        idLine = i + 1;
                    }
                }
            }

            if (scriptGuid == null || !string.Equals(scriptGuid, netEntityGuid, System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string objectName = "(unnamed)";
            if (gameObjectAnchor != null && namesByAnchor.TryGetValue(gameObjectAnchor, out string resolved) &&
                !string.IsNullOrEmpty(resolved))
            {
                objectName = resolved;
            }

            if (!hasId)
            {
                result.Anomalies.Add("A NetEntity block on \"" + objectName + "\" carries no _id value.");
                return;
            }

            result.Records.Add(new NetEntityRecord
            {
                ScenePath = result.ScenePath,
                ObjectName = objectName,
                Id = id,
                Form = NetEntityRecordForm.FullBlock,
                ValueLine = idLine
            });
        }

        private static void ReadPrefabInstance(string[] lines, Document document, SceneScanResult result)
        {
            var modifications = new List<Modification>();
            Modification pending = null;

            for (int i = document.Start; i < document.End; i++)
            {
                var line = lines[i];

                var targetMatch = ModificationTarget.Match(line);
                if (targetMatch.Success)
                {
                    if (pending != null)
                    {
                        modifications.Add(pending);
                    }

                    pending = new Modification { TargetGuid = targetMatch.Groups[1].Value };
                    continue;
                }

                if (pending == null)
                {
                    continue;
                }

                var pathMatch = ModificationPath.Match(line);
                if (pathMatch.Success)
                {
                    pending.PropertyPath = pathMatch.Groups[1].Value.Trim();
                    continue;
                }

                var valueMatch = ModificationValue.Match(line);
                if (valueMatch.Success)
                {
                    pending.Value = valueMatch.Groups[1].Value.Trim();
                    pending.ValueLine = i + 1;
                }
            }

            if (pending != null)
            {
                modifications.Add(pending);
            }

            string nameOverride = null;
            foreach (var modification in modifications)
            {
                if (modification.PropertyPath == "m_Name" && !string.IsNullOrEmpty(modification.Value))
                {
                    nameOverride = modification.Value;
                    break;
                }
            }

            foreach (var modification in modifications)
            {
                if (modification.PropertyPath != "_id")
                {
                    continue;
                }

                if (!int.TryParse(modification.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                {
                    result.Anomalies.Add("A prefab instance carries an _id override that is not a number: \"" +
                                         modification.Value + "\".");
                    continue;
                }

                result.Records.Add(new NetEntityRecord
                {
                    ScenePath = result.ScenePath,
                    ObjectName = ResolveInstanceName(nameOverride, modification.TargetGuid),
                    Id = id,
                    Form = NetEntityRecordForm.PrefabOverride,
                    ValueLine = modification.ValueLine
                });
            }
        }

        private static string ResolveInstanceName(string nameOverride, string sourcePrefabGuid)
        {
            if (!string.IsNullOrEmpty(nameOverride))
            {
                return nameOverride;
            }

            var prefabPath = AssetDatabase.GUIDToAssetPath(sourcePrefabGuid);
            if (!string.IsNullOrEmpty(prefabPath))
            {
                return Path.GetFileNameWithoutExtension(prefabPath);
            }

            return "(prefab instance)";
        }
    }
}