using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using U3D.Net;
using U3D.UI;

namespace U3D.Editor
{
    public static class U3DInventoryTools
    {
        private const string PlayerMenuPrefabPath = "Assets/U3D/Prefabs/U3D Player Menu Canvas.prefab";
        private const string PlayerMenuInstanceName = "U3D Player Menu";

        /// <summary>
        /// Adds a new Inventory GameObject to the scene at the scene view pivot, then offers the
        /// player menu so players can see what they're carrying. Ignores current selection
        /// (matches U3DScorableTools.AddScorable's pattern).
        /// </summary>
        public static void AddInventory()
        {
            U3DInventory existing = Object.FindAnyObjectByType<U3DInventory>();
            if (existing != null)
            {
                Debug.Log("U3DInventory already exists in this scene. Selecting the existing one.");
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            GameObject inventoryObj = CreateInventory();

            if (FindPlayerMenu() == null)
            {
                bool add = EditorUtility.DisplayDialog(
                    "Add Inventory",
                    "Add the Player Menu too?\n\n" +
                    "It shows players what they're carrying, how many, and which key uses each item. " +
                    "Touch players use it to take items out, since they have no number keys.",
                    "Add Player Menu",
                    "Not Now");

                if (add)
                    CreatePlayerMenu();
            }

            Selection.activeGameObject = inventoryObj;
            EditorGUIUtility.PingObject(inventoryObj);
        }

        /// <summary>
        /// Adds the player menu bar to the scene, plus an Inventory if the scene has none,
        /// since the bar has nothing to show without one.
        /// </summary>
        public static void AddPlayerMenu()
        {
            U3DPlayerMenu existing = FindPlayerMenu();
            if (existing != null)
            {
                EditorUtility.DisplayDialog("Add Player Menu",
                    "This scene already has a Player Menu.\n\nFound: " + existing.gameObject.name,
                    "OK");
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            GameObject instance = CreatePlayerMenu();
            if (instance == null) return;

            if (Object.FindAnyObjectByType<U3DInventory>() == null)
                CreateInventory();

            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        private static GameObject CreateInventory()
        {
            GameObject inventoryObj = new GameObject("Inventory");
            inventoryObj.AddComponent<U3DInventory>();

            if (SceneView.lastActiveSceneView != null)
                inventoryObj.transform.position = SceneView.lastActiveSceneView.pivot;

            Undo.RegisterCreatedObjectUndo(inventoryObj, "Add Inventory");
            EditorUtility.SetDirty(inventoryObj);
            return inventoryObj;
        }

        private static U3DPlayerMenu FindPlayerMenu()
        {
            return Object.FindAnyObjectByType<U3DPlayerMenu>(FindObjectsInactive.Include);
        }

        /// <summary>
        /// Places the player menu prefab in the scene. Builds the prefab first if the project
        /// doesn't have it, which is a one-time step in the template source project.
        /// </summary>
        private static GameObject CreatePlayerMenu()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerMenuPrefabPath);
            if (prefab == null)
                prefab = BuildPlayerMenuPrefab();

            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Player Menu Not Found",
                    "Could not find or create the Player Menu prefab at:\n" + PlayerMenuPrefabPath +
                    "\n\nMake sure the U3D template folders have not been moved or renamed.",
                    "OK");
                return null;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = PlayerMenuInstanceName;
            Undo.RegisterCreatedObjectUndo(instance, "Add Player Menu");
            return instance;
        }

        /// <summary>
        /// On Interact collection: the player presses the Interact key while near the object.
        /// Needs any collider so the interaction system can find the object, never a trigger,
        /// so an existing collider is left exactly as authored — a solid collider keeps the
        /// object blocking the player, which is valid for this mode.
        /// </summary>
        public static void ApplyInteractCollectable()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            if (selected.GetComponent<Collider>() == null)
                selected.AddComponent<BoxCollider>();

            EnsureCollectable(selected, U3DCollectable.CollectionMethod.OnInteract);
        }

        /// <summary>
        /// On Enter collection: the player picks the object up by walking into it (pass-through
        /// pickups like coins and gems). Requires a trigger collider. Adds one when the object
        /// has none. If the object already has a solid collider, converting it to a trigger
        /// stops it blocking the player, so the creator is asked to confirm before that happens.
        /// </summary>
        public static void ApplyEnterCollectable()
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
                BoxCollider box = selected.AddComponent<BoxCollider>();
                box.isTrigger = true;
            }
            else if (!collider.isTrigger)
            {
                bool proceed = EditorUtility.DisplayDialog(
                    "Make On Enter Collectable",
                    $"'{selected.name}' has a solid collider that currently blocks the player.\n\n" +
                    "On Enter collection needs the player to pass through the object, so this collider " +
                    "will be changed to a trigger and will stop blocking. If this object should stay solid, " +
                    "cancel and use Make On Interact Collectable instead.\n\nProceed?",
                    "Proceed",
                    "Cancel"
                );

                if (!proceed)
                    return;

                // A MeshCollider must be convex before it can act as a trigger.
                if (collider is MeshCollider meshCollider)
                    meshCollider.convex = true;
                collider.isTrigger = true;
            }

            EnsureCollectable(selected, U3DCollectable.CollectionMethod.OnEnter);
        }

        /// <summary>
        /// Shared setup for both collectable tools. U3DCollectable requires a NetEntity, so
        /// adding the component brings one along and the ID assigner numbers it. Also adds a
        /// U3DInventory and the player menu if the scene lacks them, since a collectable does
        /// nothing without an inventory and players can't see what they collected without the
        /// menu, and restores the original selection afterwards so the creator stays focused on
        /// the object they just set up.
        /// </summary>
        private static void EnsureCollectable(GameObject selected, U3DCollectable.CollectionMethod method)
        {
            U3DCollectable collectable = selected.GetComponent<U3DCollectable>();
            if (collectable == null)
                collectable = selected.AddComponent<U3DCollectable>();

            collectable.SetCollectionMethod(method);

            if (!collectable.PrefabToCollect.IsValid)
                FillPrefabToCollect(selected, collectable);

            bool addedSceneObjects = false;

            if (Object.FindAnyObjectByType<U3DInventory>() == null)
            {
                CreateInventory();
                addedSceneObjects = true;
            }

            if (FindPlayerMenu() == null)
            {
                CreatePlayerMenu();
                addedSceneObjects = true;
            }

            if (addedSceneObjects)
            {
                Selection.activeGameObject = selected;
                EditorGUIUtility.PingObject(selected);
            }

            EditorUtility.SetDirty(selected);
        }

        /// <summary>
        /// Fills in what the collectable hands out, when the answer is unambiguous: the
        /// creator dragged a prefab into the scene and made that instance collectable, so
        /// copies of its own source prefab are what they almost certainly want. Only fills
        /// when that prefab can actually be spawned on every machine. Anything else is a
        /// guess, so it stays empty and the creator is told what to assign.
        /// </summary>
        private static void FillPrefabToCollect(GameObject selected, U3DCollectable collectable)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(selected);

            if (source != null && source.GetComponent<NetEntity>() != null)
            {
                collectable.PrefabToCollect = NetPrefab.EditorCreate(source);
                EditorUtility.SetDirty(collectable);
                Debug.Log($"'{selected.name}' will hand out copies of the '{source.name}' prefab. Change Prefab To Collect in the Inspector to hand out something else instead.", selected);
                return;
            }

            EditorUtility.DisplayDialog(
                "Make Collectable",
                $"'{selected.name}' can now be collected.\n\n" +
                "One thing left: assign Prefab To Collect in the Inspector. That is what goes into the player's inventory, and it has to be a prefab from your Project rather than an object in your scene.\n\n" +
                "The prefab also needs a Net Entity component, so that when a player takes it back out of their inventory it appears for everyone.",
                "OK");
        }

        // ---------- Player menu prefab builder ----------

        /// <summary>
        /// Builds the player menu prefab from U3DUIStyle, saves it at PlayerMenuPrefabPath and
        /// returns the asset. Runs once in the template source project; creators receive the
        /// saved prefab and restyle it like any other prefab.
        /// </summary>
        private static GameObject BuildPlayerMenuPrefab()
        {
            var uiResources = new DefaultControls.Resources();
            var tmpResources = new TMP_DefaultControls.Resources();

            GameObject root = new GameObject("U3D Player Menu Canvas", typeof(RectTransform));
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -5;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            root.AddComponent<GraphicRaycaster>();
            root.AddComponent<U3DCanvasCssScaler>();
            U3DPlayerMenu menu = root.AddComponent<U3DPlayerMenu>();

            // Bar
            GameObject bar = DefaultControls.CreatePanel(uiResources);
            bar.name = "Bar";
            bar.transform.SetParent(root.transform, false);
            U3DUIStyle.ApplyPanelStyle(bar);
            RectTransform barRect = bar.GetComponent<RectTransform>();
            barRect.anchorMin = new Vector2(0.5f, 0f);
            barRect.anchorMax = new Vector2(0.5f, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.anchoredPosition = new Vector2(0f, 12f);

            VerticalLayoutGroup barLayout = bar.AddComponent<VerticalLayoutGroup>();
            barLayout.padding = new RectOffset(8, 8, 4, 8);
            barLayout.spacing = 4f;
            barLayout.childAlignment = TextAnchor.UpperCenter;
            barLayout.childControlWidth = true;
            barLayout.childControlHeight = true;
            barLayout.childForceExpandWidth = false;
            barLayout.childForceExpandHeight = false;

            ContentSizeFitter barFitter = bar.AddComponent<ContentSizeFitter>();
            barFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            barFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Header: title (drag or tap to collapse) and the mode arrows
            GameObject header = new GameObject("Header", typeof(RectTransform));
            header.transform.SetParent(bar.transform, false);
            HorizontalLayoutGroup headerLayout = header.AddComponent<HorizontalLayoutGroup>();
            headerLayout.childAlignment = TextAnchor.MiddleCenter;
            headerLayout.spacing = 6f;
            headerLayout.childControlWidth = true;
            headerLayout.childControlHeight = true;
            headerLayout.childForceExpandWidth = false;
            headerLayout.childForceExpandHeight = false;

            GameObject title = TMP_DefaultControls.CreateText(tmpResources);
            title.name = "Title";
            title.transform.SetParent(header.transform, false);
            TextMeshProUGUI titleTMP = title.GetComponent<TextMeshProUGUI>();
            titleTMP.text = "Inventory";
            U3DUIStyle.ApplyTitleStyle(titleTMP);
            titleTMP.raycastTarget = true;
            LayoutElement titleLayout = title.AddComponent<LayoutElement>();
            titleLayout.preferredWidth = 160f;
            titleLayout.preferredHeight = 24f;
            AddHandleTriggers(title, menu);

            GameObject arrows = new GameObject("Mode Arrows", typeof(RectTransform));
            arrows.transform.SetParent(header.transform, false);
            HorizontalLayoutGroup arrowsLayout = arrows.AddComponent<HorizontalLayoutGroup>();
            arrowsLayout.spacing = 2f;
            arrowsLayout.childControlWidth = true;
            arrowsLayout.childControlHeight = true;
            arrowsLayout.childForceExpandWidth = false;
            arrowsLayout.childForceExpandHeight = false;
            CreateArrowButton(arrows.transform, "Up", "^", tmpResources);
            CreateArrowButton(arrows.transform, "Down", "v", tmpResources);
            arrows.SetActive(false);

            // Slots
            GameObject slotsObj = new GameObject("Slots", typeof(RectTransform));
            slotsObj.transform.SetParent(bar.transform, false);
            GridLayoutGroup grid = slotsObj.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(64f, 64f);
            grid.spacing = new Vector2(4f, 4f);
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = U3DInventory.SLOT_COUNT;

            var slots = new U3DPlayerMenuSlot[U3DInventory.SLOT_COUNT];
            for (int i = 0; i < U3DInventory.SLOT_COUNT; i++)
                slots[i] = CreateSlot(slotsObj.transform, i, tmpResources);

            // Collapsed chip: the title alone in a panel, matching the Settings chip
            GameObject chipObj = DefaultControls.CreatePanel(uiResources);
            chipObj.name = "Collapsed Chip";
            chipObj.transform.SetParent(root.transform, false);
            U3DUIStyle.ApplyPanelStyle(chipObj);
            RectTransform chipRect = chipObj.GetComponent<RectTransform>();
            chipRect.anchorMin = new Vector2(0.5f, 0f);
            chipRect.anchorMax = new Vector2(0.5f, 0f);
            chipRect.pivot = new Vector2(0.5f, 0f);
            chipRect.anchoredPosition = new Vector2(0f, 12f);
            chipRect.sizeDelta = new Vector2(160f, 36f);
            AddHandleTriggers(chipObj, menu);

            GameObject chipText = TMP_DefaultControls.CreateText(tmpResources);
            chipText.name = "Label";
            chipText.transform.SetParent(chipObj.transform, false);
            RectTransform chipTextRect = chipText.GetComponent<RectTransform>();
            chipTextRect.anchorMin = Vector2.zero;
            chipTextRect.anchorMax = Vector2.one;
            chipTextRect.offsetMin = new Vector2(8f, 0f);
            chipTextRect.offsetMax = new Vector2(-8f, 0f);
            TextMeshProUGUI chipLabel = chipText.GetComponent<TextMeshProUGUI>();
            chipLabel.text = "Inventory";
            U3DUIStyle.ApplyTitleStyle(chipLabel);
            chipLabel.raycastTarget = false;
            chipObj.SetActive(false);

            // Wire the menu's references
            var menuSO = new SerializedObject(menu);
            menuSO.FindProperty("bar").objectReferenceValue = barRect;
            menuSO.FindProperty("collapsedChip").objectReferenceValue = chipRect;
            menuSO.FindProperty("collapsedLabel").objectReferenceValue = chipLabel;
            menuSO.FindProperty("slotGrid").objectReferenceValue = grid;
            menuSO.FindProperty("titleLabel").objectReferenceValue = titleTMP;
            menuSO.FindProperty("modeArrows").objectReferenceValue = arrows;
            SerializedProperty slotsProp = menuSO.FindProperty("slots");
            slotsProp.arraySize = slots.Length;
            for (int i = 0; i < slots.Length; i++)
                slotsProp.GetArrayElementAtIndex(i).objectReferenceValue = slots[i];
            menuSO.ApplyModifiedPropertiesWithoutUndo();

            SetLayerRecursively(root, LayerMask.NameToLayer("UI"));

            GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, PlayerMenuPrefabPath, out bool success);
            Object.DestroyImmediate(root);

            if (!success) return null;

            AssetDatabase.SaveAssets();
            Debug.Log("Built the Player Menu prefab at " + PlayerMenuPrefabPath, asset);
            return asset;
        }

        private static U3DPlayerMenuSlot CreateSlot(Transform parent, int index, TMP_DefaultControls.Resources tmpResources)
        {
            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = "Slot " + (index + 1);
            button.transform.SetParent(parent, false);
            U3DUIStyle.ApplyButtonStyle(button, string.Empty);

            TextMeshProUGUI nameTMP = button.GetComponentInChildren<TextMeshProUGUI>();
            nameTMP.gameObject.name = "Name";
            nameTMP.enableAutoSizing = true;
            nameTMP.fontSizeMin = 8f;
            nameTMP.fontSizeMax = U3DUIStyle.ButtonFontSize;
            nameTMP.overflowMode = TextOverflowModes.Ellipsis;
            nameTMP.raycastTarget = false;
            RectTransform nameRect = nameTMP.GetComponent<RectTransform>();
            nameRect.anchorMin = Vector2.zero;
            nameRect.anchorMax = Vector2.one;
            nameRect.offsetMin = new Vector2(3f, 12f);
            nameRect.offsetMax = new Vector2(-3f, -12f);

            TextMeshProUGUI keyTMP = CreateCornerText(button.transform, "Key", TextAlignmentOptions.TopLeft, tmpResources);
            TextMeshProUGUI countTMP = CreateCornerText(button.transform, "Count", TextAlignmentOptions.BottomRight, tmpResources);

            U3DPlayerMenuSlot slot = button.AddComponent<U3DPlayerMenuSlot>();
            var slotSO = new SerializedObject(slot);
            slotSO.FindProperty("button").objectReferenceValue = button.GetComponent<Button>();
            slotSO.FindProperty("nameLabel").objectReferenceValue = nameTMP;
            slotSO.FindProperty("countLabel").objectReferenceValue = countTMP;
            slotSO.FindProperty("keyLabel").objectReferenceValue = keyTMP;
            slotSO.ApplyModifiedPropertiesWithoutUndo();

            return slot;
        }

        private static TextMeshProUGUI CreateCornerText(Transform parent, string name, TextAlignmentOptions alignment, TMP_DefaultControls.Resources tmpResources)
        {
            GameObject textObj = TMP_DefaultControls.CreateText(tmpResources);
            textObj.name = name;
            textObj.transform.SetParent(parent, false);
            RectTransform rect = textObj.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(3f, 2f);
            rect.offsetMax = new Vector2(-3f, -2f);

            TextMeshProUGUI tmp = textObj.GetComponent<TextMeshProUGUI>();
            tmp.text = string.Empty;
            U3DUIStyle.ApplyStatusStyle(tmp);
            tmp.alignment = alignment;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static void CreateArrowButton(Transform parent, string name, string label, TMP_DefaultControls.Resources tmpResources)
        {
            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = name;
            button.transform.SetParent(parent, false);
            U3DUIStyle.ApplyButtonStyle(button, label);
            LayoutElement layout = button.AddComponent<LayoutElement>();
            layout.preferredWidth = 24f;
            layout.preferredHeight = 24f;
        }

        private static void AddHandleTriggers(GameObject target, U3DPlayerMenu menu)
        {
            EventTrigger trigger = target.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.BeginDrag, menu.OnHandleBeginDrag);
            AddTrigger(trigger, EventTriggerType.Drag, menu.OnHandleDrag);
            AddTrigger(trigger, EventTriggerType.EndDrag, menu.OnHandleEndDrag);
            AddTrigger(trigger, EventTriggerType.PointerClick, menu.OnHandleClick);
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, UnityAction<BaseEventData> action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            UnityEventTools.AddPersistentListener(entry.callback, action);
            trigger.triggers.Add(entry);
        }

        private static void SetLayerRecursively(GameObject target, int layer)
        {
            if (layer < 0) return;
            target.layer = layer;
            foreach (Transform child in target.transform)
                SetLayerRecursively(child.gameObject, layer);
        }
    }
}
