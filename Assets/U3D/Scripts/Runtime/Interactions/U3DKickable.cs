using System.Collections;
using U3D.Net;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Lets a player kick an object away from them.
    ///
    /// A kick is decided in one instant: the kicker takes ownership, works out a velocity
    /// from where they are looking, and hands the object to the world. Every machine runs
    /// the resulting flight itself and agrees on where it stopped, so a kick costs two
    /// messages regardless of how far the object travels.
    ///
    /// OnKicked and OnSleep fire on every machine � wire sounds, effects and animation
    /// there. OnKickedByMe fires only for the player who kicked it � wire score, UI and
    /// personal feedback there. OnImpact fires on every machine from that machine's own
    /// physics.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetEntity))]
    [RequireComponent(typeof(NetRigidbody))]
    public class U3DKickable : NetComponent, IU3DInteractable, IU3DInventoryActivatable
    {
        [Header("Kick Configuration")]
        [Tooltip("Base kick force multiplier")]
        [SerializeField] private float kickForce = 8f;

        [Tooltip("Additional upward force when kicking")]
        [SerializeField] private float upwardKickBoost = 1.5f;

        [Tooltip("Maximum kick velocity")]
        [SerializeField] private float maxKickVelocity = 15f;

        [Tooltip("Minimum velocity required to trigger kick events")]
        [SerializeField] private float minKickVelocity = 0.8f;

        [Header("Interaction Settings")]
        [Tooltip("Maximum distance to kick from")]
        [SerializeField] private float maxKickDistance = 1.5f;

        [Tooltip("When enabled, this object is permanently destroyed when it falls out of world bounds instead of respawning. Requires a U3DDestroyable component on this object.")]
        [SerializeField] private bool destroyOnOutOfBounds = false;

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this object. Edit the text on that object directly. At runtime the label tracks this object's position so it travels with it.")]
        public U3DWorldspaceUI labelUI;

        [Header("Events")]
        [Tooltip("Fired on EVERY player's machine when this object is kicked by anyone. Wire sounds, effects and animation here.")]
        public UnityEvent OnKicked;

        [Tooltip("Fired only for the player who kicked it. Wire score, UI and personal feedback here.")]
        public UnityEvent OnKickedByMe;

        [Tooltip("Fired on every machine when a moving kicked object hits something.")]
        public UnityEvent OnImpact;

        [Tooltip("Fired on every machine when the object comes to rest after a kick.")]
        public UnityEvent OnSleep;

        [Tooltip("Called when the local player enters kick range")]
        public UnityEvent OnEnterKickRange;

        [Tooltip("Called when the local player exits kick range")]
        public UnityEvent OnExitKickRange;

        [Tooltip("Called when a kick attempt by the local player fails")]
        public UnityEvent OnKickFailed;

        [Tooltip("Called when object is reset due to world bounds violation")]
        public UnityEvent OnWorldBoundsReset;

        [HideInInspector]
        [SerializeField] private float worldBoundsFloor = -50f;
        [HideInInspector]
        [SerializeField] private float worldBoundsRadius = 1000f;
        [HideInInspector]
        [SerializeField] private float boundsCheckInterval = 1f;

        private NetRigidbody _motion;
        private Rigidbody _rb;
        private U3DGrabbable _grabbable;

        private NetMessage _kicked;

        private Camera _playerCamera;
        private Transform _playerTransform;
        private bool _isInKickRange;

        private NetRigidbody.MotionState _lastMotionState = NetRigidbody.MotionState.Resting;
        private bool _labelHidden;
        private Coroutine _boundsCheckCoroutine;
        private bool _warnedAboutMissingDestroyable;

        private void Awake()
        {
            _motion = GetComponent<NetRigidbody>();
            _rb = GetComponent<Rigidbody>();
            _grabbable = GetComponent<U3DGrabbable>();
        }

        private void Start()
        {
            FindPlayerComponents();
            LinkLabelUI();
            StartBoundsMonitoring();
        }

        protected override void OnNetSpawn()
        {
            _kicked = RegisterMessage(NetKeys.KickableKicked, HandleKicked);

            _lastMotionState = _motion.State;
        }

        /// <summary>
        /// Tell the assigned label to track this object's position from here on. The label
        /// is not reparented � it stays where the creator placed it in the hierarchy and
        /// simply follows this transform.
        /// </summary>
        private void LinkLabelUI()
        {
            if (labelUI == null) return;
            labelUI.BeginFollowing(transform);
        }

        private void Update()
        {
            UpdatePlayerProximity();
            TrackMotionState();
        }

        /// <summary>
        /// The label is hidden while the object is flying and comes back when it has
        /// actually stopped, and coming to rest is what OnSleep means. Both are derived
        /// from the object's own motion state rather than sent, so they happen on every
        /// machine without a message.
        /// </summary>
        private void TrackMotionState()
        {
            NetRigidbody.MotionState state = _motion.State;
            if (state == _lastMotionState) return;

            bool wasMoving = _lastMotionState == NetRigidbody.MotionState.Free
                          || _lastMotionState == NetRigidbody.MotionState.Settling;

            _lastMotionState = state;

            if (state != NetRigidbody.MotionState.Resting || !wasMoving) return;

            if (labelUI != null && _labelHidden)
            {
                labelUI.gameObject.SetActive(true);
                _labelHidden = false;
            }

            OnSleep?.Invoke();
        }

        private void UpdatePlayerProximity()
        {
            if (_playerTransform == null)
            {
                FindPlayerComponents();
                return;
            }

            Vector3 playerGroundPosition = new Vector3(
                _playerTransform.position.x,
                transform.position.y,
                _playerTransform.position.z);

            float distanceToPlayer = Vector3.Distance(transform.position, playerGroundPosition);

            bool wasInRange = _isInKickRange;
            _isInKickRange = distanceToPlayer <= maxKickDistance;

            if (_isInKickRange && !wasInRange) OnEnterKickRange?.Invoke();
            else if (!_isInKickRange && wasInRange) OnExitKickRange?.Invoke();
        }

        private void FindPlayerComponents()
        {
            U3DPlayerController playerController = U3DPlayerController.FindLocalPlayer();
            if (playerController != null)
            {
                _playerTransform = playerController.transform;
                _playerCamera = playerController.GetComponentInChildren<Camera>();
            }
            else
            {
                _playerTransform = null;
                _playerCamera = null;
            }

            if (_playerCamera == null) _playerCamera = Camera.main;
        }

        // Kick

        public void Kick()
        {
            if (!CanAttemptKick())
            {
                OnKickFailed?.Invoke();
                return;
            }

            KickTowardCamera(kickForce);
        }

        /// <summary>
        /// Kicks without the proximity check. Used when U3DInventory summons an item
        /// straight to the player � the player asked for it, so the walk-up-to-it rule
        /// does not apply.
        /// </summary>
        public void OnInventoryActivate()
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return;
            KickTowardCamera(kickForce);
        }

        /// <summary>
        /// Kicks in the direction the local player is facing, with a custom force. Pass a
        /// value of zero or less to use the object's configured Kick Force.
        /// </summary>
        public void KickInCameraDirection(float customForce = -1f)
        {
            KickTowardCamera(customForce > 0f ? customForce : kickForce);
        }

        private void KickTowardCamera(float force)
        {
            if (_playerCamera == null || _playerTransform == null) FindPlayerComponents();

            if (_playerCamera == null)
            {
                OnKickFailed?.Invoke();
                return;
            }

            Vector3 flatForward = _playerCamera.transform.forward;
            flatForward.y = 0f;
            if (flatForward.sqrMagnitude < 0.0001f)
            {
                flatForward = _playerTransform != null ? _playerTransform.forward : transform.forward;
                flatForward.y = 0f;
            }
            flatForward.Normalize();

            Vector3 kickVelocity = flatForward * force + Vector3.up * upwardKickBoost;
            KickInDirection(kickVelocity.normalized, kickVelocity.magnitude);
        }

        /// <summary>
        /// Kicks in an explicit direction with an explicit force. Every other kick entry
        /// point ends up here.
        /// </summary>
        public void KickInDirection(Vector3 direction, float force)
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return;
            if (!IsLive || Session == null || Entity == null)
            {
                OnKickFailed?.Invoke();
                return;
            }

            Vector3 kickVelocity = direction.normalized * force;
            if (kickVelocity.magnitude > maxKickVelocity)
                kickVelocity = kickVelocity.normalized * maxKickVelocity;

            if (kickVelocity.magnitude < minKickVelocity)
            {
                OnKickFailed?.Invoke();
                return;
            }

            if (!Entity.RequestAuthority())
            {
                OnKickFailed?.Invoke();
                return;
            }

            TriggerPlayerAnimation("KickTrigger");

            _motion.ReleaseToWorld(kickVelocity, Vector3.zero);
            _kicked.SendToAll();
        }

        /// <summary>
        /// Runs on every machine, including the kicker's. The object is already moving by
        /// the time this lands, because the release travels ahead of it.
        /// </summary>
        private void HandleKicked(PeerId sender, object[] args)
        {
            if (Session != null && sender.IsValid && sender != Session.LocalPeer)
                TriggerPeerAnimation(sender, "KickTrigger");

            if (labelUI != null && !_labelHidden)
            {
                labelUI.gameObject.SetActive(false);
                _labelHidden = true;
            }

            OnKicked?.Invoke();

            if (Session != null && sender == Session.LocalPeer) OnKickedByMe?.Invoke();
        }

        /// <summary>
        /// Plays a one-shot animation on the local player's avatar, immediately, at the
        /// moment of the action.
        /// </summary>
        private void TriggerPlayerAnimation(string triggerName)
        {
            U3DPlayerController playerController = U3DPlayerController.FindLocalPlayer();
            if (playerController == null) return;

            U3DNetworkedAnimator networkedAnimator = playerController.GetComponent<U3DNetworkedAnimator>();
            if (networkedAnimator == null) return;

            networkedAnimator.TriggerAnimation(triggerName);
        }

        /// <summary>
        /// Plays a one-shot animation on another player's avatar. The channel is the
        /// object's own event message, which every machine already receives with the
        /// sender named, so nothing is added to the wire. F23
        /// </summary>
        private void TriggerPeerAnimation(PeerId peer, string triggerName)
        {
            if (Session == null) return;
            if (!Session.TryGetPlayerEntity(peer, out NetEntity playerEntity)) return;
            if (playerEntity == null) return;

            U3DNetworkedAnimator networkedAnimator = playerEntity.GetComponentInChildren<U3DNetworkedAnimator>(true);
            if (networkedAnimator == null) return;

            networkedAnimator.TriggerAnimation(triggerName);
        }

        private bool CanAttemptKick()
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return false;
            if (!_isInKickRange) return false;
            if (!IsLive || Entity == null || Session == null) return false;
            return Entity.CanTake(Session.LocalPeer);
        }

        // World bounds

        private void StartBoundsMonitoring()
        {
            if (_boundsCheckCoroutine == null)
                _boundsCheckCoroutine = StartCoroutine(MonitorWorldBounds());
        }

        private IEnumerator MonitorWorldBounds()
        {
            while (true)
            {
                yield return new WaitForSeconds(boundsCheckInterval);

                if (_grabbable != null && _grabbable.IsGrabbed) continue;
                if (!IsLive || Session == null || Entity == null) continue;

                // Only the peer entitled to speak for the object acts, so one reset happens
                // rather than one per machine.
                if (Entity.IsOwned)
                {
                    if (!Entity.HasAuthority) continue;
                }
                else if (!Session.IsReporter)
                {
                    continue;
                }

                bool needsReset = transform.position.y < worldBoundsFloor
                    || Vector3.Distance(Vector3.zero, transform.position) > worldBoundsRadius;

                if (!needsReset) continue;

                if (destroyOnOutOfBounds)
                {
                    U3DDestroyable destroyable = GetComponent<U3DDestroyable>();
                    if (destroyable == null)
                    {
                        if (!_warnedAboutMissingDestroyable)
                        {
                            _warnedAboutMissingDestroyable = true;
                            Debug.LogWarning($"'{name}' has Destroy On Out Of Bounds switched on but no U3D Destroyable component, so it fell out of the world and cannot be removed or returned. Add U3D Destroyable to it, or switch Destroy On Out Of Bounds off.", this);
                        }
                        continue;
                    }

                    destroyable.RequestDestroy();
                    continue;
                }

                ResetToSpawn();
            }
        }

        /// <summary>
        /// Puts the object back where it started, on every machine, and reports a world
        /// bounds recovery. The reset itself lives on the motion component, which every
        /// object that can be sent home carries; what belongs here is this component's own
        /// creator event.
        /// </summary>
        // PORT: OnWorldBoundsReset fires only on the peer that performed the reset, unlike
        // OnKicked and OnSleep. Deriving it from a settle is not possible � a settle after
        // a reset and a settle after a kick are the same message. F30
        public void ResetToSpawn()
        {
            if (!_motion.ResetToAuthored()) return;

            OnWorldBoundsReset?.Invoke();
        }
        private void OnCollisionEnter(Collision collision)
        {
            if (_motion.State != NetRigidbody.MotionState.Free) return;
            if (collision.relativeVelocity.magnitude <= 1.5f) return;

            OnImpact?.Invoke();
        }

        // IU3DInteractable

        public void OnInteract()
        {
            Kick();
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }

        public bool CanInteract() => CanAttemptKick();

        public string GetInteractionPrompt()
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return "Cannot kick while grabbed";
            return "Kick";
        }

        // Public API

        public bool IsInKickRange => _isInKickRange;

        private void OnDestroy()
        {
            if (_boundsCheckCoroutine != null) StopCoroutine(_boundsCheckCoroutine);
        }

        private void OnValidate()
        {
            if (kickForce <= 0f)
                Debug.LogWarning("U3DKickable: Kick force should be greater than 0");

            if (maxKickVelocity < kickForce)
                Debug.LogWarning("U3DKickable: Max kick velocity is less than kick force - kicks will be clamped");

            if (maxKickDistance <= 0f)
                Debug.LogWarning("U3DKickable: Max kick distance should be positive");
        }
    }
}