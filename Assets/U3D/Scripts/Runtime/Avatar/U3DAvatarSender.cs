using U3D.Net;
using UnityEngine;

/// <summary>
/// Sends this player's body to everyone else, twenty times a second, and their head and
/// hands at the same rate while they are in VR.
///
/// Lives on the local player prefab and reads the controller directly. Every value it
/// carries is one something else already holds for its own reasons, so nothing here
/// computes anything about the player — it reads, packs and hands over. The VR pose is
/// read straight off the IK component, which stores exactly the five values that travel.
///
/// Twenty a second rather than every frame, and rather than every fixed step. A body
/// moving is not more interesting at a hundred and twenty frames a second than at twenty,
/// and the receiving side interpolates between what arrives. What that rate costs is four
/// hundred and eighty bytes a second per player in each direction, and roughly eight
/// hundred and twenty while that player is in VR.
///
/// Two independent intervals rather than one gate. They happen to be equal today, and a
/// single check would make one send swallow the other on every tick the other did not
/// fire — working by coincidence and breaking the moment either rate changed.
///
/// A NetComponent rather than a plain MonoBehaviour, so the send runs in the session's
/// tick pass after the controller has moved at execution order 100 and the values are this
/// step's rather than last step's.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(U3DPlayerController))]
// U3DPlayerController requires CharacterController and PlayerInput, neither of which reaches
// NetEntity, so this is declared here rather than inherited. F40.
[RequireComponent(typeof(NetEntity))]
public class U3DAvatarSender : NetComponent
{
    /// <summary>Twenty a second, per G16. Uniform for every player, with no rate that
    /// falls off with distance — that was considered and left out of version one.</summary>
    private const float SendInterval = 1f / 20f;

    /// <summary>Twenty a second, matching the body. Nothing on the wire names a rate, so
    /// this may change later; the receiver's hundred-millisecond buffer holds about two
    /// samples at this one.</summary>
    private const float PoseSendInterval = 1f / 20f;

    private U3DPlayerController _controller;
    private U3DAvatarManager _avatarManager;
    private float _nextSendAt;

    private NetMessage _poseMessage;
    private readonly object[] _poseScratch = new object[6];
    private float _nextPoseSendAt;
    private ushort _poseSequence;

    void Awake()
    {
        _controller = GetComponent<U3DPlayerController>();
        _avatarManager = GetComponent<U3DAvatarManager>();

        // Registered here rather than in a spawn callback because NetEntity walks its
        // component list for duplicate keys at spawn, and a key registered after that
        // walk would never be checked.
        _poseMessage = RegisterMessage(NetKeys.AvatarPose, IgnoreOwnEcho);
    }

    /// <summary>
    /// A broadcast is delivered locally and synchronously inside Send, so this machine
    /// receives every pose it sends. There is nothing to do with it: the local player is
    /// simulated rather than played back, and this prefab carries no playback buffer. The
    /// handler exists because a message can only be sent through one, and it is empty
    /// deliberately rather than unfinished.
    /// </summary>
    private void IgnoreOwnEcho(PeerId sender, object[] args) { }

    /// <summary>
    /// Runs in the session's tick pass, at the top of a fixed step. The intervals are this
    /// machine's own clock and are never compared against another player's.
    /// </summary>
    protected override void OnNetTick()
    {
        if (_controller == null) return;
        if (Net.Session == null || !Net.Session.IsConnected) return;

        // A player alone in a room composes nothing. The send would reach nobody, and the
        // room of one is the common case for the whole of a solo experience.
        if (Net.Session.Peers.Count < 2) return;

        if (Time.time >= _nextSendAt)
        {
            _nextSendAt = Time.time + SendInterval;
            Net.Session.PublishAvatarState(Compose());
        }

        if (Time.time >= _nextPoseSendAt)
        {
            _nextPoseSendAt = Time.time + PoseSendInterval;
            PublishPose();
        }
    }

    /// <summary>
    /// This player's body as it stands right now.
    ///
    /// The steerable reference is this player's steerable's authored scene ID, or zero when
    /// they are not driving one. Every peer has the same scene, so the number is enough for
    /// the far side to find the same steerable and read the costume and pose from it — none
    /// of that has to travel.
    ///
    /// The rideable reference is still sent as zero. Nothing consumes it: a rider's own
    /// position already includes wherever the platform carried them, so the far side needs
    /// no reference to place them.
    ///
    /// The head pitch is dead weight. It is written, sent and decoded, and nothing reads it
    /// — head orientation travels as a full rotation on its own tag. The field stays in the
    /// layout forever, because removing one is as forbidden as adding one.
    /// </summary>
    private NetAvatarState Compose()
    {
        return new NetAvatarState(
            transform.position,
            transform.eulerAngles.y,
            _controller.CameraPitch,
            ComposeFlags(),
            0,
            ComposeSteerable());
    }

    /// <summary>
    /// This player's head and both wrists, read off the IK component that already stores
    /// them for its own solve. Nothing is computed here: the offsets are stored as offsets,
    /// so the value that travels is the value the local player is posed from.
    ///
    /// Sent only while in VR, so a desktop-only room pays nothing, and only once both hands
    /// have been written. An unwritten hand is an offset of zero, which composes to the head
    /// bone rather than to a hand — well-formed on the wire and wrong on every screen, with
    /// nothing able to tell it from a real pose. B99.
    ///
    /// The sequence number is cast to ushort explicitly. Arithmetic on a ushort in C#
    /// produces an int, which the codec's field test refuses, and the refusal is a console
    /// error a deployed build has nowhere to show.
    /// </summary>
    private void PublishPose()
    {
        if (_poseMessage == null) return;
        if (!_controller.IsInVRMode) return;
        if (_avatarManager == null) return;

        U3D.U3DAvatarIK ik = _avatarManager.AvatarIK;
        if (ik == null || !ik.HasHandPose) return;

        _poseScratch[0] = ik.HeadLocalRotation;
        _poseScratch[1] = ik.LeftHandOffset;
        _poseScratch[2] = ik.LeftHandLocalRotation;
        _poseScratch[3] = ik.RightHandOffset;
        _poseScratch[4] = ik.RightHandLocalRotation;
        _poseScratch[5] = (ushort)_poseSequence;

        _poseMessage.SendToAll(_poseScratch);

        _poseSequence++;
    }

    /// <summary>
    /// The authored scene ID of the steerable this player is driving, or zero for none.
    ///
    /// A steerable with no entity reads as zero, which the far side treats as "not driving".
    /// That is the right failure: the driver's body still travels normally and simply arrives
    /// without its costume, rather than the packet naming a number nobody can resolve.
    /// </summary>
    private ushort ComposeSteerable()
    {
        U3D.U3DSteerable steerable = _controller.CurrentSteerable;
        return steerable != null ? steerable.NetId : (ushort)0;
    }

    /// <summary>
    /// The thirteen movement flags. Every one is a value the controller already publishes,
    /// and each travels in every packet rather than as an event, so a lost packet corrects
    /// itself on the next one and a player arriving needs no catch-up.
    ///
    /// Two things deliberately absent. Speed and movement direction are worked out by the
    /// receiver from the sprint and crouch flags and from where the body actually moved,
    /// which cannot disagree with what is on screen the way a sent value can. And
    /// suppress-locomotion is worked out from the steerable rather than carried, so no bit
    /// is spent on it.
    /// </summary>
    private ushort ComposeFlags()
    {
        ushort flags = 0;

        if (_controller.IsMoving) flags |= NetAvatar.FlagMoving;
        if (_controller.IsSprinting) flags |= NetAvatar.FlagSprinting;
        if (_controller.IsCrouching) flags |= NetAvatar.FlagCrouching;
        if (_controller.IsFlying) flags |= NetAvatar.FlagFlying;
        if (_controller.IsJumping) flags |= NetAvatar.FlagJumping;
        if (_controller.IsSwimming) flags |= NetAvatar.FlagSwimming;
        if (_controller.IsClimbing) flags |= NetAvatar.FlagClimbing;
        if (_controller.IsSeated) flags |= NetAvatar.FlagSeated;
        if (_controller.IsInVRMode) flags |= NetAvatar.FlagInVR;
        if (_controller.IsFirstPerson) flags |= NetAvatar.FlagFirstPerson;
        if (_controller.IsGrounded) flags |= NetAvatar.FlagGrounded;
        if (_controller.IsPushing) flags |= NetAvatar.FlagPushing;
        if (_controller.IsPulling) flags |= NetAvatar.FlagPulling;

        return flags;
    }
}