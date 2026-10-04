using UnityEngine;
using UnityEditor;
using U3D.Net;

namespace U3D.Editor
{
    public static class U3DInventoryTools
    {
        /// <summary>
        /// Adds a new Inventory GameObject to the scene at the scene view pivot.
        /// Ignores current selection (matches U3DScorableTools.AddScorable's pattern).
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

            GameObject inventoryObj = new GameObject("Inventory");
            inventoryObj.AddComponent<U3DInventory>();

            if (SceneView.lastActiveSceneView != null)
                inventoryObj.transform.position = SceneView.lastActiveSceneView.pivot;

            Selection.activeGameObject = inventoryObj;
            EditorGUIUtility.PingObject(inventoryObj);
            EditorUtility.SetDirty(inventoryObj);
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
        /// adding the component brings one along and the ID assigner numbers it. Also
        /// pair-adds a U3DInventory if the scene has none, since a collectable does nothing
        /// without one, and restores the original selection afterwards so the creator stays
        /// focused on the object they just set up.
        /// </summary>
        private static void EnsureCollectable(GameObject selected, U3DCollectable.CollectionMethod method)
        {
            U3DCollectable collectable = selected.GetComponent<U3DCollectable>();
            if (collectable == null)
                collectable = selected.AddComponent<U3DCollectable>();

            collectable.SetCollectionMethod(method);

            if (!collectable.PrefabToCollect.IsValid)
                FillPrefabToCollect(selected, collectable);

            if (UnityEngine.Object.FindAnyObjectByType<U3DInventory>() == null)
            {
                AddInventory();
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
    }
}