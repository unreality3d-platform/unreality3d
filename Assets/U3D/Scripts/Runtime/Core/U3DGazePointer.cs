using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace U3D
{
    /// <summary>
    /// Drives Unity's UGUI event system from a camera-forward raycast instead of a mouse cursor.
    /// Lets players interact with worldspace UI by looking at it and pressing Interact (R on desktop,
    /// right grip or right trigger in VR — the same Interact action as everything else in the U3D
    /// interaction system).
    ///
    /// Press Interact while the reticle is over a worldspace UI element and that element receives
    /// a complete pointer-down, pointer-up, pointer-click sequence — all fired in the single press
    /// event, with no state held between frames. That covers Buttons and Toggles (which respond to
    /// pointer-click) and Slider tracks (which respond to pointer-down by jumping the handle to the
    /// clicked point). It also fires hover enter/exit so creators get visual hover feedback.
    ///
    /// What it intentionally does NOT do: drag. There is no press/hold/release state machine and
    /// no per-frame drag dispatch. Drag-to-scrub and drag-to-scroll belong to the planned dedicated
    /// worldspace VR interaction pass. An earlier version included drag machinery; it never
    /// produced working drag, and its held state could strand across interrupted press cycles and
    /// stall the main thread.
    ///
    /// This component is fully local and frame-based. It reads the Interact action directly in
    /// Update. It is intentionally NOT wired through the networked input path — clicking a UI
    /// element is local input.
    /// </summary>
    public class U3DGazePointer : MonoBehaviour
    {
        [Header("Gaze Configuration")]
        [Tooltip("Maximum distance the gaze ray reaches.")]
        [SerializeField] private float gazeMaxDistance = 10f;

        [Tooltip("Layers the gaze ray considers. UI canvas hits use GraphicRaycaster, not Physics, so this only affects how non-UI physics colliders block the gaze from reaching distant UI.")]
        [SerializeField] private LayerMask blockingLayers = ~0;

        private Camera playerCamera;
        private U3DPlayerController playerController;
        private EventSystem eventSystem;
        private PointerEventData pointerData;

        // The Interact action, read directly each frame. Same action the rest of the U3D
        // interaction system uses — read from U3DPlayerInput, which owns the input asset.
        private InputAction interactAction;
        private bool interactWasPressed;

        // The UI element the gaze ray is currently hitting (if any). Tracked across frames so we
        // can fire pointer-exit when the gaze moves off it.
        private GameObject currentHover;

        // Cached canvas list, refreshed periodically. Worldspace canvases register themselves
        // by having an active GraphicRaycaster component in the scene.
        private List<GraphicRaycaster> cachedRaycasters = new List<GraphicRaycaster>();
        private float lastRaycasterCacheTime;
        private const float RAYCASTER_CACHE_INTERVAL = 1f;

        public void Initialize(Camera camera, U3DPlayerController controller)
        {
            playerCamera = camera;
            playerController = controller;
            eventSystem = EventSystem.current;

            // A single long-lived PointerEventData is reused so the EventSystem sees a consistent
            // "pointer". It carries no press/drag state between frames in this version — only the
            // current hover target and screen position get updated.
            pointerData = new PointerEventData(eventSystem)
            {
                pointerId = -10  // Distinct from mouse (-1) and touches (0+).
            };

            ResolveInteractAction();
            RefreshRaycasterCache();
        }

        /// <summary>
        /// Get the Interact action from the network manager, which owns the input asset and
        /// exposes the action via GetInteractAction(). The manager may not exist yet on the
        /// first frame, so Update retries until it resolves.
        /// </summary>
        /// <summary>
        /// Get the Interact action from U3DPlayerInput, which owns the input asset and exposes
        /// the action as a property. Player input may not exist yet on the first frame, so
        /// Update retries until it resolves.
        /// </summary>
        private void ResolveInteractAction()
        {
            var playerInput = U3D.Input.U3DPlayerInput.Instance;
            if (playerInput != null)
                interactAction = playerInput.InteractAction;
        }

        private void Update()
        {
            if (playerCamera == null || eventSystem == null) return;

            if (interactAction == null)
            {
                ResolveInteractAction();
                if (interactAction == null) return;
            }

            // Refresh the canvas list periodically. Cheaper than FindObjectsByType every frame,
            // still fast enough that new worldspace canvases (e.g. spawned dynamically) become
            // gaze-targetable within a second.
            if (Time.time - lastRaycasterCacheTime > RAYCASTER_CACHE_INTERVAL)
                RefreshRaycasterCache();

            UpdateGazeHover();

            // Press edge detection, done locally and frame-based. The Interact action includes a
            // Press interaction on its VR trigger bindings, so IsPressed() reads as a clean
            // digital state on every platform. Only the rising edge matters — the whole
            // interaction fires in one frame, so there is no release path to track.
            bool interactIsPressed = interactAction.IsPressed();

            if (interactIsPressed && !interactWasPressed)
                FireClick();

            interactWasPressed = interactIsPressed;
        }

        /// <summary>
        /// Send a complete pointer-down, pointer-up, pointer-click sequence to whatever UI element
        /// the gaze is currently over — all in this single frame, with nothing held afterward.
        ///
        /// The three events serve different controls: Buttons and Toggles act on pointer-click;
        /// Slider tracks act on pointer-down (jumping the handle to the clicked point). Sending the
        /// full sequence means one code path drives every common control. Because none of it is
        /// retained between frames, there is no state that can be stranded by an interrupted press.
        /// </summary>
        private void FireClick()
        {
            if (currentHover == null) return;

            // pressPosition / pointerPressRaycast describe where the press landed — Slider reads
            // these to work out where on its track the click fell.
            pointerData.pressPosition = pointerData.position;
            pointerData.pointerPressRaycast = pointerData.pointerCurrentRaycast;

            // pointer-down first. ExecuteHierarchy walks up from the hovered object to find a
            // handler, so hovering a child still drives the parent control. Slider's track jump
            // happens here.
            GameObject pressHandler = ExecuteEvents.ExecuteHierarchy(
                currentHover, pointerData, ExecuteEvents.pointerDownHandler);

            pointerData.pointerPress = pressHandler;
            pointerData.rawPointerPress = currentHover;

            // pointer-up immediately after — the press is instantaneous, there is no hold.
            if (pressHandler != null)
                ExecuteEvents.Execute(pressHandler, pointerData, ExecuteEvents.pointerUpHandler);

            // pointer-click last. Button.onClick and Toggle's flip happen here.
            ExecuteEvents.ExecuteHierarchy(currentHover, pointerData, ExecuteEvents.pointerClickHandler);

            // Clear the press references immediately. Nothing about this interaction survives the
            // frame — the next press starts clean.
            pointerData.pointerPress = null;
            pointerData.rawPointerPress = null;

            // Deselect whatever the click sequence selected. Unity's Button selects itself on
            // pointer-down by default, and the EventSystem's selection state is what the cursor
            // manager treats as "UI has focus" — which then unlocks the cursor and gates off
            // player input until the user clicks back into the game window. The gaze interaction
            // is fire-and-forget; we don't want lingering selection state.
            if (eventSystem.currentSelectedGameObject != null)
                eventSystem.SetSelectedGameObject(null);
        }

        private void RefreshRaycasterCache()
        {
            cachedRaycasters.Clear();
            GraphicRaycaster[] all = FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None);
            foreach (var caster in all)
            {
                if (caster.gameObject.activeInHierarchy && caster.enabled)
                {
                    Canvas canvas = caster.GetComponent<Canvas>();
                    if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
                        cachedRaycasters.Add(canvas != null ? caster : null);
                }
            }
            lastRaycasterCacheTime = Time.time;
        }

        private void UpdateGazeHover()
        {
            // Ray origin: GetVREyePosition() returns the authoritative eye position the same way
            // U3DPlayerController positions the camera in VR (avatar head bone + offset). In
            // non-VR it returns the camera's world position. Using it here keeps the gaze ray
            // anchored to the same point as the camera regardless of script execution order —
            // reading playerCamera.transform.position directly was a frame behind in VR, which
            // made the ray originate offset from the reticle.
            Vector3 rayOrigin = playerController != null
                ? playerController.GetVREyePosition()
                : playerCamera.transform.position;

            Ray gazeRay = new Ray(rayOrigin, playerCamera.transform.forward);

            GameObject newHover = FindClosestUIHit(gazeRay, out Vector3 worldHitPoint);

            if (newHover != null)
            {
                Vector2 screenPos = playerCamera.WorldToScreenPoint(worldHitPoint);
                pointerData.position = screenPos;
            }

            // Handle hover transitions: exit old, enter new. Stateless beyond the single
            // currentHover reference — there is no press or drag state to get stranded.
            if (newHover != currentHover)
            {
                if (currentHover != null)
                    ExecuteEvents.ExecuteHierarchy(currentHover, pointerData, ExecuteEvents.pointerExitHandler);

                pointerData.pointerEnter = newHover;

                if (newHover != null)
                    ExecuteEvents.ExecuteHierarchy(newHover, pointerData, ExecuteEvents.pointerEnterHandler);

                currentHover = newHover;
            }
        }

        /// <summary>
        /// Find the closest worldspace UI element the gaze ray hits. First-hit-wins, by distance
        /// along the gaze ray. Also checks for physics colliders along the ray; if a wall is
        /// closer than the closest UI element, the UI hit is rejected.
        /// </summary>
        private GameObject FindClosestUIHit(Ray ray, out Vector3 worldHitPoint)
        {
            worldHitPoint = Vector3.zero;
            GameObject closestHit = null;
            float closestDistance = gazeMaxDistance;

            if (Physics.Raycast(ray, out RaycastHit physicsHit, gazeMaxDistance, blockingLayers, QueryTriggerInteraction.Ignore))
            {
                closestDistance = physicsHit.distance;
            }

            foreach (var caster in cachedRaycasters)
            {
                if (caster == null) continue;

                Canvas canvas = caster.GetComponent<Canvas>();
                if (canvas == null) continue;

                RectTransform rect = canvas.GetComponent<RectTransform>();
                Plane plane = new Plane(rect.forward * -1f, rect.position);

                if (!plane.Raycast(ray, out float enter)) continue;
                if (enter < 0f || enter > closestDistance) continue;

                Vector3 hitPoint = ray.GetPoint(enter);
                Vector2 localPoint = rect.InverseTransformPoint(hitPoint);

                if (!rect.rect.Contains(localPoint)) continue;

                Camera eventCamera = canvas.worldCamera != null ? canvas.worldCamera : playerCamera;
                Vector2 screenPoint = eventCamera.WorldToScreenPoint(hitPoint);

                var tempData = new PointerEventData(eventSystem) { position = screenPoint };
                var results = new List<RaycastResult>();
                caster.Raycast(tempData, results);

                if (results.Count == 0) continue;

                closestHit = results[0].gameObject;
                closestDistance = enter;
                worldHitPoint = hitPoint;

                pointerData.pointerCurrentRaycast = results[0];
            }

            return closestHit;
        }
    }
}