using U3D;
using U3D.Net;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(100)]
[RequireComponent(typeof(CharacterController), typeof(PlayerInput))]

public class U3DPlayerController : MonoBehaviour
{
    [Header("Basic Movement")]
    [SerializeField] private bool enableMovement = true;
    [SerializeField] private float walkSpeed = 4f;
    [SerializeField] private float runSpeed = 8f;
    [SerializeField] private float gravity = -20f;

    [HideInInspector][SerializeField] private float groundCheckDistance = 0.1f;

    public enum PerspectiveMode { FirstPersonOnly, ThirdPersonOnly, SmoothScroll }

    [Header("Perspective Control")]
    [SerializeField] private PerspectiveMode perspectiveMode = PerspectiveMode.SmoothScroll;

    [HideInInspector][SerializeField] private float perspectiveTransitionSpeed = 8f;
    [HideInInspector][SerializeField] private bool enableCameraCollision = true;
    [HideInInspector][SerializeField] private bool enableSmoothTransitions = true;

    [Header("Mouse Sensitivity Settings")]
    [SerializeField] private float baseMouseSensitivity = 1.0f;
    [SerializeField] private float webglSensitivityMultiplier = 0.25f;
    [SerializeField] private float mobileSensitivityMultiplier = 0.8f;
    [SerializeField] private float userSensitivityMultiplier = 1.0f;
    [SerializeField] private bool enableMouseSmoothing = true;

    [Tooltip("How far mouse look blends toward each new sample. 1 is no smoothing at all, lower is smoother and laggier. Runs once per frame, so this is not the same scale as the old per-tick value.")]
    [Range(0.05f, 1f)]
    [SerializeField] private float mouseSmoothingAmount = 0.5f;

    [HideInInspector] private float mouseSensitivity;
    [HideInInspector] private float cameraOrbitSensitivity;

    [HideInInspector][SerializeField] private float lookUpLimit = 80f;
    [HideInInspector][SerializeField] private float lookDownLimit = -80f;
    [HideInInspector][SerializeField] private float cameraCollisionRadius = 0.2f;
    [HideInInspector][SerializeField] private float cameraCollisionBuffer = 0.1f;

    [Header("AAA Camera System")]
    [SerializeField] private bool enableAdvancedCamera = true;
    [SerializeField] private float characterTurnSpeed = 90f;

    [Header("Mouse Look Behavior")]
    [SerializeField] private bool enableAlwaysFreeLook = true;

    [Header("Third-Person Camera")]
    [Tooltip("How high the camera sits in third-person view, in player-root-local space. First-person height comes from the Camera child's Y position in the prefab. Set this to match the prefab Camera child's Y if you want consistent height across the perspective switch, or set it lower for an over-the-shoulder feel.")]
    [SerializeField] private float thirdPersonCameraHeight = 1.65f;

    [Tooltip("How far behind the player the third-person camera sits.")]
    [SerializeField] private float thirdPersonCameraDistance = 5f;

    [Tooltip("How long, in seconds, the camera takes to transition between first and third person when using SmoothScroll perspective mode.")]
    [SerializeField] private float transitionTime = 1.5f;

    private U3DInteractionManager _interactionManager;

    private float _runtimeMouseSensitivity;
    private float _runtimeOrbitSensitivity;
    private RuntimePlatform _currentPlatform;

    private float currentTransitionValue = 0f;
    private float targetTransitionValue = 0f;
    private bool isTransitioning = false;
    private Vector3 originalFirstPersonPosition;

    private Transform cameraPivot;
    private float cameraYaw = 0f;
    private float cameraPitchAdvanced = 0f;
    private bool isLeftMouseDragging = false;
    private bool isRightMouseDragging = false;
    private bool isBothMouseForward = false;

    private bool advancedModeActive = false;

    [Header("Advanced Movement")]
    [SerializeField] private bool enableSprintToggle = true;
    [SerializeField] private bool enableAutoRun = true;
    [SerializeField] private bool enableFlying = true;
    [SerializeField] private bool enableCrouchToggle = true;
    [SerializeField] private bool enableTeleport = true;
    [SerializeField] private bool enableViewZoom = true;

    [Header("Jump Settings")]
    [SerializeField] private bool enableJumping = true;
    [SerializeField] private float jumpHeight = 2f;
    [SerializeField] private float[] additionalJumps = new float[] { 4f };

    [HideInInspector][SerializeField] private float zoomFOV = 30f;
    [HideInInspector][SerializeField] private float defaultFOV = 60f;
    [HideInInspector][SerializeField] private float zoomSpeed = 5f;

    private const int DISPLAY_NAME_MAX_LENGTH = 40;

    private Vector2 _smoothedMouseInput = Vector2.zero;

    private CharacterController characterController;
    private PlayerInput playerInput;
    private Camera playerCamera;

    private Vector3 _velocity;
    private Vector3 _moveVelocity;
    private Vector2 moveInput;
    private bool isGrounded;
    private int jumpCount;
    private bool isSprinting;
    private bool isCrouching;
    private bool isFlying;
    private bool isAutoRunning;

    private float cameraPitch;
    private bool isFirstPerson = true;
    private Vector3 firstPersonPosition;
    private Vector3 thirdPersonPosition;
    private float currentCameraDistance;
    private float targetFOV;
    private bool lookInverted;
    private float originalCameraHeight;
    private float crouchCameraOffset = -0.5f;

    private bool _jumpPressedPending;
    private U3D.U3DTrampoline _pendingTrampoline;
    private float _pendingTrampolineHeight;
    private float _pendingTrampolineLaunchTime;

    private U3DWebGLCursorManager _cursorManager;
    private U3D.Input.U3DPlayerInput _input;

    private bool _isInVRMode = false;
    private U3D.XR.U3DWebXRManager _webXRManager;
    private U3D.XR.U3DVRTeleporter _vrTeleporter;
    private U3DGazePointer _gazePointer;

    private UnityEngine.InputSystem.XR.TrackedPoseDriver _headInputSystemPoseDriver;
    private Transform _avatarHeadBone;
    private Transform _rawHmdReference;
    private U3DAvatarManager _avatarManager;

    [Header("VR Eye Offset")]
    [Tooltip("Camera position relative to the avatar's head bone in player-local space. Increase Y if the camera sits too low (pointing at the neck). Increase Z to move the camera forward inside the head. Adjust until the camera lands at eye level when you look at the avatar in a mirror.")]
    [SerializeField] private Vector3 vrEyeOffset = new Vector3(0f, 0.2f, 0.08f);

    [Header("VR Body-Follow-Head")]
    [Tooltip("How far (in degrees) the head may turn away from the body before the body turns to keep up, in degrees. 60 gives a 120-degree free-look range and is close to a real neck limit. The body turns only far enough to bring the head back to this angle, and the view does not turn with it.")]
    [SerializeField] private float vrBodyFollowDeadzone = 60f;

    [Tooltip("How fast (degrees per second) the body turns to keep up with the head, once the head is past the limit above. The player's own view does not move while this happens, so this is a matter of feel and of how the body looks to other people. 90 matches characterTurnSpeed.")]
    [SerializeField] private float vrBodyFollowSpeed = 90f;

    private bool _vrRecenterPending = false;
    private float _vrRecenterTargetYaw = 0f;
    private Transform _trackingOrigin;

    [Header("VR Teleport")]
    [Tooltip("Material used for the VR teleport arc and reticle line renderers. Assign any URP/Unlit material in the Inspector. Required — without it the arc will not render in WebGL builds.")]
    [SerializeField] private Material vrTeleportMaterial;

    private const float VR_MOVEMENT_SPEED_MULTIPLIER = 1.0f;

    private U3D.U3DRideableController _currentRideable;

    // ==================== LOCAL STATE SURFACE ====================
    // Every value below replaced a [Networked] property. Read-only where this
    // controller owns the value, with an explicit setter where an outside
    // component is the writer. Phase 5 reads these to assemble the packet.

    public bool IsMoving { get; private set; }
    public bool IsJumping { get; private set; }
    public bool IsSwimming { get; private set; }
    public bool IsClimbing { get; private set; }
    public bool IsSeated { get; private set; }
    public bool IsPushing { get; private set; }
    public bool IsPulling { get; private set; }
    public bool SuppressLocomotion { get; private set; }
    public float CameraPitch { get; private set; }
    public string DisplayName { get; private set; }
    public U3D.U3DSteerable CurrentSteerable { get; private set; }
    public U3D.U3DRideableController CurrentRideable => _currentRideable;

    public Vector2 MoveIntent { get; private set; }
    public float VerticalVelocity => _velocity.y;

    void CalculateRuntimeSensitivity()
    {
        _currentPlatform = Application.platform;

        bool touchInputActive = U3D.Input.U3DSimpleTouchZones.Instance != null
            && U3D.Input.U3DSimpleTouchZones.Instance.IsTouchEnabled;

        float platformMultiplier;

        if (touchInputActive)
        {
            // Touch look must not be scaled by the desktop-browser multiplier.
            // Application.platform reports WebGLPlayer on mobile, which would
            // otherwise quarter finger-drag look via webglSensitivityMultiplier.
            // Touch uses the shared mouse base scaled only by the user's Settings
            // preference — no platform damper.
            platformMultiplier = 1.0f;
        }
        else
        {
            switch (_currentPlatform)
            {
                case RuntimePlatform.WebGLPlayer:
                    platformMultiplier = webglSensitivityMultiplier;
                    break;
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.Android:
                    platformMultiplier = mobileSensitivityMultiplier;
                    break;
                default:
                    platformMultiplier = 1.0f;
                    break;
            }
        }

        _runtimeMouseSensitivity = baseMouseSensitivity * platformMultiplier * userSensitivityMultiplier;
        _runtimeOrbitSensitivity = baseMouseSensitivity * platformMultiplier * userSensitivityMultiplier;

        mouseSensitivity = _runtimeMouseSensitivity;
        cameraOrbitSensitivity = _runtimeOrbitSensitivity;
    }

    public void SetUserSensitivity(float sensitivity)
    {
        userSensitivityMultiplier = Mathf.Clamp(sensitivity, 0.1f, 3.0f);
        CalculateRuntimeSensitivity();
        SaveSensitivitySettings();
    }

    public float GetUserSensitivity() => userSensitivityMultiplier;
    public float GetEffectiveSensitivity() => _runtimeMouseSensitivity;

    void LoadSensitivitySettings()
    {
        userSensitivityMultiplier = PlayerPrefs.GetFloat("U3D_MouseSensitivity", 1.0f);
    }

    void SaveSensitivitySettings()
    {
        PlayerPrefs.SetFloat("U3D_MouseSensitivity", userSensitivityMultiplier);
        PlayerPrefs.Save();
    }

    void Awake()
    {
        characterController = GetComponent<CharacterController>();
        playerInput = GetComponent<PlayerInput>();
        playerCamera = GetComponentInChildren<Camera>();

        if (playerCamera == null)
        {
            Debug.LogError("U3DPlayerController: No Camera found in children. Please add a Camera as a child object.");
            enabled = false;
            return;
        }

        firstPersonPosition = playerCamera.transform.localPosition;
        thirdPersonPosition = firstPersonPosition + Vector3.back * thirdPersonCameraDistance;
        currentCameraDistance = 0f;
        targetFOV = defaultFOV;
        playerCamera.fieldOfView = defaultFOV;

        _headInputSystemPoseDriver = playerCamera.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
        if (_headInputSystemPoseDriver != null)
            _headInputSystemPoseDriver.enabled = false;
        else
            Debug.LogError("U3DPlayerController: No Tracked Pose Driver (Input System) found on the Player Camera. VR head tracking will not work.");

        _avatarManager = GetComponent<U3DAvatarManager>();

        InitializeCameraPivot();
        LoadPlayerPreferences();
    }

    void Start()
    {
        InitializeComponents();
        CalculateRuntimeSensitivity();
        ConfigureLocalPlayer();

        if (enableAdvancedCamera && cameraPivot != null)
            cameraYaw = transform.eulerAngles.y;

        _webXRManager = U3D.XR.U3DWebXRManager.Instance;
        if (_webXRManager != null)
            _webXRManager.RegisterLocalPlayer(this);

        switch (perspectiveMode)
        {
            case PerspectiveMode.FirstPersonOnly: SetFirstPerson(); break;
            case PerspectiveMode.ThirdPersonOnly: SetThirdPerson(); break;
            case PerspectiveMode.SmoothScroll: SetFirstPerson(); break;
        }

        // Identity wiring: subscribe to the local-profile-ready event and apply
        // immediately if the profile already landed before this ran. Push-driven,
        // no polling. OnDestroy unsubscribes — the event is static, so a missed
        // unsubscribe leaks a destroyed player on every scene load.
        FirebaseIntegration.OnLocalProfileReady += HandleLocalProfileReady;
        if (FirebaseIntegration.IsLocalProfileReady)
            ApplyDisplayNameFromProfile(FirebaseIntegration.LocalProfile);

        if (Net.Session != null)
            Net.Session.PeerReachable += HandlePeerReachable;
    }

    void OnDestroy()
    {
        FirebaseIntegration.OnLocalProfileReady -= HandleLocalProfileReady;

        if (Net.Session != null)
            Net.Session.PeerReachable -= HandlePeerReachable;

        if (_webXRManager != null)
            _webXRManager.UnregisterLocalPlayer(this);
    }

    void InitializeCameraPivot()
    {
        if (!enableAdvancedCamera) return;

        originalFirstPersonPosition = firstPersonPosition;

        GameObject pivotGO = new GameObject("CameraPivot");
        cameraPivot = pivotGO.transform;
        cameraPivot.SetParent(transform);
        cameraPivot.localPosition = firstPersonPosition;
        cameraPivot.localRotation = Quaternion.identity;

        if (playerCamera != null)
        {
            playerCamera.transform.SetParent(cameraPivot);
            UpdateCameraTransitionPosition();
            cameraYaw = transform.eulerAngles.y;
            cameraPitchAdvanced = 0f;
        }
    }

    void UpdateCameraTransitionPosition()
    {
        if (cameraPivot == null || playerCamera == null) return;

        Vector3 targetPosition;

        if (currentTransitionValue <= 0.01f)
        {
            // Pure first-person: camera sits exactly at firstPersonPosition,
            // which is the prefab Camera child's local position captured in Awake.
            targetPosition = Vector3.zero;
        }
        else
        {
            // Linearly interpolate distance and height from first-person to third-person
            // values across the transition. First-person height is firstPersonPosition.y
            // (the prefab Camera Y), third-person height is the Inspector field.
            float distance = Mathf.Lerp(0f, thirdPersonCameraDistance, currentTransitionValue);
            float heightAtTransition = Mathf.Lerp(firstPersonPosition.y, thirdPersonCameraHeight, currentTransitionValue);
            float relativeHeight = heightAtTransition - firstPersonPosition.y;
            targetPosition = new Vector3(0f, relativeHeight, -distance);
        }

        if (isCrouching)
            targetPosition.y += crouchCameraOffset;

        if (currentTransitionValue > 0.01f && enableCameraCollision)
            targetPosition = GetCollisionSafeCameraPosition(targetPosition);

        playerCamera.transform.localPosition = targetPosition;
    }

    void InitializeComponents()
    {
        _cursorManager = FindAnyObjectByType<U3DWebGLCursorManager>();
        if (_cursorManager == null)
        {
            Debug.LogWarning("U3DPlayerController: No U3DWebGLCursorManager found in the scene. Mouse look requires it. Add the U3D CORE - DO NOT DELETE prefab to this scene.");
        }

        // Camera-forward raycast drives worldspace UI events globally — works on desktop
        // and in VR with the same code path. Fully local and frame-based: it reads the
        // Interact action itself and is not part of any input snapshot.
        if (_gazePointer == null && playerCamera != null)
        {
            GameObject pointerGO = new GameObject("U3DGazePointer");
            pointerGO.transform.SetParent(transform, false);
            _gazePointer = pointerGO.AddComponent<U3DGazePointer>();
            _gazePointer.Initialize(playerCamera, this);
        }
    }

    void ConfigureLocalPlayer()
    {
        if (playerInput != null)
            playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;

        if (playerCamera != null)
        {
            playerCamera.enabled = true;
            playerCamera.tag = "MainCamera";
        }

        InitializeInteractionManager();
    }

    void InitializeInteractionManager()
    {
        _interactionManager = FindAnyObjectByType<U3DInteractionManager>();

        if (_interactionManager == null)
        {
            GameObject interactionManagerObj = new GameObject("U3DInteractionManager");
            _interactionManager = interactionManagerObj.AddComponent<U3DInteractionManager>();
        }
    }

    bool IsCursorLocked()
    {
        if (_isInVRMode) return true;
        if (_cursorManager != null) return _cursorManager.IsCursorLocked;
        return false;
    }

    private bool ResolveInput()
    {
        if (_input == null)
            _input = U3D.Input.U3DPlayerInput.Instance;

        return _input != null;
    }

    // ==================== FRAME LOOP ====================
    // Every press edge is handled here, once per frame, because the fixed step runs
    // zero, one or two times per frame and would eat or repeat them. Look lives here
    // for the same reason: on desktop it is an accumulated mouse delta, so a frame
    // with two fixed steps applied the same hand movement twice.

    void Update()
    {
        if (_collisionHolders.Count > 0)
            RefreshCollisionEnabled();

        if (!ResolveInput()) return;

        var input = _input.ConsumeInput();

        _jumpPressedPending = input.JumpPressed;

        HandleDismountIntent(input);
        HandleButtonInputs(input);
        HandleTeleportPress(input);
        HandleLook(input);
        HandleLocalCameraRender();
        HandleCameraPositioning();
        HandleZoom();
    }

    // ==================== FIXED LOOP ====================
    // Levels and rates only, read through PeekInput, which clears nothing.

    void FixedUpdate()
    {
        if (!ResolveInput()) return;

        var input = _input.PeekInput();

        ProcessPendingTrampolineLaunch();

        if (_isInVRMode)
        {
            HandleVRMovement(input);
            UpdateVRCameraPitch();
        }
        else
        {
            // Riding skips locomotion but must not skip the input capture. moveInput is
            // written nowhere else, so leaving it out froze it at whatever the player was
            // doing when they mounted — which a seat childed to a rideable then read as
            // the player trying to stand. B62.
            if (_currentRideable == null)
                HandleMovement(input);
            else
                moveInput = input.MovementInput;
        }

        ApplyMotion();
    }

    void HandleDismountIntent(U3DPlayerInputState input)
    {
        if (_currentRideable == null) return;

        bool wantsDismount;

        if (_isInVRMode)
        {
            // The stick only counts as dismount intent when the teleporter is not
            // consuming it. Fly is a discrete press and is unambiguous either way.
            bool teleporterHasStick = _vrTeleporter != null && _vrTeleporter.IsArmed;
            wantsDismount = (!teleporterHasStick && input.MovementInput.magnitude > 0.1f)
                || input.FlyPressed;
        }
        else
        {
            wantsDismount = input.MovementInput.magnitude > 0.1f
                || input.BothMouseHeld
                || input.FlyPressed
                || input.AutoRunTogglePressed;
        }

        if (wantsDismount)
            DismountRideable(_currentRideable);
    }

    void HandleButtonInputs(U3DPlayerInputState input)
    {
        if (enableJumping && input.JumpPressed)
            HandleJump();

        if (enableSprintToggle && input.SprintPressed)
            isSprinting = !isSprinting;

        if (enableCrouchToggle && input.CrouchPressed)
        {
            isCrouching = !isCrouching;

            if (isCrouching)
            {
                characterController.height = 1f;
                characterController.center = new Vector3(0, 0.5f, 0);
            }
            else
            {
                characterController.height = 2f;
                characterController.center = new Vector3(0, 1f, 0);
            }
        }

        if (isCrouching && IsMoving && !isFlying && !IsSwimming)
        {
            isCrouching = false;
            characterController.height = 2f;
            characterController.center = new Vector3(0, 1f, 0);
        }

        if (enableFlying && input.FlyPressed)
        {
            isFlying = !isFlying;
            _velocity = Vector3.zero;
        }

        if (enableAutoRun && input.AutoRunTogglePressed)
            isAutoRunning = !isAutoRunning;

        if (input.InteractPressed)
        {
            if (_interactionManager != null)
                _interactionManager.OnPlayerInteract();
            else
                Debug.LogWarning("No interaction manager found - interaction ignored");
        }

        if (input.RemovePressed)
        {
            if (CurrentSteerable != null)
            {
                CurrentSteerable.Exit();
            }
            else
            {
                U3DPlayerAttachments attachments = GetComponent<U3DPlayerAttachments>();
                if (attachments != null)
                    attachments.RemoveLast();
            }
        }

        targetFOV = input.ZoomHeld ? zoomFOV : defaultFOV;

        if (perspectiveMode == PerspectiveMode.SmoothScroll && Mathf.Abs(input.PerspectiveScroll) > 0.1f)
        {
            if (input.PerspectiveScroll > 0.1f && !isFirstPerson)
                SetFirstPerson();
            else if (input.PerspectiveScroll < -0.1f && isFirstPerson)
                SetThirdPerson();
        }
    }

    void HandleTeleportPress(U3DPlayerInputState input)
    {
        if (!enableTeleport) return;
        if (!input.TeleportPressed) return;

        if (_isInVRMode)
        {
            if (_vrTeleporter != null)
                _vrTeleporter.OnTeleportButtonPressed();
        }
        else
        {
            PerformTeleport();
        }
    }

    void HandleJump()
    {
        if (IsClimbing) return;
        if (isFlying) return;

        if (_currentRideable != null)
            DismountRideable(_currentRideable);

        if (isGrounded || jumpCount < additionalJumps.Length + 1)
        {
            float jumpForce;
            if (jumpCount == 0)
                jumpForce = Mathf.Sqrt(jumpHeight * -2f * gravity);
            else if (jumpCount <= additionalJumps.Length)
                jumpForce = Mathf.Sqrt(additionalJumps[jumpCount - 1] * -2f * gravity);
            else
                return;

            _velocity.y = jumpForce;
            jumpCount++;
            IsJumping = true;
        }
    }

    void HandleLook(U3DPlayerInputState input)
    {
        if (!enableMovement) return;
        if (_isInVRMode) return;
        if (!IsLocalLookMoveInputAuthoritative()) return;

        Vector2 rawLookInput = input.LookInput;

        if (lookInverted)
            rawLookInput.y = -rawLookInput.y;

        Vector2 sensitivityAdjustedInput = rawLookInput * _runtimeMouseSensitivity;

        bool touchInputActive = U3D.Input.U3DSimpleTouchZones.Instance != null
            && U3D.Input.U3DSimpleTouchZones.Instance.IsTouchEnabled;

        Vector2 finalLookInput;
        if (enableMouseSmoothing && !touchInputActive)
            finalLookInput = Vector2.Lerp(_smoothedMouseInput, sensitivityAdjustedInput, mouseSmoothingAmount);
        else
            finalLookInput = sensitivityAdjustedInput;

        _smoothedMouseInput = finalLookInput;

        HandleAdvancedMouseControls(input);

        if (enableAlwaysFreeLook && !isLeftMouseDragging && !isRightMouseDragging && !isBothMouseForward)
        {
            if (enableAdvancedCamera && cameraPivot != null)
            {
                if (Mathf.Abs(finalLookInput.x) > 0.01f)
                {
                    transform.Rotate(Vector3.up, finalLookInput.x);
                    cameraYaw += finalLookInput.x;
                }

                if (Mathf.Abs(finalLookInput.y) > 0.01f)
                {
                    cameraPitchAdvanced -= finalLookInput.y;
                    cameraPitchAdvanced = Mathf.Clamp(cameraPitchAdvanced, lookDownLimit, lookUpLimit);
                    CameraPitch = cameraPitchAdvanced;
                }

                cameraPivot.localRotation = Quaternion.Euler(cameraPitchAdvanced, 0f, 0f);
            }
            else
            {
                if (Mathf.Abs(finalLookInput.x) > 0.01f)
                    transform.Rotate(Vector3.up, finalLookInput.x);

                if (Mathf.Abs(finalLookInput.y) > 0.01f)
                {
                    cameraPitch -= finalLookInput.y;
                    cameraPitch = Mathf.Clamp(cameraPitch, lookDownLimit, lookUpLimit);
                    CameraPitch = cameraPitch;
                }
            }
        }
        else if (!enableAlwaysFreeLook && !isLeftMouseDragging && !isRightMouseDragging && !enableAdvancedCamera)
        {
            if (Mathf.Abs(finalLookInput.x) > 0.01f)
                transform.Rotate(Vector3.up, finalLookInput.x);

            if (Mathf.Abs(finalLookInput.y) > 0.01f)
            {
                cameraPitch -= finalLookInput.y;
                cameraPitch = Mathf.Clamp(cameraPitch, lookDownLimit, lookUpLimit);
                CameraPitch = cameraPitch;
            }
        }
    }

    /// <summary>
    /// Single authority for whether local non-VR game look/move input is live
    /// this frame. Consulted only on the non-VR path — VR routes through
    /// HandleVRMovement, so this method intentionally contains no VR branch.
    ///
    /// Desktop: authoritative only when the pointer is captured (IsCursorLocked),
    /// preserving free-cursor-over-UI suppression.
    /// Touch: authoritative when touch is the active input source. Touch has no
    /// pointer-capture concept; the touch zone component already owns the "is the
    /// user driving input" determination.
    /// </summary>
    private bool IsLocalLookMoveInputAuthoritative()
    {
        bool touchInputActive = U3D.Input.U3DSimpleTouchZones.Instance != null
            && U3D.Input.U3DSimpleTouchZones.Instance.IsTouchEnabled;

        if (touchInputActive)
            return true;

        return IsCursorLocked();
    }

    void HandleAdvancedMouseControls(U3DPlayerInputState input)
    {
        if (!enableAdvancedCamera || cameraPivot == null) return;

        bool wasLeftMouseDragging = isLeftMouseDragging;

        isLeftMouseDragging = input.LeftMouseHeld;
        isRightMouseDragging = input.RightMouseHeld;
        isBothMouseForward = input.BothMouseHeld;

        if (wasLeftMouseDragging && !isLeftMouseDragging && !isRightMouseDragging && !isBothMouseForward)
            cameraYaw = transform.eulerAngles.y;

        Vector2 processedInput = _smoothedMouseInput;

        if (isBothMouseForward)
        {
            if (Mathf.Abs(processedInput.x) > 0.01f)
            {
                transform.Rotate(Vector3.up, processedInput.x);
                cameraYaw += processedInput.x;
            }
            if (Mathf.Abs(processedInput.y) > 0.01f)
            {
                cameraPitchAdvanced -= processedInput.y;
                cameraPitchAdvanced = Mathf.Clamp(cameraPitchAdvanced, lookDownLimit, lookUpLimit);
                CameraPitch = cameraPitchAdvanced;
            }
        }
        else if (isRightMouseDragging && !isLeftMouseDragging)
        {
            if (Mathf.Abs(processedInput.x) > 0.01f)
            {
                transform.Rotate(Vector3.up, processedInput.x);
                cameraYaw += processedInput.x;
            }
            if (Mathf.Abs(processedInput.y) > 0.01f)
            {
                cameraPitchAdvanced -= processedInput.y;
                cameraPitchAdvanced = Mathf.Clamp(cameraPitchAdvanced, lookDownLimit, lookUpLimit);
                CameraPitch = cameraPitchAdvanced;
            }
        }
        else if (isLeftMouseDragging && !isRightMouseDragging)
        {
            if (Mathf.Abs(processedInput.x) > 0.01f)
                cameraYaw += processedInput.x;
            if (Mathf.Abs(processedInput.y) > 0.01f)
            {
                cameraPitchAdvanced -= processedInput.y;
                cameraPitchAdvanced = Mathf.Clamp(cameraPitchAdvanced, lookDownLimit, lookUpLimit);
            }
            CameraPitch = cameraPitchAdvanced;
        }

        if (isLeftMouseDragging && !isRightMouseDragging)
            cameraPivot.rotation = Quaternion.Euler(cameraPitchAdvanced, cameraYaw, 0f);
        else
            cameraPivot.localRotation = Quaternion.Euler(cameraPitchAdvanced, 0f, 0f);
    }

    void HandleMovement(U3DPlayerInputState input)
    {
        if (!enableMovement) return;
        if (!IsLocalLookMoveInputAuthoritative()) return;

        moveInput = input.MovementInput;

        // While seated on a plain U3DSeat the CharacterController is switched off — the
        // seat anchors the body directly. moveInput is captured above so U3DSeat can still
        // detect the player trying to move and stand them up, but the disabled controller
        // must not be driven. Steerables leave the controller enabled.
        if (!characterController.enabled) return;

        if (IsClimbing) return;

        Vector2 advancedMovement = HandleAdvancedKeyboardMovement(input);

        if (isBothMouseForward) advancedMovement.y = 1f;
        if (isAutoRunning) advancedMovement.y = 1f;

        Vector2 finalMovement = (advancedMovement.magnitude > 0.1f) ? advancedMovement : moveInput;

        if (enableAdvancedCamera && cameraPivot != null)
        {
            bool isStartingToMove = (finalMovement.magnitude > 0.1f && !IsMoving);
            if (isStartingToMove && !isRightMouseDragging)
                transform.rotation = Quaternion.Euler(0, cameraYaw, 0);
        }

        Vector3 forward, right;

        if (enableAdvancedCamera && cameraPivot != null)
        {
            forward = cameraPivot.forward;
            right = cameraPivot.right;
        }
        else
        {
            forward = playerCamera.transform.forward;
            right = playerCamera.transform.right;
        }

        bool freeMovement = isFlying || IsSwimming;

        if (!freeMovement)
        {
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();
        }

        Vector3 moveDirection = (forward * finalMovement.y + right * finalMovement.x).normalized;
        float currentSpeed = GetCurrentSpeed();
        Vector3 moveVelocity = moveDirection * currentSpeed;

        if (freeMovement)
        {
            // Level state, not the press edge: ascend/descend continues for as long as the
            // key is held.
            Vector3 flyDirection = moveDirection;
            if (input.JumpHeld) flyDirection += Vector3.up;
            if (input.CrouchHeld) flyDirection += Vector3.down;
            _moveVelocity = flyDirection * currentSpeed;
        }
        else
        {
            _moveVelocity = moveVelocity;
        }

        IsMoving = moveVelocity.magnitude > 0.1f;
        MoveIntent = ToBodyLocalIntent(moveDirection);
    }

    Vector2 HandleAdvancedKeyboardMovement(U3DPlayerInputState input)
    {
        Vector2 advancedMovement = Vector2.zero;

        if (moveInput.y != 0)
            advancedMovement.y = moveInput.y;

        if (enableAdvancedCamera)
        {
            if (!isRightMouseDragging)
            {
                if (input.TurnLeft)
                {
                    float turnDelta = -characterTurnSpeed * Time.fixedDeltaTime;
                    transform.Rotate(Vector3.up, turnDelta);
                    if (cameraPivot != null) cameraYaw += turnDelta;
                }
                if (input.TurnRight)
                {
                    float turnDelta = characterTurnSpeed * Time.fixedDeltaTime;
                    transform.Rotate(Vector3.up, turnDelta);
                    if (cameraPivot != null) cameraYaw += turnDelta;
                }
            }

            if (input.StrafeLeft) advancedMovement.x = -1f;
            if (input.StrafeRight) advancedMovement.x = 1f;
        }
        else
        {
            advancedMovement.x = moveInput.x;
        }

        return advancedMovement;
    }

    /// <summary>
    /// One CharacterController.Move per fixed step, carrying horizontal and vertical
    /// together. Gravity is always integrated. Previously gravity returned early while
    /// grounded and horizontal moved on its own call, so a jump impulse was discarded on
    /// the step that set it and only escaped the ground on frames where the controller's
    /// own isGrounded happened to flicker false. isGrounded is refreshed here, after the
    /// move, because that is the only point at which the controller has reported it.
    /// </summary>
    void ApplyMotion()
    {
        if (!characterController.enabled)
        {
            _moveVelocity = Vector3.zero;
            return;
        }

        if (IsClimbing)
        {
            _velocity = Vector3.zero;
            _moveVelocity = Vector3.zero;
            return;
        }

        float dt = Time.fixedDeltaTime;

        if (isFlying || IsSwimming)
        {
            _velocity.y = 0f;
            characterController.Move(_moveVelocity * dt);
        }
        else
        {
            _velocity.y += gravity * dt;

            Vector3 motion = _moveVelocity;
            motion.y += _velocity.y;
            characterController.Move(motion * dt);
        }

        isGrounded = characterController.isGrounded;

        if (isGrounded && _velocity.y < 0f)
        {
            _velocity.y = -2f;
            jumpCount = 0;
            IsJumping = false;
        }

        _moveVelocity = Vector3.zero;
    }

    // ==================== VR/WebXR MODE HANDLING ====================

    public void SetVRMode(bool enabled)
    {
        bool wasInVR = _isInVRMode;
        _isInVRMode = enabled;

        if (enabled && !wasInVR) EnterVRMode();
        else if (!enabled && wasInVR) ExitVRMode();
    }

    private void EnterVRMode()
    {
        if (_cursorManager != null)
            _cursorManager.SetVRMode(true);

        cameraPitch = 0f;
        cameraPitchAdvanced = 0f;

        // Auto-run has no VR binding (the X button belongs to attachment removal), so a
        // toggle carried in from desktop would be stuck on with no way to turn it off.
        // VR forward motion is the stick; clear the state on entry.
        isAutoRunning = false;

        // Capture the body's current heading before the pose driver starts writing the
        // camera's rotation. The recenter in LateUpdate turns the tracking origin until the
        // camera's world yaw lands on this heading, and leaves the body alone — so at entry
        // the head and the body agree whichever way the player is physically facing.
        _vrRecenterTargetYaw = transform.eulerAngles.y;
        _vrRecenterPending = true;

        if (_headInputSystemPoseDriver != null)
        {
            _headInputSystemPoseDriver.trackingType = UnityEngine.InputSystem.XR.TrackedPoseDriver.TrackingType.RotationOnly;
            _headInputSystemPoseDriver.enabled = true;
        }

        TryResolveHeadBone();

        EnsureTrackingOrigin();

        if (_trackingOrigin != null && playerCamera != null)
        {
            playerCamera.transform.SetParent(_trackingOrigin);
            playerCamera.transform.localRotation = Quaternion.identity;
        }

        EnsureRawHmdReference();

        if (_vrTeleporter == null)
        {
            var go = new GameObject("U3DVRTeleporter");
            _vrTeleporter = go.AddComponent<U3D.XR.U3DVRTeleporter>();
            _vrTeleporter.ArcMaterial = vrTeleportMaterial;
            _vrTeleporter.Initialize(this, playerCamera);
        }
        else
        {
            // Re-entry path: teleporter already exists, just refresh its camera ref
            // in case anything reparented during the previous VR session.
            _vrTeleporter.UpdateCamera(playerCamera);
        }

        if (vrTeleportMaterial == null)
            Debug.LogWarning("U3DPlayerController: vrTeleportMaterial is not assigned. VR teleport arc will not render. Assign a URP/Unlit material in the Inspector on the player prefab.");
    }

    /// <summary>
    /// Creates the XR tracking origin: a transform sitting between the player root and
    /// everything the headset poses. The camera, the raw HMD reference and both runtime
    /// controller objects all hang from it.
    ///
    /// It exists so that turning the body is a real change to the angle between the head and
    /// the body. The pose driver writes the headset's rotation into the camera's local
    /// rotation, so with the camera parented straight to the body, turning the body carried
    /// the camera with it and left that angle exactly as it was — the follow could never
    /// close it and ran until the player physically turned their head back.
    ///
    /// Everything tracked must share this parent, not just the camera. The hand poses are
    /// built by subtracting the headset's position from each controller's, so a controller
    /// left under the body while the headset reference sits under the origin would be
    /// measured in a different frame and both hands would sit off to one side by the angle
    /// between head and body.
    ///
    /// Created in code as CameraPivot is, so no prefab changes and nothing for a creator to
    /// configure. Destroyed with the rest of the VR state on exit.
    /// </summary>
    private void EnsureTrackingOrigin()
    {
        if (_trackingOrigin != null) return;

        GameObject originGO = new GameObject("U3D_TrackingOrigin");
        _trackingOrigin = originGO.transform;
        _trackingOrigin.SetParent(transform, false);
        _trackingOrigin.localPosition = Vector3.zero;
        _trackingOrigin.localRotation = Quaternion.identity;
    }

    private void EnsureRawHmdReference()
    {
        if (_rawHmdReference != null) return;

        GameObject hmdRefGO = new GameObject("U3D_RawHmdReference");
        hmdRefGO.SetActive(false);
        hmdRefGO.transform.SetParent(_trackingOrigin != null ? _trackingOrigin : transform, false);
        hmdRefGO.transform.localPosition = Vector3.zero;
        hmdRefGO.transform.localRotation = Quaternion.identity;

        var tpd = hmdRefGO.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
        tpd.trackingType = UnityEngine.InputSystem.XR.TrackedPoseDriver.TrackingType.PositionOnly;
        tpd.updateType = UnityEngine.InputSystem.XR.TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

        tpd.positionInput = new InputActionProperty(new InputAction(
            "U3D_RawHmdPosition",
            InputActionType.Value,
            "<XRHMD>/centerEyePosition",
            expectedControlType: "Vector3"));

        tpd.trackingStateInput = new InputActionProperty(new InputAction(
            "U3D_RawHmdTrackingState",
            InputActionType.Value,
            "<XRHMD>/trackingState",
            expectedControlType: "Integer"));

        hmdRefGO.SetActive(true);

        _rawHmdReference = hmdRefGO.transform;
    }

    private void ExitVRMode()
    {
        if (_headInputSystemPoseDriver != null)
            _headInputSystemPoseDriver.enabled = false;

        _avatarHeadBone = null;
        _vrRecenterPending = false;

        if (_rawHmdReference != null)
        {
            Destroy(_rawHmdReference.gameObject);
            _rawHmdReference = null;
        }

        if (_vrTeleporter != null)
        {
            _vrTeleporter.CancelAim();
            Destroy(_vrTeleporter.gameObject);
            _vrTeleporter = null;
        }

        // The camera leaves the tracking origin before the origin is destroyed, or it would
        // be destroyed with it. It returns to CameraPivot where there is one, and to the
        // player root where the advanced camera is switched off and no pivot was ever built.
        if (playerCamera != null)
        {
            playerCamera.transform.SetParent(cameraPivot != null ? cameraPivot : transform);
            playerCamera.transform.localRotation = Quaternion.identity;

            if (cameraPivot != null)
                UpdateCameraTransitionPosition();
            else
                playerCamera.transform.localPosition = firstPersonPosition;
        }

        // Destroying the origin takes the runtime controller objects with it, since they are
        // its children. U3DAvatarIK rebuilds them on the next VR entry, because its own
        // references read as null once they are gone.
        if (_trackingOrigin != null)
        {
            Destroy(_trackingOrigin.gameObject);
            _trackingOrigin = null;
        }

        if (playerCamera != null)
        {
            cameraYaw = transform.eulerAngles.y;
            cameraPitch = 0f;
        }

        if (_cursorManager != null)
            _cursorManager.SetVRMode(false);
    }

    private void TryResolveHeadBone()
    {
        if (_avatarHeadBone != null) return;
        if (_avatarManager == null) return;

        Animator avatarAnimator = _avatarManager.GetAvatarAnimator();
        if (avatarAnimator == null || !avatarAnimator.isHuman) return;

        _avatarHeadBone = avatarAnimator.GetBoneTransform(HumanBodyBones.Head);
    }

    void LateUpdate()
    {
        if (!_isInVRMode || playerCamera == null) return;

        if (_avatarHeadBone == null)
            TryResolveHeadBone();

        if (_avatarHeadBone != null && _trackingOrigin != null)
        {
            // Recenter pass: runs once per VR session entry, on the first frame the pose
            // driver has written a non-identity rotation to the camera. The headset's
            // reference space has an arbitrary forward at session start, so the tracking
            // origin is turned until the camera's world yaw lands on the heading the player
            // had before entering. The body is not touched, which is what makes the angle
            // between head and body zero at entry however the player is physically facing.
            if (_vrRecenterPending)
            {
                bool hmdHasValidPose = playerCamera.transform.localRotation != Quaternion.identity;

                if (hmdHasValidPose)
                {
                    float cameraWorldYaw = playerCamera.transform.eulerAngles.y;
                    float yawCorrection = Mathf.DeltaAngle(cameraWorldYaw, _vrRecenterTargetYaw);
                    _trackingOrigin.Rotate(Vector3.up, yawCorrection, Space.World);
                    _vrRecenterPending = false;
                }
            }

            // Body-follow-head. The angle between head and body is the difference between the
            // camera's world yaw and the body's. That is a real measurement now the camera
            // hangs from the tracking origin rather than from the body: turning the body
            // genuinely shrinks it, where before the camera turned with the body and the
            // number never moved.
            //
            // The limit is anatomical rather than a trigger. Past it the body turns only far
            // enough to bring the head back to the edge of the comfortable range, so the
            // remaining angle shrinks by exactly what the body turns and the follow ends.
            // The origin is turned back by the same amount about the same axis, so the
            // player's view does not turn at all while their body does. Both rotations are
            // in the body's own frame so they cancel exactly, including on a tilted platform.
            if (!_vrRecenterPending)
            {
                float headBodyAngle = Mathf.DeltaAngle(
                    transform.eulerAngles.y,
                    playerCamera.transform.eulerAngles.y);
                float absDelta = Mathf.Abs(headBodyAngle);

                if (absDelta > vrBodyFollowDeadzone)
                {
                    float overshoot = absDelta - vrBodyFollowDeadzone;
                    float catchUpThisFrame = Mathf.Min(overshoot, vrBodyFollowSpeed * Time.deltaTime);
                    float rotateBy = catchUpThisFrame * Mathf.Sign(headBodyAngle);

                    transform.Rotate(Vector3.up, rotateBy);
                    _trackingOrigin.Rotate(Vector3.up, -rotateBy);
                    cameraYaw = transform.eulerAngles.y;
                }
            }

            // Camera position, written after the body-follow so it reads this frame's final
            // body orientation. First person sits at the avatar's animated eye position so
            // the view is locked inside the head — every frame, because the head bone is
            // animated. Third person snaps to a fixed point behind and above the body root:
            // instantly, never interpolated, because a smooth camera dolly in VR is
            // unrequested translational motion and a known sickness trigger. The body root
            // is the anchor, not the animated head bone, so the third-person camera does not
            // jitter with head-bob. Headset rotation still drives the view through the pose
            // driver, and the origin is what it is written into.
            if (isFirstPerson)
            {
                playerCamera.transform.position = _avatarHeadBone.position + transform.TransformVector(vrEyeOffset);
            }
            else
            {
                playerCamera.transform.position =
                    transform.position
                    + Vector3.up * thirdPersonCameraHeight
                    - transform.forward * thirdPersonCameraDistance;
            }
        }

        // Traced here rather than in the fixed step: the arc reads the animated eye
        // position and the camera's forward, both of which change every frame. Runs last
        // so it sees this frame's final body orientation.
        if (_vrTeleporter != null)
            _vrTeleporter.RenderAim();
    }

    /// <summary>
    /// Returns the authoritative VR eye position, computed the same way LateUpdate
    /// positions the camera in VR mode. Used by U3DVRTeleporter so the arc origin
    /// is anchored to the same point as the camera, regardless of script execution
    /// order. In non-VR mode, returns the camera's current world position.
    /// </summary>
    public Vector3 GetVREyePosition()
    {
        if (_isInVRMode && _avatarHeadBone != null)
            return _avatarHeadBone.position + transform.TransformVector(vrEyeOffset);

        if (playerCamera != null)
            return playerCamera.transform.position;

        return transform.position;
    }

    private void HandleVRMovement(U3DPlayerInputState input)
    {
        if (!enableMovement) return;

        Vector2 vrMoveInput = input.MovementInput;
        if (isAutoRunning) vrMoveInput.y = 1f;
        float snapTurnInput = input.LookInput.x;

        // Snap turn always works, regardless of teleport state. The VR look axis is a
        // stick rate, so it is correct at fixed rate and stays here. It turns the body, and
        // the tracking origin is a child of the body, so the view turns with it and the
        // angle between head and body is unchanged — the body-follow has nothing to react
        // to and cannot fight an intentional turn.
        if (Mathf.Abs(snapTurnInput) > 0.1f)
        {
            float turnDelta = snapTurnInput * 90f * Time.fixedDeltaTime;
            transform.Rotate(Vector3.up, turnDelta);
            cameraYaw += turnDelta;
        }

        // Teleport gesture: the stick click that arms and disarms is a press edge and
        // is handled in Update. Tick reads the stick's position only, so it is safe at
        // the fixed rate however many times it runs. While armed it consumes the stick
        // and suppresses locomotion — locomotion only. Gravity still runs in ApplyMotion,
        // which is what pulls the player back down after a teleport.
        if (enableTeleport && _vrTeleporter != null)
        {
            if (_vrTeleporter.Tick(vrMoveInput.y))
            {
                IsMoving = false;
                moveInput = Vector2.zero;
                MoveIntent = Vector2.zero;
                return;
            }
        }

        // Movement-intent dismount by stick. The press-edge half lives in Update.
        if (_currentRideable != null && vrMoveInput.magnitude > 0.1f)
            DismountRideable(_currentRideable);

        if (IsClimbing)
        {
            // Climb has its own animator state, so the locomotion blend is not consulted
            // here and the raw stick is the useful value to carry.
            moveInput = vrMoveInput;
            MoveIntent = vrMoveInput;
            return;
        }

        // Captured before the disabled-controller return. A seated player's controller is
        // switched off, and U3DSeat reads MoveInput to detect them trying to stand — so
        // returning first left a VR rider or sitter reading a frozen value forever.
        moveInput = vrMoveInput;

        if (!characterController.enabled) return;

        Vector3 forward = playerCamera != null ? playerCamera.transform.forward : transform.forward;
        Vector3 right = playerCamera != null ? playerCamera.transform.right : transform.right;

        bool freeMovement = isFlying || IsSwimming;

        if (!freeMovement)
        {
            forward.y = 0f;
            right.y = 0f;
            forward.Normalize();
            right.Normalize();
        }

        Vector3 moveDirection = (forward * vrMoveInput.y + right * vrMoveInput.x).normalized;
        float currentSpeed = GetCurrentSpeed() * VR_MOVEMENT_SPEED_MULTIPLIER;

        // Read the toggle state set by HandleButtonInputs, not the held button state.
        // Reading the press edge directly would make VR sprint a one-frame burst while
        // the desktop path is a toggle, and the two would fight over isSprinting.
        if (isSprinting)
            currentSpeed = runSpeed * VR_MOVEMENT_SPEED_MULTIPLIER;

        Vector3 moveVelocity = moveDirection * currentSpeed;

        if (freeMovement)
        {
            Vector3 flyDirection = moveDirection;
            if (input.JumpHeld) flyDirection += Vector3.up;
            if (input.CrouchHeld) flyDirection += Vector3.down;
            _moveVelocity = flyDirection * currentSpeed;
        }
        else
        {
            _moveVelocity = moveVelocity;
        }

        MoveIntent = ToBodyLocalIntent(moveDirection);
        IsMoving = moveVelocity.magnitude > 0.1f;
    }

    private void UpdateVRCameraPitch()
    {
        if (playerCamera == null) return;

        // Unity reports Euler angles as 0 to 360, so looking twenty degrees down reads as
        // 340 rather than -20. Every other writer of CameraPitch produces a signed value
        // clamped to the look limits, and this one did not, so the property meant two
        // different things depending on whether the player was in VR. Converted here rather
        // than by each reader: one property, one convention.
        float pitch = playerCamera.transform.localEulerAngles.x;
        if (pitch > 180f) pitch -= 360f;

        CameraPitch = Mathf.Clamp(pitch, lookDownLimit, lookUpLimit);
    }

    // ==================== END VR/WebXR MODE HANDLING ====================

    void HandleLocalCameraRender()
    {
        if (!enableMovement || playerCamera == null) return;
        if (_isInVRMode) return;
        if (!IsCursorLocked()) return;
        playerCamera.transform.localRotation = Quaternion.Euler(cameraPitch, 0f, 0f);
    }

    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (_pendingTrampoline != null) return;
        if (isGrounded) return;
        if (_velocity.y > -4f) return;
        if (hit.normal.y < 0.5f) return;

        var trampoline = hit.collider.GetComponentInParent<U3D.U3DTrampoline>();
        if (trampoline == null) return;

        trampoline.HandleLanding(this);
    }

    public void QueueTrampolineLaunch(U3D.U3DTrampoline trampoline, float height, float contactTime)
    {
        if (trampoline == null || height <= 0f) return;

        float minimumContact = Time.fixedDeltaTime * 2f;
        _pendingTrampoline = trampoline;
        _pendingTrampolineHeight = height;
        _pendingTrampolineLaunchTime = Time.fixedTime + Mathf.Max(contactTime, minimumContact);
    }

    void ProcessPendingTrampolineLaunch()
    {
        if (_pendingTrampoline == null) return;
        if (Time.fixedTime < _pendingTrampolineLaunchTime) return;

        var trampoline = _pendingTrampoline;
        _pendingTrampoline = null;

        _velocity.y = Mathf.Sqrt(_pendingTrampolineHeight * -2f * gravity);
        jumpCount = 1;
        IsJumping = true;

        if (trampoline != null)
            trampoline.NotifyLaunched();
    }

    public void PerformTeleport()
    {
        if (playerCamera == null)
        {
            Debug.LogWarning("Cannot teleport - player camera is null");
            return;
        }

        Vector3 screenCenter = new Vector3(Screen.width / 2, Screen.height / 2, 0);
        Ray ray = playerCamera.ScreenPointToRay(screenCenter);
        RaycastHit[] allHits = Physics.RaycastAll(ray, 100f);

        RaycastHit bestHit = new RaycastHit();
        bool foundHit = false;
        float closestDistance = float.MaxValue;

        foreach (RaycastHit hit in allHits)
        {
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                continue;
            if (hit.collider.isTrigger)
                continue;
            if (hit.distance < closestDistance)
            {
                bestHit = hit;
                closestDistance = hit.distance;
                foundHit = true;
            }
        }

        if (foundHit)
        {
            Vector3 teleportPos = bestHit.point;
            float playerHeight = characterController != null ? characterController.height : 2f;
            teleportPos.y += (playerHeight * 0.5f) + 0.1f;

            if (U3DSeat.CurrentlyOccupied != null)
                U3DSeat.CurrentlyOccupied.Stand();

            if (_currentRideable != null)
                DismountRideable(_currentRideable);
            else if (transform.parent != null)
            {
                SuspendCollision(this);
                transform.SetParent(null, true);
                ResumeCollision(this);
            }

            SuspendCollision(this);
            transform.position = teleportPos;
            ResumeCollision(this);

            _velocity = Vector3.zero;
            _smoothedMouseInput = Vector2.zero;
        }
    }

    void HandleCameraPositioning()
    {
        if (_isInVRMode) return;

        if (perspectiveMode == PerspectiveMode.SmoothScroll)
        {
            if (Mathf.Abs(currentTransitionValue - targetTransitionValue) > 0.001f)
            {
                currentTransitionValue = Mathf.MoveTowards(
                    currentTransitionValue,
                    targetTransitionValue,
                    Time.deltaTime / transitionTime
                );
                isTransitioning = true;
            }
            else
            {
                currentTransitionValue = targetTransitionValue;
                isTransitioning = false;
            }

            UpdateCameraTransitionPosition();
        }
        else if (enableSmoothTransitions)
        {
            Vector3 targetPosition = isFirstPerson ? firstPersonPosition : thirdPersonPosition;

            if (isCrouching)
                targetPosition.y += crouchCameraOffset;

            if (enableCameraCollision && !isFirstPerson)
                targetPosition = GetCollisionSafeCameraPosition(targetPosition);

            playerCamera.transform.localPosition = Vector3.Lerp(
                playerCamera.transform.localPosition,
                targetPosition,
                Time.deltaTime * perspectiveTransitionSpeed
            );
        }
    }

    void HandleZoom()
    {
        if (!enableViewZoom) return;
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFOV, Time.deltaTime * zoomSpeed);
    }

    Vector3 GetCollisionSafeCameraPosition(Vector3 desiredPosition)
    {
        Vector3 pivotWorldPosition = cameraPivot != null ? cameraPivot.position : (transform.position + firstPersonPosition);

        Vector3 cameraWorldTarget;
        if (cameraPivot != null)
            cameraWorldTarget = pivotWorldPosition + cameraPivot.rotation * desiredPosition;
        else
            cameraWorldTarget = transform.TransformPoint(desiredPosition);

        Vector3 direction = (cameraWorldTarget - pivotWorldPosition).normalized;
        float maxDistance = Vector3.Distance(pivotWorldPosition, cameraWorldTarget);

        if (maxDistance < 0.1f) return desiredPosition;

        int layerMask = ~(LayerMask.GetMask("Ignore Raycast") | LayerMask.GetMask("Player"));

        if (Physics.SphereCast(pivotWorldPosition, cameraCollisionRadius, direction, out RaycastHit hit, maxDistance, layerMask))
        {
            float safeDistance = Mathf.Max(0.1f, hit.distance - cameraCollisionBuffer);
            Vector3 safeWorldPosition = pivotWorldPosition + direction * safeDistance;

            if (cameraPivot != null)
                return Quaternion.Inverse(cameraPivot.rotation) * (safeWorldPosition - pivotWorldPosition);
            else
                return transform.InverseTransformPoint(safeWorldPosition);
        }

        return desiredPosition;
    }

    float GetCurrentSpeed()
    {
        if (isSprinting) return runSpeed;
        else if (isCrouching) return walkSpeed * 0.5f;
        else return walkSpeed;
    }

    /// <summary>
    /// Expresses a world-space movement direction in the body's own frame, as the X and Z
    /// the avatar's directional blend tree expects. The camera can be orbited away from the
    /// body's heading, so the input vector alone does not describe which way the avatar is
    /// actually travelling relative to the way it is facing. The vertical component is
    /// discarded: flight and swimming steer in three dimensions, the blend tree does not.
    /// </summary>
    private Vector2 ToBodyLocalIntent(Vector3 worldDirection)
    {
        if (worldDirection.sqrMagnitude < 0.0001f) return Vector2.zero;

        Vector3 local = transform.InverseTransformDirection(worldDirection);
        Vector2 intent = new Vector2(local.x, local.z);
        return intent.sqrMagnitude > 1f ? intent.normalized : intent;
    }

    void SetFirstPerson()
    {
        isFirstPerson = true;
        currentCameraDistance = 0f;
        if (perspectiveMode == PerspectiveMode.SmoothScroll)
            targetTransitionValue = 0f;
    }

    void SetThirdPerson()
    {
        isFirstPerson = false;
        currentCameraDistance = thirdPersonCameraDistance;
        if (perspectiveMode == PerspectiveMode.SmoothScroll)
            targetTransitionValue = 1f;
    }

    void LoadPlayerPreferences()
    {
        lookInverted = PlayerPrefs.GetInt("U3D_LookInverted", 0) == 1;
        LoadSensitivitySettings();
    }

    public void SetMouseSmoothing(bool enabled)
    {
        enableMouseSmoothing = enabled;
        PlayerPrefs.SetInt("U3D_MouseSmoothing", enabled ? 1 : 0);
        PlayerPrefs.Save();
    }

    public bool GetMouseSmoothing() => enableMouseSmoothing;
    public void SetLookInverted(bool inverted) { lookInverted = inverted; }
    public bool IsWebGLPlatform() => Application.platform == RuntimePlatform.WebGLPlayer;

    public float GetPlatformSensitivityMultiplier()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.WebGLPlayer: return webglSensitivityMultiplier;
            case RuntimePlatform.IPhonePlayer:
            case RuntimePlatform.Android: return mobileSensitivityMultiplier;
            default: return 1.0f;
        }
    }

    public string GetPlatformName()
    {
        switch (Application.platform)
        {
            case RuntimePlatform.WebGLPlayer: return "WebGL";
            case RuntimePlatform.IPhonePlayer: return "iOS";
            case RuntimePlatform.Android: return "Android";
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor: return "Windows";
            case RuntimePlatform.OSXPlayer:
            case RuntimePlatform.OSXEditor: return "macOS";
            case RuntimePlatform.LinuxPlayer:
            case RuntimePlatform.LinuxEditor: return "Linux";
            default: return "Desktop";
        }
    }

    // Push-driven identity callback. Invoked by FirebaseIntegration when the
    // local user's profile resolves, initially or on re-delivery.
    private void HandleLocalProfileReady(FirebaseIntegration.UserInfo profile)
    {
        ApplyDisplayNameFromProfile(profile);
    }

    // Empty or null names return early, leaving DisplayName empty rather than writing a
    // placeholder. The nametag decides what an unnamed player looks like.
    private void ApplyDisplayNameFromProfile(FirebaseIntegration.UserInfo profile)
    {
        if (profile == null) return;
        if (string.IsNullOrEmpty(profile.displayName)) return;
        string chosenName = profile.displayName;
        if (chosenName.Length > DISPLAY_NAME_MAX_LENGTH)
            chosenName = chosenName.Substring(0, DISPLAY_NAME_MAX_LENGTH);
        DisplayName = chosenName;

        Net.Session?.PublishDisplayName(DisplayName, PeerId.None);
    }

    /// <summary>
    /// Tells a newcomer what the local player is called. Needed because a name that was
    /// set before the newcomer's connection opened reached nobody — the send happened
    /// and the transport had nowhere to put it. Without this a newcomer sees a generated
    /// name until the local player's next profile event, which may be never.
    /// </summary>
    private void HandlePeerReachable(PeerId peer)
    {
        if (string.IsNullOrEmpty(DisplayName)) return;
        Net.Session?.PublishDisplayName(DisplayName, peer);
    }

    public void OnMove(InputAction.CallbackContext context) { }
    public void OnLook(InputAction.CallbackContext context) { }
    public void OnJump(InputAction.CallbackContext context) { }
    public void OnSprint(InputAction.CallbackContext context) { }
    public void OnCrouch(InputAction.CallbackContext context) { }
    public void OnZoom(InputAction.CallbackContext context) { }
    public void OnFly(InputAction.CallbackContext context) { }
    public void OnAutoRun(InputAction.CallbackContext context) { }
    public void OnPerspectiveSwitch(InputAction.CallbackContext context) { }
    public void OnInteract(InputAction.CallbackContext context) { }
    public void OnPause(InputAction.CallbackContext context) { }
    public void OnTeleport(InputAction.CallbackContext context) { }

    public bool IsGrounded => isGrounded;
    public bool IsSprinting => isSprinting;
    public bool IsCrouching => isCrouching;
    public bool IsFlying => isFlying;
    public bool IsAutoRunning => isAutoRunning;
    public bool IsFirstPerson => isFirstPerson;
    public bool IsCameraTransitioning => isTransitioning;
    public float CurrentSpeed => GetCurrentSpeed();
    /// <summary>
    /// The authored walk and run speeds. Public because a remote body has to work out how
    /// fast the player it represents is moving, and it has no controller of its own to
    /// ask: every player runs the same published build, so these numbers are the same on
    /// every machine and the receiver derives the speed rather than being sent it.
    /// </summary>
    public float WalkSpeed => walkSpeed;
    public float RunSpeed => runSpeed;

    // PORT: IsInVRMode and IsInVR both return _isInVRMode. C2 shadow pair surviving
    // the rewrite. Needs a reader sweep before either is removed. B23.
    public bool IsInVRMode => _isInVRMode;
    public Vector2 MoveInput => moveInput;
    public bool JumpPressedThisFrame => _jumpPressedPending;
    public CharacterController CharacterController => characterController;
    public Transform CameraTransform => playerCamera != null ? playerCamera.transform : null;
    public bool IsInVR => _isInVRMode;
    public bool EnableMovement => enableMovement;
    public bool EnableJumping => enableJumping;
    public bool EnableSprintToggle => enableSprintToggle;
    public bool EnableCrouchToggle => enableCrouchToggle;
    public bool EnableFlying => enableFlying;
    public bool EnableAutoRun => enableAutoRun;
    public bool EnableTeleport => enableTeleport;
    public bool EnableViewZoom => enableViewZoom;
    public bool EnableAdvancedCamera => enableAdvancedCamera;
    public Transform RawHmdReference => _rawHmdReference;

    /// <summary>
    /// The XR tracking origin, or null outside VR. Everything the headset poses must be a
    /// child of it, so a component creating its own tracked object parents it here rather
    /// than to the player root. See EnsureTrackingOrigin for why.
    /// </summary>
    public Transform TrackingOrigin => _trackingOrigin;

    /// <summary>
    /// After the two-prefab split a controller only ever exists on the local player,
    /// so this is true by construction. Kept as a property because roughly fifteen
    /// components read it and all of them stay correct.
    /// </summary>
    public bool IsLocalPlayer => true;

    public static U3DPlayerController FindLocalPlayer()
    {
        return FindAnyObjectByType<U3DPlayerController>();
    }

    /// <summary>
    /// Moves the player somewhere else outright — a respawn, a trash-zone recovery. A seat
    /// and a rideable both end here: the seat holds the player in place from its own
    /// LateUpdate and would drag them back, and the rideable parents them to a platform that
    /// would carry them off again. A steerable does neither — its costume is a child of this
    /// transform and travels with the player, so a costume survives a respawn deliberately.
    /// </summary>
    public void SetPosition(Vector3 position)
    {
        if (U3DSeat.CurrentlyOccupied != null)
            U3DSeat.CurrentlyOccupied.Stand();

        if (_currentRideable != null)
            DismountRideable(_currentRideable);

        if (transform.parent != null)
        {
            SuspendCollision(this);
            transform.SetParent(null, true);
            ResumeCollision(this);
        }

        SuspendCollision(this);
        transform.position = position;
        ResumeCollision(this);

        _velocity = Vector3.zero;
    }

    public void SetRotation(float yRotation)
    {
        transform.rotation = Quaternion.Euler(0, yRotation, 0);

        // HandleMovement snaps the body to cameraYaw on the first step after standing
        // still. An external rotation (e.g. respawn) that doesn't update cameraYaw leaves it
        // stale, so that snap swings the view back to the pre-respawn heading. Keep them matched.
        cameraYaw = yRotation;
    }

    public void SetCameraPitch(float pitch)
    {
        cameraPitch = pitch;
    }

    public void SetSwimmingState(bool swimming)
    {
        IsSwimming = swimming;
        if (swimming) _velocity = Vector3.zero;
    }

    public void SetClimbingState(bool climbing)
    {
        IsClimbing = climbing;
        if (climbing) _velocity = Vector3.zero;
    }

    public void SetSeatedState(bool seated)
    {
        IsSeated = seated;
    }

    /// <summary>
    /// Whether this player is pushing an object. Written by U3DMovable, read by the
    /// animator and folded into the avatar packet, so a remote body plays the push
    /// animation from the same state the local one does.
    /// </summary>
    public void SetPushingState(bool pushing)
    {
        IsPushing = pushing;
    }

    /// <summary>
    /// Whether this player is pulling an object. Written by U3DMovable, on the same
    /// path as pushing.
    /// </summary>
    public void SetPullingState(bool pulling)
    {
        IsPulling = pulling;
    }

    public void SetSuppressLocomotion(bool suppress)
    {
        SuppressLocomotion = suppress;
    }

    public void SetMoving(bool moving)
    {
        IsMoving = moving;
    }

    public void SetClimbDetachVelocity(float upwardVelocity)
    {
        _velocity = new Vector3(0f, upwardVelocity, 0f);
    }

    // ==================== COLLISION SUSPEND ====================
    // The CharacterController's enabled flag has several owners: a seat, a rideable, a
    // climb, and this controller's own repositioning. Each used to write true when it
    // finished, handing collision back to a player another owner was still holding.
    // Callers now name themselves and the flag follows the set. Destroyed holders are
    // pruned, so an exit path nobody wrote cannot leave the player permanently frozen.

    private readonly HashSet<Component> _collisionHolders = new HashSet<Component>();

    public void SuspendCollision(Component holder)
    {
        if (holder == null) return;
        _collisionHolders.Add(holder);
        RefreshCollisionEnabled();
    }

    public void ResumeCollision(Component holder)
    {
        if (holder == null) return;
        _collisionHolders.Remove(holder);
        RefreshCollisionEnabled();
    }

    private void RefreshCollisionEnabled()
    {
        _collisionHolders.RemoveWhere(h => h == null);

        if (characterController != null)
            characterController.enabled = _collisionHolders.Count == 0;
    }

    public void MountRideable(U3D.U3DRideableController rideable)
    {
        _currentRideable = rideable;
        _velocity = Vector3.zero;
        IsMoving = false;

        SuspendCollision(rideable);
        transform.SetParent(rideable.transform, true);
    }

    public void DismountRideable(U3D.U3DRideableController rideable)
    {
        if (_currentRideable != rideable) return;

        _currentRideable = null;

        transform.SetParent(null, true);
        ResumeCollision(rideable);
    }

    public void TakeControlOfSteerable(U3D.U3DSteerable steerable)
    {
        CurrentSteerable = steerable;
    }

    /// <summary>
    /// Ends steering the normal way, called by the steerable itself as it tears down its
    /// costume. Everything the steerable set on this controller is cleared here rather than
    /// in each caller, so a path that ends steering without going through the steerable
    /// still leaves this controller in a consistent state.
    /// </summary>
    public void ExitSteerable()
    {
        IsSeated = false;
        SuppressLocomotion = false;
        CurrentSteerable = null;
    }

    public bool IsRiding(U3D.U3DRideableController rideable) => _currentRideable == rideable;
}