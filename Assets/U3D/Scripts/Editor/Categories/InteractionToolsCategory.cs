using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using U3D.Net;

namespace U3D.Editor
{
    public class InteractionToolsCategory : IToolCategory
    {
        public string CategoryName => "Interactions";
        public System.Action<int> OnRequestTabSwitch { get; set; }
        private List<CreatorTool> tools;

        public InteractionToolsCategory()
        {
            tools = new List<CreatorTool>
            {
                new CreatorTool("🟢 Make Object Spawner", "Spawns a prefab at this location. Turn on 'Networked Spawn' so every player sees the spawned object, then use the component's Register Prefab(s) for Multiplayer button to prepare your prefab.", ApplyObjectSpawner, true),
                new CreatorTool("🟢 Make Grabbable", "Objects can be picked up from an adjustable distance. By default a released object floats where it's let go. Switch on Drop On Release to make it fall, or Throwable to throw it where the player is looking. Pair with Add Grab Point to control exactly how the object sits in the hand.", ApplyGrabbable, true),
                new CreatorTool("🟢 Add Grab Point", "Controls how a Grabbable sits in the hand. Adds a Grab Point child — move it to where the hand grips the object, and rotate it so its green arrow points away from the player when held. Without one, objects use the Grab Offset field and keep their pickup rotation.", ApplyAddGrabPoint, true),
                new CreatorTool("🟢 Make Kickable", "Objects on the ground can be propelled forward at foot level", ApplyKickable, true),
                new CreatorTool("🟢 Make Movable", "Objects can be pushed, pulled and rotated along surfaces with player movement. Toggle push and pull independently, and enable rotation for ratcheted camera-yaw turning. Activates with the interaction key — toggle on to start, toggle off or walk out of range to stop.", ApplyMovable, true),
                new CreatorTool("🟢 Make Climbable", "Surfaces players can climb (W=up, S=down, A/D=lateral, Space=detach)", ApplyClimbable, true),
                new CreatorTool("🟢 Make Swimmable", "Players swim through this trigger volume — full 3D movement, no gravity. Add a Box, Sphere, or Mesh collider sized to your water.", ApplySwimmable, true),
                new CreatorTool("🟢 Make Trampoline", "Players who jump or fall onto this object bounce upward. Tune the bounce height in the Inspector, or turn on Randomize Height for a random bounce between min and max each landing.", ApplyTrampoline, true),
                new CreatorTool("🟢 Make Enter Trigger", "Execute actions when player enters trigger area", ApplyEnterTrigger, true),
                new CreatorTool("🟢 Make Exit Trigger", "Execute actions when player exits trigger area", ApplyExitTrigger, true),
                new CreatorTool("🟢 Make Interact Trigger", "Execute actions when player interacts with this object (Interact key or mouse click)", ApplyInteractTrigger, true),
                new CreatorTool("🟢 Make On Interact Collectable", "Players press the Interact key nearby to pick this up into their Inventory. Stays solid, so it can still block the player. Pairs with the Inventory in Game Systems.", U3DInventoryTools.ApplyInteractCollectable, true),
                new CreatorTool("🟢 Make On Enter Collectable", "Players pick this up by walking into it — for pass-through items like coins or gems. Becomes a trigger, so it won't block the player. Pairs with the Inventory in Game Systems.", U3DInventoryTools.ApplyEnterCollectable, true),
                new CreatorTool("🟢 Make Attachment", "Lets players wear an accessory — or a whole set — on chosen parts of their avatar. Place this on a persistent scene object as the visual indicator, then assign your accessory prefab in the Inspector. Use Add Attachment Point to mark where each piece sits on the body. Pieces without an attachment point won't attach.", ApplyMakeAttachment, true),
                new CreatorTool("🟢 Add Attachment Point", "Marks where an accessory piece sits on the avatar. Select your attachment source for a single accessory worn as one piece, or open your accessory prefab and select each piece for a set, then click to add an attachment point and pick its bone.", ApplyAddAttachmentPoint, true),
                new CreatorTool("🟢 Make Trigger Zone", "Fire events when zone goes from empty to occupied, and when it clears", ApplyTriggerZone, true),
                new CreatorTool("🟢 Make Delayed Trigger Activation", "Disables a trigger's collider briefly at scene start so OnTriggerEnter only fires on real entries, not on scene-load overlap. Use on triggers that start with an animated object already inside.", ApplyDelayedTriggerActivation, true),
                new CreatorTool("🟢 Add Blend Shape Control", "Animates a blend shape on this object's model — a smooth shape change like a face morph or an object transforming. Wire Morph In and Morph Out to any trigger's events (a Trigger Zone's occupied and cleared, an Enter or Exit Trigger, an Interact Trigger's Toggle), or to a thrown object's impact. The change plays for the player who sets it off.", ApplyBlendShapeControl, true),
                // ── Movement ──
                new CreatorTool("🟢 Add Seat", "Adds a sit point to this object. Position and rotate the Seat child to match your visuals. Players exit by resuming movement from stationary seats, and via the interact key from seats on Steerables.", ApplyAddSeat, true),
                new CreatorTool("🟢 Make Rideable", "Players can get on top and will be moved with the object", ApplyMakeRideable, true),
                new CreatorTool("🟢 Make Steerable", "Lets players steer this object with movement controls. Place this on a persistent scene object — not spawned objects. Assign your steerable's visuals and optional avatar-replacement in the Inspector.", ApplyMakeSteerable, true),
                new CreatorTool("🚧 Add Scene Portal", "Portal to load a different scene", () => { }, true),
                new CreatorTool("🚧 Add 1-Way Portal", "Portal for one-direction travel within scene", () => { }, true),
                new CreatorTool("🚧 Add 2-Way Portal", "Portal for bi-directional travel within scene", () => { }, true),
            };
        }

        public List<CreatorTool> GetTools() => tools;

        public void DrawCategory()
        {
            EditorGUILayout.LabelField("Interaction Tools", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Add interactive behaviors to your objects. Select an object first, then click Apply.", MessageType.Info);
            EditorGUILayout.Space(10);

            bool inMovementSection = false;

            foreach (var tool in tools)
            {
                if (!inMovementSection && IsMovementTool(tool.title))
                {
                    inMovementSection = true;
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                    GUIStyle movementHeaderStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontStyle = FontStyle.BoldAndItalic,
                        alignment = TextAnchor.MiddleCenter
                    };
                    EditorGUILayout.LabelField("Movement", movementHeaderStyle);
                    EditorGUILayout.Space(4);
                }

                ProjectToolsTab.DrawCategoryTool(tool);
            }
        }

        private static bool IsMovementTool(string title)
        {
            return title == "🟢 Add Seat"
                || title == "🟢 Make Rideable"
                || title == "🟢 Make Steerable"
                || title == "🚧 Add Scene Portal"
                || title == "🚧 Add 1-Way Portal"
                || title == "🚧 Add 2-Way Portal";
        }

        private static void ApplyObjectSpawner()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (selected.GetComponent<U3DObjectSpawner>() == null)
                selected.AddComponent<U3DObjectSpawner>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyGrabbable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (!selected.GetComponent<Collider>())
                selected.AddComponent<BoxCollider>();

            if (selected.GetComponent<U3DGrabbable>() == null)
                selected.AddComponent<U3DGrabbable>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyMovable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (!selected.GetComponent<Collider>())
                selected.AddComponent<BoxCollider>();

            EnsureNetEntity(selected);

            Rigidbody rb = selected.GetComponent<Rigidbody>();
            if (rb == null)
                rb = selected.AddComponent<Rigidbody>();
            rb.useGravity = true;
            rb.mass = 5f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            if (selected.GetComponent<U3DMovable>() == null)
                selected.AddComponent<U3DMovable>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyAddGrabPoint()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (selected.GetComponent<U3DGrabbable>() == null)
            {
                EditorUtility.DisplayDialog(
                    "Add Grab Point",
                    "This object isn't grabbable yet. Run Make Grabbable on it first, then click Add Grab Point.",
                    "OK");
                return;
            }

            // Idempotent — reuse an existing marker anywhere under this object.
            var existing = selected.GetComponentInChildren<U3DGrabPoint>(true);
            if (existing != null)
            {
                Debug.Log($"'{selected.name}' already has a Grab Point. Adjust its position and rotation in the Scene view.");
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            GameObject pointGO = new GameObject("Grab Point");
            Undo.RegisterCreatedObjectUndo(pointGO, "Add Grab Point");
            pointGO.transform.SetParent(selected.transform, false);
            pointGO.transform.localPosition = Vector3.zero;
            pointGO.transform.localRotation = Quaternion.identity;
            pointGO.AddComponent<U3DGrabPoint>();

            // Works inside an open Prefab Stage too — mark the stage dirty so the
            // save prompt appears, matching the Add Attachment Point pattern.
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.IsPartOfPrefabContents(selected))
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(prefabStage.scene);

            EditorUtility.SetDirty(selected);
            Selection.activeGameObject = pointGO;
            EditorGUIUtility.PingObject(pointGO);
        }

        private static void ApplyKickable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (!selected.GetComponent<Collider>())
                selected.AddComponent<BoxCollider>();

            Rigidbody rb = selected.GetComponent<Rigidbody>();
            if (rb == null)
                rb = selected.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = true;
            rb.mass = 1f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            if (selected.GetComponent<U3DKickable>() == null)
                selected.AddComponent<U3DKickable>();

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

        private static void ApplyEnterTrigger()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                collider = selected.AddComponent<BoxCollider>();
            collider.isTrigger = true;

            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DEnterTrigger>() == null)
            {
                selected.AddComponent<U3DEnterTrigger>();
            }
            else
            {
                Debug.Log(
                    $"'{selected.name}' already has a U3D Enter Trigger. " +
                    $"To add a second trigger that fires on a different tag, use the Inspector's Add Component button " +
                    $"and search for 'U3D Enter Trigger'. Each trigger can have its own Required Tag and Events."
                );
            }

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyExitTrigger()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                collider = selected.AddComponent<BoxCollider>();
            collider.isTrigger = true;

            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DExitTrigger>() == null)
            {
                selected.AddComponent<U3DExitTrigger>();
            }
            else
            {
                Debug.Log(
                    $"'{selected.name}' already has a U3D Exit Trigger. " +
                    $"To add a second trigger that fires on a different tag, use the Inspector's Add Component button " +
                    $"and search for 'U3D Exit Trigger'. Each trigger can have its own Required Tag and Events."
                );
            }

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyInteractTrigger()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                selected.AddComponent<BoxCollider>();

            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DInteractTrigger>() == null)
            {
                selected.AddComponent<U3DInteractTrigger>();
            }
            else
            {
                Debug.Log($"'{selected.name}' already has a U3D Interact Trigger.");
            }

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyTriggerZone()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                collider = selected.AddComponent<BoxCollider>();
            collider.isTrigger = true;

            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DTriggerZone>() == null)
            {
                selected.AddComponent<U3DTriggerZone>();
            }
            else
            {
                Debug.Log(
                    $"'{selected.name}' already has a U3D Trigger Zone. " +
                    $"To add a second zone that fires on a different tag, use the Inspector's Add Component button " +
                    $"and search for 'U3D Trigger Zone'. Each zone can have its own Required Tag and Events."
                );
            }

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyDelayedTriggerActivation()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
            {
                Debug.LogWarning("Make Delayed Trigger Activation: selected object has no Collider. Add a trigger first (Make Enter Trigger, Make Exit Trigger, or Make Trigger Zone) before applying this.");
                return;
            }

            if (selected.GetComponent<U3DDelayedTriggerActivation>() == null)
                selected.AddComponent<U3DDelayedTriggerActivation>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyClimbable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            EnsureClimbableLayerExists();

            if (!selected.GetComponent<Collider>())
                selected.AddComponent<BoxCollider>();

            SetLayerRecursive(selected, U3DClimbable.CLIMBABLE_LAYER);

            if (selected.GetComponent<U3DClimbable>() == null)
                selected.AddComponent<U3DClimbable>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplySwimmable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                collider = selected.AddComponent<BoxCollider>();
            collider.isTrigger = true;

            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DSwimmable>() == null)
            {
                selected.AddComponent<U3DSwimmable>();
            }
            else
            {
                Debug.Log(
                    $"'{selected.name}' already has a U3D Swimmable. " +
                    $"To add a second swimmable that fires on a different tag, use the Inspector's Add Component button " +
                    $"and search for 'U3D Swimmable'. Each swimmable can have its own Required Tag and Events."
                );
            }

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyTrampoline()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Collider existingCollider = selected.GetComponent<Collider>();
            if (existingCollider == null)
            {
                selected.AddComponent<BoxCollider>();
            }
            else if (existingCollider.isTrigger)
            {
                existingCollider.isTrigger = false;
                Debug.Log($"'{selected.name}': existing {existingCollider.GetType().Name} was a trigger — set to solid so players can land on it. Trampolines need a solid surface.");
            }

            if (selected.GetComponent<U3DTrampoline>() == null)
                selected.AddComponent<U3DTrampoline>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyAddSeat()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            // Add Seat on a Steerable installs a driver seat (U3DDriverPose) inside the
            // steerable's assigned visual prefab, rather than a seat object in the scene.
            var steerable = selected.GetComponent<U3D.U3DSteerable>();
            if (steerable != null)
            {
                AddDriverSeatToSteerable(steerable);
                return;
            }

            // Guard: catch Add Seat invoked directly on a costume prefab — a prefab used as
            // some Steerable's Vehicle or Replacement Visual — whether by editing it in a
            // Prefab Stage or selecting the asset in the Project. The ordinary path below would
            // bury a room-participating seat inside the costume, which takes part in nothing.
            // Redirect to selecting the Steerable, which routes to AddDriverSeatToSteerable.
            if (IsSteerableCostumePrefabContext(selected))
            {
                EditorUtility.DisplayDialog(
                    "Add Seat",
                    "This prefab is used as a Steerable's visual (a costume). Adding an ordinary seat here would bury the seat inside the costume, which won't work.\n\n" +
                    "To add a driver seat: close this prefab, select the Steerable in your scene, and click Add Seat. It installs the seat into the Vehicle Visual for you.\n\n" +
                    "(Driver seats only work on the Vehicle Visual, not the Replacement Visual.)",
                    "OK");
                return;
            }

            // Idempotent seat child — reuse if already present.
            Transform existingSeat = selected.transform.Find("Seat");
            if (existingSeat != null)
            {
                EnsureNetEntity(existingSeat.gameObject);
                EditorUtility.SetDirty(existingSeat.gameObject);

                Debug.Log($"'{selected.name}' already has a Seat child. Adjust its position and rotation in the Inspector.");
                Selection.activeGameObject = existingSeat.gameObject;
                EditorGUIUtility.PingObject(existingSeat.gameObject);
                return;
            }

            GameObject seatGO = new GameObject("Seat");
            Undo.RegisterCreatedObjectUndo(seatGO, "Add Seat");
            seatGO.transform.SetParent(selected.transform, false);
            seatGO.transform.localPosition = Vector3.zero;
            seatGO.transform.localRotation = Quaternion.identity;

            // U3DSeat has [RequireComponent(typeof(Collider))]. AddComponent will refuse
            // to add U3DSeat unless a Collider already exists on the same GameObject.
            // This is a trigger — the OverlapSphere in U3DInteractionManager uses
            // QueryTriggerInteraction.Collide and only hits triggers, not solid colliders.
            // Sphere is small and unobtrusive; it's a detection volume, not a physics body.
            var seatCollider = seatGO.AddComponent<SphereCollider>();
            seatCollider.isTrigger = true;
            seatCollider.radius = 0.3f;

            // The entity goes on the seat itself rather than on the object carrying it, so a
            // rideable platform and the seat bolted to it hold separate numbers. Occupancy is
            // claimed against the seat's own entity.
            EnsureNetEntity(seatGO);

            seatGO.AddComponent<U3DSeat>();

            // Select and ping so the creator immediately sees what needs adjusting.
            EditorUtility.SetDirty(selected);
            Selection.activeGameObject = seatGO;
            EditorGUIUtility.PingObject(seatGO);
        }

        /// <summary>
        /// True when 'selected' is (or is part of) a prefab asset that a U3DSteerable in any
        /// open scene references as its Vehicle or Replacement Visual. Covers both editing the
        /// costume in a Prefab Stage and selecting the costume asset in the Project window.
        /// Used to block the ordinary U3DSeat path from dropping a seat inside a costume that
        /// takes part in nothing. Steerables in unopened scenes can't be scanned, so this is a
        /// best-effort guard against the common workflow, not a guarantee.
        /// </summary>
        private static bool IsSteerableCostumePrefabContext(GameObject selected)
        {
            string prefabPath = null;

            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
                prefabPath = prefabStage.assetPath;
            else if (PrefabUtility.IsPartOfPrefabAsset(selected))
                prefabPath = AssetDatabase.GetAssetPath(selected);

            if (string.IsNullOrEmpty(prefabPath)) return false;

            var steerables = Object.FindObjectsByType<U3D.U3DSteerable>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var s in steerables)
            {
                if (s == null) continue;

                if (s.VehicleVisualPrefab != null
                    && AssetDatabase.GetAssetPath(s.VehicleVisualPrefab) == prefabPath)
                    return true;

                if (s.ReplacementVisualPrefab != null
                    && AssetDatabase.GetAssetPath(s.ReplacementVisualPrefab) == prefabPath)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Installs a driver seat (U3DDriverPose) inside the steerable's assigned visual
        /// prefab. Mirrors the spawner's prefab-edit pattern: load isolated contents, add an
        /// idempotent child, save. Deliberately adds no NetEntity and registers nothing — the
        /// marker is a position, built locally on every peer from the steerable it belongs to.
        /// If the steerable's avatar mode is Hidden (the default), it is switched to Seated so
        /// the posed driver is visible to everyone.
        /// </summary>
        private static void AddDriverSeatToSteerable(U3D.U3DSteerable steerable)
        {
            GameObject visualPrefab = steerable.VehicleVisualPrefab;
            if (visualPrefab == null)
            {
                EditorUtility.DisplayDialog(
                    "Add Seat",
                    "This Steerable has no visual prefab assigned yet. Assign your steerable's visual (the costume the driver rides) to 'Vehicle Visual Prefab' first, then click Add Seat to place the driver seat inside it.",
                    "OK");
                return;
            }

            string path = AssetDatabase.GetAssetPath(visualPrefab);
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog(
                    "Add Seat",
                    "The assigned visual is not a Project prefab asset. Assign a prefab from your Project (not a scene object) to 'Vehicle Visual Prefab', then click Add Seat.",
                    "OK");
                return;
            }

            ConfigureDriverPosePrefab(path, out string message);

            // A driver seat needs a visible humanoid to pose onto. HiddenAvatar (the steerable
            // default) would hide it, so move to a visible mode. Avatar Mode governs the pose
            // (Seated vs Standing) from here.
            if (steerable.AvatarMode == U3D.SteerableAvatarMode.HiddenAvatar)
            {
                var so = new SerializedObject(steerable);
                var modeProp = so.FindProperty("avatarMode");
                if (modeProp != null)
                {
                    modeProp.enumValueIndex = (int)U3D.SteerableAvatarMode.SeatedAvatar;
                    so.ApplyModifiedProperties();
                    message += "\n\nAvatar Mode was Hidden, so it was set to Seated — otherwise the driver would be invisible.";
                }
            }

            EditorUtility.DisplayDialog("Add Seat", message, "OK");

            EditorGUIUtility.PingObject(visualPrefab);
            Debug.Log($"U3DSteerable driver seat: open the '{visualPrefab.name}' prefab and move its 'Driver Seat' child to where the driver should be. Choose Seated or Standing in this steerable's Avatar Mode — Standing anchors the feet to the marker so you can sit it on a surface, Seated anchors the hips.");
        }

        /// <summary>
        /// Adds an idempotent U3DDriverPose child to the prefab at the given path using the
        /// LoadPrefabContents pattern. Adding only a child leaves the prefab root's authored
        /// transform untouched, so the visual still places itself on the player exactly as
        /// the creator built it.
        /// </summary>
        private static void ConfigureDriverPosePrefab(string path, out string message)
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var existing = prefabRoot.GetComponentInChildren<U3D.U3DDriverPose>(true);
                if (existing != null)
                {
                    message = $"'{prefabRoot.name}' already has a driver seat ('{existing.name}'). Open the prefab to reposition it.";
                    return;
                }

                GameObject seatGO = new GameObject("Driver Seat");
                seatGO.transform.SetParent(prefabRoot.transform, false);
                seatGO.transform.localPosition = Vector3.zero;
                seatGO.transform.localRotation = Quaternion.identity;
                seatGO.AddComponent<U3D.U3DDriverPose>();

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);

                message = $"Added a driver seat to '{prefabRoot.name}'. Open the prefab and move the 'Driver Seat' child to where the driver should sit or stand.";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        /// <summary>
        /// Places an attachment-point marker, mirroring Add Seat. Behavior follows the selection:
        ///  • A piece selected inside an open Prefab Stage → adds a marker straight to that piece, so
        ///    a creator building a costume marks each child by selecting it and clicking.
        ///  • The scene attachment source selected → reaches into its assigned prefab. If the prefab
        ///    has no markers yet, offers to mark the whole accessory as one piece (a helmet) or to
        ///    defer to per-piece editing for a costume. If it already has markers, reports and defers.
        ///  • Anything else → explains where markers belong.
        /// Each piece is whatever a marker is parented to, so a marker on the prefab root makes the
        /// whole prefab one piece, and a marker inside a child makes that child a piece.
        /// </summary>
        private static void ApplyAddAttachmentPoint()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            // Case 1 — a piece selected inside an open Prefab Stage. Mark it directly.
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.IsPartOfPrefabContents(selected))
            {
                if (SelectedHasDirectMarker(selected))
                {
                    EditorUtility.DisplayDialog(
                        "Add Attachment Point",
                        $"'{selected.name}' already has an attachment point. Position that one, or select a different piece.",
                        "OK");
                    return;
                }

                AddMarkerChild(selected.transform);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(prefabStage.scene);

                EditorUtility.DisplayDialog(
                    "Add Attachment Point",
                    $"Added an attachment point to '{selected.name}'. Move it to where this piece's bone sits inside the piece, pick its Target Bone, then save the prefab. Select another piece and click again to mark it too.",
                    "OK");
                return;
            }

            // Case 2 — the scene attachment source. Reach into its assigned prefab.
            var source = selected.GetComponent<U3DAttachmentSource>();
            if (source != null)
            {
                GameObject prefab = source.AccessoryPrefab;
                if (prefab == null)
                {
                    EditorUtility.DisplayDialog(
                        "Add Attachment Point",
                        "Assign your accessory prefab to this source's 'Accessory Prefab' field first, then click Add Attachment Point.",
                        "OK");
                    return;
                }

                string path = AssetDatabase.GetAssetPath(prefab);
                if (string.IsNullOrEmpty(path) || prefab.scene.IsValid())
                {
                    EditorUtility.DisplayDialog(
                        "Add Attachment Point",
                        $"'{prefab.name}' is a scene object, not a Project prefab asset. Assign a prefab from your Project to 'Accessory Prefab', then click Add Attachment Point.",
                        "OK");
                    return;
                }

                if (prefab.GetComponentInChildren<U3DAttachmentPoint>(true) != null)
                {
                    EditorUtility.DisplayDialog(
                        "Add Attachment Point",
                        $"'{prefab.name}' already has at least one attachment point.\n\n" +
                        "For a single accessory, you're set — open the prefab to position the point and pick its bone.\n\n" +
                        "For a costume with pieces on different bones, open the prefab, select each piece, and click Add Attachment Point on each.",
                        "OK");
                    return;
                }

                bool wholeThing = EditorUtility.DisplayDialog(
                    "Add Attachment Point",
                    $"Add an attachment point to '{prefab.name}'?\n\n" +
                    "Whole accessory — a single item (a helmet, a hat) worn on one bone. The point goes on the prefab root and the whole thing attaches as one piece.\n\n" +
                    "Per piece — a costume with parts on different bones. Open the prefab, select each piece, and click Add Attachment Point on each.",
                    "Whole accessory", "Per piece (I'll open the prefab)");

                if (!wholeThing) return;

                AddMarkerToPrefabRoot(path, out string prefabName);

                EditorUtility.DisplayDialog(
                    "Add Attachment Point",
                    $"Added an attachment point to '{prefabName}'. Open the prefab, move the 'Attachment Point' to where the bone sits inside the accessory (for a helmet, the base of the skull), and pick its Target Bone.",
                    "OK");
                return;
            }

            // Case 3 — a plain scene object that isn't a source.
            EditorUtility.DisplayDialog(
                "Add Attachment Point",
                "Attachment points live inside your accessory prefab, not on a loose scene object.\n\n" +
                "Either select your attachment source (the scene object with the Attachment Source) for a single accessory, or open your accessory prefab and select the piece you want to attach, then click Add Attachment Point.",
                "OK");
        }

        /// <summary>
        /// True when the given object has an attachment-point marker parented directly to it — i.e.
        /// this object is already set up as a piece. A marker deeper in its hierarchy belongs to a
        /// nested piece, not this one, so it doesn't count.
        /// </summary>
        private static bool SelectedHasDirectMarker(GameObject piece)
        {
            var markers = piece.GetComponentsInChildren<U3DAttachmentPoint>(true);
            for (int i = 0; i < markers.Length; i++)
                if (markers[i].transform.parent == piece.transform)
                    return true;
            return false;
        }

        /// <summary>
        /// Adds an "Attachment Point" child to the given transform with a U3DAttachmentPoint on it.
        /// Used for marking a piece selected in an open Prefab Stage.
        /// </summary>
        private static void AddMarkerChild(Transform parent)
        {
            GameObject markerGO = new GameObject("Attachment Point");
            Undo.RegisterCreatedObjectUndo(markerGO, "Add Attachment Point");
            markerGO.transform.SetParent(parent, false);
            markerGO.transform.localPosition = Vector3.zero;
            markerGO.transform.localRotation = Quaternion.identity;
            markerGO.AddComponent<U3DAttachmentPoint>();
        }

        /// <summary>
        /// Adds an "Attachment Point" child to the root of the prefab at the given path, using the
        /// LoadPrefabContents pattern, then saves. A marker on the root makes the whole prefab one
        /// piece — the single-accessory case.
        /// </summary>
        private static void AddMarkerToPrefabRoot(string path, out string prefabName)
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(path);
            try
            {
                prefabName = prefabRoot.name;

                GameObject markerGO = new GameObject("Attachment Point");
                markerGO.transform.SetParent(prefabRoot.transform, false);
                markerGO.transform.localPosition = Vector3.zero;
                markerGO.transform.localRotation = Quaternion.identity;
                markerGO.AddComponent<U3DAttachmentPoint>();

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static void ApplyMakeSteerable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            // The OverlapSphere in U3DInteractionManager uses QueryTriggerInteraction.Collide
            // and only hits triggers — a solid collider is invisible to it.
            // If the creator wants a solid vehicle body for physics, they should add a
            // separate solid collider on a child GameObject.
            Collider existingCollider = selected.GetComponent<Collider>();
            if (existingCollider == null)
            {
                var cap = selected.AddComponent<CapsuleCollider>();
                cap.isTrigger = true;
            }
            else if (!existingCollider.isTrigger)
            {
                existingCollider.isTrigger = true;
                Debug.Log($"'{selected.name}': existing {existingCollider.GetType().Name} was solid — set to trigger so the interaction system can detect it. Add a solid collider on a child object if you need a physics body.");
            }

            // A steerable's identity travels in the avatar packet as a scene object ID, so every
            // peer can rebuild the costume the driver is riding.
            EnsureNetEntity(selected);

            if (selected.GetComponent<U3DSteerable>() == null)
                selected.AddComponent<U3DSteerable>();

            EditorUtility.SetDirty(selected);
        }

        /// <summary>
        /// Installs an attachment source on a scene object: a trigger collider the interaction system
        /// can detect (its OverlapSphere uses QueryTriggerInteraction.Collide and ignores solid
        /// colliders), plus a NetEntity so every client can resolve this source by a stable id
        /// when rebuilding the worn accessory. The accessory itself is a separate prefab the creator
        /// assigns in the Inspector; markers are placed with Add Attachment Point. Guards against being
        /// run on the accessory prefab instead of a scene object.
        /// </summary>
        private static void ApplyMakeAttachment()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            // The source belongs on a scene object, not on the accessory prefab. Catch a creator who
            // selected the prefab (in the Project or open in a Prefab Stage) and redirect.
            bool isPrefabContext =
                PrefabUtility.IsPartOfPrefabAsset(selected)
                || UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;

            if (isPrefabContext)
            {
                EditorUtility.DisplayDialog(
                    "Make Attachment",
                    "This looks like an accessory prefab, not a scene object.\n\n" +
                    "The attachment source goes on an object in your scene — the visual indicator players walk up to. You don't run Make Attachment on the accessory prefab itself.\n\n" +
                    "To set up the accessory: select your scene object, click Make Attachment, assign your accessory prefab, then use Add Attachment Point.",
                    "OK");
                return;
            }

            if (selected.GetComponent<U3DAttachmentSource>() != null)
            {
                EditorUtility.DisplayDialog(
                    "Make Attachment",
                    $"'{selected.name}' is already an attachment source.\n\n" +
                    "Assign your accessory prefab to its 'Accessory Prefab' field in the Inspector, then use Add Attachment Point to mark where each piece sits on the avatar.",
                    "OK");
                return;
            }

            Collider existingCollider = selected.GetComponent<Collider>();
            if (existingCollider == null)
            {
                var box = selected.AddComponent<BoxCollider>();
                box.isTrigger = true;
            }
            else if (!existingCollider.isTrigger)
            {
                existingCollider.isTrigger = true;
                Debug.Log($"'{selected.name}': existing {existingCollider.GetType().Name} was solid — set to trigger so the interaction system can detect it. Add a solid collider on a child object if you need a physics body.");
            }

            EnsureNetEntity(selected);

            selected.AddComponent<U3DAttachmentSource>();
            EditorUtility.SetDirty(selected);

            EditorUtility.DisplayDialog(
                "Make Attachment",
                "Added an attachment source to this object.\n\n" +
                "Next: in the Inspector, assign your accessory prefab to the 'Accessory Prefab' field. Then use Add Attachment Point — select this source for a single accessory worn as one piece, or open the prefab and select each piece for a set.",
                "OK");
        }

        private static void ApplyBlendShapeControl()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            SkinnedMeshRenderer smr = selected.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null)
            {
                EditorUtility.DisplayDialog(
                    "Add Blend Shape Control",
                    "This object has no Skinned Mesh Renderer on it or its children.\n\n" +
                    "Blend shapes live on a skinned mesh — the kind Unity creates when you import a model that has shape keys (Blender) or blend shapes (Maya, 3ds Max). A plain mesh can't have them.\n\n" +
                    "Select the model (or the object holding its skinned mesh), then click Add Blend Shape Control.",
                    "OK");
                return;
            }

            if (smr.sharedMesh != null && smr.sharedMesh.blendShapeCount == 0)
            {
                EditorUtility.DisplayDialog(
                    "Add Blend Shape Control",
                    $"'{smr.name}' is a skinned mesh, but its model has no blend shapes.\n\n" +
                    "Add shape keys to the model in your 3D tool (in Blender, the Shape Keys list) and re-export, then click Add Blend Shape Control.",
                    "OK");
                return;
            }

            if (selected.GetComponent<U3DBlendShape>() == null)
                selected.AddComponent<U3DBlendShape>();

            EditorUtility.SetDirty(selected);
        }

        private static void ApplyMakeRideable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            MakeRideableSetup(selected);
        }

        private static void MakeRideableSetup(GameObject selected)
        {
            Undo.RecordObject(selected, "Make Rideable");

            if (selected.GetComponent<U3DRideableController>() == null)
                selected.AddComponent<U3DRideableController>();

            // A rideable's identity travels in the avatar packet as a scene object ID, so a rider
            // standing on it is placed relative to it on every peer. The platform's own motion is
            // not yet shared — each peer runs the route from its own start moment.
            EnsureNetEntity(selected);

            // Idempotent trigger child — find by name if it already exists, otherwise create.
            Transform existingTrigger = selected.transform.Find("RideableTrigger");
            if (existingTrigger == null)
            {
                GameObject triggerZoneGO = new GameObject("RideableTrigger");
                Undo.RegisterCreatedObjectUndo(triggerZoneGO, "Make Rideable");
                triggerZoneGO.transform.SetParent(selected.transform, false);
                triggerZoneGO.transform.localPosition = Vector3.zero;

                var triggerCollider = triggerZoneGO.AddComponent<BoxCollider>();
                triggerCollider.isTrigger = true;
                triggerCollider.center = new Vector3(0f, 1f, 0f);
                triggerCollider.size = new Vector3(1f, 3f, 1f);

                triggerZoneGO.AddComponent<U3DRideableTrigger>();
            }

            // Idempotent first waypoint — only create if no Waypoint_0 already exists in
            // the scene. Avoids duplicating the creator's existing waypoints when they
            // re-run the tool to upgrade an existing rideable.
            GameObject existingWaypoint = GameObject.Find("Waypoint_0");
            if (existingWaypoint == null)
            {
                GameObject waypointGO = new GameObject("Waypoint_0");
                Undo.RegisterCreatedObjectUndo(waypointGO, "Make Rideable");
                waypointGO.transform.position = selected.transform.position;
            }

            EditorUtility.SetDirty(selected);
            Selection.activeGameObject = selected;
        }

        // ========== SHARED HELPERS ==========

        /// <summary>
        /// Gives an object a NetEntity if it has none. Presence of the component is what makes an
        /// object take part in the room, per the identity spec; the ID itself is assigned by the
        /// editor's own pass when the component validates, so nothing is set here.
        /// Components that declare NetEntity through [RequireComponent] get it automatically and
        /// do not call this.
        /// </summary>
        internal static void EnsureNetEntity(GameObject target)
        {
            if (target == null) return;
            if (target.GetComponent<NetEntity>() == null)
                Undo.AddComponent<NetEntity>(target);
        }

        private static void EnsureClimbableLayerExists()
        {
            int layer = LayerMask.NameToLayer(U3DClimbable.CLIMBABLE_LAYER_NAME);
            if (layer == -1)
            {
                Debug.LogWarning(
                    $"Layer '{U3DClimbable.CLIMBABLE_LAYER_NAME}' not found in project settings. " +
                    $"U3DClimbable uses layer {U3DClimbable.CLIMBABLE_LAYER} ('{U3DClimbable.CLIMBABLE_LAYER_NAME}') for organization. " +
                    $"Add this layer in Edit > Project Settings > Tags and Layers."
                );
            }
        }

        private static void SetLayerRecursive(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}