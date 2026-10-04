using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace U3D.Editor
{
    public class SystemsToolsCategory : IToolCategory
    {
        public string CategoryName => "Game Systems";
        public System.Action<int> OnRequestTabSwitch { get; set; }
        private List<CreatorTool> tools;

        private const string CORE_PREFAB_PATH = "Assets/U3D/Prefabs/U3D CORE - DO NOT DELETE.prefab";
        private const string PLAYER_PREFAB_PATH = "Assets/U3D/Prefabs/U3D_PlayerController.prefab";
        private const string RETICLE_MATERIAL_PATH = "Assets/U3D/U3D_Assets/UI/U3DReticle.mat";
        private const string RETICLE_CHILD_NAME = "Reticle";
        private const string PLAYER_CAMERA_NAME = "PlayerCamera";

        public SystemsToolsCategory()
        {
            tools = new List<CreatorTool>
            {
                new CreatorTool("🟢 Add U3D Core Prefab", "Required in every scene. Contains networking, deployment, player spawning, and platform systems. Will not add a duplicate.", AddCorePrefab),
                new CreatorTool("🟢 Add Quest System", "Create single-player missions and objectives for your experience", () => U3DQuestSystemTools.CreateQuestSystem()),
                new CreatorTool("🟢 Add Scorable", "Creates a worldspace scoreboard you can place anywhere in your scene.", () => U3DScorableTools.AddScorable()),
                new CreatorTool("🟢 Make Scorable", "Adds a U3DScorable component to the selected object. The object should have a TextMeshPro component in its hierarchy.", () => U3DScorableTools.MakeScorable(), true),
                new CreatorTool("🟢 Add Gaze Reticle", "Adds a small aiming dot at screen center to the player. Helps players see where they're pointing for UI and interaction, especially in VR. Hides automatically in third-person. Adds to the player prefab; will not add a duplicate.", AddGazeReticle),
                new CreatorTool("🟢 Add Inventory", "10-slot hotkey inventory for the local player. Press keys 1-0 to use items from each slot. Items are collected via Make Collectable in the Interactions category.", () => U3DInventoryTools.AddInventory()),
                new CreatorTool("🟢 Make Destroyable", "Lets this object be removed for every player at once. Works on objects created during play — from an Object Spawner or an inventory. Objects placed in the scene cannot be destroyed for other players; send those home with a Trash Handler in Respawn mode. Fires OnDestroyed on every machine before removal.", AddDestroyable, true),
                new CreatorTool("🟢 Add Trash Handler", "Creates a trigger zone that destroys or respawns objects that enter it. Use as a world floor catcher, an out-of-bounds reset zone, or a scored-object collector.", AddTrashHandler),
                new CreatorTool("🟢 Make Trash Handler", "Turns the selected object's trigger collider into a destroy or respawn zone. Requires a trigger Collider on the object.", MakeTrashHandler, true),
                new CreatorTool("🟢 Add 1-Way Portal", "Creates a portal trigger and a destination marker. Players who walk into the trigger are teleported to the marker. Reposition both anywhere in the scene.", AddOneWayPortal),
                new CreatorTool("🟢 Add 2-Way Portal", "Creates two linked portals. Players who walk into either portal are teleported to the other. Reposition each portal anywhere in the scene.", AddTwoWayPortal),
                new CreatorTool("🚧 Add Dialogue System", "Critical for storytelling, NPCs, and guided experiences", () => { }),
                new CreatorTool("🚧 Add Quiz System", "Interactive questions and knowledge tests", () => { }),
                new CreatorTool("🚧 Add Checkpoint System", "Save progress and restart points for complex experiences", () => { }),
                new CreatorTool("🚧 Add Achievement / Award System", "Unlock rewards and track progression", () => { }),
                new CreatorTool("🚧 Add Timer System", "Countdown timers, time limits, scheduled events", () => { }),
                new CreatorTool("🚧 Add Progress Bar", "Visual progress tracking for objectives or loading", () => { }),
            };
        }

        public List<CreatorTool> GetTools() => tools;

        public void DrawCategory()
        {
            EditorGUILayout.LabelField("Game Systems", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Add complete game systems to enhance player engagement.", MessageType.Info);
            EditorGUILayout.Space(10);

            foreach (var tool in tools)
            {
                ProjectToolsTab.DrawCategoryTool(tool);
            }
        }

        private static void AddCorePrefab()
        {
            // Check if U3D CORE already exists in the scene by looking for key components
            var existingManagers = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var mb in existingManagers)
            {
                if (mb != null && mb.gameObject.name.Contains("U3D CORE"))
                {
                    EditorUtility.DisplayDialog("U3D Core",
                        "U3D CORE is already in this scene.\n\nFound: " + mb.gameObject.name,
                        "OK");
                    Selection.activeGameObject = mb.gameObject;
                    return;
                }
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CORE_PREFAB_PATH);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("U3D Core Prefab Not Found",
                    "Could not find the U3D Core prefab at:\n" + CORE_PREFAB_PATH +
                    "\n\nMake sure the U3D template prefab has not been moved or renamed.",
                    "OK");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.transform.position = Vector3.zero;
            Undo.RegisterCreatedObjectUndo(instance, "Add U3D Core");

            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        private static void AddDestroyable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (selected.GetComponent<U3DDestroyable>() == null)
                selected.AddComponent<U3DDestroyable>();
            else
                EditorUtility.DisplayDialog("Make Destroyable",
                    $"'{selected.name}' already has a U3DDestroyable component.", "OK");

            EditorUtility.SetDirty(selected);
        }

        private static void AddTrashHandler()
        {
            GameObject go = new GameObject("Trash Handler");

            BoxCollider col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(10f, 1f, 10f);

            go.AddComponent<U3DTrashHandler>();

            Undo.RegisterCreatedObjectUndo(go, "Add Trash Handler");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        private static void MakeTrashHandler()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider col = selected.GetComponent<Collider>();
            if (col == null)
                col = selected.AddComponent<BoxCollider>();
            col.isTrigger = true;

            if (selected.GetComponent<U3DTrashHandler>() == null)
                selected.AddComponent<U3DTrashHandler>();
            else
                EditorUtility.DisplayDialog("Make Trash Handler",
                    $"'{selected.name}' already has a U3DTrashHandler component.", "OK");

            EditorUtility.SetDirty(selected);
        }

        private static void AddOneWayPortal()
        {
            GameObject parent = new GameObject("1-Way Portal");

            GameObject trigger = new GameObject("Portal Trigger");
            trigger.transform.SetParent(parent.transform, false);
            BoxCollider col = trigger.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(2f, 2f, 1f);
            col.center = new Vector3(0f, 1f, 0f);
            U3DPortal portal = trigger.AddComponent<U3DPortal>();

            GameObject destGO = new GameObject("Portal Destination");
            destGO.transform.SetParent(parent.transform, false);
            destGO.transform.localPosition = new Vector3(0f, 0f, 4f);
            U3DPortalDestination dest = destGO.AddComponent<U3DPortalDestination>();

            WireDestination(portal, dest);

            Undo.RegisterCreatedObjectUndo(parent, "Add 1-Way Portal");
            Selection.activeGameObject = parent;
            EditorGUIUtility.PingObject(parent);
        }

        private static void AddTwoWayPortal()
        {
            GameObject parent = new GameObject("2-Way Portal");

            GameObject portalA = CreatePortalNode("Portal A", parent.transform, Vector3.zero);
            GameObject portalB = CreatePortalNode("Portal B", parent.transform, new Vector3(0f, 0f, 6f));

            WireDestination(portalA.GetComponent<U3DPortal>(), portalB.GetComponent<U3DPortalDestination>());
            WireDestination(portalB.GetComponent<U3DPortal>(), portalA.GetComponent<U3DPortalDestination>());

            Undo.RegisterCreatedObjectUndo(parent, "Add 2-Way Portal");
            Selection.activeGameObject = parent;
            EditorGUIUtility.PingObject(parent);
        }

        private static GameObject CreatePortalNode(string name, Transform parent, Vector3 localPosition)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            BoxCollider col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(2f, 2f, 1f);
            col.center = new Vector3(0f, 1f, 0f);

            go.AddComponent<U3DPortal>();
            U3DPortalDestination dest = go.AddComponent<U3DPortalDestination>();
            dest.useRotation = false;

            return go;
        }

        private static void WireDestination(U3DPortal portal, U3DPortalDestination destination)
        {
            SerializedObject so = new SerializedObject(portal);
            so.FindProperty("destination").objectReferenceValue = destination;
            so.ApplyModifiedProperties();
        }

        private static void AddGazeReticle()
        {
            // Verify the material exists before touching the prefab, so we never half-modify it.
            Material reticleMat = AssetDatabase.LoadAssetAtPath<Material>(RETICLE_MATERIAL_PATH);
            if (reticleMat == null)
            {
                EditorUtility.DisplayDialog("Reticle Material Not Found",
                    "Could not find the reticle material at:\n" + RETICLE_MATERIAL_PATH +
                    "\n\nMake sure the U3D template asset has not been moved or renamed.",
                    "OK");
                return;
            }

            // Confirm the player prefab exists.
            GameObject playerPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PLAYER_PREFAB_PATH);
            if (playerPrefabAsset == null)
            {
                EditorUtility.DisplayDialog("Player Prefab Not Found",
                    "Could not find the player prefab at:\n" + PLAYER_PREFAB_PATH +
                    "\n\nMake sure the U3D template prefab has not been moved or renamed.",
                    "OK");
                return;
            }

            // LoadPrefabContents gives an isolated, in-memory copy of the prefab. We modify
            // that copy, then SaveAsPrefabAsset writes it back. UnloadPrefabContents must
            // always run afterward (even on early return) or the isolated scene leaks —
            // hence the try/finally.
            GameObject contentsRoot = PrefabUtility.LoadPrefabContents(PLAYER_PREFAB_PATH);
            bool modified = false;

            try
            {
                // The prefab isn't instantiated in a scene, so GameObject.Find won't work.
                // Walk the transform hierarchy from the loaded root instead.
                Transform cameraTransform = FindDeepChild(contentsRoot.transform, PLAYER_CAMERA_NAME);
                if (cameraTransform == null)
                {
                    EditorUtility.DisplayDialog("Camera Not Found",
                        "Could not find a child named '" + PLAYER_CAMERA_NAME + "' inside the player prefab.\n\n" +
                        "The player prefab structure may have changed.",
                        "OK");
                    return;
                }

                // Idempotency: don't add a second Reticle if the creator runs the tool twice.
                Transform existing = cameraTransform.Find(RETICLE_CHILD_NAME);
                if (existing != null)
                {
                    EditorUtility.DisplayDialog("Gaze Reticle",
                        "The player prefab already has a Gaze Reticle.\n\n" +
                        "To remove it, delete the '" + RETICLE_CHILD_NAME + "' child under " +
                        PLAYER_CAMERA_NAME + " in " + PLAYER_PREFAB_PATH + ".",
                        "OK");
                    return;
                }

                // Build the reticle GameObject with the exact settings confirmed working:
                // Quad mesh, U3DReticle material, position (0,0,2), scale 0.01, Ignore Raycast
                // layer, no collider, U3DGazeReticle hide-logic component.
                GameObject reticle = new GameObject(RETICLE_CHILD_NAME);
                reticle.transform.SetParent(cameraTransform, false);
                reticle.transform.localPosition = new Vector3(0f, 0f, 2f);
                reticle.transform.localRotation = Quaternion.identity;
                reticle.transform.localScale = Vector3.one * 0.01f;
                reticle.layer = LayerMask.NameToLayer("Ignore Raycast");

                MeshFilter meshFilter = reticle.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = GetBuiltinQuadMesh();

                MeshRenderer meshRenderer = reticle.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = reticleMat;
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;

                reticle.AddComponent<U3D.U3DGazeReticle>();

                PrefabUtility.SaveAsPrefabAsset(contentsRoot, PLAYER_PREFAB_PATH);
                modified = true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contentsRoot);
            }

            if (modified)
            {
                AssetDatabase.SaveAssets();
                EditorUtility.DisplayDialog("Gaze Reticle Added",
                    "A gaze reticle was added to the player prefab.\n\n" +
                    "It shows a small dot at screen center and hides automatically in third-person.\n\n" +
                    "To remove it later, delete the '" + RETICLE_CHILD_NAME + "' child under " +
                    PLAYER_CAMERA_NAME + " in the player prefab.",
                    "OK");

                GameObject playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PLAYER_PREFAB_PATH);
                if (playerPrefab != null)
                    EditorGUIUtility.PingObject(playerPrefab);
            }
        }

        // Recursive transform search — needed because the prefab is loaded as isolated
        // contents, not instantiated, so GameObject.Find / FindObjectOfType don't apply.
        private static Transform FindDeepChild(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            foreach (Transform child in parent)
            {
                Transform result = FindDeepChild(child, name);
                if (result != null) return result;
            }
            return null;
        }

        // Unity's built-in Quad mesh. There's no direct API to load it by name, so borrow
        // it from a temporary primitive, then destroy the primitive immediately. The mesh
        // is a shared built-in asset and outlives the temporary GameObject.
        private static Mesh GetBuiltinQuadMesh()
        {
            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Mesh quad = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);
            return quad;
        }
    }
}