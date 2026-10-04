using UnityEngine;
using UnityEngine.InputSystem.HID;
using UnityEngine.UIElements;
using UnityEngine.XR;

#if WEBXR_ENABLED
using WebXR;
using static WebXR.WebXRCamera;
#endif

namespace U3D
{
    /// <summary>
    /// Drives a humanoid avatar's pose from VR input. Owns arm IK (shoulder/upper/lower/hand)
    /// and head bone rotation, for the local player and for a remote body alike.
    ///
    /// Local VR player: reads controller poses directly from De-Panther's WebXRController
    /// components, stores them as rig-local pose, solves two-bone IK on
    /// shoulder/upperArm/lowerArm/hand, and applies the camera rotation to the head bone.
    ///
    /// Remote avatar: carries no player controller, so its pose arrives instead. The playback
    /// buffer writes the same five stored values through ReceivePose and reports the owner's
    /// VR state through SetRemoteVRState, and everything below it runs identically. Nothing in
    /// ResolveTargets or ApplyHeadRotation tests which avatar it is on.
    ///
    /// The stored hand values are offsets from the head bone rather than points in player-root
    /// space. Both viewpoints compose them the same way, so the local player sees their arms
    /// exactly where a remote viewer does, and a receiver whose body is a tenth of a second
    /// behind still places the arms against its own head rather than against one that was never
    /// there.
    ///
    /// Two IK weights rather than one. The head weight follows whether the owner is in VR; the
    /// arm weight additionally requires that both hand poses have been written at least once.
    /// An unwritten hand position is zero, which ResolveTargets would turn into the player
    /// root's own origin at the feet, so a single weight would raise arm IK onto a pose that
    /// does not exist yet.
    ///
    /// Runs in LateUpdate after the Animator evaluates so it can override the animated
    /// arm and head pose, and at execution order 103 so the avatar manager has already
    /// settled the avatar root's rotation — a parent rotation written after a child's
    /// world rotation would drag the solved bones with it.
    ///
    /// Auto-attached to the avatar instance by U3DAvatarManager. Creators do not need
    /// to add this component manually.
    ///
    /// Note on input source: this component originally read XR controller poses through
    /// the new Input System using <XRController>{LeftHand}/devicePosition bindings, then
    /// tried Unity's XR Input subsystem (InputDevices.GetDevicesAtXRNode), but neither
    /// approach exposes left and right hand poses independently in De-Panther's WebXR
    /// runtime. We use De-Panther's own WebXRController components: each one has a hand
    /// property (LEFT/RIGHT/NONE) and its transform is updated every frame with the
    /// controller pose. This component creates those controller objects itself when VR mode begins, as
    /// children of the player controller's XR tracking origin — the same transform the
    /// camera and the raw HMD reference hang from.They have to share that parent
    /// because a hand pose is read as the difference between two of those transforms.
    /// </summary>
    [DefaultExecutionOrder(103)]
    public class U3DAvatarIK : MonoBehaviour
    {
        [Header("IK Tuning")]
        [Tooltip("Seconds for the IK weight to lerp from 0 to 1 (or 1 to 0) when VR mode toggles. Higher = more snap, lower = smoother but laggier.")]
        [SerializeField] private float ikTransitionTime = 0.2f;

        [Tooltip("How far the elbow bends out from the body. 0 = no hint (elbow may flip awkwardly), 1 = strong outward bend. 0.5 is natural.")]
        [Range(0f, 1f)]
        [SerializeField] private float elbowOutwardHint = 0.5f;

        [Tooltip("How far the elbow bends down. 0 = elbow points sideways, 1 = elbow points down. 0.3 is natural for arms held in front.")]
        [Range(0f, 1f)]
        [SerializeField] private float elbowDownwardHint = 0.3f;

        [SerializeField] private Vector3 leftHandRotationOffset = new Vector3(0f, -90f, -90f);
        [SerializeField] private Vector3 rightHandRotationOffset = new Vector3(0f, 90f, 90f);

        [Tooltip("Fixed Euler offset applied to the avatar head bone in VR. Mecanim head bone bind orientation rarely matches the camera/HMD orientation; this corrects the difference. Tune empirically for each avatar rig.")]
        [SerializeField] private Vector3 headRotationOffset = new Vector3(0f, -90f, 0f);

        [Header("Debug")]
        [Tooltip("Show on-screen pose data overlay in VR. A deployed WebGL VR build has no console, so this overlay is the way to read pose state on the headset. Diagnostic use only — disable for production.")]
        [SerializeField] private bool showDebugOverlay = false;

        // Owning controller. Null on the remote avatar prefab, which carries no controller
        // at all. Every read below treats null as "this is not the local player."
        private U3DPlayerController _playerController;

        // The player root this avatar hangs from. Always this instance's parent, which is
        // the same object the controller sits on when there is a controller. Cached because
        // the remote prefab has no controller to ask.
        private Transform _playerRoot;

        private Animator _animator;

        // Cached humanoid bones (resolved at Initialize)
        private Transform _leftShoulder;
        private Transform _leftUpperArm;
        private Transform _leftLowerArm;
        private Transform _leftHand;

        private Transform _rightShoulder;
        private Transform _rightUpperArm;
        private Transform _rightLowerArm;
        private Transform _rightHand;

        private Transform _head;

        // Cached arm bone lengths (computed once from initial hierarchy)
        private float _leftUpperArmLength;
        private float _leftLowerArmLength;
        private float _rightUpperArmLength;
        private float _rightLowerArmLength;

        // Rig-local VR pose. The rotations are in player-root space; the two hand values are
        // offsets from the head bone's own player-root-space position, which is what travels
        // and what a remote body recomposes against its own head. Held here rather than on the
        // controller: these are this component's own working values, and the remote avatar
        // prefab has no controller to hold them.
        private Vector3 _leftHandOffset;
        private Quaternion _leftHandLocalRot = Quaternion.identity;
        private Vector3 _rightHandOffset;
        private Quaternion _rightHandLocalRot = Quaternion.identity;
        private Quaternion _headLocalRot = Quaternion.identity;

        // Whether each hand has ever been written. Until both have, the stored offsets are
        // zero, which composes to the head bone itself rather than to a hand — so the arm
        // weight stays down and nothing is sent. B99.
        private bool _leftHandWritten;
        private bool _rightHandWritten;

        // What a remote body's owner last reported about itself. Pushed every frame by the
        // playback buffer whether or not a pose resolved, so leaving VR lowers the weight at
        // once rather than waiting for the pose stream to go stale.
        private bool _remoteInVR;
        private bool _remotePoseFresh;

#if WEBXR_ENABLED
        // De-Panther WebXRController references, created by this component at the start of
        // each VR session and destroyed with the tracking origin at the end of one.
        // De-Panther writes the controller pose to each component's transform every frame and
        // exposes a hand property (LEFT/RIGHT/NONE) for handedness. This is the canonical
        // De-Panther API for per-hand controller data and works when Unity's XR Input
        // subsystem returns nothing on WebGL/WebXR.
        private WebXRController _leftHandController;
        private WebXRController _rightHandController;
#endif

        // IK weight state (lerped each frame)
        private float _headIKWeight;
        private float _armIKWeight;
        private float _targetHeadWeight;
        private float _targetArmWeight;

        // Debug overlay state
        private GameObject _debugPanel;
        private TextMesh _debugText;
        private int _debugFrameCounter;

        public bool IsReady => _animator != null
            && _leftHand != null && _rightHand != null
            && _leftUpperArm != null && _leftLowerArm != null
            && _rightUpperArm != null && _rightLowerArm != null;

        /// <summary>
        /// True when this avatar belongs to the player at this machine. A controller only
        /// ever exists on the local player prefab, so its presence is the test.
        /// </summary>
        private bool IsLocalAvatar => _playerController != null;

        /// <summary>
        /// True when the owner of this avatar is currently in VR. For the local player that
        /// is the controller's own state. For a remote body it is what that peer's packets
        /// say — their VR flag, plus a pose having arrived recently enough to pose from.
        /// A controller test cannot answer it: a remote avatar has none and would read false
        /// forever.
        /// </summary>
        private bool OwnerInVR => _playerController != null
            ? _playerController.IsInVRMode
            : (_remoteInVR && _remotePoseFresh);

        /// <summary>
        /// Whether both hand poses hold a real value. False until the local controllers have
        /// been bound and written once, and until a remote body's first pose arrives.
        /// </summary>
        public bool HasHandPose => _leftHandWritten && _rightHandWritten;

        public Vector3 LeftHandOffset => _leftHandOffset;
        public Quaternion LeftHandLocalRotation => _leftHandLocalRot;
        public Vector3 RightHandOffset => _rightHandOffset;
        public Quaternion RightHandLocalRotation => _rightHandLocalRot;
        public Quaternion HeadLocalRotation => _headLocalRot;

        // Set each frame by U3DAvatarManager.UpdateAvatarVisibility before ShouldRender
        // is called. Carries the resolved U3DSteerable so ShouldRender can check avatar
        // mode without resolving it itself.
        private U3D.U3DSteerable _resolvedSteerable;

        public void SetResolvedSteerable(U3D.U3DSteerable steerable)
        {
            _resolvedSteerable = steerable;
        }

        /// <summary>
        /// Writes a pose that arrived from its owner. The five values are the same ones the
        /// local path stores for itself, in the same spaces, so everything below this runs
        /// unchanged — the component does not know which route a pose took.
        ///
        /// Marks both hands written, because a pose that arrived carries both by construction.
        /// </summary>
        public void ReceivePose(Quaternion headRotation,
            Vector3 leftHandOffset, Quaternion leftHandRotation,
            Vector3 rightHandOffset, Quaternion rightHandRotation)
        {
            _headLocalRot = headRotation;
            _leftHandOffset = leftHandOffset;
            _leftHandLocalRot = leftHandRotation;
            _rightHandOffset = rightHandOffset;
            _rightHandLocalRot = rightHandRotation;

            _leftHandWritten = true;
            _rightHandWritten = true;
        }

        /// <summary>
        /// What a remote body's owner reports about itself, pushed every frame by the playback
        /// buffer. inVR is their VR flag from the avatar packet; poseIsFresh is whether a pose
        /// has arrived recently enough to drive from.
        ///
        /// Both are needed and they answer different halves. A pose arriving at all implies VR,
        /// since nothing sends one otherwise — so freshness alone would raise the weight
        /// correctly and lower it only after the stale window. The flag is what lowers it the
        /// moment that player leaves VR.
        ///
        /// No effect on the local player, whose controller answers for itself.
        /// </summary>
        public void SetRemoteVRState(bool inVR, bool poseIsFresh)
        {
            _remoteInVR = inVR;
            _remotePoseFresh = poseIsFresh;
        }

        /// <summary>
        /// Called by U3DAvatarManager after avatar instantiation. Wires this component to
        /// its owning player controller, which is null for a remote avatar.
        /// </summary>
        public void Initialize(U3DPlayerController owner)
        {
            _playerController = owner;
            _playerRoot = transform.parent != null ? transform.parent : transform;

            CacheBones();
            // WebXRController references are resolved per-frame in LateUpdate via BindXRActions.
            // No setup needed at initialization time.
        }

        void CacheBones()
        {
            _animator = GetComponent<Animator>();
            if (_animator == null)
            {
                Debug.LogWarning("U3DAvatarIK: No Animator on avatar instance. IK disabled.");
                return;
            }

            if (_animator.avatar == null || !_animator.avatar.isHuman)
            {
                Debug.LogWarning("U3DAvatarIK: Avatar is not humanoid. IK disabled.");
                return;
            }

            _leftShoulder = _animator.GetBoneTransform(HumanBodyBones.LeftShoulder);
            _leftUpperArm = _animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            _leftLowerArm = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _leftHand = _animator.GetBoneTransform(HumanBodyBones.LeftHand);

            _rightShoulder = _animator.GetBoneTransform(HumanBodyBones.RightShoulder);
            _rightUpperArm = _animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            _rightLowerArm = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _rightHand = _animator.GetBoneTransform(HumanBodyBones.RightHand);

            _head = _animator.GetBoneTransform(HumanBodyBones.Head);

            // Cache bone lengths from initial pose. These reflect the avatar's actual
            // arm proportions and stay constant for the lifetime of this avatar instance.
            if (_leftUpperArm != null && _leftLowerArm != null && _leftHand != null)
            {
                _leftUpperArmLength = Vector3.Distance(_leftUpperArm.position, _leftLowerArm.position);
                _leftLowerArmLength = Vector3.Distance(_leftLowerArm.position, _leftHand.position);
            }

            if (_rightUpperArm != null && _rightLowerArm != null && _rightHand != null)
            {
                _rightUpperArmLength = Vector3.Distance(_rightUpperArm.position, _rightLowerArm.position);
                _rightLowerArmLength = Vector3.Distance(_rightLowerArm.position, _rightHand.position);
            }
        }

        /// <summary>
        /// The head bone's position in player-root space, which is what a stored hand offset
        /// is measured from and what it is composed against.
        ///
        /// One method serving both ends deliberately. IsReady does not test the head bone and
        /// ApplyHeadRotation returns early when it is missing, so a rig with no head reaches
        /// the arm path — and with both ends taking zero there, such a rig degrades to
        /// root-anchored hands consistently rather than throwing at one end and not the other.
        /// </summary>
        private Vector3 HeadAnchorLocal()
        {
            if (_head == null || _playerRoot == null) return Vector3.zero;
            return _playerRoot.InverseTransformPoint(_head.position);
        }

        /// <summary>
        /// Create the two per-hand controller objects under the tracking origin, one per VR
        /// session. Nothing else in the project produces a WebXRController and there is no
        /// creator-facing route to one, so this is the only source of them and there is
        /// nothing to search the scene for.
        ///
        /// Both references go null together when the tracking origin is destroyed at the end
        /// of a VR session, which is what makes the test at the top re-create them on the next
        /// entry. Called every frame while in VR, so on all but the first frame it is two
        /// reference tests.
        /// </summary>
        void BindXRActions()
        {
#if WEBXR_ENABLED
            if (_leftHandController != null && _rightHandController != null)
                return;

            Transform trackingParent = ResolveTrackingParent();
            if (trackingParent == null) return;

            if (_leftHandController == null)
                _leftHandController = CreateRuntimeController(WebXRControllerHand.LEFT, trackingParent);

            if (_rightHandController == null)
                _rightHandController = CreateRuntimeController(WebXRControllerHand.RIGHT, trackingParent);
#endif
        }

        /// <summary>
        /// The transform every headset-posed object hangs from: the player controller's XR
        /// tracking origin. Both controller objects share it with the camera and the raw HMD
        /// reference, which is what makes the headset-to-hand vector in ReadLocalControllerPoses
        /// a subtraction of two positions in one frame. A controller left under the player root
        /// would be turned by the body's facing while the headset reference was not, and both
        /// hands would sit off to one side by the angle between head and body — on this screen
        /// and, since the pose travels, on every observer's.
        ///
        /// Falls back to the player root, which is where everything hung before the tracking
        /// origin existed, so a null origin degrades to the old arrangement rather than to no
        /// hands at all.
        /// </summary>
        private Transform ResolveTrackingParent()
        {
            if (_playerController != null && _playerController.TrackingOrigin != null)
                return _playerController.TrackingOrigin;

            return _playerRoot;
        }

#if WEBXR_ENABLED
        WebXRController CreateRuntimeController(WebXRControllerHand handAssignment, Transform trackingParent)
        {
            string label = handAssignment == WebXRControllerHand.LEFT ? "U3D_WebXRController_L" : "U3D_WebXRController_R";
            GameObject go = new GameObject(label);
            go.transform.SetParent(trackingParent, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;

            var ctrl = go.AddComponent<WebXRController>();
            ctrl.hand = handAssignment;
            return ctrl;
        }
#endif

        void LateUpdate()
        {
            if (!IsReady) return;

            bool isLocal = IsLocalAvatar;
            bool ownerInVR = OwnerInVR;

            // Refresh WebXRController references each frame (cheap when refs are alive).
            if (isLocal && ownerInVR)
            {
                BindXRActions();
            }

            if (showDebugOverlay && isLocal && ownerInVR)
            {
                EnsureDebugPanel();
                UpdateDebugOverlay();
            }
            else if (_debugPanel != null)
            {
                _debugPanel.SetActive(false);
            }

            // Read controller poses every frame regardless of weight, so the stored pose
            // stays fresh through a fade-out rather than freezing at the moment VR ended.
            // Runs before the weights are computed, because the read is what can make the
            // hand poses valid this frame and the arm weight depends on that.
            if (isLocal && ownerInVR)
            {
                ReadLocalControllerPoses();
            }

            // Two targets. The head follows VR state alone; the arms additionally wait for a
            // hand pose to exist, because an unwritten offset composes to the head bone and
            // an arm raised onto that reaches the wrong place with nothing saying so.
            _targetHeadWeight = ownerInVR ? 1f : 0f;
            _targetArmWeight = (ownerInVR && HasHandPose) ? 1f : 0f;

            float lerpStep = (ikTransitionTime > 0.001f)
                ? Time.deltaTime / ikTransitionTime
                : 1f;
            _headIKWeight = Mathf.MoveTowards(_headIKWeight, _targetHeadWeight, lerpStep);
            _armIKWeight = Mathf.MoveTowards(_armIKWeight, _targetArmWeight, lerpStep);

            // Skip IK math entirely if both weights are effectively zero.
            if (_headIKWeight < 0.001f && _armIKWeight < 0.001f) return;

            // Drive the head bone first, so the arm solver's target reconstruction (which
            // doesn't use the head) is unaffected and the head pose lands in the same
            // frame as the arms. Setting a bone's rotation does not move that bone, so the
            // head anchor ResolveTargets reads is the same either side of this call.
            ApplyHeadRotation();

            if (_armIKWeight < 0.001f) return;

            ResolveTargets(out Vector3 leftTargetWorldPos, out Quaternion leftTargetWorldRot,
                           out Vector3 rightTargetWorldPos, out Quaternion rightTargetWorldRot);

            // Cache animator-output rotations BEFORE we overwrite them, so the lerp
            // can blend smoothly between animator pose and IK pose at partial weights.
            Quaternion animLeftUpper = _leftUpperArm.rotation;
            Quaternion animLeftLower = _leftLowerArm.rotation;
            Quaternion animLeftHand = _leftHand.rotation;
            Quaternion animRightUpper = _rightUpperArm.rotation;
            Quaternion animRightLower = _rightLowerArm.rotation;
            Quaternion animRightHand = _rightHand.rotation;

            SolveTwoBoneIK(
                _leftUpperArm, _leftLowerArm, _leftHand,
                _leftUpperArmLength, _leftLowerArmLength,
                leftTargetWorldPos, leftTargetWorldRot,
                isLeftSide: true,
                out Quaternion ikLeftUpper, out Quaternion ikLeftLower, out Quaternion ikLeftHand);

            _leftUpperArm.rotation = Quaternion.Slerp(animLeftUpper, ikLeftUpper, _armIKWeight);
            _leftLowerArm.rotation = Quaternion.Slerp(animLeftLower, ikLeftLower, _armIKWeight);
            _leftHand.rotation = Quaternion.Slerp(animLeftHand, ikLeftHand, _armIKWeight);

            SolveTwoBoneIK(
                _rightUpperArm, _rightLowerArm, _rightHand,
                _rightUpperArmLength, _rightLowerArmLength,
                rightTargetWorldPos, rightTargetWorldRot,
                isLeftSide: false,
                out Quaternion ikRightUpper, out Quaternion ikRightLower, out Quaternion ikRightHand);

            _rightUpperArm.rotation = Quaternion.Slerp(animRightUpper, ikRightUpper, _armIKWeight);
            _rightLowerArm.rotation = Quaternion.Slerp(animRightLower, ikRightLower, _armIKWeight);
            _rightHand.rotation = Quaternion.Slerp(animRightHand, ikRightHand, _armIKWeight);
        }

        void EnsureDebugPanel()
        {
            if (_debugPanel != null)
            {
                if (!_debugPanel.activeSelf) _debugPanel.SetActive(true);
                return;
            }
            if (_playerController == null || _playerController.CameraTransform == null) return;

            _debugPanel = new GameObject("U3DAvatarIK_DebugPanel");
            _debugPanel.transform.SetParent(_playerController.CameraTransform, false);
            _debugPanel.transform.localPosition = new Vector3(0f, 0f, 2f);
            _debugPanel.transform.localRotation = Quaternion.identity;
            _debugPanel.transform.localScale = Vector3.one;

            _debugText = _debugPanel.AddComponent<TextMesh>();
            _debugText.fontSize = 64;
            _debugText.characterSize = 0.012f;
            _debugText.anchor = TextAnchor.MiddleCenter;
            _debugText.alignment = TextAlignment.Left;
            _debugText.color = Color.yellow;

            // Render on top of geometry
            _debugText.GetComponent<MeshRenderer>().material.renderQueue = 4000;
        }

        void UpdateDebugOverlay()
        {
            if (_debugPanel == null) return;

            _debugFrameCounter++;

#if WEBXR_ENABLED
            string lFound = _leftHandController != null ? "FOUND" : "NULL";
            string rFound = _rightHandController != null ? "FOUND" : "NULL";

            Vector3 lp = Vector3.zero;
            if (_leftHandController != null)
                lp = _leftHandController.transform.position;

            Vector3 rp = Vector3.zero;
            if (_rightHandController != null)
                rp = _rightHandController.transform.position;

            string lName = _leftHandController != null ? _leftHandController.gameObject.name : "(none)";
            string rName = _rightHandController != null ? _rightHandController.gameObject.name : "(none)";

            string lMode = _leftHandController == null ? "-"
                : _leftHandController.isControllerActive ? "CTRL"
                : _leftHandController.isHandActive ? "HAND"
                : "NONE";
            string rMode = _rightHandController == null ? "-"
                : _rightHandController.isControllerActive ? "CTRL"
                : _rightHandController.isHandActive ? "HAND"
                : "NONE";
#else
            string lFound = "WEBXR_DISABLED";
            string rFound = "WEBXR_DISABLED";
            Vector3 lp = Vector3.zero;
            Vector3 rp = Vector3.zero;
            string lName = "(disabled)";
            string rName = "(disabled)";
            string lMode = "WEBXR_DISABLED";
            string rMode = "WEBXR_DISABLED";
#endif

            Vector3 lRot = _leftHandLocalRot.eulerAngles;
            Vector3 rRot = _rightHandLocalRot.eulerAngles;

            // TPD diagnostic: read the TPD state directly from the camera so we can
            // see whether the headset rotation write is landing.
            string tpdDiag = "(no camera)";
            if (_playerController != null && _playerController.CameraTransform != null)
            {
                Transform camT = _playerController.CameraTransform;
                var tpd = camT.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                Vector3 camLocalEuler = camT.localEulerAngles;
                Vector3 camWorldEuler = camT.eulerAngles;

                if (tpd != null)
                {
                    string tpdEnabled = tpd.enabled ? "ON" : "OFF";
                    string tpdType = tpd.trackingType.ToString();
                    string posEnabled = (tpd.positionInput.action != null && tpd.positionInput.action.enabled) ? "Y" : "N";
                    string rotEnabled = (tpd.rotationInput.action != null && tpd.rotationInput.action.enabled) ? "Y" : "N";
                    tpdDiag = $"TPD {tpdEnabled} {tpdType} pos:{posEnabled} rot:{rotEnabled}";
                }
                else
                {
                    tpdDiag = "TPD MISSING";
                }
                tpdDiag += $"\nCamLocal: ({camLocalEuler.x:F0},{camLocalEuler.y:F0},{camLocalEuler.z:F0})";
                tpdDiag += $"\nCamWorld: ({camWorldEuler.x:F0},{camWorldEuler.y:F0},{camWorldEuler.z:F0})";
                tpdDiag += $"\nBodyYaw: {_playerRoot.eulerAngles.y:F0}";
            }

            // Geometry diagnostic: world positions of camera, avatar head bone, and
            // player root, plus the offsets between them. Tells us where the camera
            // actually sits on the avatar rig — measured values, no guessing.
            string geomDiag = "(no geom)";
            if (_playerController != null && _playerController.CameraTransform != null)
            {
                Vector3 camPos = _playerController.CameraTransform.position;
                Vector3 rootPos = _playerRoot.position;
                Vector3 headPos = (_head != null) ? _head.position : Vector3.zero;

                geomDiag = $"Cam:  ({camPos.x:F2},{camPos.y:F2},{camPos.z:F2})";
                geomDiag += $"\nHead: ({headPos.x:F2},{headPos.y:F2},{headPos.z:F2})";
                geomDiag += $"\nRoot: ({rootPos.x:F2},{rootPos.y:F2},{rootPos.z:F2})";
                geomDiag += $"\nC-H:  ({(camPos.x - headPos.x):F2},{(camPos.y - headPos.y):F2},{(camPos.z - headPos.z):F2})";
                geomDiag += $"\nH-R:  ({(headPos.x - rootPos.x):F2},{(headPos.y - rootPos.y):F2},{(headPos.z - rootPos.z):F2})";
            }

            _debugText.text =
                $"Frame: {_debugFrameCounter}\n" +
                $"L Ctrl {lFound}: {lName}\n" +
                $"R Ctrl {rFound}: {rName}\n" +
                $"L Mode {lMode}  Rot ({lRot.x:F0},{lRot.y:F0},{lRot.z:F0})\n" +
                $"R Mode {rMode}  Rot ({rRot.x:F0},{rRot.y:F0},{rRot.z:F0})\n" +
                $"L-Pos: ({lp.x:F2}, {lp.y:F2}, {lp.z:F2})\n" +
                $"R-Pos: ({rp.x:F2}, {rp.y:F2}, {rp.z:F2})\n" +
                $"Hands: {(HasHandPose ? "WRITTEN" : "WAITING")}  Head w {_headIKWeight:F2}  Arm w {_armIKWeight:F2}\n" +
                $"---\n" +
                $"{tpdDiag}\n" +
                $"---\n" +
                $"{geomDiag}";
        }

        /// <summary>
        /// De-Panther's WebXRController writes pose to its transform every frame in
        /// WebXR reference space. We compute the IRL head-to-hand vector by subtracting
        /// the IRL HMD's pose (read from the player controller's raw HMD reference, which
        /// is a position-only TPD-driven transform) from the controller's pose. Both are
        /// in WebXR space, so the subtraction yields a clean IRL vector. Then we anchor
        /// that vector to the avatar's head bone world position to place hands relative
        /// to the avatar rig, and store the result as an offset from the head bone rather
        /// than as a point — the same value that travels, so the local player poses from
        /// exactly what a remote viewer poses from.
        ///
        /// The locked-to-avatar player camera CANNOT be used as the IRL HMD reference
        /// because U3DPlayerController.LateUpdate overrides the camera's world position
        /// to follow the avatar head bone — it no longer reflects where the IRL HMD
        /// actually is. The raw HMD reference exists specifically as the unmoved
        /// De-Panther TPD output for this purpose.
        ///
        /// A hand is written only while its controller reports isControllerActive. The same
        /// transform is also written by De-Panther from the wrist joint when the headset
        /// switches to hand tracking, and the wrist joint's rotation convention is not the
        /// target ray's the hand offsets were tuned against — so an idle controller handing
        /// over to hands flipped both avatar hands. A controller that is inactive for any
        /// reason (hand tracking, a tracking dropout, or created and not yet updated) holds the
        /// last good pose, and since the stored values are what travel, observers hold it too.
        ///
        /// Until a hand has been written once it is not valid and the arm weight stays down —
        /// the head rotation below is written unconditionally and would otherwise raise both
        /// arms onto offsets of zero, which compose to the head bone. B99.
        /// </summary>
        void ReadLocalControllerPoses()
        {
            Transform cam = _playerController.CameraTransform;
            Transform rawHmd = _playerController.RawHmdReference;
            if (cam == null) return;

            // Fallback: if the raw HMD reference doesn't exist yet (first frame of VR mode
            // before EnterVRMode finished), fall back to the camera transform. Hand
            // placement is slightly off for that one frame, which is imperceptible.
            Vector3 irlHmdPos = (rawHmd != null) ? rawHmd.position : cam.position;
            Vector3 headBoneWorld = (_head != null) ? _head.position : cam.position;
            Vector3 anchorLocal = HeadAnchorLocal();

#if WEBXR_ENABLED
            if (_leftHandController != null && _leftHandController.isControllerActive)
            {
                Transform t = _leftHandController.transform;
                Vector3 handWorld = headBoneWorld + (t.position - irlHmdPos);
                _leftHandOffset = _playerRoot.InverseTransformPoint(handWorld) - anchorLocal;
                _leftHandLocalRot = Quaternion.Inverse(_playerRoot.rotation) * t.rotation;
                _leftHandWritten = true;
            }

            if (_rightHandController != null && _rightHandController.isControllerActive)
            {
                Transform t = _rightHandController.transform;
                Vector3 handWorld = headBoneWorld + (t.position - irlHmdPos);
                _rightHandOffset = _playerRoot.InverseTransformPoint(handWorld) - anchorLocal;
                _rightHandLocalRot = Quaternion.Inverse(_playerRoot.rotation) * t.rotation;
                _rightHandWritten = true;
            }
#endif

            // Head pose comes from the camera transform, which De-Panther's WebXRCamera
            // poses from the headset every frame.
            _headLocalRot = Quaternion.Inverse(_playerRoot.rotation) * cam.rotation;
        }

        /// <summary>
        /// Converts the stored wrist offsets into world space, by adding back the head bone
        /// position they were measured from. One method rather than two: the local and remote
        /// paths are identical, because the local player deliberately reads back through the
        /// same stored values and the same composition a remote viewer runs, so both
        /// viewpoints see the arms in exactly the same place.
        /// </summary>
        void ResolveTargets(
            out Vector3 leftPos, out Quaternion leftRot,
            out Vector3 rightPos, out Quaternion rightRot)
        {
            Vector3 anchorLocal = HeadAnchorLocal();

            leftPos = _playerRoot.TransformPoint(anchorLocal + _leftHandOffset);
            leftRot = _playerRoot.rotation * _leftHandLocalRot;
            rightPos = _playerRoot.TransformPoint(anchorLocal + _rightHandOffset);
            rightRot = _playerRoot.rotation * _rightHandLocalRot;
        }

        /// <summary>
        /// Drives the avatar's humanoid head bone from the stored head rotation, which the
        /// local path writes every VR frame as the camera's rotation in player-root-local
        /// space and which a remote body receives from its owner. Runs in LateUpdate after
        /// the Animator evaluates, so the write overrides any animator-authored head pose for
        /// the duration of the VR session. When the owner exits VR the head weight lerps to
        /// zero and the animator's head channel resumes uncontested.
        ///
        /// headRotationOffset corrects for the Mecanim head bone's bind orientation not
        /// matching the camera/HMD orientation. Same pattern as the per-hand offsets.
        /// Identity is harmless to apply — the head sits in bind orientation aligned with
        /// body yaw — so no early return is needed before the first real value lands.
        /// </summary>
        void ApplyHeadRotation()
        {
            if (_head == null) return;
            if (_headIKWeight < 0.001f) return;

            Quaternion headOffset = Quaternion.Euler(headRotationOffset);
            Quaternion targetWorldRot = _playerRoot.rotation * _headLocalRot * headOffset;

            // Cache animator output so the IK weight lerp blends smoothly during the
            // VR-on/VR-off transition. Same pattern the arms use.
            Quaternion animHead = _head.rotation;
            _head.rotation = Quaternion.Slerp(animHead, targetWorldRot, _headIKWeight);
        }

        /// <summary>
        /// Two-bone IK solve. Given a fixed shoulder/upper-arm root and a target wrist
        /// pose, computes upper-arm and lower-arm rotations that place the hand at the
        /// target with the elbow bent in a natural direction.
        ///
        /// Math: law of cosines for the elbow bend angle, then construct the elbow
        /// position using a bend-direction hint (lateral away from torso, slightly down).
        /// </summary>
        void SolveTwoBoneIK(
            Transform upperArm, Transform lowerArm, Transform hand,
            float upperLen, float lowerLen,
            Vector3 targetPos, Quaternion targetRot,
            bool isLeftSide,
            out Quaternion upperRotation, out Quaternion lowerRotation, out Quaternion handRotation)
        {
            Vector3 shoulderPos = upperArm.position;
            Vector3 toTarget = targetPos - shoulderPos;
            float chord = toTarget.magnitude;

            // Clamp chord so the law of cosines stays valid even when reaching beyond arm extent.
            float armExtent = upperLen + lowerLen;
            float clampedChord = Mathf.Clamp(chord, 0.01f, armExtent - 0.01f);

            // Law of cosines: angle at shoulder between upper arm and chord.
            float cosShoulder = (upperLen * upperLen + clampedChord * clampedChord - lowerLen * lowerLen)
                                / (2f * upperLen * clampedChord);
            cosShoulder = Mathf.Clamp(cosShoulder, -1f, 1f);
            float shoulderAngle = Mathf.Acos(cosShoulder);

            // Bend direction hint: laterally outward from the torso, slightly down.
            // In avatar root space, "outward" is +X for right arm, -X for left arm; "down" is -Y.
            float lateralSign = isLeftSide ? -1f : 1f;
            Vector3 outwardWorld = _playerRoot.right * lateralSign;
            Vector3 downWorld = -_playerRoot.up;
            Vector3 bendHint = (outwardWorld * elbowOutwardHint + downWorld * elbowDownwardHint).normalized;

            Vector3 chordDir = toTarget.normalized;

            // Build elbow position. Project bendHint onto the plane perpendicular to chord
            // to get the actual bend direction; then offset from the chord by the IK geometry.
            Vector3 bendPerp = (bendHint - Vector3.Dot(bendHint, chordDir) * chordDir).normalized;
            if (bendPerp.sqrMagnitude < 0.001f)
            {
                // Degenerate: bendHint parallel to chord. Pick a fallback perpendicular.
                bendPerp = Vector3.Cross(chordDir, _playerRoot.forward).normalized;
                if (bendPerp.sqrMagnitude < 0.001f)
                    bendPerp = Vector3.Cross(chordDir, Vector3.up).normalized;
            }

            float alongChord = Mathf.Cos(shoulderAngle) * upperLen;
            float perpFromChord = Mathf.Sin(shoulderAngle) * upperLen;
            Vector3 elbowPos = shoulderPos + chordDir * alongChord + bendPerp * perpFromChord;

            // Build rotations. We pre-multiply by the inverse of each bone's bind-pose
            // forward direction to get a corrective rotation that aligns the actual bone
            // with the desired direction. For a humanoid Mecanim rig, the bone's forward
            // in world space is (childPos - bonePos), so that is the reference.
            Vector3 upperForward = elbowPos - shoulderPos;
            Vector3 lowerForward = targetPos - elbowPos;

            Vector3 upperBindForward = lowerArm.position - upperArm.position;
            Vector3 lowerBindForward = hand.position - lowerArm.position;

            Quaternion upperDelta = Quaternion.FromToRotation(upperBindForward, upperForward);

            upperRotation = upperDelta * upperArm.rotation;

            // After the upper arm rotates, the lower arm's bind-forward rotates with it,
            // so the lower delta is computed against the already-rotated reference.
            Vector3 lowerBindForwardAfterUpper = upperDelta * lowerBindForward;
            Quaternion lowerDeltaAdjusted = Quaternion.FromToRotation(lowerBindForwardAfterUpper, lowerForward);
            lowerRotation = lowerDeltaAdjusted * (upperDelta * lowerArm.rotation);

            // Apply per-hand rotation offset. WebXR controller pose and Mecanim humanoid
            // hand bone orientation use different axis conventions; the offset is a fixed
            // correction. The two offsets are mirror images so both hands rotate
            // symmetrically about the avatar's centerline.
            Vector3 offsetEuler = isLeftSide ? leftHandRotationOffset : rightHandRotationOffset;
            Quaternion handOffset = Quaternion.Euler(offsetEuler);
            handRotation = targetRot * handOffset;
        }

        /// <summary>
        /// Used by U3DAvatarManager to query whether the avatar should be visible from the
        /// local viewpoint. Returns true when this avatar should render. Returns false only
        /// for the local player's own avatar in desktop first-person when the creator opted
        /// into hideInFirstPerson, or for any avatar riding a steerable in HiddenAvatar mode.
        /// </summary>
        public bool ShouldRender(bool hideInFirstPersonPref)
        {
            // Remote avatar: always visible unless the steerable it is riding hides it.
            if (_playerController == null)
            {
                if (_resolvedSteerable != null
                    && _resolvedSteerable.AvatarMode == U3D.SteerableAvatarMode.HiddenAvatar)
                    return false;
                return true;
            }

            // Hide the humanoid when the local player is steering in HiddenAvatar mode.
            // Checked before the VR early-out so it applies in VR too, not just desktop.
            if (U3D.U3DSteerable.CurrentlySteering != null
                && U3D.U3DSteerable.CurrentlySteering.AvatarMode == U3D.SteerableAvatarMode.HiddenAvatar)
                return false;

            // Local VR player: show the body.
            if (_playerController.IsInVRMode) return true;

            // Local desktop player: respect the creator's hideInFirstPerson preference.
            if (hideInFirstPersonPref
                && _playerController.IsFirstPerson
                && !_playerController.IsCameraTransitioning)
                return false;

            return true;
        }
    }
}