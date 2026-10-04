using U3D.Net;
using UnityEngine;

/// <summary>
/// Another player's body, played back from the packets they sent.
///
/// Arrivals are held for 100 ms before they are shown, and the pose on screen is
/// interpolated between the two held packets that bracket that moment. The delay is
/// what buys smooth movement out of a stream that arrives unevenly: with it, a late
/// packet is still early enough, and without it every gap in delivery is a stutter.
/// Nothing is ever extrapolated forward, which is why the packet carries no velocity.
///
/// Two streams, one timeline. The avatar packet carries the body twenty times a second;
/// a VR player's head and hands arrive on their own tag at the same rate, with their own
/// counter. Both are resolved against one playback moment, so a hand never belongs to a
/// different instant than the head it is measured from.
///
/// Lives on the remote avatar rather than on the session, so its lifetime is the
/// avatar's: a player leaving destroys their avatar and this goes with it. A buffer
/// held per peer on the session would be a second record to keep in step with the
/// peer list, and would have to answer for itself when a player left mid-playback.
///
/// Game-side rather than in the seam, because it drives the animator. The session reaches
/// it through INetAvatarPlayback, which is what lets the seam deliver a packet without
/// naming anything outside itself.
///
/// The clock is this machine's own throughout and is never compared against another
/// machine's, so G48 is untouched.
/// </summary>
[DisallowMultipleComponent]
// Declared rather than assumed. A NetComponent whose GameObject carries no NetEntity binds
// nothing, so it receives no messages and reports no error — the silent-discard shape reached
// through Add Component. F40.
[RequireComponent(typeof(NetEntity))]
public class NetAvatarPlayback : NetComponent, INetAvatarPlayback
{
    /// <summary>
    /// How far behind live the body is shown, in seconds. G18's value.
    /// </summary>
    public const float BufferDelay = 0.1f;

    /// <summary>
    /// How many arrivals are held. At twenty a second, sixteen is eight hundred
    /// milliseconds of history, which is well past the delay and leaves room for a
    /// burst arriving out of order. The oldest is dropped when it fills.
    /// </summary>
    private const int Capacity = 16;

    /// <summary>
    /// How long after the newest VR pose arrived the arms keep being driven from it.
    /// Three lost samples at twenty a second. Past that the weight falls and the animator
    /// takes the arms back, which is what a player whose pose stream stops should look
    /// like rather than arms held wherever they last were.
    ///
    /// A constant rather than a creator setting: nothing a creator knows bears on it.
    /// </summary>
    private const float PoseStaleAfter = 0.25f;

    private readonly NetAvatarSample[] _samples = new NetAvatarSample[Capacity];
    private int _count;

    private ushort _newestSequence;
    private bool _hasAny;
    private bool _hasPose;
    private U3DNetworkedAnimator _animator;
    private U3DAvatarManager _avatarManager;

    /// <summary>
    /// One arrival of a VR player's head and hands. Its own buffer beside the body's
    /// rather than a field on the body sample: the two streams have their own counters and
    /// their own arrival times, and a VR peer's pose stops arriving the moment they leave
    /// VR while their body carries on.
    /// </summary>
    private readonly struct PoseSample
    {
        public readonly ushort Sequence;
        public readonly Quaternion HeadRotation;
        public readonly Vector3 LeftOffset;
        public readonly Quaternion LeftRotation;
        public readonly Vector3 RightOffset;
        public readonly Quaternion RightRotation;
        public readonly float ReceivedAt;

        public PoseSample(ushort sequence, Quaternion headRotation,
            Vector3 leftOffset, Quaternion leftRotation,
            Vector3 rightOffset, Quaternion rightRotation, float receivedAt)
        {
            Sequence = sequence;
            HeadRotation = headRotation;
            LeftOffset = leftOffset;
            LeftRotation = leftRotation;
            RightOffset = rightOffset;
            RightRotation = rightRotation;
            ReceivedAt = receivedAt;
        }
    }

    private readonly PoseSample[] _poseSamples = new PoseSample[Capacity];
    private int _poseCount;
    private ushort _newestPoseSequence;
    private bool _hasAnyPose;
    private float _newestPoseReceivedAt;
    private U3D.U3DAvatarIK _avatarIK;

    /// <summary>
    /// Whether a packet has ever arrived for this player. Until one has, there is
    /// nothing to say about where their body is, and the avatar manager keeps it out
    /// of sight rather than showing it wherever it happened to be created.
    /// </summary>
    public bool HasSample => _hasAny;

    /// <summary>The pose being shown right now, interpolated. Meaningless before the
    /// first packet, which HasSample is how a caller knows.</summary>
    public Vector3 Position { get; private set; }
    public float BodyYaw { get; private set; }

    /// <summary>Where this player's head is aimed, in degrees, negative down. Dead weight:
    /// carried in the packet forever under the no-growth rule and read by nothing. Head
    /// orientation arrives as a full rotation on its own tag.</summary>
    public float HeadPitch { get; private set; }

    /// <summary>
    /// The movement flags belonging to the moment being shown, rather than to the
    /// moment the packet arrived, so an animation change lands at the position where
    /// it actually happened.
    /// </summary>
    public ushort Flags { get; private set; }

    /// <summary>The rideable and steerable this player is on, or 0. Carried and not
    /// yet consumed: the parenting that uses them is its own piece.</summary>
    public ushort Rideable { get; private set; }
    public ushort Steerable { get; private set; }

    public bool IsMoving => (Flags & NetAvatar.FlagMoving) != 0;
    public bool IsSprinting => (Flags & NetAvatar.FlagSprinting) != 0;
    public bool IsCrouching => (Flags & NetAvatar.FlagCrouching) != 0;
    public bool IsFlying => (Flags & NetAvatar.FlagFlying) != 0;
    public bool IsJumping => (Flags & NetAvatar.FlagJumping) != 0;
    public bool IsSwimming => (Flags & NetAvatar.FlagSwimming) != 0;
    public bool IsClimbing => (Flags & NetAvatar.FlagClimbing) != 0;
    public bool IsSeated => (Flags & NetAvatar.FlagSeated) != 0;
    public bool IsGrounded => (Flags & NetAvatar.FlagGrounded) != 0;
    public bool IsPushing => (Flags & NetAvatar.FlagPushing) != 0;
    public bool IsPulling => (Flags & NetAvatar.FlagPulling) != 0;

    /// <summary>
    /// Whether this player says they are in VR. Read from the played-back moment like
    /// every other flag, so it belongs to the same instant as the body and the pose it
    /// is used alongside.
    /// </summary>
    public bool IsInVR => (Flags & NetAvatar.FlagInVR) != 0;

    /// <summary>
    /// Registered here rather than in a spawn callback because NetEntity walks its
    /// component list for duplicate keys at spawn, and a key registered after that
    /// walk would never be checked.
    /// </summary>
    private void Awake()
    {
        RegisterMessage(NetKeys.AvatarPose, ReceivePose);
    }

    /// <summary>
    /// Takes one arrival. Out-of-order packets are discarded rather than sorted into
    /// place: the wrapping counter is what identifies them, and a packet that took a
    /// longer road than one already held describes a moment that has been shown.
    /// </summary>
    public void Receive(ushort sequence, in NetAvatarState state)
    {
        if (_hasAny && !NetAvatar.IsNewer(sequence, _newestSequence)) return;

        _newestSequence = sequence;
        _hasAny = true;

        if (_count == Capacity)
        {
            for (int i = 1; i < Capacity; i++)
                _samples[i - 1] = _samples[i];
            _count = Capacity - 1;
        }

        _samples[_count++] = new NetAvatarSample(sequence, state, Time.time);
    }

    /// <summary>
    /// Takes one arrival of this player's head and hands.
    ///
    /// Refused unless the sender owns this body, which every receiving peer enforces for
    /// itself rather than trusting the claim — the same posture the worn-attachment set
    /// takes and the same one the authority rules take.
    ///
    /// Its own counter, never compared against the body's: the two streams arrive
    /// independently, and an overtaken sample would throw a hand backwards.
    /// </summary>
    private void ReceivePose(PeerId sender, object[] args)
    {
        if (args == null || args.Length < 6) return;
        if (Entity == null || sender != Entity.Owner) return;

        if (!(args[0] is Quaternion headRotation)) return;
        if (!(args[1] is Vector3 leftOffset)) return;
        if (!(args[2] is Quaternion leftRotation)) return;
        if (!(args[3] is Vector3 rightOffset)) return;
        if (!(args[4] is Quaternion rightRotation)) return;
        if (!(args[5] is ushort sequence)) return;

        if (_hasAnyPose && !NetAvatar.IsNewer(sequence, _newestPoseSequence)) return;

        _newestPoseSequence = sequence;
        _hasAnyPose = true;
        _newestPoseReceivedAt = Time.time;

        if (_poseCount == Capacity)
        {
            for (int i = 1; i < Capacity; i++)
                _poseSamples[i - 1] = _poseSamples[i];
            _poseCount = Capacity - 1;
        }

        _poseSamples[_poseCount++] = new PoseSample(sequence, headRotation,
            leftOffset, leftRotation, rightOffset, rightRotation, Time.time);
    }

    /// <summary>
    /// Advances playback and writes the body's pose. Runs in the session's render pass
    /// at execution order 110, after the avatar manager has settled the avatar root at
    /// 101 and after the Animator has posed the rig, so a held object computed against
    /// a hand bone this frame is computed against a hand that has already arrived.
    /// </summary>
    protected override void OnNetRender()
    {
        if (!_hasAny) return;

        float target = Time.time - BufferDelay;

        Resolve(target, out Vector3 position, out float yaw, out float pitch, out NetAvatarState carrier);

        Vector3 previous = Position;
        bool hadPose = _hasPose;

        Position = position;
        BodyYaw = yaw;
        HeadPitch = pitch;
        Flags = carrier.Flags;
        Rideable = carrier.Rideable;
        Steerable = carrier.Steerable;
        _hasPose = true;

        transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

        DriveAnimator(hadPose ? position - previous : Vector3.zero);

        DriveVRPose(target);

        Prune(target);
    }

    /// <summary>
    /// Hands the played-back state to the animator. The direction of travel is where
    /// the body actually moved since the last frame, expressed in its own facing, which
    /// is what the blend tree wants and what makes a strafe read as a strafe. Derived
    /// rather than sent, for the reason the speed is: it cannot disagree with the
    /// motion on screen.
    ///
    /// A body driving a steerable that poses its driver standing holds a standing idle
    /// instead of a walk, which is the same filter the local player gets from the
    /// controller's SuppressLocomotion. Derived from the steerable the packet named rather
    /// than carried, so no flag bit is spent on it. The avatar manager resolves the
    /// steerable at execution order 101 and this runs in the render pass at 110, so the
    /// answer is this frame's.
    /// </summary>
    private void DriveAnimator(Vector3 travelled)
    {
        if (_animator == null)
        {
            _animator = GetComponent<U3DNetworkedAnimator>();
            if (_animator == null) return;
        }

        Vector2 direction = Vector2.zero;
        if (travelled.sqrMagnitude > 0.000001f)
        {
            Vector3 local = transform.InverseTransformDirection(travelled.normalized);
            direction = new Vector2(local.x, local.z);
        }

        bool isMoving = IsMoving;
        float speed = U3DNetworkedAnimator.DeriveRemoteSpeed(IsMoving, IsSprinting, IsCrouching);

        if (_avatarManager == null)
            _avatarManager = GetComponent<U3DAvatarManager>();

        if (_avatarManager != null && _avatarManager.RemoteSuppressLocomotion)
        {
            isMoving = false;
            speed = 0f;
            direction = Vector2.zero;
        }

        _animator.ApplyRemoteState(
            isMoving, IsCrouching, IsFlying, IsSwimming,
            IsGrounded, IsClimbing, IsJumping, IsSeated,
            IsPushing && isMoving, IsPulling && isMoving,
            speed, direction);
    }

    /// <summary>
    /// Hands the played-back head and hands to the IK component, which owns the head bone
    /// and both arms for a remote body exactly as it does for the local player.
    ///
    /// The VR state is pushed every frame whether or not a pose resolved, because that is
    /// what lowers the arms when a player leaves VR: they stop sending poses and say so on
    /// the very next avatar packet, so the flag answers immediately where waiting for the
    /// pose stream to go stale would take a quarter of a second.
    ///
    /// The IK runs at execution order 103 and this runs in the render pass at 110, so a
    /// pose written here is consumed on the following frame. Every value involved is
    /// measured against the player root, so the root moving in between carries the solved
    /// bones with it.
    /// </summary>
    private void DriveVRPose(float target)
    {
        if (_avatarManager == null)
            _avatarManager = GetComponent<U3DAvatarManager>();

        if (_avatarIK == null)
        {
            if (_avatarManager == null) return;
            _avatarIK = _avatarManager.AvatarIK;
            if (_avatarIK == null) return;
        }

        bool fresh = _hasAnyPose && (Time.time - _newestPoseReceivedAt) <= PoseStaleAfter;
        _avatarIK.SetRemoteVRState(IsInVR, fresh);

        if (_poseCount == 0) return;

        ResolvePose(target,
            out Quaternion headRotation,
            out Vector3 leftOffset, out Quaternion leftRotation,
            out Vector3 rightOffset, out Quaternion rightRotation);

        _avatarIK.ReceivePose(headRotation, leftOffset, leftRotation, rightOffset, rightRotation);

        PrunePoses(target);
    }

    /// <summary>
    /// The pose at one moment. Between two held packets it is interpolated; before the
    /// oldest one it is that packet, which is the ordinary state for the first hundred
    /// milliseconds after a player appears; past the newest it is the newest, held
    /// still. Holding the last pose is deliberate rather than a gap — what a body does
    /// when its player goes quiet is G19's timeline, and this piece does not build it.
    /// </summary>
    private void Resolve(float target, out Vector3 position, out float yaw, out float pitch, out NetAvatarState carrier)
    {
        NetAvatarSample newest = _samples[_count - 1];

        if (_count == 1 || target >= newest.ReceivedAt)
        {
            position = newest.State.Position;
            yaw = newest.State.BodyYaw;
            pitch = newest.State.HeadPitch;
            carrier = newest.State;
            return;
        }

        NetAvatarSample oldest = _samples[0];
        if (target <= oldest.ReceivedAt)
        {
            position = oldest.State.Position;
            yaw = oldest.State.BodyYaw;
            pitch = oldest.State.HeadPitch;
            carrier = oldest.State;
            return;
        }

        for (int i = 1; i < _count; i++)
        {
            NetAvatarSample after = _samples[i];
            if (after.ReceivedAt < target) continue;

            NetAvatarSample before = _samples[i - 1];
            float span = after.ReceivedAt - before.ReceivedAt;
            float t = span > 0.0001f ? (target - before.ReceivedAt) / span : 1f;

            position = Vector3.Lerp(before.State.Position, after.State.Position, t);
            yaw = Mathf.LerpAngle(before.State.BodyYaw, after.State.BodyYaw, t);
            pitch = Mathf.Lerp(before.State.HeadPitch, after.State.HeadPitch, t);

            // Flags belong to a moment rather than to a span, so they are the moment
            // being shown rather than a blend of two. Halfway between a walk and a
            // stop is one or the other, not both.
            carrier = t < 1f ? before.State : after.State;
            return;
        }

        position = newest.State.Position;
        yaw = newest.State.BodyYaw;
        pitch = newest.State.HeadPitch;
        carrier = newest.State;
    }

    /// <summary>
    /// The head and hands at one moment, resolved against the same playback target the
    /// body uses so the two cannot come from different instants. Positions interpolate
    /// linearly and rotations by shortest arc; before the oldest and past the newest it
    /// holds the end sample, exactly as the body does.
    /// </summary>
    private void ResolvePose(float target,
        out Quaternion headRotation,
        out Vector3 leftOffset, out Quaternion leftRotation,
        out Vector3 rightOffset, out Quaternion rightRotation)
    {
        PoseSample newest = _poseSamples[_poseCount - 1];

        if (_poseCount == 1 || target >= newest.ReceivedAt)
        {
            headRotation = newest.HeadRotation;
            leftOffset = newest.LeftOffset;
            leftRotation = newest.LeftRotation;
            rightOffset = newest.RightOffset;
            rightRotation = newest.RightRotation;
            return;
        }

        PoseSample oldest = _poseSamples[0];
        if (target <= oldest.ReceivedAt)
        {
            headRotation = oldest.HeadRotation;
            leftOffset = oldest.LeftOffset;
            leftRotation = oldest.LeftRotation;
            rightOffset = oldest.RightOffset;
            rightRotation = oldest.RightRotation;
            return;
        }

        for (int i = 1; i < _poseCount; i++)
        {
            PoseSample after = _poseSamples[i];
            if (after.ReceivedAt < target) continue;

            PoseSample before = _poseSamples[i - 1];
            float span = after.ReceivedAt - before.ReceivedAt;
            float t = span > 0.0001f ? (target - before.ReceivedAt) / span : 1f;

            headRotation = Quaternion.Slerp(before.HeadRotation, after.HeadRotation, t);
            leftOffset = Vector3.Lerp(before.LeftOffset, after.LeftOffset, t);
            leftRotation = Quaternion.Slerp(before.LeftRotation, after.LeftRotation, t);
            rightOffset = Vector3.Lerp(before.RightOffset, after.RightOffset, t);
            rightRotation = Quaternion.Slerp(before.RightRotation, after.RightRotation, t);
            return;
        }

        headRotation = newest.HeadRotation;
        leftOffset = newest.LeftOffset;
        leftRotation = newest.LeftRotation;
        rightOffset = newest.RightOffset;
        rightRotation = newest.RightRotation;
    }

    /// <summary>
    /// Drops packets old enough that nothing will interpolate from them again. One is
    /// always kept behind the playback moment, because that is the packet the current
    /// pose is being interpolated away from.
    /// </summary>
    private void Prune(float target)
    {
        int drop = 0;
        while (drop + 1 < _count && _samples[drop + 1].ReceivedAt < target)
            drop++;

        if (drop == 0) return;

        for (int i = drop; i < _count; i++)
            _samples[i - drop] = _samples[i];
        _count -= drop;
    }

    /// <summary>
    /// The same rule for the pose buffer, run separately because the two streams fill at
    /// their own rates and a VR peer's stops entirely when they leave VR.
    /// </summary>
    private void PrunePoses(float target)
    {
        int drop = 0;
        while (drop + 1 < _poseCount && _poseSamples[drop + 1].ReceivedAt < target)
            drop++;

        if (drop == 0) return;

        for (int i = drop; i < _poseCount; i++)
            _poseSamples[i - drop] = _poseSamples[i];
        _poseCount -= drop;
    }
}