using UnityEngine;
using UnityEditor;
using TMPro;

namespace U3D.Editor
{
    public static class U3DScorableTools
    {
        public static void AddScorable()
        {
            GameObject scoreObj = new GameObject("Scorable");

            Canvas canvas = scoreObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            scoreObj.AddComponent<UnityEngine.UI.CanvasScaler>();
            scoreObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            scoreObj.AddComponent<U3DWorldspaceUI>();

            RectTransform canvasRect = scoreObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(200, 100);
            canvasRect.localScale = Vector3.one * 0.01f;

            var tmpResources = new TMPro.TMP_DefaultControls.Resources();
            GameObject textObj = TMPro.TMP_DefaultControls.CreateText(tmpResources);
            textObj.name = "ScoreText";
            textObj.transform.SetParent(scoreObj.transform, false);
            textObj.layer = LayerMask.NameToLayer("UI");

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            TextMeshProUGUI tmp = textObj.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = "0";
                tmp.fontSize = 36;
                tmp.color = new Color32(50, 50, 50, 255);
                tmp.alignment = TMPro.TextAlignmentOptions.Center;
            }

            // The score is one value everybody has to agree on, so the scoreboard needs a number
            // every peer can resolve. Score changes route to whoever holds write permission for
            // room values and the result is broadcast.
            InteractionToolsCategory.EnsureNetEntity(scoreObj);

            U3DScorable scorable = scoreObj.AddComponent<U3DScorable>();

            // Wire the TMP reference automatically so creators don't have to.
            var so = new SerializedObject(scorable);
            var scoreTextProp = so.FindProperty("scoreText");
            if (scoreTextProp != null)
            {
                scoreTextProp.objectReferenceValue = tmp;
                so.ApplyModifiedProperties();
            }

            if (SceneView.lastActiveSceneView != null)
                scoreObj.transform.position = SceneView.lastActiveSceneView.pivot;

            Undo.RegisterCreatedObjectUndo(scoreObj, "Add Scorable");
            Selection.activeGameObject = scoreObj;
            EditorGUIUtility.PingObject(scoreObj);
            EditorUtility.SetDirty(scoreObj);
        }

        public static void MakeScorable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first. To create a new worldspace scoreboard instead, use Add Scorable.");
                return;
            }

            InteractionToolsCategory.EnsureNetEntity(selected);

            if (selected.GetComponent<U3DScorable>() == null)
                selected.AddComponent<U3DScorable>();

            EditorUtility.SetDirty(selected);
        }
    }
}