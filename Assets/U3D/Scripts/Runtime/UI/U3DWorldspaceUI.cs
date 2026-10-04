using System.Collections;
using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Worldspace UI component with proximity fade and optional camera-facing billboard behavior.
    /// Attach to a World Space Canvas or any parent transform containing UI elements.
    /// Proximity fade is measured from the local player's body position, not the camera,
    /// so third-person camera distance doesn't affect visibility.
    ///
    /// When an interactable calls BeginFollowing(target), this UI tracks the target's
    /// world position with a captured Y offset, but does NOT reparent to it and does
    /// NOT inherit its rotation or scale. The label remains a free-standing world-space
    /// object that just happens to chase another object's position.
    ///
    /// Labels remain hidden during the bootstrap window and begin their fade only after
    /// U3DLoadingCamera fires OnHandoffComplete, so the fade doesn't coincide with the
    /// camera swap. A safety timeout opens the gate anyway if no LoadingCamera exists
    /// or if handoff never completes.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    public class U3DWorldspaceUI : MonoBehaviour
    {
        [Header("Billboard Settings")]
        [Tooltip("Face the camera. Disable to keep the canvas at a fixed rotation.")]
        public bool faceCamera = true;

        [Tooltip("Lock Y-axis rotation (prevents tilting when camera looks up/down)")]
        public bool lockYAxis = true;

        [Header("Proximity Settings")]
        [Tooltip("Distance at which UI becomes fully hidden")]
        [Min(0.1f)]
        public float hideDistance = 10f;

        [Tooltip("Distance at which UI becomes fully visible (should be less than hideDistance)")]
        [Min(0.1f)]
        public float showDistance = 2f;

        [Header("Animation Settings")]
        [Tooltip("Speed of fade animation")]
        [Min(0.1f)]
        public float fadeSpeed = 5f;

        // Safety: if no LoadingCamera exists or handoff never completes, open the fade gate
        // after this many seconds so labels don't stay invisible forever.
        private const float HandoffFallbackTimeout = 5f;

        // Throttled search for Camera.main while it doesn't exist yet (bootstrap window).
        private const float CameraSearchInterval = 0.25f;

        private CanvasGroup canvasGroup;
        private Camera targetCamera;
        private Transform _localPlayerTransform;
        private float targetAlpha;
        private bool _fadeGateOpen;
        private float _nextCameraSearchTime;

        // Follow-target state. Set by an interactable's BeginFollowing call.
        // When _followTarget is non-null, LateUpdate overrides this UI's world position
        // to (target.x, target.y + _followYOffset, target.z), tracking the target
        // horizontally and at a fixed world-space height above it. The label is NOT
        // reparented to the target — it stays where the creator authored it in the
        // scene hierarchy, and never inherits the target's rotation or scale.
        //
        // A scene-placed worldspace UI that no interactable has called BeginFollowing
        // on is left entirely alone — its position and hierarchy are untouched.
        private Transform _followTarget;
        private float _followYOffset;

        /// <summary>
        /// Called by an interactable to begin tracking its world position. Captures
        /// the current world-space Y delta between this label and the target, so
        /// LateUpdate can preserve that offset as the target moves. Calling this
        /// multiple times is safe — the most recent target wins and the offset is
        /// recaptured each call.
        /// </summary>
        public void BeginFollowing(Transform target)
        {
            if (target == null) return;
            _followTarget = target;
            _followYOffset = transform.position.y - target.position.y;
        }

        void Awake()
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null)
                canvasGroup = gameObject.AddComponent<CanvasGroup>();

            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            // If a LoadingCamera already completed its handoff before we awoke
            // (e.g. this component was spawned at runtime after the bootstrap window),
            // open the gate immediately. Otherwise, subscribe to the event.
            if (U3DLoadingCamera.HandoffComplete)
            {
                _fadeGateOpen = true;
            }
            else
            {
                U3DLoadingCamera.OnHandoffComplete += OnHandoffComplete;
                StartCoroutine(HandoffFallback());
            }
        }

        void OnDestroy()
        {
            U3DLoadingCamera.OnHandoffComplete -= OnHandoffComplete;
        }

        void Start()
        {
            FindTargetCamera();
        }

        void LateUpdate()
        {
            if (targetCamera == null)
            {
                if (Time.unscaledTime >= _nextCameraSearchTime)
                {
                    FindTargetCamera();
                    _nextCameraSearchTime = Time.unscaledTime + CameraSearchInterval;
                }
                if (targetCamera == null) return;
            }

            if (_localPlayerTransform == null)
                FindLocalPlayer();

            if (_followTarget != null)
                UpdateFollowPosition();

            if (faceCamera)
                UpdateBillboardRotation();

            UpdateProximityFade();
        }

        private void OnHandoffComplete()
        {
            _fadeGateOpen = true;
        }

        private IEnumerator HandoffFallback()
        {
            yield return new WaitForSeconds(HandoffFallbackTimeout);

            if (!_fadeGateOpen)
            {
                _fadeGateOpen = true;
            }
        }

        void FindTargetCamera()
        {
            targetCamera = Camera.main;
        }

        void FindLocalPlayer()
        {
            var localPlayer = U3DPlayerController.FindLocalPlayer();
            if (localPlayer != null)
                _localPlayerTransform = localPlayer.transform;
        }

        /// <summary>
        /// Override world position to track the follow target horizontally with the
        /// captured Y offset preserved. Rotation and scale are left untouched —
        /// rotation gets handled by the billboard code below, and scale stays at
        /// whatever the creator authored.
        /// </summary>
        void UpdateFollowPosition()
        {
            Vector3 targetPos = _followTarget.position;
            transform.position = new Vector3(targetPos.x, targetPos.y + _followYOffset, targetPos.z);
        }

        void UpdateBillboardRotation()
        {
            Vector3 directionToCamera = targetCamera.transform.position - transform.position;

            if (lockYAxis)
                directionToCamera.y = 0f;

            if (directionToCamera.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(-directionToCamera);
        }

        void UpdateProximityFade()
        {
            Transform distanceSource = _localPlayerTransform != null
                ? _localPlayerTransform
                : targetCamera.transform;

            float distance = Vector3.Distance(distanceSource.position, transform.position);

            if (distance >= hideDistance)
            {
                targetAlpha = 0f;
            }
            else if (distance <= showDistance)
            {
                targetAlpha = 1f;
            }
            else
            {
                float t = (distance - showDistance) / (hideDistance - showDistance);
                targetAlpha = 1f - t;
            }

            if (_fadeGateOpen)
            {
                canvasGroup.alpha = Mathf.MoveTowards(canvasGroup.alpha, targetAlpha, fadeSpeed * Time.deltaTime);
            }

            canvasGroup.interactable = canvasGroup.alpha > 0.1f;
            canvasGroup.blocksRaycasts = canvasGroup.alpha > 0.1f;
        }

        void OnValidate()
        {
            if (showDistance >= hideDistance)
                showDistance = hideDistance - 0.5f;

            if (showDistance < 0.1f)
                showDistance = 0.1f;
        }
    }
}