using System.Collections.Generic;
using U3D;
using UnityEngine;

[DefaultExecutionOrder(101)]
public class U3DAvatarManager : MonoBehaviour
{
    [Header("Avatar Configuration")]
    [SerializeField] private GameObject avatarFBX;
    [SerializeField] private float avatarScaleMultiplier = 1f;

    [Header("Avatar Positioning")]
    [SerializeField] private Vector3 avatarOffset = Vector3.zero;
    [SerializeField] private bool followPlayerRotation = true;
    [SerializeField] private bool hideInFirstPerson = true;

    [Header("VR IK")]
    [Tooltip("XR input actions asset. Used by the auto-attached U3DAvatarIK to read VR controller poses. Should reference the same U3DInputActions asset used by the player controller.")]
    [SerializeField] private UnityEngine.InputSystem.InputActionAsset xrInputActions;

    // Core components. playerController is null on the remote avatar prefab, which carries no
    // controller — its root holds this component, U3DNetworkedAnimator, NetEntity,
    // NetAvatarPlayback and U3DPlayerAttachments, with the nametag on a child and the body
    // instantiated at runtime. Every read of playerController below is a visibility or VR
    // decision that only ever applied to the local player, so a null controller is a normal
    // state rather than an error.
    private U3DPlayerController playerController;
    // Null on the local player, which is simulated here rather than played back.
    private NetAvatarPlayback _playback;
    private GameObject avatarInstance;
    private Animator avatarAnimator;
    private Avatar avatarAsset;
    private Renderer[] avatarRenderers;
    private U3DAvatarIK avatarIK;
    private Transform _handAnchor;

    // Neutral-pose reference data, captured once at avatar initialization via
    // HumanPoseHandler. The avatar is silently posed to Unity's muscle-neutral stance
    // (the same normalized pose on every humanoid rig, regardless of whether the file
    // was exported in T-pose or A-pose), each bone's rotation relative to the avatar
    // root is recorded, and the original pose is restored — all within one frame, never
    // visible. Attachments and grabs bake their orientation against this fixed frame
    // instead of the bone's animated rotation at the attach moment, so attaching
    // mid-fly, mid-jump, or mid-swing produces the identical result as attaching at
    // idle. Also caches the elbow→wrist direction in each hand bone's local frame,
    // used to place held objects a fixed distance out from the wrist joint.
    private Quaternion[] _neutralBoneRotations;
    private bool[] _neutralBoneValid;
    private Vector3 _leftHandAnchorDir;
    private Vector3 _rightHandAnchorDir;
    private bool _leftHandAnchorValid;
    private bool _rightHandAnchorValid;

    // Renderers of cosmetic attachments riding this avatar's bones, registered by
    // U3DPlayerAttachments. Toggled alongside the body in UpdateAvatarVisibility so attachments
    // follow the avatar's own first-person / VR / third-person visibility with no special rules.
    private readonly List<Renderer> _attachmentRenderers = new List<Renderer>();

    // Subset of attachment renderers riding the head bone, registered separately by
    // U3DPlayerAttachments. For the local wearer in VR first person these render shadow-only, so a
    // face-covering piece (a costume head) can't block their own view — other players, VR third
    // person, and desktop all see it normally, and the wearer keeps its shadow. The authored shadow
    // mode is captured at registration and restored whenever suppression doesn't apply, so a piece
    // the creator shipped with shadows off stays that way.
    private struct HeadAttachmentEntry
    {
        public Renderer Renderer;
        public UnityEngine.Rendering.ShadowCastingMode OriginalMode;
    }
    private readonly List<HeadAttachmentEntry> _headAttachmentEntries = new List<HeadAttachmentEntry>();

    private U3DNetworkedAnimator networkedAnimator;
    private bool isInitialized = false;

    // ==================== Remote steerable costume ====================
    // A remote driver's costume, rebuilt here from the steerable ID their packet carries.
    // Nothing about the costume travels: the ID names a steerable in this machine's own
    // scene, and the visual prefabs, the avatar mode and the driver seat are all read from
    // it. Local players never reach any of this — they have a controller, and U3DSteerable
    // builds their costume on entry.
    private ushort _remoteSteerableId;
    private U3D.U3DSteerable _remoteSteerable;
    private GameObject _remoteVehicleInstance;
    private GameObject _remoteReplacementInstance;
    private U3D.U3DDriverPose _remoteDriverPose;
    private Transform _remoteDriverHips;
    private bool _remoteAnchorActive;
    private bool _remoteAnchorToHips;
    private Vector3 _remoteAvatarOriginalLocalPos;

    // VR idle suppression: when the local player is in VR and the player controller's
    // movement state indicates idle, the avatar Animator's speed is set to 0 to freeze
    // all animation playback. This suppresses the breathing, weight-shift, and finger
    // motion baked into the idle clip, which would otherwise transfer to the camera
    // and hands in VR. When the player starts moving (walk, run, jump, crouch, fly,
    // swim, climb), Animator speed is restored to 1 so those animations play normally.
    //
    // vrIdleSuppressionActive is whether the local player was in VR on the previous frame,
    // so the transitions in and out can be acted on. It is read from the controller every
    // frame rather than switched on by a call at VR entry, because such a call can arrive
    // before this avatar exists — entering VR before spawn is an ordinary sequence.
    private bool vrIdleSuppressionActive = false;
    private float freezeScheduledTime = -1f;
    private bool _prevSeated;
    private bool _prevSuppressLocomotion;

    void Start()
    {
        playerController = GetComponent<U3DPlayerController>();
        _playback = GetComponent<NetAvatarPlayback>();

        networkedAnimator = GetComponent<U3DNetworkedAnimator>();
        if (networkedAnimator == null)
        {
            Debug.LogError("U3DAvatarManager: U3DNetworkedAnimator not found. Add it to the prefab.");
            return;
        }

        if (avatarFBX != null)
            InitializeAvatar();
        else
            Debug.LogWarning("U3DAvatarManager: No avatar FBX assigned. This player will have no visible body.");
    }

    void OnDestroy()
    {
        // A player leaving destroys their avatar, and the costume is a child of it, so Unity
        // removes it either way. Cleared explicitly so the static reference and the anchor
        // state do not outlive the object they describe.
        TearDownRemoteSteerable();
    }

    void InitializeAvatar()
    {
        try
        {
            avatarInstance = Instantiate(avatarFBX, transform);
            avatarInstance.transform.localPosition = avatarOffset;
            avatarInstance.transform.localRotation = Quaternion.identity;
            avatarInstance.transform.localScale = Vector3.one * avatarScaleMultiplier;

            ResolveHumanoidAvatarAsset();

            avatarAnimator = avatarInstance.GetComponent<Animator>();
            if (avatarAnimator == null)
                avatarAnimator = avatarInstance.AddComponent<Animator>();

            // An Animator added above starts with no Avatar, and an FBX prefab whose root
            // carries no Animator is a normal way for a creator to ship a rig. Without this
            // the instance is non-humanoid, so neutral-pose capture bails, IK disables itself
            // and held objects fall back to a synthetic anchor floating in front of the chest.
            if (avatarAnimator.avatar == null && avatarAsset != null)
                avatarAnimator.avatar = avatarAsset;

            // Root motion must always be off — the player controller owns all positional movement.
            // Any avatar prefab with Apply Root Motion enabled would otherwise drift away from the capsule.
            avatarAnimator.applyRootMotion = false;

            CaptureNeutralPoseData();

            ConnectToAnimationSystem();

            // Every Renderer, not only SkinnedMeshRenderer: rigid children of a rig — glasses,
            // a buckle, a prop — plus particle and line renderers all need to follow the same
            // first-person hiding rule as the body.
            avatarRenderers = avatarInstance.GetComponentsInChildren<Renderer>();

            // Auto-attach VR IK. Works for any humanoid avatar (default and creator-supplied).
            // If the avatar isn't humanoid, U3DAvatarIK logs a warning and disables itself.
            avatarIK = avatarInstance.GetComponent<U3DAvatarIK>();
            if (avatarIK == null)
                avatarIK = avatarInstance.AddComponent<U3DAvatarIK>();
            avatarIK.Initialize(playerController);

            isInitialized = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"U3DAvatarManager: Failed to initialize avatar: {e.Message}");
        }
    }

    /// <summary>
    /// Reads the humanoid Avatar asset off the source FBX so it can be handed to the
    /// instance's Animator if that Animator has none. Warns when the FBX carries no
    /// humanoid Avatar, which is the single most common cause of an avatar that spawns
    /// looking correct but has no usable bones.
    /// </summary>
    void ResolveHumanoidAvatarAsset()
    {
        avatarAsset = null;
        if (avatarFBX == null) return;

        Animator sourceAnimator = avatarFBX.GetComponent<Animator>();
        Avatar sourceAvatar = sourceAnimator != null ? sourceAnimator.avatar : null;

        if (sourceAvatar != null && sourceAvatar.isHuman)
            avatarAsset = sourceAvatar;
        else
            Debug.LogWarning($"U3DAvatarManager: '{avatarFBX.name}' has no Humanoid Avatar. Set Animation Type to Humanoid in the model's Import Settings, or VR hands, held objects and attachments will not find bones.");
    }

    /// <summary>
    /// Captures the neutral-pose reference frame for every mapped Humanoid bone. Poses the
    /// avatar to the muscle-neutral stance with HumanPoseHandler, records each bone's
    /// rotation relative to the avatar root plus the elbow→wrist direction in each hand's
    /// local frame, then restores the original pose. Fully synchronous — the neutral pose
    /// is never rendered. No-op on non-humanoid avatars; consumers fall back to
    /// pose-dependent behavior when no data was captured.
    /// </summary>
    private void CaptureNeutralPoseData()
    {
        _neutralBoneRotations = null;
        _neutralBoneValid = null;
        _leftHandAnchorValid = false;
        _rightHandAnchorValid = false;

        if (avatarInstance == null || avatarAnimator == null) return;
        if (!avatarAnimator.isHuman || avatarAnimator.avatar == null) return;

        HumanPoseHandler poseHandler = null;
        try
        {
            poseHandler = new HumanPoseHandler(avatarAnimator.avatar, avatarInstance.transform);

            HumanPose pose = new HumanPose();
            poseHandler.GetHumanPose(ref pose);

            Vector3 originalBodyPosition = pose.bodyPosition;
            Quaternion originalBodyRotation = pose.bodyRotation;
            float[] originalMuscles = (float[])pose.muscles.Clone();

            // Muscle-neutral stance, hips aligned to the avatar root — the same
            // normalized pose on every humanoid rig.
            for (int i = 0; i < pose.muscles.Length; i++)
                pose.muscles[i] = 0f;
            pose.bodyRotation = Quaternion.identity;
            poseHandler.SetHumanPose(ref pose);

            Quaternion invRoot = Quaternion.Inverse(avatarInstance.transform.rotation);
            int boneCount = (int)HumanBodyBones.LastBone;
            _neutralBoneRotations = new Quaternion[boneCount];
            _neutralBoneValid = new bool[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                Transform bone = avatarAnimator.GetBoneTransform((HumanBodyBones)i);
                if (bone == null) continue;
                _neutralBoneRotations[i] = invRoot * bone.rotation;
                _neutralBoneValid[i] = true;
            }

            CaptureHandAnchorDirection(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                out _leftHandAnchorDir, out _leftHandAnchorValid);
            CaptureHandAnchorDirection(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                out _rightHandAnchorDir, out _rightHandAnchorValid);

            // Restore the pose exactly as it was.
            pose.bodyPosition = originalBodyPosition;
            pose.bodyRotation = originalBodyRotation;
            System.Array.Copy(originalMuscles, pose.muscles, originalMuscles.Length);
            poseHandler.SetHumanPose(ref pose);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"U3DAvatarManager: neutral pose capture failed ({e.Message}) — attachments and grabs will use pose-dependent fallback.");
            _neutralBoneRotations = null;
            _neutralBoneValid = null;
            _leftHandAnchorValid = false;
            _rightHandAnchorValid = false;
        }
        finally
        {
            poseHandler?.Dispose();
        }
    }

    /// <summary>
    /// Records the elbow→wrist direction expressed in the hand bone's local frame, read
    /// while the avatar is posed neutral. Both directions are rotation-only (unaffected by
    /// scale), so the result is a constant unit direction that rides the hand at runtime.
    /// </summary>
    private void CaptureHandAnchorDirection(HumanBodyBones lowerArmBone, HumanBodyBones handBone,
        out Vector3 handLocalDirection, out bool valid)
    {
        handLocalDirection = Vector3.zero;
        valid = false;

        Transform lowerArm = avatarAnimator.GetBoneTransform(lowerArmBone);
        Transform hand = avatarAnimator.GetBoneTransform(handBone);
        if (lowerArm == null || hand == null) return;

        Vector3 worldDir = hand.position - lowerArm.position;
        if (worldDir.sqrMagnitude < 0.0001f) return;

        handLocalDirection = hand.InverseTransformDirection(worldDir.normalized);
        valid = true;
    }

    /// <summary>
    /// Returns the given Humanoid bone's neutral-pose rotation relative to the avatar
    /// root, captured at avatar initialization. At runtime,
    /// bone.rotation * Quaternion.Inverse(result) gives the world frame a marker should
    /// align to so that its authored forward faces the avatar's forward whenever the bone
    /// is in the neutral stance — making baked orientations independent of the animated
    /// pose at the attach moment. False when no data exists (non-humanoid avatar, unmapped
    /// bone, or capture failure); callers fall back to pose-dependent behavior.
    /// </summary>
    public bool TryGetNeutralBoneRotation(HumanBodyBones bone, out Quaternion neutralRelRotation)
    {
        neutralRelRotation = Quaternion.identity;
        if (_neutralBoneRotations == null || _neutralBoneValid == null) return false;

        int index = (int)bone;
        if (index < 0 || index >= _neutralBoneRotations.Length) return false;
        if (!_neutralBoneValid[index]) return false;

        neutralRelRotation = _neutralBoneRotations[index];
        return true;
    }

    /// <summary>
    /// Provides the grab frame for a resolved hand: the hand bone's neutral-pose rotation
    /// (for pose-independent orientation baking) and the elbow→wrist direction in hand-local
    /// space (for placing held objects a fixed distance out from the wrist joint, along the
    /// forearm line).
    ///
    /// Which hand it is comes from the transform itself, compared against this avatar's own
    /// two mapped hand bones, rather than from anything the caller passes alongside it. A
    /// second carrier of a fact already available is one that can come to disagree.
    ///
    /// Returns false when the resolved transform is not one of this avatar's Humanoid hand
    /// bones — the synthetic anchor is that case — or when no neutral data was captured.
    /// Callers then fall back to pose-dependent behavior.
    /// </summary>
    public bool TryGetHandGrabFrame(Transform resolvedHand,
        out Quaternion neutralRelRotation, out Vector3 anchorLocalDirection)
    {
        neutralRelRotation = Quaternion.identity;
        anchorLocalDirection = Vector3.zero;

        if (resolvedHand == null) return false;
        if (avatarAnimator == null || !avatarAnimator.isHuman) return false;

        bool leftHand;
        if (avatarAnimator.GetBoneTransform(HumanBodyBones.LeftHand) == resolvedHand)
            leftHand = true;
        else if (avatarAnimator.GetBoneTransform(HumanBodyBones.RightHand) == resolvedHand)
            leftHand = false;
        else
            return false;

        HumanBodyBones handBone = leftHand ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
        if (!TryGetNeutralBoneRotation(handBone, out neutralRelRotation)) return false;

        bool anchorValid = leftHand ? _leftHandAnchorValid : _rightHandAnchorValid;
        if (!anchorValid) return false;

        anchorLocalDirection = leftHand ? _leftHandAnchorDir : _rightHandAnchorDir;
        return true;
    }

    void ConnectToAnimationSystem()
    {
        if (networkedAnimator == null || avatarAnimator == null)
        {
            Debug.LogError("U3DAvatarManager: Cannot connect animation system - missing components");
            return;
        }

        networkedAnimator.SetAvatarAnimator(avatarAnimator);
    }

    /// <summary>
    /// Runs after the Animator has posed the rig for this frame, which is what makes the
    /// avatar-root rotation reset meaningful and what lets a held object's pose be computed
    /// against a finished hand bone. Visibility runs every frame regardless of VR state;
    /// idle suppression reads VR state itself each frame.
    ///
    /// The remote costume is resolved before visibility, because the visibility decision
    /// reads the resolved steerable, and anchored after the root rotation is settled, for
    /// the same reason U3DSteerable anchors in LateUpdate: the bones are posed by then.
    /// </summary>
    void LateUpdate()
    {
        if (!isInitialized) return;

        UpdateRemoteSteerable();

        UpdateAvatarVisibility();

        if (followPlayerRotation && avatarInstance != null)
            avatarInstance.transform.localRotation = Quaternion.identity;

        ApplyRemoteDriverAnchor();

        UpdateVRIdleSuppression();
    }

    /// <summary>
    /// Freezes the avatar Animator while the local player is in VR and idle, and releases it
    /// otherwise. VR state is read from the controller every frame and the transitions are
    /// handled here: leaving VR restores speed 1, entering VR records the seated and
    /// suppress-locomotion values the pose-change check compares against. Nothing outside
    /// this method switches suppression on or off, so VR entered before this avatar existed
    /// is picked up on the first frame the avatar runs.
    ///
    /// Unfreezing is instant; freezing is delayed slightly to let any in-progress animation
    /// transition complete cleanly, preventing the avatar from getting stuck mid-blend when
    /// exiting states like Flying. If the player starts moving again during the delay, the
    /// pending freeze is cancelled. A remote body has no controller and is never frozen.
    /// </summary>
    void UpdateVRIdleSuppression()
    {
        if (avatarAnimator == null || playerController == null) return;

        bool inVR = playerController.IsInVRMode;
        if (inVR != vrIdleSuppressionActive)
        {
            vrIdleSuppressionActive = inVR;

            if (inVR)
            {
                _prevSeated = playerController.IsSeated;
                _prevSuppressLocomotion = playerController.SuppressLocomotion;
            }
            else
            {
                avatarAnimator.speed = 1f;
                freezeScheduledTime = -1f;
            }
        }

        if (!vrIdleSuppressionActive) return;

        bool seated = playerController.IsSeated;
        bool suppressLocomotion = playerController.SuppressLocomotion;

        // When a pose-defining flag changes (sitting down or up, entering or leaving a
        // standing steerable), unfreeze briefly so the transition into the new pose plays,
        // then let the delayed-freeze path below re-freeze on the new static pose. Runs
        // before the early-outs so it works even when the animator is already frozen at
        // speed 0 — which is the case when you sit from a standstill.
        if (seated != _prevSeated || suppressLocomotion != _prevSuppressLocomotion)
        {
            avatarAnimator.speed = 1f;
            freezeScheduledTime = Time.time + 0.3f;
            _prevSeated = seated;
            _prevSuppressLocomotion = suppressLocomotion;
        }

        bool movementFlagsClear = !playerController.IsMoving
                               && !playerController.IsCrouching
                               && !playerController.IsFlying
                               && !playerController.IsSwimming
                               && !playerController.IsClimbing
                               && !playerController.IsJumping;

        if (!movementFlagsClear)
        {
            if (avatarAnimator.speed != 1f) avatarAnimator.speed = 1f;
            freezeScheduledTime = -1f;
            return;
        }

        if (avatarAnimator.speed == 0f) return;

        if (freezeScheduledTime < 0f)
        {
            freezeScheduledTime = Time.time + 0.3f;
            return;
        }

        if (Time.time >= freezeScheduledTime)
        {
            avatarAnimator.speed = 0f;
            freezeScheduledTime = -1f;
        }
    }

    // ==================== Remote steerable ====================

    /// <summary>
    /// Keeps this remote body's costume matching the steerable ID its packets carry. Does
    /// nothing for the local player, whose costume is built by U3DSteerable itself on entry.
    ///
    /// Only a change in the number does any work — the ID is compared every frame and the
    /// build and teardown run on the transitions. A number that does not resolve is treated
    /// as no steerable, which is the ordinary outcome when a packet arrives from a build
    /// whose scene differs, and it is silent by design.
    /// </summary>
    private void UpdateRemoteSteerable()
    {
        if (playerController != null) return;
        if (_playback == null) return;

        ushort incoming = _playback.HasSample ? _playback.Steerable : (ushort)0;
        if (incoming == _remoteSteerableId) return;

        TearDownRemoteSteerable();
        _remoteSteerableId = incoming;

        if (incoming == 0) return;

        _remoteSteerable = ResolveSteerable(incoming);
        if (_remoteSteerable == null) return;

        BuildRemoteCostume(_remoteSteerable);
    }

    /// <summary>
    /// Finds the steerable an ID names in this machine's own scene. The session's entity
    /// table is the only thing that maps a number to an object, and an ID it does not hold
    /// yields nothing rather than an error, matching how every other unresolvable ID is
    /// treated.
    /// </summary>
    private U3D.U3DSteerable ResolveSteerable(ushort id)
    {
        if (U3D.Net.Net.Session == null) return null;
        if (!U3D.Net.Net.Session.TryGetEntity(id, out U3D.Net.NetEntity entity)) return null;
        if (entity == null) return null;

        return entity.GetComponent<U3D.U3DSteerable>();
    }

    /// <summary>
    /// Builds the costume for a remote driver, by the same rule the local player's costume
    /// is built: the prefabs are instantiated as children of the body, keeping whatever
    /// position, rotation and scale the creator authored into them. The driver seat marker
    /// is found inside the rebuilt costume rather than sent, because every machine builds
    /// the same prefab and therefore the same marker in the same place.
    ///
    /// The seat's entry event fires here so a creator's engine sound, headlight or particle
    /// burst plays for everyone watching, not only for the person driving.
    /// </summary>
    private void BuildRemoteCostume(U3D.U3DSteerable steerable)
    {
        if (steerable.VehicleVisualPrefab != null)
        {
            _remoteVehicleInstance = U3D.U3DSteerable.InstantiateVisual(
                steerable.VehicleVisualPrefab, transform);

            _remoteDriverPose = _remoteVehicleInstance.GetComponentInChildren<U3D.U3DDriverPose>(true);
        }

        if (steerable.ReplacementVisualPrefab != null)
        {
            _remoteReplacementInstance = U3D.U3DSteerable.InstantiateVisual(
                steerable.ReplacementVisualPrefab, transform);
        }

        if (_remoteDriverPose != null)
        {
            if (steerable.AvatarMode != U3D.SteerableAvatarMode.HiddenAvatar)
                CaptureRemoteAnchorTargets(steerable.AvatarMode);

            _remoteDriverPose.OnDriverEnter?.Invoke();
        }
    }

    /// <summary>
    /// Caches the point on this body that should land on the driver seat: the hips when the
    /// steerable poses its driver seated, the avatar instance's own base when it poses them
    /// standing. Mirrors U3DSteerable's local capture, against the same two references.
    /// </summary>
    private void CaptureRemoteAnchorTargets(U3D.SteerableAvatarMode mode)
    {
        _remoteAnchorActive = false;
        _remoteDriverHips = null;
        _remoteAnchorToHips = false;

        if (avatarInstance == null) return;

        if (mode == U3D.SteerableAvatarMode.SeatedAvatar)
        {
            if (avatarAnimator == null || !avatarAnimator.isHuman) return;

            Transform hips = avatarAnimator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return;

            _remoteDriverHips = hips;
            _remoteAnchorToHips = true;
        }

        _remoteAvatarOriginalLocalPos = avatarInstance.transform.localPosition;
        _remoteAnchorActive = true;
    }

    /// <summary>
    /// Shifts this body so its hips or its base land on the driver seat marker, re-derived
    /// each frame so it tracks the animation as it settles and loops. The marker rides the
    /// costume which rides this body, so the body's own motion cancels in the delta and
    /// there is no feedback.
    /// </summary>
    private void ApplyRemoteDriverAnchor()
    {
        if (!_remoteAnchorActive) return;
        if (avatarInstance == null || _remoteDriverPose == null) return;

        Vector3 reference = (_remoteAnchorToHips && _remoteDriverHips != null)
            ? _remoteDriverHips.position
            : avatarInstance.transform.position;

        Vector3 delta = _remoteDriverPose.transform.position - reference;
        if (delta.sqrMagnitude < 1e-10f) return;

        avatarInstance.transform.position += delta;
    }

    /// <summary>
    /// Removes a remote driver's costume and puts the body back where it sits normally.
    /// Runs on every change of the steerable ID, including to zero, and on destruction.
    /// </summary>
    private void TearDownRemoteSteerable()
    {
        if (_remoteDriverPose != null)
            _remoteDriverPose.OnDriverExit?.Invoke();

        if (_remoteAnchorActive && avatarInstance != null)
            avatarInstance.transform.localPosition = _remoteAvatarOriginalLocalPos;

        if (_remoteVehicleInstance != null)
        {
            Destroy(_remoteVehicleInstance);
            _remoteVehicleInstance = null;
        }

        if (_remoteReplacementInstance != null)
        {
            Destroy(_remoteReplacementInstance);
            _remoteReplacementInstance = null;
        }

        _remoteDriverPose = null;
        _remoteDriverHips = null;
        _remoteAnchorActive = false;
        _remoteAnchorToHips = false;
        _remoteSteerable = null;
        _remoteSteerableId = 0;
    }

    /// <summary>
    /// Whether this body's locomotion animation should be held at a standing idle, which is
    /// what a steerable in Standing mode means. Derived from the steerable rather than
    /// carried in the packet, so no flag bit is spent on it — the local player reaches the
    /// same answer through the controller's own SuppressLocomotion.
    ///
    /// Read by the playback buffer as it hands movement values to the animator.
    /// </summary>
    public bool RemoteSuppressLocomotion =>
        _remoteSteerable != null
        && _remoteSteerable.AvatarMode == U3D.SteerableAvatarMode.StandingAvatar;

    void UpdateAvatarVisibility()
    {
        if (avatarRenderers == null) return;

        // Steerable resolution for the IK component's render decision. A controller means
        // this is the local player, and the steerable it is driving is the one the static
        // reports. A remote body has no controller and no static to read, so it uses the
        // steerable its packets named, resolved by UpdateRemoteSteerable above.
        if (avatarIK != null)
        {
            U3D.U3DSteerable resolvedSteerable = (playerController != null)
                ? U3D.U3DSteerable.CurrentlySteering
                : _remoteSteerable;

            avatarIK.SetResolvedSteerable(resolvedSteerable);
        }

        bool shouldShow = (avatarIK != null)
            ? avatarIK.ShouldRender(hideInFirstPerson)
            : ResolveVisibilityFallback();

        // Another player's body is kept out of sight until a packet has said where they
        // are. It is created the moment the roster names them, which is a whole connection
        // handshake before anything can arrive, and a body shown in that gap stands at a
        // position nobody chose — possibly inside the scene, or inside somebody else — and
        // then jumps to where the player really is. Nothing to do with the local player,
        // who has no playback and is simulated here.
        if (_playback != null && !_playback.HasSample)
            shouldShow = false;

        foreach (var r in avatarRenderers)
        {
            if (r != null && r.enabled != shouldShow)
                r.enabled = shouldShow;
        }

        // Cosmetic attachments follow the avatar's own visibility — registered by
        // U3DPlayerAttachments when each accessory is built, toggled here in lockstep with the body
        // so a worn hat hides in first-person desktop and shows in VR and third-person exactly as
        // the avatar does.
        for (int i = 0; i < _attachmentRenderers.Count; i++)
        {
            Renderer r = _attachmentRenderers[i];
            if (r != null && r.enabled != shouldShow)
                r.enabled = shouldShow;
        }

        // Head-attachment view protection. For the local wearer in VR first person, pieces riding
        // the head render shadow-only so a face-covering costume head can't blind them — they keep
        // the piece's shadow as a grounding cue, everyone else sees it normally. In VR third person
        // the camera is behind the body, so the piece shows in full. Applied per frame against the
        // current state so entering and exiting VR and perspective switches restore the authored
        // mode with no separate transition handling.
        bool suppressHeadPieces = playerController != null
            && playerController.IsInVRMode
            && playerController.IsFirstPerson;

        for (int i = 0; i < _headAttachmentEntries.Count; i++)
        {
            HeadAttachmentEntry entry = _headAttachmentEntries[i];
            if (entry.Renderer == null) continue;

            UnityEngine.Rendering.ShadowCastingMode desired = suppressHeadPieces
                ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
                : entry.OriginalMode;

            if (entry.Renderer.shadowCastingMode != desired)
                entry.Renderer.shadowCastingMode = desired;
        }
    }

    /// <summary>
    /// Visibility resolution used when the IK component isn't available (for example a
    /// non-humanoid avatar). Mirrors the IK component's logic so behavior stays consistent.
    /// A remote body with no controller is visible unless the steerable it is riding hides
    /// it, which is the same test the IK component makes.
    /// </summary>
    bool ResolveVisibilityFallback()
    {
        if (playerController == null)
        {
            if (_remoteSteerable != null
                && _remoteSteerable.AvatarMode == U3D.SteerableAvatarMode.HiddenAvatar)
                return false;
            return true;
        }

        if (playerController.IsInVRMode) return true;
        if (hideInFirstPerson && playerController.IsFirstPerson) return false;
        return true;
    }

    /// <summary>
    /// Registers a cosmetic attachment's renderers so they follow this avatar's visibility.
    /// Called by U3DPlayerAttachments when an accessory is built. Skips nulls and duplicates.
    /// </summary>
    public void RegisterAttachmentRenderers(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null && !_attachmentRenderers.Contains(renderers[i]))
                _attachmentRenderers.Add(renderers[i]);
        }
    }

    /// <summary>
    /// Removes a cosmetic attachment's renderers from visibility tracking. Called by
    /// U3DPlayerAttachments before the accessory instance is destroyed.
    /// </summary>
    public void UnregisterAttachmentRenderers(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
            _attachmentRenderers.Remove(renderers[i]);
    }

    /// <summary>
    /// Registers head-riding attachment renderers for VR view protection, in addition to the
    /// general registration. Called by U3DPlayerAttachments for pieces attached to the head bone
    /// or a socket under it. Captures each renderer's authored shadow mode so it can be restored
    /// exactly. Skips nulls and duplicates.
    /// </summary>
    public void RegisterHeadAttachmentRenderers(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;

            bool exists = false;
            for (int j = 0; j < _headAttachmentEntries.Count; j++)
            {
                if (_headAttachmentEntries[j].Renderer == r) { exists = true; break; }
            }
            if (exists) continue;

            _headAttachmentEntries.Add(new HeadAttachmentEntry
            {
                Renderer = r,
                OriginalMode = r.shadowCastingMode
            });
        }
    }

    /// <summary>
    /// Removes head-riding attachment renderers from VR view protection, restoring each one's
    /// authored shadow mode. Called by U3DPlayerAttachments before the accessory is destroyed.
    /// </summary>
    public void UnregisterHeadAttachmentRenderers(Renderer[] renderers)
    {
        if (renderers == null) return;
        for (int i = 0; i < renderers.Length; i++)
        {
            Renderer r = renderers[i];
            if (r == null) continue;

            for (int j = _headAttachmentEntries.Count - 1; j >= 0; j--)
            {
                if (_headAttachmentEntries[j].Renderer == r)
                {
                    if (r != null)
                        r.shadowCastingMode = _headAttachmentEntries[j].OriginalMode;
                    _headAttachmentEntries.RemoveAt(j);
                }
            }
        }
    }

    /// <summary>
    /// Resolves the hand Transform for attaching held or summoned objects, using this
    /// player's equipped avatar. Both U3DGrabbable and U3DInventory call this so hand
    /// resolution lives in one place and cannot drift between them.
    ///
    /// The hand is named by role rather than by bone name, so any humanoid rig resolves
    /// with no per-rig field editing — a Mixamo rig with a "mixamorig:" prefix and the
    /// shipped rig reach the same answer. A bone name was the control here before and was
    /// tried first, which meant that on a rig whose bone happened to be called "RightHand"
    /// the name answered and the role never ran, while on any other rig the reverse
    /// happened — one fact reachable two ways, agreeing by luck. B41.
    ///
    /// PlayerPosition means no hand attachment is wanted; the result then depends solely on
    /// createAnchorIfMissing. That last resort is a persistent synthetic anchor parented in
    /// front of and above the player, for non-humanoid avatars with no hand bone. When
    /// false, returns null so best-effort callers can skip cleanly instead of spawning one.
    /// </summary>
    public Transform ResolveHandBone(U3DHandChoice hand, bool createAnchorIfMissing)
    {
        if (hand != U3DHandChoice.PlayerPosition
            && avatarAnimator != null
            && avatarAnimator.isHuman)
        {
            HumanBodyBones boneId = hand == U3DHandChoice.LeftHand
                ? HumanBodyBones.LeftHand
                : HumanBodyBones.RightHand;

            Transform roleBone = avatarAnimator.GetBoneTransform(boneId);
            if (roleBone != null) return roleBone;
        }

        if (!createAnchorIfMissing) return null;

        if (_handAnchor == null)
        {
            GameObject anchor = new GameObject($"{transform.name}_HandAnchor");
            anchor.transform.SetParent(transform);
            anchor.transform.localPosition = Vector3.forward * 0.5f + Vector3.up * 1.2f;
            anchor.transform.localRotation = Quaternion.identity;
            _handAnchor = anchor.transform;
        }
        return _handAnchor;
    }

    public bool IsAvatarInitialized => isInitialized;
    public GameObject GetAvatarInstance() => avatarInstance;
    public Animator GetAvatarAnimator() => avatarAnimator;
    public U3DNetworkedAnimator GetNetworkedAnimator() => networkedAnimator;

    /// <summary>
    /// The IK component on this player's avatar instance, or null before the avatar has
    /// been built and on a player with no avatar FBX assigned. Exposed because the pose a
    /// VR player sends and the pose a remote body receives both live on it, and neither
    /// sender nor playback sits on the same GameObject.
    /// </summary>
    public U3DAvatarIK AvatarIK => avatarIK;

    void OnValidate()
    {
        if (avatarScaleMultiplier <= 0f) avatarScaleMultiplier = 1f;
    }
}