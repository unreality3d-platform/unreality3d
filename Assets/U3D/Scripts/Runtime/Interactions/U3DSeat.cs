using UnityEngine;
using UnityEngine.Events;
using U3D.Net;
using U3DNet = U3D.Net.Net;

namespace U3D
{
    /// <summary>
    /// A seat the local player can sit in. Sitting anchors the player to the seat's
    /// hips bone when the avatar is humanoid, and to the seat transform otherwise.
    ///
    /// Occupancy is arbitrated by an authority claim on the seat's own entity, and
    /// vacating is derived rather than sent. Sitting claims; standing sets the seated
    /// flag false, which every other machine already receives twenty times a second in
    /// the avatar packet. So a seat asks one question when somebody tries to use it —
    /// does its owner still report seated — and nothing about a seat goes on the wire
    /// beyond the claim itself.
    ///
    /// Deliberately not a NetComponent. The entity's component list is gathered with
    /// GetComponentsInChildren, which walks straight through a nested entity, so a seat
    /// bolted to a rideable would be collected by the platform's entity as well as its
    /// own and would end up claiming against the platform. G293.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class U3DSeat : MonoBehaviour, IU3DInteractable
    {
        [Header("Seat Configuration")]
        [Tooltip("How far in front of the seat the player is placed when standing up.")]
        [SerializeField] private float standOffsetForward = 0.6f;

        [Header("Events")]
        [Tooltip("Called when the local player sits down.")]
        public UnityEvent OnSit;

        [Tooltip("Called when the local player stands up.")]
        public UnityEvent OnStand;

        [Tooltip("Called when this seat becomes occupied.")]
        public UnityEvent OnOccupied;

        [Tooltip("Called when this seat is vacated.")]
        public UnityEvent OnVacated;

        public static U3DSeat CurrentlyOccupied { get; private set; }

        public bool IsOccupied => CurrentlyOccupied == this;

        private U3DPlayerController _localPlayer;
        private Transform _seatedHips;
        private bool _standArmed;

        // This seat's own entity, never an ancestor's. GetComponent rather than
        // GetComponentInParent: a seat childed to a rideable would otherwise resolve to
        // the platform and every sit would claim the platform instead.
        private NetEntity _entity;

        private void Awake()
        {
            _entity = GetComponent<NetEntity>();
        }

        private void OnEnable()
        {
            if (_entity != null)
                _entity.AuthorityChanged += HandleAuthorityChanged;
        }

        private void Start()
        {
            _localPlayer = U3DPlayerController.FindLocalPlayer();
        }

        // Covers every way a seated session can end without Stand(): the seat destroyed,
        // disabled, or torn down with the scene. Runs before OnDestroy on destruction, so
        // this one method is the whole teardown. B24.
        private void OnDisable()
        {
            if (_entity != null)
                _entity.AuthorityChanged -= HandleAuthorityChanged;

            if (CurrentlyOccupied == this)
                Stand();
        }

        // ==================== IU3DInteractable ====================

        public bool CanInteract()
        {
            if (CurrentlyOccupied != null) return false;
            if (IsOccupiedByAnotherPlayer()) return false;
            return true;
        }

        public void OnInteract()
        {
            if (!CanInteract()) return;

            if (_localPlayer == null)
                _localPlayer = U3DPlayerController.FindLocalPlayer();
            if (_localPlayer == null) return;

            Sit(_localPlayer);
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }
        public string GetInteractionPrompt() => "Sit";

        // ==================== Occupancy ====================

        /// <summary>
        /// Whether somebody else is in this seat. Ownership alone does not answer it — a
        /// player who stood up leaves their claim behind, because nothing is sent when
        /// they get up. What settles it is whether the owner still reports seated, which
        /// is a value already arriving for every remote player and already held locally.
        ///
        /// Evaluated when somebody presses Interact rather than every frame, so an
        /// unoccupied scene costs nothing.
        ///
        /// A player who stands from a seat and takes a steerable in the same moment still
        /// reports seated, so their old seat stays held until they leave the steerable.
        /// Accepted: brief, and closing it would cost a field on a packet that travels
        /// twenty times a second.
        /// </summary>
        private bool IsOccupiedByAnotherPlayer()
        {
            if (_entity == null || !_entity.IsLive) return false;

            PeerId owner = _entity.Owner;
            if (!owner.IsValid) return false;

            var session = U3DNet.Session;
            if (session == null) return false;

            if (owner == session.LocalPeer)
                return _localPlayer != null && _localPlayer.IsSeated;

            return IsRemotePeerSeated(session, owner);
        }

        /// <summary>
        /// Reads the seated flag off a remote player's played-back body. Their avatar is
        /// the first slot of their personal block, so the peer ID gives the entity with no
        /// lookup table to keep in step. Anything that does not resolve — a peer whose
        /// body has not been built, or one no packet has arrived for — reads as not
        /// seated, which frees the seat rather than stranding it.
        /// </summary>
        private bool IsRemotePeerSeated(INetSession session, PeerId owner)
        {
            int block = owner.BlockIndex;
            if (block < 0) return false;

            ushort avatarId = NetEntity.PersonalBlockBase(block);
            if (avatarId == 0) return false;

            if (!session.TryGetEntity(avatarId, out NetEntity avatar)) return false;
            if (avatar == null) return false;

            var playback = avatar.GetComponent<NetAvatarPlayback>();
            if (playback == null || !playback.HasSample) return false;

            return playback.IsSeated;
        }

        /// <summary>
        /// Two people pressing Interact in the same instant both sit on their own screens,
        /// and the claim contest resolves to the lower peer ID on every machine. The loser
        /// learns it here and gets up. An unowned correction is not an eviction — nobody
        /// is taking the seat, and the player's own packets already say they are in it.
        /// </summary>
        private void HandleAuthorityChanged(NetEntity entity, PeerId previous, PeerId current)
        {
            if (CurrentlyOccupied != this) return;
            if (!current.IsValid) return;

            var session = U3DNet.Session;
            if (session == null) return;
            if (current == session.LocalPeer) return;

            Stand();
        }

        // ==================== Sit / Stand ====================

        private void Sit(U3DPlayerController player)
        {
            CurrentlyOccupied = this;
            _localPlayer = player;

            // Applies here immediately and reaches everyone else as a claim. A seat with no
            // entity, or one in a session that has not opened, simply sits locally.
            if (_entity != null)
                _entity.RequestAuthority();

            _seatedHips = ResolveHipsBone(player);

            // Disarmed until movement input has returned to neutral. Sitting is triggered
            // by a key press that often arrives while a movement key is still down, and the
            // stand check would otherwise fire on the very next frame. B67.
            _standArmed = false;

            player.SuspendCollision(this);

            Vector3 flatForward = SeatFlatForward();
            player.SetRotation(Quaternion.LookRotation(flatForward, Vector3.up).eulerAngles.y);

            player.SetSeatedState(true);

            if (_seatedHips == null)
                player.transform.position = transform.position;

            OnSit?.Invoke();
            OnOccupied?.Invoke();
        }

        public void Stand()
        {
            if (CurrentlyOccupied != this) return;

            // The static clears whether or not a player is still there to release. A player
            // destroyed while seated used to strand it, and CanInteract reads it, so every
            // seat in the scene refused interaction for the rest of the session. B74.
            //
            // Ownership is deliberately not given up here. Clearing the seated flag below
            // is the vacate, and every machine reads it from the packet already arriving.
            // A claim left behind on an empty seat refuses nobody.
            if (_localPlayer != null)
            {
                Vector3 flatForward = SeatFlatForward();

                // Collision is already suspended by Sit, so the position writes directly.
                _localPlayer.transform.position += flatForward * standOffsetForward;

                _localPlayer.SetRotation(Quaternion.LookRotation(flatForward, Vector3.up).eulerAngles.y);
                _localPlayer.SetSeatedState(false);

                // Releases this seat's hold only. A player also riding a platform keeps the
                // platform's hold and stays mounted rather than being handed collision back
                // while still parented to it. B62.
                _localPlayer.ResumeCollision(this);
            }

            CurrentlyOccupied = null;
            _seatedHips = null;
            _standArmed = false;

            OnStand?.Invoke();
            OnVacated?.Invoke();
        }

        // ==================== Hips anchoring ====================

        private Transform ResolveHipsBone(U3DPlayerController player)
        {
            var avatarManager = player.GetComponent<U3DAvatarManager>();
            if (avatarManager == null) return null;

            Animator animator = avatarManager.GetAvatarAnimator();
            if (animator == null || !animator.isHuman) return null;

            return animator.GetBoneTransform(HumanBodyBones.Hips);
        }

        private Vector3 SeatFlatForward()
        {
            Vector3 f = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (f.sqrMagnitude < 0.0001f)
                f = transform.right;
            return f.normalized;
        }

        private void LateUpdate()
        {
            if (CurrentlyOccupied != this) return;
            if (_localPlayer == null) return;

            // No hips bone means a non-humanoid avatar, which previously returned here and
            // left the player behind the moment the seat moved.
            if (_seatedHips == null)
            {
                _localPlayer.transform.position = transform.position;
                return;
            }

            Vector3 delta = transform.position - _seatedHips.position;
            if (delta.sqrMagnitude < 1e-10f) return;

            _localPlayer.transform.position += delta;
        }

        // ==================== Movement-input stand detection ====================

        private void Update()
        {
            if (CurrentlyOccupied != this) return;
            if (_localPlayer == null) return;

            bool movingNow = _localPlayer.MoveInput.magnitude > 0.1f;

            if (!_standArmed)
            {
                if (!movingNow) _standArmed = true;
                return;
            }

            if (movingNow)
                Stand();
        }

        // ==================== Gizmo ====================

        private void OnDrawGizmos()
        {
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;

            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.06f);

            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
            Gizmos.DrawLine(origin, origin + forward * 0.5f);

            Vector3 tip = origin + forward * 0.5f;
            Vector3 right = transform.right;
            Gizmos.DrawLine(tip, tip - forward * 0.15f + right * 0.1f);
            Gizmos.DrawLine(tip, tip - forward * 0.15f - right * 0.1f);

            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.5f);
            Gizmos.DrawSphere(tip, 0.03f);
        }
    }
}