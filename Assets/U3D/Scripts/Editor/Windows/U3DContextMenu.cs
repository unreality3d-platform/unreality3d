using U3D;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    /// <summary>
    /// U3D context menu for right-click hierarchy creation.
    /// Quest System items live here because quest documentation directs users
    /// to this menu. All other tools are in the Creator Dashboard.
    /// </summary>
    public static class U3DContextMenu
    {
        private const int QUEST_PRIORITY = 100;

        // ========================================
        // QUEST SYSTEM
        // ========================================

        [MenuItem("GameObject/U3D/Quest System/U3D Quest", false, QUEST_PRIORITY)]
        public static void CreateQuest()
        {
            U3DQuestSystemTools.CreateBasicQuest();
        }

        [MenuItem("GameObject/U3D/Quest System/U3D Quest Giver", false, QUEST_PRIORITY + 1)]
        public static void CreateQuestGiver()
        {
            U3DQuestSystemTools.CreateQuestGiver();
        }

        [MenuItem("GameObject/U3D/Quest System/U3D Quest Objective", false, QUEST_PRIORITY + 2)]
        public static void CreateQuestObjective()
        {
            GameObject objectiveObj = new GameObject("Quest Objective");
            U3DQuestObjective objective = objectiveObj.AddComponent<U3DQuestObjective>();
            objective.objectiveDescription = "Complete this objective";

            if (Selection.activeGameObject != null && Selection.activeGameObject.GetComponent<U3DQuest>() != null)
            {
                objectiveObj.transform.SetParent(Selection.activeGameObject.transform);

                U3DQuest parentQuest = Selection.activeGameObject.GetComponent<U3DQuest>();
                parentQuest.RefreshObjectives();
            }
            else
            {
                PositionInScene(objectiveObj);
            }

            Selection.activeGameObject = objectiveObj;
            EditorGUIUtility.PingObject(objectiveObj);
        }

        [MenuItem("GameObject/U3D/Quest System/U3D Quest Trigger", false, QUEST_PRIORITY + 3)]
        public static void CreateQuestTrigger()
        {
            if (Selection.activeGameObject != null)
            {
                GameObject selectedObj = Selection.activeGameObject;

                if (selectedObj.GetComponent<U3DQuestTrigger>() == null)
                {
                    Collider collider = selectedObj.GetComponent<Collider>();
                    if (collider == null)
                        collider = selectedObj.AddComponent<BoxCollider>();
                    collider.isTrigger = true;

                    selectedObj.AddComponent<U3DQuestTrigger>();
                    Undo.RegisterCreatedObjectUndo(selectedObj, "Add Quest Trigger");
                }
            }
            else
            {
                GameObject triggerObj = CreateTriggerObject("Quest Trigger", Color.yellow);
                triggerObj.AddComponent<U3DQuestTrigger>();

                PositionInScene(triggerObj);
                Selection.activeGameObject = triggerObj;
                EditorGUIUtility.PingObject(triggerObj);
            }
        }

        // ========================================
        // UTILITY METHODS
        // ========================================

        private static void PositionInScene(GameObject obj)
        {
            if (SceneView.lastActiveSceneView != null)
                obj.transform.position = SceneView.lastActiveSceneView.pivot;
            else
                obj.transform.position = Vector3.zero;
        }

        private static GameObject CreateTriggerObject(string name, Color color)
        {
            GameObject triggerObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            triggerObj.name = name;

            Collider collider = triggerObj.GetComponent<Collider>();
            collider.isTrigger = true;

            Renderer renderer = triggerObj.GetComponent<Renderer>();
            renderer.material = CreateTransparentMaterial(color);

            return triggerObj;
        }

        private static Material CreateTransparentMaterial(Color color)
        {
            Material material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            color.a = 0.5f;
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = 3000;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            return material;
        }
    }
}