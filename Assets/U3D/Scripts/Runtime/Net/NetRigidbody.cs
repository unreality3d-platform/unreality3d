using System;
using System.Collections;
using UnityEngine;

namespace U3D.Net
{
    [RequireComponent(typeof(NetEntity))]
    public class NetRigidbody : NetComponent
    {
        public const float SettleDuration = 0.2f;

        private const float HomeTolerance = 0.05f;

        /// <summary>
        /// Driven samples per second. Twenty, matching the avatar packet, because a driven
        /// object and a body are watched the same way and there is nothing to gain from a
        /// second rate. The tick runs at the physics rate, so this is an accumulator here
        /// rather than a property of the tick.
        /// </summary>
        private const float DrivenSendRate = 20f;

        private const float DrivenSendInterval = 1f / DrivenSendRate;

        /// <summary>
        /// How quickly a receiving peer closes the gap to the latest driven sample. A
        /// direct write is three identical frames then a jump at sixty frames against
        /// twenty samples, which reads as stuttering on an object moving at walking pace.
        /// </summary>
        private const float DrivenFollowRate = 18f;

        /// <summary>
        /// A driven sample further than this from where the object currently is, is
        /// applied outright rather than eased. Easing across a long distance draws the
        /// object through whatever is between the two, which is worse than appearing.
        /// </summary>
        private const float DrivenSnapDistance = 2f;

        public enum MotionState
        {
            Resting,
            Carried,
            Driven,
            Free,
            Settling
        }

        [Tooltip("When enabled, this object falls to the ground whenever it arrives at the position it was placed at — when the experience starts, and again after it is returned there. Use it for objects positioned above ground level.")]
        [SerializeField] private bool startActive = false;

        [SerializeField] private float restSpeedThreshold = 0.05f;
        [SerializeField] private float restHoldTime = 0.25f;

        private Rigidbody _rb;
        private bool _hasRigidbody;
        private MotionState _state = MotionState.Resting;

        private float _restTimer;
        private bool _settleSent;
        private bool _startActiveHandled;
        private Vector3 _authoredPosition;
        private Quaternion _authoredRotation = Quaternion.identity;

        private Vector3 _settleFrom;
        private Quaternion _settleFromRotation;
        private Vector3 _settleTo;
        private Quaternion _settleToRotation;
        private float _settleElapsed;

        private Vector3 _drivenPosition;
        private Quaternion _drivenRotation = Quaternion.identity;
        private bool _hasDrivenSample;
        private ushort _drivenSequence;
        private ushort _lastDrivenSequence;
        private bool _hasReceivedDrivenSample;
        private float _drivenSendTimer;

        private bool _releaseAfterDeparture;

        private Collider[] _bodyColliders;
        private CharacterController _localCharacter;
        private Coroutine _clearanceRoutine;

        private bool _carrySuspend;
        private bool _clearanceSuspend;
        private bool _flightSuspend;
        private bool _collisionIgnored;

        private NetMessage _releaseMessage;
        private NetMessage _settleMessage;
        private NetMessage _drivenMessage;
        private NetMessage _driveMessage;

        public event Action<NetRigidbody, Vector3, Quaternion> CameToRest;

        /// <summary>
        /// Raised on every peer immediately before an incoming release is applied, so a
        /// listener can detach the object from whatever was carrying it. Fires before the
        /// pose and velocity land, because a dynamic body must not still be parented to a
        /// moving hand when its velocity is set.
        /// </summary>
        public event Action<NetRigidbody, PeerId> Releasing;

        /// <summary>
        /// Raised on every peer when someone begins steering this object, carrying the
        /// peer who took hold of it. A component that wants a creator event on every
        /// machine listens here rather than raising one where the drive was started, which
        /// would reach only the driver.
        /// </summary>
        public event Action<NetRigidbody, PeerId> DriveBegan;

        /// <summary>
        /// Raised on every peer when the object stops being steered, whatever ended it —
        /// the driver letting go, a release arriving, or authority moving elsewhere.
        /// Derived from the state leaving Driven rather than sent, so it costs nothing and
        /// cannot disagree with the object's actual state.
        /// </summary>
        public event Action<NetRigidbody> DriveEnded;

        /// <summary>
        /// Raised on the peer performing a reset, immediately before the authored pose is
        /// published, so whatever is holding or steering the object lets go of it first.
        /// A component that must disengage listens here rather than being called by name:
        /// this assembly cannot name a game component, and a reset can be started by
        /// something that knows nothing about which interactables the object carries.
        /// </summary>
        public event Action<NetRigidbody> Resetting;

        public MotionState State => _state;
        public bool HasRigidbody => _hasRigidbody;

        /// <summary>
        /// Whether this object is sitting somewhere other than where the scene put it. The
        /// reporter reads this to tell a newcomer what to move, because nothing else in the
        /// project records it — a live object cannot be compared against the scene file it
        /// came from, and the authored pose is captured here at spawn for exactly this
        /// reason and the re-arm's.
        ///
        /// The same tolerance the re-arm uses, held once, so the two cannot come to
        /// disagree about which objects are home.
        ///
        /// Start Active is not part of the question. A crate someone kicked across the room
        /// is displaced whether or not it was authored above the ground, and a newcomer
        /// needs to be told about it for the same reason.
        /// </summary>
        public bool IsAwayFromAuthoredPose
        {
            get
            {
                if (!IsLive) return false;
                float tolerance = HomeTolerance * HomeTolerance;
                return (transform.position - _authoredPosition).sqrMagnitude > tolerance;
            }
        }

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _hasRigidbody = _rb != null;
            _bodyColliders = GetComponentsInChildren<Collider>(true);
        }

        protected override void OnNetSpawn()
        {
            _releaseMessage = RegisterMessage(NetKeys.MotionRelease, HandleRelease);
            _settleMessage = RegisterMessage(NetKeys.MotionSettle, HandleSettle);
            _drivenMessage = RegisterMessage(NetKeys.MotionDriven, HandleDriven);
            _driveMessage = RegisterMessage(NetKeys.MotionDrive, HandleDrive);

            _authoredPosition = transform.position;
            _authoredRotation = transform.rotation;

            _drivenPosition = transform.position;
            _drivenRotation = transform.rotation;
            _hasDrivenSample = false;
            _hasReceivedDrivenSample = false;
            _drivenSequence = 0;
            _drivenSendTimer = 0f;
            _releaseAfterDeparture = false;

            if (Net.Session != null) Net.Session.PeerLeft += HandlePeerLeft;

            SetState(MotionState.Resting);
        }

        protected override void OnNetDespawn()
        {
            if (Net.Session != null) Net.Session.PeerLeft -= HandlePeerLeft;

            StopPlayerClearance();
        }

        private void OnDisable()
        {
            StopPlayerClearance();
        }

        protected override void OnAuthorityChanged(PeerId previous, PeerId current)
        {
            if (_state == MotionState.Carried || _state == MotionState.Driven)
            {
                SetState(MotionState.Resting);
            }
            else
            {
                ApplyPhysicsRole();
                ApplySuspension();
            }
        }

        /// <summary>
        /// The peer who was carrying or steering this object has left the room. Every
        /// machine notes it; one of them acts, on the next tick.
        ///
        /// Noted rather than acted on here for two reasons. The session raises this before
        /// it chooses a successor reporter, so if the departing peer held that role every
        /// remaining machine's IsReporter is false at this instant. And the session clears
        /// the object's ownership immediately after this event, which has to have happened
        /// before anybody can take it.
        /// </summary>
        private void HandlePeerLeft(PeerId peer)
        {
            if (!IsLive || Entity == null) return;
            if (_state != MotionState.Carried && _state != MotionState.Driven) return;
            if (Entity.Owner != peer) return;

            _releaseAfterDeparture = true;
        }

        /// <summary>
        /// Hands a departed peer's object back to the world, published once by the peer
        /// entitled to speak for an unowned object so that every machine runs the same
        /// fall from the same starting conditions.
        ///
        /// A release rather than a settle, and the difference is the whole fix. A settle
        /// places the object at a pose and leaves it kinematic, so publishing one here
        /// would leave a dropped object hanging exactly where the hand was. Only a release
        /// hands it to physics. What happens next is the creator's own choice already
        /// made: an object with gravity falls and settles where it lands, and an object
        /// without gravity stays where it was let go, which is what it does for an ordinary
        /// drop too.
        /// </summary>
        private void TryReleaseAfterDeparture()
        {
            if (Net.Session == null || Entity == null) return;

            if (Entity.IsOwned
                || _state == MotionState.Free
                || _state == MotionState.Settling)
            {
                _releaseAfterDeparture = false;
                return;
            }

            if (!Net.Session.IsReporter) return;

            _releaseAfterDeparture = false;

            if (!Entity.RequestAuthority()) return;

            ReleaseToWorld(Vector3.zero, Vector3.zero);
        }

        public void Carry()
        {
            SetState(MotionState.Carried);
        }

        /// <summary>
        /// Takes hold of the object for steering, and tells every peer so their own copy
        /// follows the samples that are about to arrive. Called by the component that is
        /// doing the steering, which has already taken authority.
        ///
        /// A caller with no authority is refused rather than driving locally, because a
        /// local-only drive is precisely the failure this replaces: the object moves on one
        /// screen and nowhere else.
        /// </summary>
        public void Drive()
        {
            if (!IsLive || !HasAuthority) return;

            SetState(MotionState.Driven);

            if (_driveMessage != null) _driveMessage.SendToAll();
        }

        public void Rest()
        {
            SetState(MotionState.Resting);
        }

        /// <summary>
        /// Hands the object to the world on every peer, carrying the starting conditions
        /// each peer needs to run the flight locally. An object with no Rigidbody cannot
        /// come to rest on its own, so it publishes its resting pose in the same breath.
        /// </summary>
        public void ReleaseToWorld(Vector3 linearVelocity, Vector3 angularVelocity)
        {
            if (!IsLive || !HasAuthority || _releaseMessage == null) return;

            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;

            _releaseMessage.SendToAll(position, rotation, linearVelocity, angularVelocity);

            if (!_hasRigidbody) PublishRest(position, rotation);
        }

        /// <summary>
        /// Publishes a resting pose to every peer. Called when the object settles on its
        /// own, and available to callers that place an object deliberately, such as a
        /// respawn.
        ///
        /// Ownership is not given up here. The settle carries that, and it is handed over
        /// in HandleSettle so that every peer runs the same transition from the same
        /// message — including this one, since a broadcast is delivered locally and
        /// synchronously inside the send.
        /// </summary>
        public void PublishRest(Vector3 position, Quaternion rotation)
        {
            if (!IsLive || _settleMessage == null) return;

            _settleMessage.SendToAll(position, rotation);
        }

        /// <summary>
        /// Puts the object back where the scene put it, on every machine. One path for
        /// every object that can be sent home, whatever it is: a trash zone, a bounds
        /// recovery and a creator-wired button all arrive here.
        ///
        /// Resetting is raised first so anything holding or steering the object lets go
        /// before the pose is published, because a body still parented to a moving hand
        /// cannot be placed anywhere meaningful.
        ///
        /// Returns whether the reset was published. A peer that could not take the object
        /// did nothing, which is what lets a caller raise its own creator event only when
        /// something actually happened.
        /// </summary>
        public bool ResetToAuthored()
        {
            if (!IsLive || Entity == null || Net.Session == null) return false;

            Resetting?.Invoke(this);

            if (!Entity.HasAuthority && !Entity.RequestAuthority()) return false;

            PublishRest(_authoredPosition, _authoredRotation);
            return true;
        }

        public void Release(Vector3 position, Quaternion rotation, Vector3 linearVelocity, Vector3 angularVelocity)
        {
            _releaseAfterDeparture = false;

            transform.SetPositionAndRotation(position, rotation);
            SetState(MotionState.Free);

            if (_hasRigidbody)
            {
                _rb.position = position;
                _rb.rotation = rotation;
                _rb.linearVelocity = linearVelocity;
                _rb.angularVelocity = angularVelocity;
            }

            BeginPlayerClearance();
        }

        public void Settle(Vector3 position, Quaternion rotation)
        {
            _releaseAfterDeparture = false;

            TryRearmStartActive(position);

            _settleFrom = transform.position;
            _settleFromRotation = transform.rotation;
            _settleTo = position;
            _settleToRotation = rotation;
            _settleElapsed = 0f;
            SetState(MotionState.Settling);
        }

        /// <summary>
        /// Puts the object where a newcomer has been told it is, with no easing. This is
        /// not a settle and deliberately does not go through one: a settle eases over 200 ms
        /// because every peer already ran the same flight and the correction is small, and a
        /// newcomer ran no flight at all. Its copy is at the authored pose because that is
        /// what the scene file said, so easing would fly every moved object across the room
        /// at the moment of joining.
        ///
        /// The Start Active flag is spent here. An object in this list has already been
        /// dropped by whoever was entitled to drop it, so this machine must not do it
        /// again — and without this the drop would fire on this peer the moment it became
        /// reporter, publishing a release and settle for an object already on the ground.
        /// </summary>
        public void PlaceAt(Vector3 position, Quaternion rotation)
        {
            if (!IsLive) return;

            _startActiveHandled = true;
            _releaseAfterDeparture = false;

            transform.SetPositionAndRotation(position, rotation);

            if (_hasRigidbody)
            {
                _rb.position = position;
                _rb.rotation = rotation;
            }

            SetState(MotionState.Resting);
        }

        /// <summary>
        /// An object arriving back at the place it was authored is arriving home, and a
        /// Start Active object drops again when it gets there. Both halves of the test
        /// matter: the destination must be home, and the object must not already be there,
        /// or an object authored just above the floor would settle within tolerance of
        /// where it started and drop itself forever.
        ///
        /// This is the only way a re-arm can reach every machine. Whichever peer performs a
        /// respawn is not necessarily the peer entitled to drop the object, so re-arming at
        /// the reset itself would arm the wrong machine and leave the entitled one hanging.
        /// The settle reaches all of them.
        /// </summary>
        private void TryRearmStartActive(Vector3 destination)
        {
            if (!startActive || !_hasRigidbody || !_startActiveHandled) return;

            float tolerance = HomeTolerance * HomeTolerance;
            if ((destination - _authoredPosition).sqrMagnitude > tolerance) return;
            if ((transform.position - _authoredPosition).sqrMagnitude <= tolerance) return;

            _startActiveHandled = false;
        }

        /// <summary>
        /// Accepts a driven sample from the peer steering this object. The sequence number
        /// discards an arrival that overtook a newer one, which the unreliable channel
        /// permits and which would otherwise pull the object backwards. Compared as a
        /// wrapping difference rather than by size, so the counter's wrap is not a gap.
        /// </summary>
        public void ApplyDrivenSample(Vector3 position, Quaternion rotation, ushort sequence)
        {
            if (_hasReceivedDrivenSample)
            {
                short age = unchecked((short)(sequence - _lastDrivenSequence));
                if (age <= 0) return;
            }

            _lastDrivenSequence = sequence;
            _hasReceivedDrivenSample = true;

            _drivenPosition = position;
            _drivenRotation = rotation;
            _hasDrivenSample = true;
        }

        // Player collision

        /// <summary>
        /// Whether this object is intangible to the local player, and why. Three reasons
        /// and the flag follows the set, because Physics.IgnoreCollision does not
        /// reference-count and one path clearing it while the other still needs it is the
        /// defect the single owner exists to prevent.
        ///
        /// Carried is held for as long as the local player is holding the object, with no
        /// timer: an object in the hand is inside the player by construction, and a
        /// CharacterController pushes itself out of anything it overlaps whether the body
        /// is kinematic, dynamic or has no Rigidbody at all.
        ///
        /// The clearance reason ends when the player has moved out of the column above the
        /// object, and covers both a release and a deliberate placement inside the player.
        ///
        /// The flight reason is the one that makes every machine's physics scene the same
        /// during a flight. Each machine holds exactly one player-shaped collider, its own,
        /// and a remote avatar carries none — so an object flying or rolling past a person
        /// is deflected on that person's machine and passes through on every other one, and
        /// the two copies come to rest apart. Suspending it on every machine removes the
        /// obstacle everywhere rather than in one place.
        /// </summary>
        private void ApplySuspension()
        {
            bool desired = _carrySuspend || _clearanceSuspend || _flightSuspend;
            if (desired == _collisionIgnored) return;

            CharacterController character = ResolveLocalCharacter();
            if (character == null) return;

            if (!SetPlayerCollisionIgnored(character, desired)) return;

            _collisionIgnored = desired;
        }

        /// <summary>
        /// Makes this object intangible to the local player until they have moved clear of
        /// it. Called by anything that puts the object inside the player deliberately —
        /// summoning an item from inventory to the hand does exactly that, and the object
        /// would otherwise shove the player the moment it exists.
        ///
        /// The clearance ends on its own, so a summon whose grab never happens recovers
        /// rather than leaving an object nobody can collide with.
        /// </summary>
        public void SuspendUntilPlayerClear()
        {
            BeginPlayerClearance();
        }

        /// <summary>
        /// Stops a just-released object from shoving the player it is standing inside. The
        /// object and the local player are made intangible to each other the instant the
        /// release lands, and stay that way until the player has moved horizontally out of
        /// the column above the object. A player standing on what they dropped keeps
        /// passing through it until they step off, which is what they expect.
        ///
        /// Geometric overlap is the wrong measurement here and was tried first. It clears
        /// the moment the falling object passes below the capsule's lower edge — which is
        /// precisely when it is about to land under the player's feet. The horizontal
        /// column is the state that actually matters.
        ///
        /// The flight reason covers the same collider pair for the whole of Free, so what
        /// survives uniquely here is the tail: an object that has settled under the feet of
        /// the player who dropped it, whose state has already left Free while they are
        /// still standing on it. Plus a summon, which never enters Free at all.
        ///
        /// This lives here rather than on the interactable because Physics.IgnoreCollision
        /// does not reference-count. Two components managing the same collider pair race,
        /// and whichever finishes first restores collision while the other is still waiting
        /// — which is precisely what puts the player on top of the object. One owner is the
        /// only arrangement that cannot produce that, and this is the one component every
        /// released object has.
        /// </summary>
        private void BeginPlayerClearance()
        {
            CharacterController character = ResolveLocalCharacter();
            if (character == null) return;

            if (_clearanceRoutine != null)
            {
                StopCoroutine(_clearanceRoutine);
                _clearanceRoutine = null;
            }

            _clearanceSuspend = true;
            ApplySuspension();

            if (!_collisionIgnored)
            {
                _clearanceSuspend = false;
                return;
            }

            _clearanceRoutine = StartCoroutine(WaitForPlayerClearance(character));
        }

        private IEnumerator WaitForPlayerClearance(CharacterController character)
        {
            const float PollInterval = 0.1f;

            float threshold = character.radius + ComputeHorizontalExtent() + 0.05f;
            float thresholdSquared = threshold * threshold;

            while (character != null)
            {
                yield return new WaitForSeconds(PollInterval);

                Vector3 playerPosition = character.transform.position;
                Vector3 objectPosition = transform.position;

                float dx = playerPosition.x - objectPosition.x;
                float dz = playerPosition.z - objectPosition.z;

                if ((dx * dx) + (dz * dz) > thresholdSquared) break;
            }

            _clearanceSuspend = false;
            ApplySuspension();
            _clearanceRoutine = null;
        }

        private float ComputeHorizontalExtent()
        {
            if (_bodyColliders == null) return 0f;

            float extent = 0f;

            for (int i = 0; i < _bodyColliders.Length; i++)
            {
                Collider bodyCollider = _bodyColliders[i];
                if (!IsUsableCollider(bodyCollider)) continue;

                Vector3 extents = bodyCollider.bounds.extents;
                extent = Mathf.Max(extent, Mathf.Max(extents.x, extents.z));
            }

            return extent;
        }

        private void StopPlayerClearance()
        {
            if (_clearanceRoutine != null)
            {
                StopCoroutine(_clearanceRoutine);
                _clearanceRoutine = null;
            }

            _clearanceSuspend = false;
            _carrySuspend = false;
            _flightSuspend = false;
            ApplySuspension();
        }

        private bool SetPlayerCollisionIgnored(CharacterController character, bool ignored)
        {
            if (character == null || _bodyColliders == null) return false;

            bool any = false;

            for (int i = 0; i < _bodyColliders.Length; i++)
            {
                Collider bodyCollider = _bodyColliders[i];
                if (!IsUsableCollider(bodyCollider)) continue;

                Physics.IgnoreCollision(bodyCollider, character, ignored);
                any = true;
            }

            return any;
        }

        private static bool IsUsableCollider(Collider candidate)
        {
            return candidate != null
                && candidate.enabled
                && !candidate.isTrigger
                && candidate.gameObject.activeInHierarchy;
        }

        private CharacterController ResolveLocalCharacter()
        {
            if (_localCharacter != null) return _localCharacter;
            if (Net.Session == null) return null;
            if (!Net.Session.TryGetPlayerEntity(Net.Session.LocalPeer, out NetEntity playerEntity)) return null;
            if (playerEntity == null) return null;

            _localCharacter = playerEntity.GetComponentInChildren<CharacterController>(true);
            return _localCharacter;
        }

        // Messages

        private void HandleRelease(PeerId sender, object[] args)
        {
            if (args == null || args.Length < 4) return;
            if (!(args[0] is Vector3 position)) return;
            if (!(args[1] is Quaternion rotation)) return;
            if (!(args[2] is Vector3 linearVelocity)) return;
            if (!(args[3] is Vector3 angularVelocity)) return;

            if (Entity == null) return;
            if (Entity.IsOwned && sender != Entity.Owner) return;

            Releasing?.Invoke(this, sender);
            Release(position, rotation, linearVelocity, angularVelocity);
        }

        /// <summary>
        /// A settle from the object's owner means two things and always did: here is where
        /// it came to rest, and I have let go of it. Only the first ever crossed the wire.
        ///
        /// The release runs after the pose is applied, so a listener reacting to authority
        /// changing sees the object where it actually ended up. It runs on the sender's own
        /// machine too, through local delivery, which is why nothing publishes a release of
        /// its own any more.
        ///
        /// A settle from the reporter for an object it does not own carries no such
        /// meaning — the reporter is speaking for an object nobody holds — so it places the
        /// object and touches no ownership.
        /// </summary>
        private void HandleSettle(PeerId sender, object[] args)
        {
            if (args == null || args.Length < 2) return;
            if (!(args[0] is Vector3 position)) return;
            if (!(args[1] is Quaternion rotation)) return;

            if (Entity == null) return;

            PeerId reporter = Net.Session != null ? Net.Session.Reporter : PeerId.None;
            bool fromOwner = Entity.IsOwned && sender == Entity.Owner;
            bool fromReporter = reporter.IsValid && sender == reporter;
            if (Entity.IsOwned && !fromOwner && !fromReporter) return;

            Settle(position, rotation);

            if (fromOwner) Entity.ReleaseOnSettle();
        }

        /// <summary>
        /// Someone has taken hold of the object to steer it. Arrives on the driver's own
        /// machine too, which is what lets a component raise one creator event from one
        /// place rather than branching on whether it was the driver.
        ///
        /// A drive announcement from anyone but the object's owner is discarded. The claim
        /// travels on the same reliable channel and is submitted first, so an announcement
        /// from a peer this machine does not yet think owns the object is a stale one.
        /// </summary>
        private void HandleDrive(PeerId sender, object[] args)
        {
            if (Entity == null) return;
            if (Entity.IsOwned && sender != Entity.Owner) return;

            if (!HasAuthority)
            {
                _hasDrivenSample = false;
                _hasReceivedDrivenSample = false;
                SetState(MotionState.Driven);
            }

            DriveBegan?.Invoke(this, sender);
        }

        private void HandleDriven(PeerId sender, object[] args)
        {
            if (args == null || args.Length < 3) return;
            if (!(args[0] is Vector3 position)) return;
            if (!(args[1] is Quaternion rotation)) return;
            if (!(args[2] is ushort sequence)) return;

            if (Entity == null) return;
            if (Entity.IsOwned && sender != Entity.Owner) return;
            if (HasAuthority) return;

            ApplyDrivenSample(position, rotation, sequence);
        }

        /// <summary>
        /// Carried is a state where the object is inside a player by construction, so the
        /// carry half of the suspension is written here rather than in Carry() — every
        /// route out of Carried passes through this method, including authority moving to
        /// somebody else, and a route that cleared it separately would be a route that
        /// could forget to.
        ///
        /// Carried does not split on authority, and that is deliberate rather than an
        /// oversight. The holder's own machine has the object inside its own capsule; every
        /// other machine has it parented to a hand bone that walks and swings, and a
        /// CharacterController depenetrates against it, so a held object shoved everybody
        /// except the person holding it. Suspending on the holder alone leaves the
        /// asymmetry that produces it. Authority is also the wrong test even where it would
        /// work: a claim applies immediately per the optimistic rule, so two machines can
        /// disagree about who is holding something, and a suspension that depends on
        /// agreement fails in exactly that window.
        ///
        /// The flight half is written here for the same reason and reads the state
        /// directly. Free is suspended on every machine, including the peer that threw or
        /// dropped the object, because the point is that no machine's copy meets a player
        /// collider the others do not have.
        ///
        /// Driven is the one state that does split on authority. The driving peer keeps
        /// contact, because a push is contact and suspending it there is what the
        /// driven-contact test refuses. Every other machine suspends, since a bystander's
        /// own capsule is an obstacle in the path of a crate that exists nowhere else.
        /// </summary>
        private void SetState(MotionState next)
        {
            MotionState previous = _state;

            _state = next;
            _restTimer = 0f;
            _settleSent = next != MotionState.Free;

            if (next != MotionState.Driven)
            {
                _hasDrivenSample = false;
                _hasReceivedDrivenSample = false;
            }
            else
            {
                _drivenSendTimer = 0f;
            }

            _carrySuspend = next == MotionState.Carried;
            _flightSuspend = next == MotionState.Free
                          || (next == MotionState.Driven && !HasAuthority);
            ApplySuspension();

            ApplyPhysicsRole();

            if (previous == MotionState.Driven && next != MotionState.Driven)
                DriveEnded?.Invoke(this);
        }

        private void ApplyPhysicsRole()
        {
            if (!_hasRigidbody) return;

            bool dynamic = _state == MotionState.Free
                        || (_state == MotionState.Driven && HasAuthority);

            if (_rb.isKinematic == !dynamic) return;

            if (!dynamic)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }

            _rb.isKinematic = !dynamic;
        }

        /// <summary>
        /// An object placed above the ground is dropped once by the one peer entitled to
        /// speak for it, so every machine agrees where it landed. An unowned object is the
        /// reporter's to drop; an owned one belongs to its owner.
        ///
        /// This runs on a tick rather than at spawn because a carrier component subscribes
        /// to Releasing in its own spawn, and which component spawns first is decided by
        /// creator-editable component order. It waits for Resting so a drop cannot be
        /// published from a pose that is still easing.
        /// </summary>
        private void TryStartActiveDrop()
        {
            if (Net.Session == null || Entity == null) return;

            _startActiveHandled = true;

            if (!startActive || !_hasRigidbody) return;

            if (Entity.IsOwned)
            {
                if (!Entity.HasAuthority) return;
            }
            else
            {
                if (!Net.Session.IsReporter) return;
                if (!Entity.RequestAuthority()) return;
            }

            ReleaseToWorld(Vector3.zero, Vector3.zero);
        }

        /// <summary>
        /// Puts a driven sample on the wire at the send rate. Runs on the driving peer
        /// only, and reads the pose after the previous physics step, which is where the
        /// object actually is rather than where it was asked to go.
        ///
        /// The sequence number is allocated here rather than by the caller, for the reason
        /// the session allocates the avatar packet's: it identifies one sample in one
        /// object's stream, and a caller that could be handed one can be handed the wrong
        /// one. It is per object rather than per peer, because two objects driven at once
        /// are two independent streams.
        /// </summary>
        private void PublishDrivenSample()
        {
            if (_drivenMessage == null) return;

            _drivenSendTimer -= Time.fixedDeltaTime;
            if (_drivenSendTimer > 0f) return;

            _drivenSendTimer += DrivenSendInterval;
            if (_drivenSendTimer < 0f) _drivenSendTimer = 0f;

            unchecked { _drivenSequence++; }

            _drivenMessage.SendToAll(transform.position, transform.rotation, _drivenSequence);
        }

        protected override void OnNetTick()
        {
            if (_releaseAfterDeparture)
            {
                TryReleaseAfterDeparture();
                return;
            }

            if (!_startActiveHandled && _state == MotionState.Resting) TryStartActiveDrop();

            if (_state == MotionState.Driven)
            {
                if (HasAuthority) PublishDrivenSample();
                return;
            }

            if (_state != MotionState.Free) return;
            if (!HasAuthority || _settleSent) return;
            if (!_hasRigidbody) return;

            bool slow = _rb.linearVelocity.magnitude < restSpeedThreshold
                     && _rb.angularVelocity.magnitude < restSpeedThreshold;

            if (!slow)
            {
                _restTimer = 0f;
                return;
            }

            _restTimer += Time.fixedDeltaTime;
            if (_restTimer < restHoldTime) return;

            Vector3 restPosition = transform.position;
            Quaternion restRotation = transform.rotation;

            _settleSent = true;
            SetState(MotionState.Resting);

            CameToRest?.Invoke(this, restPosition, restRotation);
            PublishRest(restPosition, restRotation);
        }

        protected override void OnNetRender()
        {
            if (_state == MotionState.Settling)
            {
                _settleElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_settleElapsed / SettleDuration);
                transform.SetPositionAndRotation(
                    Vector3.Lerp(_settleFrom, _settleTo, t),
                    Quaternion.Slerp(_settleFromRotation, _settleToRotation, t)
                );
                if (t >= 1f) SetState(MotionState.Resting);
                return;
            }

            if (_state == MotionState.Driven && !HasAuthority && _hasDrivenSample)
                FollowDrivenSample();
        }

        /// <summary>
        /// Closes the gap to the latest driven sample rather than writing it directly.
        /// Samples arrive twenty times a second and frames are drawn sixty, so a direct
        /// write is three identical frames and then a jump.
        /// </summary>
        private void FollowDrivenSample()
        {
            Vector3 position = transform.position;

            if ((_drivenPosition - position).sqrMagnitude > DrivenSnapDistance * DrivenSnapDistance)
            {
                transform.SetPositionAndRotation(_drivenPosition, _drivenRotation);
                return;
            }

            float t = 1f - Mathf.Exp(-DrivenFollowRate * Time.deltaTime);

            transform.SetPositionAndRotation(
                Vector3.Lerp(position, _drivenPosition, t),
                Quaternion.Slerp(transform.rotation, _drivenRotation, t)
            );
        }
    }
}