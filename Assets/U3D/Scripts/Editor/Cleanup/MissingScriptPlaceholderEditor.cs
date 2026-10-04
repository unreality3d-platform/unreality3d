using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    [CustomEditor(typeof(U3D.MissingScriptPlaceholder))]
    public class MissingScriptPlaceholderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var placeholder = (U3D.MissingScriptPlaceholder)target;
            string searchTerm = placeholder.SuggestedSearchTerm;

            EditorGUILayout.Space(8);

            if (!string.IsNullOrEmpty(searchTerm))
            {
                EditorGUILayout.HelpBox(
                    $"This object's name suggests it might use a U3D tool. " +
                    $"Open the Creator Dashboard's Project Tools tab and search \"{searchTerm}\" " +
                    $"to see matching tools.",
                    MessageType.Info);

                if (GUILayout.Button($"Open Creator Dashboard (search: \"{searchTerm}\")", GUILayout.Height(28)))
                {
                    U3DCreatorWindow.OpenWithProjectToolsSearch(searchTerm);
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Open the Creator Dashboard's Project Tools tab to browse available U3D tools " +
                    "that might replace this missing script.",
                    MessageType.Info);

                if (GUILayout.Button("Open Creator Dashboard", GUILayout.Height(28)))
                {
                    U3DCreatorWindow.OpenWithProjectToolsSearch("");
                }
            }
        }
    }
}