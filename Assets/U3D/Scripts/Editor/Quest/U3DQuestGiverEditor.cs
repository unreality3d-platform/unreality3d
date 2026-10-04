using UnityEngine;
using UnityEditor;

namespace U3D.Editor
{
    /// <summary>
    /// Custom property drawer for U3DInteractionChoice to display cleanly in Inspector
    /// Shows Label and Key fields side by side, hides choiceID
    /// </summary>
    [CustomPropertyDrawer(typeof(U3DInteractionChoice))]
    public class U3DInteractionChoiceDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            // Calculate rects for label and key fields
            float labelWidth = position.width * 0.6f;
            float keyWidth = position.width * 0.35f;
            float spacing = 5f;

            Rect labelRect = new Rect(position.x, position.y, labelWidth, position.height);
            Rect keyRect = new Rect(position.x + labelWidth + spacing, position.y, keyWidth - spacing, position.height);

            // Get properties
            SerializedProperty choiceLabel = property.FindPropertyRelative("choiceLabel");
            SerializedProperty choiceKey = property.FindPropertyRelative("choiceKey");

            // Draw fields without labels for clean appearance
            EditorGUI.PropertyField(labelRect, choiceLabel, GUIContent.none);
            EditorGUI.PropertyField(keyRect, choiceKey, GUIContent.none);

            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }

    /// <summary>
    /// Custom Editor for U3DQuestGiver to enhance Inspector experience
    /// Removed Dialog Position section, reordered Interaction Choices above Creator Events
    /// </summary>
    [CustomEditor(typeof(U3DQuestGiver))]
    public class U3DQuestGiverEditor : UnityEditor.Editor
    {
        private SerializedProperty questToGive;
        private SerializedProperty interactionMode;
        private SerializedProperty singleChoice;
        private SerializedProperty acceptChoice;
        private SerializedProperty declineChoice;
        private SerializedProperty multipleChoices;

        private void OnEnable()
        {
            questToGive = serializedObject.FindProperty("questToGive");
            interactionMode = serializedObject.FindProperty("interactionMode");
            singleChoice = serializedObject.FindProperty("singleChoice");
            acceptChoice = serializedObject.FindProperty("acceptChoice");
            declineChoice = serializedObject.FindProperty("declineChoice");
            multipleChoices = serializedObject.FindProperty("multipleChoices");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            U3DQuestGiver questGiver = (U3DQuestGiver)target;

            // Draw default properties first - Exclude interaction choices and UI references
            DrawPropertiesExcluding(serializedObject,
                "interactionMode",
                "singleChoice",
                "acceptChoice",
                "declineChoice",
                "multipleChoices",
                "dialogCanvas",
                "giverNameText",
                "questDescriptionText",
                "choicesParent",
                "dialogPositionTransform",
                "OnChoiceSelected",
                "OnPlayerEnterRangeEvent",
                "OnPlayerExitRangeEvent");

            // Custom section for mutually exclusive interaction choices - MOVED ABOVE Creator Events
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Interaction Choices", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "Choose how players interact with this quest giver. Each mode provides different interaction options.",
                MessageType.Info);

            // Interaction Mode Selection with radio button style
            EditorGUILayout.BeginVertical(GUI.skin.box);

            U3DInteractionMode currentMode = (U3DInteractionMode)interactionMode.enumValueIndex;

            // Single Mode
            EditorGUILayout.BeginHorizontal();
            bool singleSelected = EditorGUILayout.Toggle(currentMode == U3DInteractionMode.Single, GUILayout.Width(20));
            if (singleSelected && currentMode != U3DInteractionMode.Single)
            {
                interactionMode.enumValueIndex = (int)U3DInteractionMode.Single;
            }
            EditorGUILayout.LabelField("Single", EditorStyles.boldLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Default choice only", GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            if (currentMode == U3DInteractionMode.Single)
            {
                EditorGUI.indentLevel++;
                SerializedProperty choiceLabel = singleChoice.FindPropertyRelative("choiceLabel");
                SerializedProperty choiceKey = singleChoice.FindPropertyRelative("choiceKey");
                EditorGUILayout.PropertyField(choiceKey, new GUIContent("Choice Key"));
                EditorGUILayout.PropertyField(choiceLabel, new GUIContent("Choice Label"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(5);

            // Binary Mode  
            EditorGUILayout.BeginHorizontal();
            bool binarySelected = EditorGUILayout.Toggle(currentMode == U3DInteractionMode.Binary, GUILayout.Width(20));
            if (binarySelected && currentMode != U3DInteractionMode.Binary)
            {
                interactionMode.enumValueIndex = (int)U3DInteractionMode.Binary;
            }
            EditorGUILayout.LabelField("Binary", EditorStyles.boldLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Accept and Decline options", GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            if (currentMode == U3DInteractionMode.Binary)
            {
                EditorGUI.indentLevel++;
                SerializedProperty acceptLabel = acceptChoice.FindPropertyRelative("choiceLabel");
                SerializedProperty acceptKey = acceptChoice.FindPropertyRelative("choiceKey");
                SerializedProperty declineLabel = declineChoice.FindPropertyRelative("declineLabel");
                SerializedProperty declineKey = declineChoice.FindPropertyRelative("choiceKey");

                EditorGUILayout.LabelField("Accept Choice", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(acceptKey, new GUIContent("Choice Key"));
                EditorGUILayout.PropertyField(acceptLabel, new GUIContent("Choice Label"));
                EditorGUI.indentLevel--;

                EditorGUILayout.LabelField("Decline Choice", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(declineKey, new GUIContent("Choice Key"));
                EditorGUILayout.PropertyField(declineLabel, new GUIContent("Choice Label"));
                EditorGUI.indentLevel--;
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(5);

            // Multiple Mode
            EditorGUILayout.BeginHorizontal();
            bool multipleSelected = EditorGUILayout.Toggle(currentMode == U3DInteractionMode.Multiple, GUILayout.Width(20));
            if (multipleSelected && currentMode != U3DInteractionMode.Multiple)
            {
                interactionMode.enumValueIndex = (int)U3DInteractionMode.Multiple;
            }
            EditorGUILayout.LabelField("Multiple", EditorStyles.boldLabel, GUILayout.Width(60));
            EditorGUILayout.LabelField("Quiz-style multiple choices", GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();

            if (currentMode == U3DInteractionMode.Multiple)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(multipleChoices, new GUIContent("Multiple Choices"), true);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            // Creator Events section - NOW APPEARS AFTER Interaction Choices
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Creator Events", EditorStyles.boldLabel);

            SerializedProperty OnChoiceSelected = serializedObject.FindProperty("OnChoiceSelected");
            SerializedProperty OnPlayerEnterRangeEvent = serializedObject.FindProperty("OnPlayerEnterRangeEvent");
            SerializedProperty OnPlayerExitRangeEvent = serializedObject.FindProperty("OnPlayerExitRangeEvent");

            EditorGUILayout.PropertyField(OnChoiceSelected);
            EditorGUILayout.PropertyField(OnPlayerEnterRangeEvent);
            EditorGUILayout.PropertyField(OnPlayerExitRangeEvent);



            serializedObject.ApplyModifiedProperties();
        }
    }
}