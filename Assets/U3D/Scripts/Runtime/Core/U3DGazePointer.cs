using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace U3D
{
    /// <summary>
    /// Drives Unity's UGUI event system from rays instead of a mouse cursor, so players can press
    /// worldspace UI by pointing at it and pressing Interact.
    ///
    /// Two rays. The gaze ray runs from the eye along the camera's forward on every device. In VR a
    /// second ray runs from the avatar's right hand along the right controller's pointing direction,
    /// and when it hits UI it takes precedence over the gaze. In VR a small square marks where the
    /// winning ray meets the UI it is over.
    ///
    /// This component does not read input. U3DInteractionManager.OnPlayerInteract calls
    /// TryConsumeInteract first on every Interact press, from every device; when the hover is on
    /// something pressable, the press fires a complete pointer-down, pointer-up, pointer-click
    /// sequence there and the interaction stops, so one press never both clicks and grabs.
    ///
    /// No drag: nothing is held between frames, so nothing can be stranded by an interrupted press.
    /// The panels it drives are never selected and never take UI focus.
    /// </summary>
    public class U3DGazePointer : MonoBehaviour
    {
        [Header("Gaze Configuration")]
        [Tooltip("Maximum distance the gaze ray reaches.")]
        [SerializeField] private float gazeMaxDistance = 10f;

        [Tooltip("Maximum distance the right-hand ray reaches in VR.")]
        [SerializeField] private float handRayMaxDistance = 10f;

        [Tooltip("Layers that block a ray from reaching UI behind them. UI canvas hits use GraphicRaycaster, not Physics.")]
        [SerializeField] private LayerMask blockingLayers = ~0;

        [Header("VR Aim Marker")]
        [Tooltip("Size of the square marking where the ray meets UI, as the angle it covers from the eye, in degrees. Stays the same apparent size on near and far panels.")]
        [SerializeField] private float markerAngle = 1.5f;
        [SerializeField] private Color markerColor = new Color(0.55f, 0.55f, 0.55f, 0.9f);

        public static U3DGazePointer Instance { get; private set; }

        public Camera PlayerCamera => playerCamera;

        private Camera playerCamera;
        private U3DPlayerController playerController;
        private U3DAvatarManager avatarManager;
        private EventSystem eventSystem;
        private PointerEventData pointerData;

        private GameObject currentHover;
        private RectTransform marker;

        private readonly List<GraphicRaycaster> cachedRaycasters = new List<GraphicRaycaster>();
        private readonly List<RaycastResult> raycastResults = new List<RaycastResult>();
        private readonly RaycastHit[] physicsHits = new RaycastHit[16];
        private float lastRaycasterCacheTime = float.NegativeInfinity;
        private const float RAYCASTER_CACHE_INTERVAL = 1f;

        private struct UIHit
        {
            public GameObject target;
            public Canvas canvas;
            public Vector3 worldPoint;
            public RaycastResult raycast;
        }

        public void Initialize(Camera camera, U3DPlayerController controller)
        {
            playerCamera = camera;
            playerController = controller;
            avatarManager = controller != null ? controller.GetComponent<U3DAvatarManager>() : null;
            eventSystem = EventSystem.current;

            pointerData = new PointerEventData(eventSystem)
            {
                pointerId = -10
            };

            Instance = this;
            RefreshRaycasterCache();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (marker != null)
                Destroy(marker.gameObject);
        }

        /// <summary>
        /// Called by U3DInteractionManager before anything else on an Interact press. Returns true
        /// when the press landed on pressable UI and was used there.
        /// </summary>
        public static bool TryConsumeInteract()
        {
            return Instance != null && Instance.TryClickHovered();
        }

        /// <summary>
        /// Makes a canvas that just became worldspace pressable this frame rather than within a second.
        /// </summary>
        public static void RefreshWorldCanvases()
        {
            if (Instance != null)
                Instance.lastRaycasterCacheTime = float.NegativeInfinity;
        }

        private void Update()
        {
            if (playerCamera == null) return;

            if (eventSystem == null)
            {
                eventSystem = EventSystem.current;
                if (eventSystem == null) return;
                pointerData = new PointerEventData(eventSystem) { pointerId = -10 };
            }

            if (Time.time - lastRaycasterCacheTime > RAYCASTER_CACHE_INTERVAL)
                RefreshRaycasterCache();

            UpdateHover();
        }

        private bool InVR => playerController != null && playerController.IsInVRMode;

        private bool TryClickHovered()
        {
            if (currentHover == null || pointerData == null) return false;

            bool pressable =
                ExecuteEvents.GetEventHandler<IPointerClickHandler>(currentHover) != null ||
                ExecuteEvents.GetEventHandler<IPointerDownHandler>(currentHover) != null;

            if (!pressable) return false;

            FireClick();
            return true;
        }

        private void FireClick()
        {
            pointerData.pressPosition = pointerData.position;
            pointerData.pointerPressRaycast = pointerData.pointerCurrentRaycast;

            GameObject pressHandler = ExecuteEvents.ExecuteHierarchy(
                currentHover, pointerData, ExecuteEvents.pointerDownHandler);

            pointerData.pointerPress = pressHandler;
            pointerData.rawPointerPress = currentHover;

            if (pressHandler != null)
                ExecuteEvents.Execute(pressHandler, pointerData, ExecuteEvents.pointerUpHandler);

            ExecuteEvents.ExecuteHierarchy(currentHover, pointerData, ExecuteEvents.pointerClickHandler);

            pointerData.pointerPress = null;
            pointerData.rawPointerPress = null;

            if (eventSystem.currentSelectedGameObject != null)
                eventSystem.SetSelectedGameObject(null);
        }

        private void RefreshRaycasterCache()
        {
            cachedRaycasters.Clear();
            GraphicRaycaster[] all = FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None);
            foreach (var caster in all)
            {
                if (!caster.gameObject.activeInHierarchy || !caster.enabled) continue;
                Canvas canvas = caster.GetComponent<Canvas>();
                if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
                    cachedRaycasters.Add(caster);
            }
            lastRaycasterCacheTime = Time.time;
        }

        private void UpdateHover()
        {
            UIHit hit = default;
            bool found = false;
            bool inVR = InVR;

            if (inVR && avatarManager != null)
            {
                U3DAvatarIK ik = avatarManager.AvatarIK;
                if (ik != null && ik.TryGetRightHandRay(out Vector3 handOrigin, out Vector3 handDirection))
                {
                    Transform held = U3DGrabbable.CurrentlyGrabbed != null ? U3DGrabbable.CurrentlyGrabbed.transform : null;
                    found = TryFindUIHit(new Ray(handOrigin, handDirection), handRayMaxDistance, held, out hit);
                }
            }

            if (!found)
            {
                Vector3 gazeOrigin = playerController != null
                    ? playerController.GetVREyePosition()
                    : playerCamera.transform.position;
                found = TryFindUIHit(new Ray(gazeOrigin, playerCamera.transform.forward), gazeMaxDistance, null, out hit);
            }

            GameObject newHover = found ? hit.target : null;

            if (found)
            {
                pointerData.position = playerCamera.WorldToScreenPoint(hit.worldPoint);
                pointerData.pointerCurrentRaycast = hit.raycast;
            }
            else
            {
                pointerData.pointerCurrentRaycast = default;
            }

            UpdateMarker(inVR && found, hit);

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

        private void UpdateMarker(bool show, UIHit hit)
        {
            if (!show || hit.canvas == null)
            {
                if (marker != null && marker.gameObject.activeSelf)
                    marker.gameObject.SetActive(false);
                return;
            }

            if (marker == null)
            {
                var go = new GameObject("U3D Aim Marker", typeof(RectTransform), typeof(Image));
                var image = go.GetComponent<Image>();
                image.color = markerColor;
                image.raycastTarget = false;
                marker = (RectTransform)go.transform;
            }

            Transform canvasTransform = hit.canvas.transform;
            if (marker.parent != canvasTransform)
                marker.SetParent(canvasTransform, false);

            float distance = Vector3.Distance(playerCamera.transform.position, hit.worldPoint);
            float worldSize = distance * Mathf.Tan(markerAngle * Mathf.Deg2Rad);
            float canvasScale = Mathf.Abs(canvasTransform.lossyScale.x);
            float size = canvasScale > 1e-6f ? worldSize / canvasScale : 0f;

            marker.SetAsLastSibling();
            marker.gameObject.layer = canvasTransform.gameObject.layer;
            marker.sizeDelta = new Vector2(size, size);
            Vector3 local = canvasTransform.InverseTransformPoint(hit.worldPoint);
            marker.localPosition = new Vector3(local.x, local.y, 0f);
            marker.localRotation = Quaternion.identity;
            marker.localScale = Vector3.one;

            if (!marker.gameObject.activeSelf)
                marker.gameObject.SetActive(true);
        }

        private float BlockingDistance(Ray ray, float maxDistance, Transform ignore)
        {
            int count = Physics.RaycastNonAlloc(ray, physicsHits, maxDistance, blockingLayers, QueryTriggerInteraction.Ignore);
            float closest = maxDistance;
            for (int i = 0; i < count; i++)
            {
                if (ignore != null && physicsHits[i].collider.transform.IsChildOf(ignore)) continue;
                if (physicsHits[i].distance < closest)
                    closest = physicsHits[i].distance;
            }
            return closest;
        }

        private bool TryFindUIHit(Ray ray, float maxDistance, Transform ignore, out UIHit hit)
        {
            hit = default;
            bool found = false;
            float closestDistance = BlockingDistance(ray, maxDistance, ignore);

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
                var tempData = new PointerEventData(eventSystem) { position = eventCamera.WorldToScreenPoint(hitPoint) };
                raycastResults.Clear();
                caster.Raycast(tempData, raycastResults);
                if (raycastResults.Count == 0) continue;

                hit.target = raycastResults[0].gameObject;
                hit.canvas = canvas;
                hit.worldPoint = hitPoint;
                hit.raycast = raycastResults[0];
                closestDistance = enter;
                found = true;
            }

            return found;
        }
    }
}