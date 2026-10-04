using System.Collections;
using U3D.Net;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Which hand a held object goes to. Named by role rather than by bone name, so any
    /// humanoid rig resolves with no per-rig editing. Player Position means no hand
    /// attachment: the object sits at a fixed point in front of the player, which is what
    /// a non-humanoid avatar gets too.
    /// </summary>
    public enum U3DHandChoice
    {
        RightHand,
        LeftHand,
        PlayerPosition
    }

    /// <summary>
    /// Lets a player pick an object up and carry it in their hand, and optionally throw it.
    ///
    /// Holding is ownership. While somebody holds this object they own it, and the object
    /// is parented to their hand bone on every machine in the room, so it moves with the
    /// hand everywhere at once without any position traffic. Letting go publishes the
    /// object's starting conditions once; each machine runs the fall or the throw itself
    /// and agrees on the final resting place.
    ///
    /// A throw is decided in one instant: the thrower works out a velocity from where they
    /// are looking, and that velocity is published with the release. Every machine runs the
    /// flight itself and agrees on where the object stopped, so a throw costs two messages
    /// no matter how far it travels.
    ///
    /// OnGrabbed and OnReleased fire on every machine — wire sounds, effects and animation
    /// there. OnGrabbedByMe and OnReleasedByMe fire only for the player who did it — wire
    /// score, UI and personal feedback there. OnThrown and OnSleep fire on every machine.
    /// OnThrownByMe fires only for the player who threw it. OnImpact fires on every
    /// machine from that machine's own physics.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetEntity))]
    [RequireComponent(typeof(NetRigidbody))]
    public class U3DGrabbable : NetComponent, IU3DInteractable, IU3DInventoryActivatable
    {
        [Header("Grab Distance Configuration")]
        [Tooltip("Minimum distance to grab from (0 = touch only)")]
        [SerializeField] private float minGrabDistance = 0f;

        [Tooltip("Maximum distance to grab from")]
        [SerializeField] private float maxGrabDistance = 2f;

        [Header("Hand Attachment")]
        [Tooltip("Which hand this object is carried in. Works on any humanoid avatar without naming bones. Player Position carries it in front of the player instead.")]
        [SerializeField] private U3DHandChoice hand = U3DHandChoice.RightHand;

        [Tooltip("Offset from the hand position")]
        [SerializeField] private Vector3 grabOffset = Vector3.zero;

        [Header("Throw Configuration")]
        [Tooltip("Thrown in the direction the player is looking when released, then falls. Turning this on turns Drop On Release off. With neither on, a released object floats where it was let go.")]
        [SerializeField] private bool throwable = false;

        [Tooltip("Base throw force multiplier")]
        [SerializeField] private float throwForce = 10f;

        [Tooltip("Additional upward force when throwing")]
        [SerializeField] private float upwardThrowBoost = 2f;

        [Tooltip("Maximum throw velocity")]
        [SerializeField] private float maxThrowVelocity = 20f;

        [Tooltip("Minimum velocity required to count as a throw. Below this the object is simply dropped.")]
        [SerializeField] private float minThrowVelocity = 1f;

        [Tooltip("Falls straight down when released. Turning this on turns Throwable off. With neither on, a released object floats where it was let go.")]
        [SerializeField] private bool dropOnRelease = false;

        [Tooltip("When enabled, this object is permanently destroyed when it falls out of world bounds instead of respawning. Requires a U3DDestroyable component on this object.")]
        [SerializeField] private bool destroyOnOutOfBounds = false;

        [Header("Throw Impact Filter")]
        [Tooltip("Only fire OnImpact for collisions with a specific tag")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag required to fire OnImpact")]
        [SerializeField] private string requiredTag = "Player";

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this object. Edit the text on that object directly. At runtime the label tracks this object's position so it travels with it.")]
        public U3DWorldspaceUI labelUI;

        [Header("Grab Events")]
        [Tooltip("Fired on EVERY player's machine when this object is picked up by anyone. Wire sounds, effects and animation here.")]
        public UnityEvent OnGrabbed;

        [Tooltip("Fired only for the player who picked it up. Wire score, UI and personal feedback here.")]
        public UnityEvent OnGrabbedByMe;

        [Tooltip("Fired on EVERY player's machine when this object is let go by anyone.")]
        public UnityEvent OnReleased;

        [Tooltip("Fired only for the player who let it go.")]
        public UnityEvent OnReleasedByMe;

        [Tooltip("Called when the local player enters grab range")]
        public UnityEvent OnEnterGrabRange;

        [Tooltip("Called when the local player exits grab range")]
        public UnityEvent OnExitGrabRange;

        [Tooltip("Called when a grab attempt by the local player fails")]
        public UnityEvent OnGrabFailed;

        [Header("Throw Events")]
        [Tooltip("Fired on EVERY player's machine when this object is thrown by anyone. Wire sounds, effects and animation here.")]
        public UnityEvent OnThrown;

        [Tooltip("Fired only for the player who threw it. Wire score, UI and personal feedback here.")]
        public UnityEvent OnThrownByMe;

        [Tooltip("Fired on every machine when a moving thrown object hits something.")]
        public UnityEvent OnImpact;

        [Tooltip("Fired on every machine when the object comes to rest.")]
        public UnityEvent OnSleep;

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
        private Transform _originalParent;
        private U3DGrabPoint _grabPoint;

        private NetMessage _hold;
        private NetMessage _thrown;
        private bool _throwPending;

        private PeerId _holder = PeerId.None;
        private PeerId _releasedFrom = PeerId.None;
        private Transform _holdParent;
        private PeerId _pendingHoldFrom = PeerId.None;
        private Vector3 _pendingHoldPosition;
        private Quaternion _pendingHoldRotation = Quaternion.identity;

        private U3DAvatarManager _avatar;
        private Transform _playerTransform;
        private Transform _handTransform;
        private Camera _playerCamera;
        private bool _isInRange;

        private bool _labelHidden;

        private NetRigidbody.MotionState _lastMotionState = NetRigidbody.MotionState.Resting;
        private Coroutine _boundsCheckCoroutine;
        private bool _warnedAboutMissingDestroyable;

        [System.NonSerialized] private bool _validatedDropOnRelease;

        private static U3DGrabbable _currentlyGrabbed;

        /// <summary>
        /// The object currently held by the local player, or null when their hands are
        /// empty. U3DInteractionManager routes a release press straight here rather than
        /// searching for the held object again.
        /// </summary>
        public static U3DGrabbable CurrentlyGrabbed => _currentlyGrabbed;

        /// <summary>
        /// A released object floats unless it is thrown or dropped, and the Rigidbody's gravity
        /// is what makes the difference: a release hands the body to physics with the velocity
        /// it was given, and with no gravity and no velocity it stays where it was let go.
        /// Written here from the two toggles rather than left to the Rigidbody's own setting,
        /// so every machine runs the same flight from the same authored fields.
        /// </summary>
        private bool FallsWhenReleased => throwable || dropOnRelease;

        private void Awake()
        {
            _motion = GetComponent<NetRigidbody>();
            _rb = GetComponent<Rigidbody>();
            _rb.useGravity = FallsWhenReleased;
            _originalParent = transform.parent;
            _grabPoint = GetComponentInChildren<U3DGrabPoint>(true);
        }

        private void Start()
        {
            FindPlayer();
            LinkLabelUI();
            if (FallsWhenReleased) StartBoundsMonitoring();
        }

        protected override void OnNetSpawn()
        {
            _hold = RegisterMessage(NetKeys.GrabbableHold, HandleHold);

            if (throwable)
                _thrown = RegisterMessage(NetKeys.ThrowableThrown, HandleThrown);

            _motion.Releasing += HandleMotionReleasing;
            _motion.Resetting += HandleMotionResetting;

            if (throwable)
                _lastMotionState = _motion.State;

            if (Session != null)
            {
                Session.PeerLeft += HandlePeerLeft;
                Session.PeerReachable += HandlePeerReachable;
            }
        }

        protected override void OnNetDespawn()
        {
            if (_motion != null)
            {
                _motion.Releasing -= HandleMotionReleasing;
                _motion.Resetting -= HandleMotionResetting;
            }

            if (Session != null)
            {
                Session.PeerLeft -= HandlePeerLeft;
                Session.PeerReachable -= HandlePeerReachable;
            }
        }

        /// <summary>
        /// Ownership moved to somebody other than whoever was holding this, which means it
        /// was taken. Let go of it. The new holder's pose arrives as its own message and
        /// puts the object in their hand.
        /// </summary>
        protected override void OnAuthorityChanged(PeerId previous, PeerId current)
        {
            if (!_holder.IsValid || current == _holder) return;
            DetachFromHand();
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
            ApplyPendingHold();
            UpdatePlayerProximity();
            RestoreLabelWhenAtRest();
            if (throwable) TrackMotionState();
        }

        /// <summary>
        /// A hold pose that could not be applied when it arrived is retried here until it
        /// can be. Two things delay it and both resolve on their own: ownership may not
        /// have caught up yet, because the claim and the pose are two separate messages;
        /// and the holder's avatar may not exist yet, which is ordinary for a pose sent to
        /// somebody who has only just become reachable.
        ///
        /// Retrying rather than discarding is what makes this safe. Nothing re-sends a hold
        /// pose on its own, so a dropped one leaves the object out of the hand until the
        /// holder happens to let go.
        /// </summary>
        private void ApplyPendingHold()
        {
            if (!_pendingHoldFrom.IsValid) return;

            if (Entity == null)
            {
                _pendingHoldFrom = PeerId.None;
                return;
            }

            if (Entity.Owner == _pendingHoldFrom)
            {
                ApplyHold(_pendingHoldFrom, _pendingHoldPosition, _pendingHoldRotation);
                return;
            }

            if (Entity.IsOwned) _pendingHoldFrom = PeerId.None;
        }

        private void UpdatePlayerProximity()
        {
            if (_playerTransform == null)
            {
                FindPlayer();
                return;
            }

            float distanceToPlayer = Vector3.Distance(transform.position, _playerTransform.position);
            bool wasInRange = _isInRange;
            _isInRange = distanceToPlayer >= minGrabDistance && distanceToPlayer <= maxGrabDistance;

            if (_isInRange && !wasInRange) OnEnterGrabRange?.Invoke();
            else if (!_isInRange && wasInRange) OnExitGrabRange?.Invoke();
        }

        /// <summary>
        /// The label is hidden while the object is held and stays hidden through the flight
        /// after a throw, so it does not sail across the room. It comes back when the object
        /// has actually stopped.
        /// </summary>
        private void RestoreLabelWhenAtRest()
        {
            if (!_labelHidden || labelUI == null) return;
            if (_holder.IsValid) return;
            if (_motion.State != NetRigidbody.MotionState.Resting) return;

            labelUI.gameObject.SetActive(true);
            _labelHidden = false;
        }

        // Motion state tracking

        /// <summary>
        /// Coming to rest is derived from the object's own motion state rather than sent,
        /// so OnSleep happens on every machine without a message.
        /// </summary>
        private void TrackMotionState()
        {
            NetRigidbody.MotionState state = _motion.State;
            if (state == _lastMotionState) return;

            bool wasMoving = _lastMotionState == NetRigidbody.MotionState.Free
                          || _lastMotionState == NetRigidbody.MotionState.Settling;

            _lastMotionState = state;

            if (state != NetRigidbody.MotionState.Resting || !wasMoving) return;

            OnSleep?.Invoke();
        }

        // Grab

        public void Grab()
        {
            if (HeldByMe) return;

            if (!EnsureLocalPlayer())
            {
                OnGrabFailed?.Invoke();
                return;
            }

            if (!InGrabRange())
            {
                OnGrabFailed?.Invoke();
                return;
            }

            PerformGrab();
        }

        /// <summary>
        /// Grabs without the distance check. Used when U3DInventory summons an item
        /// straight into the player's hand — the player asked for it, so the walk-up-to-it
        /// rule does not apply.
        /// </summary>
        public void OnInventoryActivate()
        {
            if (HeldByMe) return;
            if (!EnsureLocalPlayer()) return;

            PerformGrab();
        }

        private void PerformGrab()
        {
            if (!IsLive || Session == null || Entity == null)
            {
                OnGrabFailed?.Invoke();
                return;
            }

            if (_currentlyGrabbed != null && _currentlyGrabbed != this)
                _currentlyGrabbed.Release();

            FindHandBone();
            if (_handTransform == null)
            {
                OnGrabFailed?.Invoke();
                return;
            }

            if (!Entity.RequestAuthority())
            {
                OnGrabFailed?.Invoke();
                return;
            }

            ComputeHoldPose(_handTransform, out Vector3 localPosition, out Quaternion localRotation);
            _hold.SendToAll(localPosition, localRotation);
        }

        /// <summary>
        /// Works out where the object sits relative to the hand bone, expressed in the
        /// hand's own space so any machine can reproduce it against its copy of that hand.
        ///
        /// With a Grab Point marker the object is moved rigidly so the marker lands on the
        /// hold point, with the marker's frame aligned to the hand's neutral-stance
        /// orientation rather than to whatever pose the animation happens to be in. That is
        /// why grabbing mid-swing, mid-jump or mid-fly produces the same in-hand pose as
        /// grabbing while standing still. The hold point sits a fixed distance out from the
        /// wrist along the forearm, because a Humanoid hand bone pivots at the wrist and
        /// without that offset objects land on the wrist instead of in the palm.
        ///
        /// Which hand this is belongs to the avatar, which resolved the transform and can
        /// identify it. This file does not derive it a second time.
        ///
        /// Without a marker the object keeps its own rotation and sits at Grab Offset.
        /// </summary>
        private void ComputeHoldPose(Transform hand, out Vector3 localPosition, out Quaternion localRotation)
        {
            Vector3 targetWorldPosition;
            Quaternion targetWorldRotation;

            if (_grabPoint != null)
            {
                Quaternion targetMarkerWorld = _playerTransform != null
                    ? _playerTransform.rotation
                    : hand.rotation;
                Vector3 holdPointWorld = hand.position;

                U3DAvatarManager avatar = _avatar;

                if (avatar != null && avatar.TryGetHandGrabFrame(hand,
                        out Quaternion neutralRelative, out Vector3 anchorLocalDirection))
                {
                    targetMarkerWorld = hand.rotation * Quaternion.Inverse(neutralRelative);
                    holdPointWorld = hand.position
                        + hand.TransformDirection(anchorLocalDirection) * _grabPoint.AnchorDistance;
                }

                targetWorldRotation = targetMarkerWorld
                    * (Quaternion.Inverse(_grabPoint.transform.rotation) * transform.rotation);
                Quaternion rigidDelta = targetWorldRotation * Quaternion.Inverse(transform.rotation);
                targetWorldPosition = holdPointWorld
                    + rigidDelta * (transform.position - _grabPoint.transform.position);
            }
            else
            {
                targetWorldPosition = hand.TransformPoint(grabOffset);
                targetWorldRotation = transform.rotation;
            }

            localPosition = hand.InverseTransformPoint(targetWorldPosition);
            localRotation = Quaternion.Inverse(hand.rotation) * targetWorldRotation;
        }

        /// <summary>
        /// Runs on every machine, including the grabber's. One write path everywhere, so
        /// the holder and everyone watching build the same result from the same message.
        /// </summary>
        private void HandleHold(PeerId sender, object[] args)
        {
            if (!IsLive || Entity == null) return;
            if (args == null || args.Length < 2) return;
            if (!(args[0] is Vector3 localPosition)) return;
            if (!(args[1] is Quaternion localRotation)) return;
            if (!sender.IsValid) return;

            if (Entity.Owner != sender)
            {
                _pendingHoldFrom = sender;
                _pendingHoldPosition = localPosition;
                _pendingHoldRotation = localRotation;
                return;
            }

            ApplyHold(sender, localPosition, localRotation);
        }

        private void ApplyHold(PeerId holder, Vector3 localPosition, Quaternion localRotation)
        {
            Transform hand = ResolveHandFor(holder);
            if (hand == null)
            {
                _pendingHoldFrom = holder;
                _pendingHoldPosition = localPosition;
                _pendingHoldRotation = localRotation;
                return;
            }

            _pendingHoldFrom = PeerId.None;
            _releasedFrom = PeerId.None;

            PeerId previousHolder = _holder;

            _motion.Carry();

            transform.SetParent(hand, false);
            transform.SetLocalPositionAndRotation(localPosition, localRotation);

            _holder = holder;
            _holdParent = hand;

            bool mine = Session != null && holder == Session.LocalPeer;
            if (mine) _currentlyGrabbed = this;
            else if (_currentlyGrabbed == this) _currentlyGrabbed = null;

            if (labelUI != null && !_labelHidden)
            {
                labelUI.gameObject.SetActive(false);
                _labelHidden = true;
            }

            if (previousHolder == holder) return;

            OnGrabbed?.Invoke();
            if (mine) OnGrabbedByMe?.Invoke();
        }

        /// <summary>
        /// Finds the hand bone belonging to a given player. My own hand is the transform
        /// the hold pose was measured against, so it is used directly — looking it up again
        /// could only introduce a way for the result to disagree with the measurement.
        /// Anyone else's hand comes from the session's record of who owns which avatar,
        /// rather than from searching the scene.
        /// </summary>
        private Transform ResolveHandFor(PeerId peer)
        {
            if (Session == null) return null;

            if (peer == Session.LocalPeer)
            {
                if (_handTransform == null) FindPlayer();
                return _handTransform;
            }

            if (!Session.TryGetPlayerEntity(peer, out NetEntity playerEntity)) return null;
            if (playerEntity == null) return null;

            U3DAvatarManager avatar = playerEntity.GetComponentInChildren<U3DAvatarManager>(true);
            if (avatar == null) return null;

            return avatar.ResolveHandBone(hand, false);
        }

        // Release and throw

        /// <summary>
        /// Lets the object go. When throwing is enabled and the player is looking in a
        /// valid direction, the object is thrown; otherwise it is let go with no velocity,
        /// and falls or floats as Drop On Release says.
        /// </summary>
        public void Release()
        {
            if (!HeldByMe) return;

            if (throwable && TryComputeThrowVelocity(out Vector3 velocity))
            {
                TriggerPlayerAnimation("ThrowTrigger");
                _throwPending = true;
                ReleaseWith(velocity, Vector3.zero);
                return;
            }

            ReleaseWith(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Lets the object go with a starting velocity. A throw uses the computed velocity;
        /// a plain drop uses zero.
        /// </summary>
        public void ReleaseWith(Vector3 linearVelocity, Vector3 angularVelocity)
        {
            if (!HeldByMe) return;
            _motion.ReleaseToWorld(linearVelocity, angularVelocity);
        }

        private bool TryComputeThrowVelocity(out Vector3 velocity)
        {
            velocity = Vector3.zero;

            if (_playerCamera == null || _playerTransform == null) FindPlayer();

            Vector3 direction = GetThrowDirection();
            direction.y += upwardThrowBoost / Mathf.Max(0.01f, throwForce);
            direction.Normalize();

            Vector3 candidate = direction * throwForce;
            if (candidate.magnitude > maxThrowVelocity)
                candidate = candidate.normalized * maxThrowVelocity;

            if (candidate.magnitude < minThrowVelocity) return false;

            velocity = candidate;
            return true;
        }

        private Vector3 GetThrowDirection()
        {
            if (_playerCamera != null) return _playerCamera.transform.forward;
            if (_playerTransform != null) return _playerTransform.forward;
            return Vector3.forward;
        }

        /// <summary>
        /// Throws with an explicit direction and force. Works whether or not the object is
        /// currently held.
        /// </summary>
        public void ThrowInDirection(Vector3 direction, float force)
        {
            if (!throwable) return;
            if (!IsLive || Session == null || Entity == null) return;

            Vector3 velocity = direction.normalized * force;
            if (velocity.magnitude > maxThrowVelocity)
                velocity = velocity.normalized * maxThrowVelocity;

            if (HeldByMe)
            {
                TriggerPlayerAnimation("ThrowTrigger");
                _throwPending = true;
                ReleaseWith(velocity, Vector3.zero);
                return;
            }

            if (!Entity.RequestAuthority()) return;

            _motion.ReleaseToWorld(velocity, Vector3.zero);
            if (_thrown != null) _thrown.SendToAll();
        }

        /// <summary>
        /// Throws in the direction the local player is facing, with a custom force. Pass a
        /// value of zero or less to use the configured throw force.
        /// </summary>
        public void ThrowInCameraDirection(float customForce = -1f)
        {
            if (!throwable) return;

            if (_playerCamera == null || _playerTransform == null) FindPlayer();

            float useForce = customForce > 0f ? customForce : throwForce;

            Vector3 direction = GetThrowDirection();
            direction.y += upwardThrowBoost / Mathf.Max(0.01f, useForce);
            direction.Normalize();

            ThrowInDirection(direction, useForce);
        }

        /// <summary>
        /// Takes the object off whoever was holding it and returns it to its original place
        /// in the hierarchy, keeping its world pose. Every machine runs this from the same
        /// cause, so the object leaves the hand everywhere at once.
        /// </summary>
        private void DetachFromHand()
        {
            if (!_holder.IsValid) return;

            bool wasMine = Session != null && _holder == Session.LocalPeer;

            transform.SetParent(_originalParent, true);
            _releasedFrom = _holder;
            _holder = PeerId.None;
            _holdParent = null;

            if (_currentlyGrabbed == this) _currentlyGrabbed = null;

            OnReleased?.Invoke();

            if (wasMine)
            {
                OnReleasedByMe?.Invoke();
                AnnounceThrowIfPending();
            }
        }

        /// <summary>
        /// The local player has let the object go and the release has been published, so
        /// the throw can now be announced to everyone.
        /// </summary>
        private void AnnounceThrowIfPending()
        {
            if (!_throwPending) return;
            _throwPending = false;

            if (_thrown != null) _thrown.SendToAll();
        }

        /// <summary>
        /// Runs on every machine. The thrower's own avatar played the throw at the moment of
        /// release; everyone else plays it here on the thrower's avatar, but only when the
        /// object was in the thrower's hand. A throw scripted from the ground has no arm
        /// movement on the thrower's own screen, so it has none anywhere else either.
        ///
        /// The hand check accepts either order of arrival. The release normally lands first
        /// and takes the object out of the hand, which is what the released-from record is
        /// for; if this message lands first, the thrower is still the holder.
        /// </summary>
        private void HandleThrown(PeerId sender, object[] args)
        {
            bool fromHand = sender.IsValid && (sender == _holder || sender == _releasedFrom);
            _releasedFrom = PeerId.None;

            if (fromHand && Session != null && sender != Session.LocalPeer)
                TriggerPeerAnimation(sender, "ThrowTrigger");

            OnThrown?.Invoke();

            if (Session != null && sender == Session.LocalPeer) OnThrownByMe?.Invoke();
        }

        /// <summary>
        /// Runs on every machine just before the release lands. Detaches the object from
        /// the hand it was riding so it is a free body before any velocity is applied.
        /// Keeping the dropped object from shoving the dropper belongs to NetRigidbody,
        /// which owns the collider pair for every released object rather than one per
        /// interactable.
        /// </summary>
        private void HandleMotionReleasing(NetRigidbody motion, PeerId sender)
        {
            _pendingHoldFrom = PeerId.None;
            DetachFromHand();
        }

        /// <summary>
        /// The object is being sent home and this machine is the one doing it. If it is in
        /// my hand, let go first, so the pose that gets published describes a free body
        /// rather than one still parented to a moving hand.
        ///
        /// Only the local holder can act. An object held by somebody else leaves their
        /// hand when the release or the settle reaches them, which is the same path a
        /// taken object already travels.
        /// </summary>
        private void HandleMotionResetting(NetRigidbody motion)
        {
            if (HeldByMe) Release();
        }

        /// <summary>
        /// The holder vanished. Every machine takes the object off the departing avatar so
        /// it is not destroyed along with them, and leaves it exactly where it was.
        ///
        /// Handing it back to the world is not done here. A drop has to be published by one
        /// machine so every machine runs the same fall from the same starting conditions,
        /// and that belongs to NetRigidbody, which owns the release and knows which peer is
        /// entitled to speak for an unowned object.
        /// </summary>
        private void HandlePeerLeft(PeerId peer)
        {
            if (_pendingHoldFrom == peer) _pendingHoldFrom = PeerId.None;
            if (_holder != peer) return;

            DetachFromHand();
        }

        /// <summary>
        /// Somebody can now be sent to. If this object is in my hand, tell them where it
        /// sits, so they do not see it hanging in the air where my hand happened to be when
        /// they arrived.
        ///
        /// Sent on becoming reachable rather than on joining. The roster names a peer
        /// before any connection to them exists, so a send at that moment goes nowhere and
        /// says nothing.
        /// </summary>
        private void HandlePeerReachable(PeerId peer)
        {
            if (!HeldByMe || _holdParent == null) return;
            _hold.SendTo(peer, transform.localPosition, transform.localRotation);
        }

        // Impact

        private void OnCollisionEnter(Collision collision)
        {
            if (!throwable) return;
            if (collision.gameObject.CompareTag("Player")) return;
            if (_motion.State != NetRigidbody.MotionState.Free) return;
            if (collision.relativeVelocity.magnitude <= 2f) return;
            if (requireTag && !collision.gameObject.CompareTag(requiredTag)) return;

            OnImpact?.Invoke();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!throwable) return;
            if (!requireTag) return;
            if (!other.CompareTag(requiredTag)) return;
            if (_motion.State != NetRigidbody.MotionState.Free) return;

            OnImpact?.Invoke();
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

                if (IsGrabbed) continue;
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
        // OnThrown and OnSleep. Deriving it from a settle is not possible — a settle after
        // a reset and a settle after a throw are the same message. F30
        public void ResetToSpawn()
        {
            if (!_motion.ResetToAuthored()) return;

            OnWorldBoundsReset?.Invoke();
        }

        /// <summary>
        /// Hands a resting object to the world with no velocity, so it falls if it is not
        /// already supported.
        /// </summary>
        public void WakeUp()
        {
            if (!IsLive || Session == null || Entity == null) return;
            if (IsGrabbed) return;

            if (!Entity.HasAuthority && !Entity.RequestAuthority()) return;

            _motion.ReleaseToWorld(Vector3.zero, Vector3.zero);
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

        // Player lookup

        private bool EnsureLocalPlayer()
        {
            if (_playerTransform == null) FindPlayer();
            return _playerTransform != null;
        }

        private bool InGrabRange()
        {
            if (_playerTransform == null) return false;
            float distance = Vector3.Distance(transform.position, _playerTransform.position);
            return distance >= minGrabDistance && distance <= maxGrabDistance;
        }

        /// <summary>
        /// Finds the local player's rig and camera. Only the local player carries a
        /// U3DPlayerController — a remote avatar has none — so anything this finds is the
        /// local player by construction, and it is available as soon as the player exists
        /// in the scene rather than once the session has registered their avatar.
        ///
        /// Resolving another peer's hand is a different question with a different answer,
        /// and goes through the session in ResolveHandFor.
        /// </summary>
        private void FindPlayer()
        {
            _playerTransform = null;
            _handTransform = null;
            _avatar = null;
            _playerCamera = null;

            U3DPlayerController player = U3DPlayerController.FindLocalPlayer();
            if (player == null) return;

            _playerTransform = player.transform;
            _avatar = player.GetComponentInChildren<U3DAvatarManager>(true);
            _playerCamera = player.GetComponentInChildren<Camera>();
            FindHandBone();

            if (_playerCamera == null) _playerCamera = Camera.main;
        }

        private void FindHandBone()
        {
            _handTransform = null;
            if (_avatar == null) return;

            _handTransform = _avatar.ResolveHandBone(hand, true);
        }

        // IU3DInteractable

        public void OnInteract()
        {
            if (HeldByMe) Release();
            else Grab();
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }

        public bool CanInteract()
        {
            if (HeldByMe) return true;
            if (!IsLive || Entity == null || Session == null) return false;
            if (!Entity.CanTake(Session.LocalPeer)) return false;
            return InGrabRange();
        }

        public string GetInteractionPrompt()
        {
            if (HeldByMe) return "Release";
            if (Entity != null && Entity.IsOwned) return "Take";
            return "Grab";
        }

        // Public API

        public bool IsGrabbed => _holder.IsValid;
        public bool HeldByMe => Session != null && _holder == Session.LocalPeer;
        public PeerId Holder => _holder;
        public bool IsInRange => _isInRange;
        public bool IsThrowable => throwable;

        private void OnDestroy()
        {
            if (_currentlyGrabbed == this) _currentlyGrabbed = null;
            if (_boundsCheckCoroutine != null) StopCoroutine(_boundsCheckCoroutine);
        }

        /// <summary>
        /// Throwable and Drop On Release exclude each other, and the one just switched on
        /// wins, so ticking either box unticks the other.
        /// </summary>
        private void OnValidate()
        {
            if (throwable && dropOnRelease)
            {
                if (dropOnRelease != _validatedDropOnRelease)
                    throwable = false;
                else
                    dropOnRelease = false;
            }

            _validatedDropOnRelease = dropOnRelease;

            if (!throwable) return;

            if (throwForce <= 0f)
                Debug.LogWarning("U3DGrabbable: Throw force should be greater than 0");

            if (maxThrowVelocity < throwForce)
                Debug.LogWarning("U3DGrabbable: Max throw velocity is less than throw force - throws will be clamped");

            if (worldBoundsFloor > 0f)
                Debug.LogWarning("U3DGrabbable: World bounds floor should typically be negative (below ground level)");

            if (worldBoundsRadius <= 0f)
                Debug.LogWarning("U3DGrabbable: World bounds radius should be positive");
        }
    }
}
