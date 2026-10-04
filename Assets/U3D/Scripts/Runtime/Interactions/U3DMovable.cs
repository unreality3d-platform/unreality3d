using System.Collections;
using U3D.Net;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Lets a player push, pull or rotate an object along the ground.
    ///
    /// All three acts share a single drive: the mover's machine simulates the object
    /// against real geometry and publishes where it ended up, and every other machine
    /// follows that. When the drive ends the object is handed to the world with whatever
    /// velocity it had, so it rolls on and settles in the same place everywhere.
    ///
    /// Direction follows the player's actual movement. Walking forward pushes; walking
    /// backward pulls. Camera yaw in place rotates. Speed is the player's own movement
    /// speed, so a sprinting player shoves harder. Move Resistance sets the Rigidbody's
    /// mass, which determines how other objects react on collision.
    ///
    /// Push requires the player to be touching the object. Pull requires only that the
    /// player stay within range — touching is not needed, because the player walks away
    /// from the object to drag it. Rotate uses a ratcheted swipe: turning the camera past
    /// a fixed threshold fires one discrete rotation step, and returning to center resets
    /// the trigger for the next step.
    ///
    /// OnMoveStart and OnMoveEnd fire on every machine — wire sounds and effects there.
    /// OnSleep and OnImpact fire on every machine from that machine's own physics.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetRigidbody))]
    public class U3DMovable : NetComponent, IU3DInteractable
    {
        [Header("Movement")]
        [Tooltip("Allow pushing the object by walking forward into it.")]
        [SerializeField] private bool canPush = true;

        [Tooltip("Allow pulling the object by walking backward while engaged.")]
        [SerializeField] private bool canPull = true;

        [Tooltip("Higher values make this object harder to move. Adjusts this object's Rigidbody mass.")]
        [SerializeField] private float moveResistance = 5f;

        [Header("Rotation")]
        [Tooltip("Allow rotating the object by turning the camera while engaged.")]
        [SerializeField] private bool canRotate = false;

        [Tooltip("Local-space axis the object rotates around. (0,1,0) spins it on the floor like a turntable; (1,0,0) tips it forward and back.")]
        [SerializeField] private Vector3 rotationAxis = Vector3.up;

        [Tooltip("Degrees the object turns per swipe. One swipe is a camera turn past the internal threshold followed by a return to center.")]
        [SerializeField] private float anglePerSwipe = 45f;

        [Header("Interaction Settings")]
        [Tooltip("Maximum distance from the object before the drive auto-disengages.")]
        [SerializeField] private float maxMoveDistance = 5f;

        [Tooltip("When enabled, this object is permanently destroyed when it falls out of world bounds instead of respawning. Requires a U3DDestroyable component on this object.")]
        [SerializeField] private bool destroyOnOutOfBounds = false;

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this object. Edit the text on that object directly. At runtime the label tracks this object's position so it travels with it.")]
        public U3DWorldspaceUI labelUI;

        [Header("Events")]
        [Tooltip("Fired on EVERY player's machine when anyone starts moving this object.")]
        public UnityEvent OnMoveStart;

        [Tooltip("Fired on EVERY player's machine when movement of this object ends.")]
        public UnityEvent OnMoveEnd;

        [Tooltip("Fired on every machine when a moving object hits something.")]
        public UnityEvent OnImpact;

        [Tooltip("Fired on every machine when the object comes to rest after movement.")]
        public UnityEvent OnSleep;

        [Tooltip("Called when object is reset due to world bounds violation")]
        public UnityEvent OnWorldBoundsReset;

        [HideInInspector]
        [SerializeField] private float worldBoundsFloor = -50f;
        [HideInInspector]
        [SerializeField] private float worldBoundsRadius = 1000f;
        [HideInInspector]
        [SerializeField] private float boundsCheckInterval = 1f;

        /// <summary>
        /// Slack added to the contact test, on top of the player's own radius and the
        /// object's horizontal extent. The character controller stops the player a little
        /// short of a surface, so a test with no tolerance never passes.
        /// </summary>
        private const float ContactTolerance = 0.15f;

        /// <summary>
        /// Camera yaw change in degrees that fires one rotation step. Not exposed to
        /// creators — the feel comes from the step size (anglePerSwipe), not the trigger
        /// sensitivity.
        /// </summary>
        private const float SwipeThreshold = 45f;

        private enum SwipeState { Center, SwipedRight, SwipedLeft }

        private NetRigidbody _motion;
        private Rigidbody _rb;
        private Collider _bodyCollider;
        private U3DGrabbable _grabbable;

        private Camera _playerCamera;
        private Transform _playerTransform;
        private U3DPlayerController _playerController;
        private CharacterController _playerCharacter;

        private bool _isInRange;
        private bool _isDriveActive;

        private bool _animPushing;
        private bool _animPulling;

        private SwipeState _swipeState = SwipeState.Center;
        private float _swipeCenterYaw;
        private float _pendingRotation;

        private NetRigidbody.MotionState _lastMotionState = NetRigidbody.MotionState.Resting;
        private bool _labelHidden;
        private Coroutine _boundsCheckCoroutine;
        private bool _warnedAboutMissingDestroyable;

        private void Awake()
        {
            _motion = GetComponent<NetRigidbody>();
            _rb = GetComponent<Rigidbody>();
            _bodyCollider = GetComponent<Collider>();
            _grabbable = GetComponent<U3DGrabbable>();
        }

        private void Start()
        {
            FindPlayerComponents();
            ApplyResistanceToMass();
            LinkLabelUI();
            StartBoundsMonitoring();
        }

        protected override void OnNetSpawn()
        {
            _lastMotionState = _motion.State;

            _motion.DriveBegan += HandleDriveBegan;
            _motion.DriveEnded += HandleDriveEnded;
            _motion.Resetting += HandleMotionResetting;
        }

        protected override void OnNetDespawn()
        {
            _motion.DriveBegan -= HandleDriveBegan;
            _motion.DriveEnded -= HandleDriveEnded;
            _motion.Resetting -= HandleMotionResetting;
        }

        /// <summary>
        /// Tell the assigned label to track this object's position from here on. The label
        /// is not reparented — it stays where the creator placed it in the hierarchy and
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
            UpdateRotateInput();

            if (_isDriveActive && !_isInRange) EndDrive();
        }

        /// <summary>
        /// The label is hidden while the object is moving and comes back when it has
        /// actually stopped, and coming to rest is what OnSleep means. Both are derived
        /// from the object's own motion state rather than sent, so they happen on every
        /// machine without a message.
        /// </summary>
        private void TrackMotionState()
        {
            NetRigidbody.MotionState state = _motion.State;
            if (state == _lastMotionState) return;

            bool wasMoving = _lastMotionState == NetRigidbody.MotionState.Free
                          || _lastMotionState == NetRigidbody.MotionState.Driven
                          || _lastMotionState == NetRigidbody.MotionState.Settling;

            _lastMotionState = state;

            if (state != NetRigidbody.MotionState.Resting || !wasMoving) return;

            ShowLabel();
            OnSleep?.Invoke();
        }

        // Movement

        /// <summary>
        /// Moves the object each physics step while the drive is active, using the player's
        /// actual movement direction rather than a fixed camera axis, so the object follows
        /// wherever the player walks.
        ///
        /// Push requires the player to be touching the object — without that the crate
        /// reaches full speed in one step, outruns the player and can be shoved from across
        /// the room. Pull requires only that the player stay within range, because the
        /// player walks away from the object to drag it; the tight contact test that push
        /// uses breaks for pull, where the distance increases by design.
        ///
        /// Pending rotation from the swipe detector is applied here so all physics writes
        /// happen in the same phase.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_isDriveActive) return;
            if (_grabbable != null && _grabbable.IsGrabbed) return;
            if (_motion.State != NetRigidbody.MotionState.Driven) return;
            if (!_motion.HasAuthority) return;

            ApplyPendingRotation();
            ApplyMovement();
        }

        private void ApplyPendingRotation()
        {
            if (_pendingRotation == 0f || !canRotate) return;

            _rb.MoveRotation(
                _rb.rotation * Quaternion.AngleAxis(_pendingRotation, rotationAxis.normalized));
            _pendingRotation = 0f;

            SetAnimationState(true, false);
        }

        private void ApplyMovement()
        {
            if (_playerController == null || _playerCamera == null)
            {
                FindPlayerComponents();
                if (_playerController == null || _playerCamera == null) return;
            }

            if (!_playerController.IsMoving)
            {
                StopHorizontalMotion();
                SetAnimationState(true, false);
                return;
            }

            Vector3 playerVelocity = _playerCharacter != null
                ? _playerCharacter.velocity
                : Vector3.zero;

            Vector3 flatVelocity = new Vector3(playerVelocity.x, 0f, playerVelocity.z);
            if (flatVelocity.sqrMagnitude < 0.01f)
            {
                StopHorizontalMotion();
                SetAnimationState(true, false);
                return;
            }

            float playerSpeed = _playerController.CurrentSpeed;
            if (playerSpeed < 0.1f)
            {
                StopHorizontalMotion();
                SetAnimationState(true, false);
                return;
            }

            // Forward/backward is determined by projecting movement onto camera forward.
            Vector3 cameraForward = _playerCamera.transform.forward;
            cameraForward.y = 0f;
            if (cameraForward.sqrMagnitude < 0.01f)
            {
                StopHorizontalMotion();
                return;
            }
            cameraForward.Normalize();

            bool movingForward = Vector3.Dot(flatVelocity.normalized, cameraForward) > 0f;

            if (movingForward)
            {
                if (!canPush)
                {
                    StopHorizontalMotion();
                    SetAnimationState(true, false);
                    return;
                }

                if (!IsTouchingPlayer())
                {
                    StopHorizontalMotion();
                    SetAnimationState(true, false);
                    return;
                }

                SetAnimationState(true, false);
            }
            else
            {
                if (!canPull)
                {
                    StopHorizontalMotion();
                    SetAnimationState(true, false);
                    return;
                }

                // Pull needs only max-distance proximity, which Update enforces by ending
                // the drive when the player leaves range. No touch contact required.
                SetAnimationState(false, true);
            }

            Vector3 moveDirection = flatVelocity.normalized;
            Vector3 moveVelocity = moveDirection * playerSpeed;

            _rb.linearVelocity = new Vector3(moveVelocity.x, _rb.linearVelocity.y, moveVelocity.z);
        }

        /// <summary>
        /// Whether the player is close enough to the object to be pushing it rather than
        /// walking near it. Used for push only — pull uses the broader range check instead.
        ///
        /// Measured the way NetRigidbody's player clearance measures the same question:
        /// the player's own radius plus the object's horizontal extent plus a little
        /// tolerance, horizontally only, so standing on the crate is not touching it. A
        /// guessed distance cannot work — the character controller stops the player at a
        /// distance the controller decides, and a test tighter than that never passes.
        /// </summary>
        private bool IsTouchingPlayer()
        {
            if (_playerTransform == null || _bodyCollider == null) return false;

            float playerRadius = _playerCharacter != null ? _playerCharacter.radius : 0.5f;
            float threshold = playerRadius + ComputeHorizontalExtent() + ContactTolerance;

            Vector3 playerPosition = _playerTransform.position;
            Vector3 objectPosition = transform.position;

            float dx = playerPosition.x - objectPosition.x;
            float dz = playerPosition.z - objectPosition.z;

            return (dx * dx) + (dz * dz) <= threshold * threshold;
        }

        /// <summary>
        /// Half the object's width at its widest horizontal axis, so a long crate is
        /// reachable from its end rather than only from its centre.
        /// </summary>
        private float ComputeHorizontalExtent()
        {
            if (_bodyCollider == null) return 0f;

            Vector3 extents = _bodyCollider.bounds.extents;
            return Mathf.Max(extents.x, extents.z);
        }

        /// <summary>
        /// Stops the object where it is without leaving the driven state, so a player who
        /// pauses keeps hold of it. Vertical motion is untouched, so a crate pushed off a
        /// ledge still falls.
        /// </summary>
        private void StopHorizontalMotion()
        {
            if (_rb == null || _rb.isKinematic) return;

            Vector3 velocity = _rb.linearVelocity;
            if (velocity.x == 0f && velocity.z == 0f) return;

            _rb.linearVelocity = new Vector3(0f, velocity.y, 0f);
        }

        // Rotation input

        /// <summary>
        /// Tracks camera yaw for the ratcheted swipe input. A single swipe is: camera turns
        /// past the threshold in one direction (fires one rotation step), then returns to
        /// center (resets for the next step). This prevents continuous spinning from a
        /// sustained camera turn — the player has to move the camera back and forth.
        ///
        /// Runs in Update because it reads input state, not physics. The actual rotation
        /// is queued as a pending value and applied in FixedUpdate so all physics writes
        /// happen in the same phase.
        /// </summary>
        private void UpdateRotateInput()
        {
            if (!canRotate || !_isDriveActive) return;
            if (_playerCamera == null) return;

            float currentYaw = _playerCamera.transform.eulerAngles.y;
            float delta = Mathf.DeltaAngle(_swipeCenterYaw, currentYaw);

            switch (_swipeState)
            {
                case SwipeState.Center:
                    if (delta >= SwipeThreshold)
                    {
                        _pendingRotation -= anglePerSwipe;
                        _swipeState = SwipeState.SwipedRight;
                    }
                    else if (delta <= -SwipeThreshold)
                    {
                        _pendingRotation += anglePerSwipe;
                        _swipeState = SwipeState.SwipedLeft;
                    }
                    break;

                case SwipeState.SwipedRight:
                    if (delta <= 0f)
                    {
                        _swipeState = SwipeState.Center;
                        _swipeCenterYaw = currentYaw;
                    }
                    break;

                case SwipeState.SwipedLeft:
                    if (delta >= 0f)
                    {
                        _swipeState = SwipeState.Center;
                        _swipeCenterYaw = currentYaw;
                    }
                    break;
            }
        }

        // Drive management

        /// <summary>
        /// Takes the object and begins driving it.
        ///
        /// Not refused when somebody else is already driving. A crate belongs to the world
        /// rather than to a hand, so it can be taken mid-drive the same way a grabbable can
        /// be taken from one — the claim arbitrates, and the previous driver's drive ends on
        /// their own machine when authority moves.
        /// </summary>
        private void StartDrive()
        {
            if (!CanStartDrive()) return;
            if (!Entity.RequestAuthority()) return;

            _isDriveActive = true;
            SetAnimationState(true, false);

            if (canRotate && _playerCamera != null)
                _swipeCenterYaw = _playerCamera.transform.eulerAngles.y;

            _swipeState = SwipeState.Center;
            _pendingRotation = 0f;

            _motion.Drive();
        }

        /// <summary>
        /// Ends the drive and hands the object to the world carrying whatever velocity it
        /// has, so it rolls on and settles rather than stopping dead. Every machine runs
        /// that tail itself from the release.
        ///
        /// A machine that cannot publish stops driving and changes nothing else. Resting
        /// the object here would freeze this copy while every other machine goes on moving
        /// it, with nothing on the wire to correct it.
        /// </summary>
        private void EndDrive()
        {
            if (!_isDriveActive) return;

            _isDriveActive = false;
            _pendingRotation = 0f;
            SetAnimationState(false, false);

            if (!IsLive || Entity == null || !_motion.HasAuthority) return;

            _motion.ReleaseToWorld(_rb.linearVelocity, _rb.angularVelocity);
        }

        /// <summary>
        /// Someone has started driving this object, on every machine including the
        /// driver's, so a creator's start event reaches everybody from one place.
        /// </summary>
        private void HandleDriveBegan(NetRigidbody motion, PeerId driver)
        {
            HideLabel();
            OnMoveStart?.Invoke();
        }

        /// <summary>
        /// The drive has ended, however it ended — the driver letting go, a release
        /// landing, or authority moving elsewhere. Derived from the motion state rather
        /// than sent, so it cannot disagree with what the object is actually doing.
        /// </summary>
        private void HandleDriveEnded(NetRigidbody motion)
        {
            if (_isDriveActive)
            {
                _isDriveActive = false;
                _pendingRotation = 0f;
                SetAnimationState(false, false);
            }

            OnMoveEnd?.Invoke();
        }

        private void SetAnimationState(bool pushing, bool pulling)
        {
            if (pushing == _animPushing && pulling == _animPulling) return;
            _animPushing = pushing;
            _animPulling = pulling;
            if (_playerController == null) return;
            _playerController.SetPushingState(pushing);
            _playerController.SetPullingState(pulling);
        }

        // Labels

        private void HideLabel()
        {
            if (labelUI == null || _labelHidden) return;
            labelUI.gameObject.SetActive(false);
            _labelHidden = true;
        }

        private void ShowLabel()
        {
            if (labelUI == null || !_labelHidden) return;
            labelUI.gameObject.SetActive(true);
            _labelHidden = false;
        }

        // Proximity

        private void UpdatePlayerProximity()
        {
            if (_playerTransform == null)
            {
                FindPlayerComponents();
                return;
            }

            float distanceToPlayer = Vector3.Distance(transform.position, _playerTransform.position);
            _isInRange = distanceToPlayer <= maxMoveDistance;
        }

        private void FindPlayerComponents()
        {
            U3DPlayerController controller = U3DPlayerController.FindLocalPlayer();
            if (controller != null)
            {
                _playerTransform = controller.transform;
                _playerController = controller;
                _playerCamera = controller.GetComponentInChildren<Camera>();
                _playerCharacter = controller.CharacterController;
            }
            else
            {
                _playerTransform = null;
                _playerController = null;
                _playerCamera = null;
                _playerCharacter = null;
            }

            if (_playerCamera == null) _playerCamera = Camera.main;
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
        // OnMoveStart and OnSleep. Deriving it from a settle is not possible — a settle
        // after a reset and a settle after a push are the same message. F30
        public void ResetToSpawn()
        {
            if (!_motion.ResetToAuthored()) return;

            OnWorldBoundsReset?.Invoke();
        }

        /// <summary>
        /// The object is being sent home. Let go of it first, so an active drive is not
        /// still writing velocity into a body that is about to be placed.
        /// </summary>
        private void HandleMotionResetting(NetRigidbody motion)
        {
            if (_isDriveActive) EndDrive();
        }

        /// <summary>
        /// Ends the drive and stops the object where it is.
        /// </summary>
        public void StopMoving()
        {
            if (_isDriveActive) EndDrive();
        }

        private void OnCollisionEnter(Collision collision)
        {
            NetRigidbody.MotionState state = _motion.State;
            if (state != NetRigidbody.MotionState.Free && state != NetRigidbody.MotionState.Driven) return;
            if (collision.relativeVelocity.magnitude <= 1.5f) return;

            OnImpact?.Invoke();
        }

        /// <summary>
        /// Apply Move Resistance value to Rigidbody mass. Called on Start and whenever the
        /// value changes in the Inspector.
        /// </summary>
        private void ApplyResistanceToMass()
        {
            if (_rb != null) _rb.mass = moveResistance;
        }

        // IU3DInteractable

        public void OnInteract()
        {
            if (_isDriveActive) EndDrive();
            else StartDrive();
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }

        public bool CanInteract()
        {
            if (_isDriveActive) return true;
            return CanStartDrive();
        }

        public string GetInteractionPrompt()
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return "Cannot move while grabbed";
            if (_isDriveActive) return "Release";
            return "Move";
        }

        private bool CanStartDrive()
        {
            if (_grabbable != null && _grabbable.IsGrabbed) return false;
            if (!_isInRange) return false;
            if (!IsLive || Entity == null || Session == null) return false;
            return Entity.CanTake(Session.LocalPeer);
        }

        // Public API

        public bool IsDriveActive => _isDriveActive;
        public bool IsInRange => _isInRange;

        private void OnDestroy()
        {
            if (_boundsCheckCoroutine != null) StopCoroutine(_boundsCheckCoroutine);

            if (_isDriveActive && _playerController != null)
            {
                _playerController.SetPushingState(false);
                _playerController.SetPullingState(false);
            }
        }

        private void OnValidate()
        {
            if (moveResistance <= 0f)
                Debug.LogWarning("U3DMovable: Move Resistance should be greater than 0");

            if (maxMoveDistance <= 0f)
                Debug.LogWarning("U3DMovable: Max move distance should be positive");

            if (canRotate && anglePerSwipe <= 0f)
                Debug.LogWarning("U3DMovable: Angle Per Swipe should be positive");

            if (_rb == null) _rb = GetComponent<Rigidbody>();
            ApplyResistanceToMass();
        }
    }
}
