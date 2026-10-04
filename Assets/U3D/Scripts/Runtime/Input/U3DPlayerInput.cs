using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace U3D.Input
{
    /// <summary>
    /// Owns all local player input: Input Actions, touch zones, VR controller polling, and the
    /// UI focus registry. Produces one U3DPlayerInputState snapshot per tick via ConsumeInput().
    ///
    /// Deliberately has no networking dependency. It runs identically with or without a network
    /// backend present, which is what lets the transport be replaced underneath it.
    /// </summary>
    public class U3DPlayerInput : MonoBehaviour
    {
        [Header("Input System Integration")]
        [SerializeField] private InputActionAsset inputActionAsset;

        public static U3DPlayerInput Instance { get; private set; }

        private readonly List<IUIInputHandler> _uiInputHandlers = new List<IUIInputHandler>();
        private readonly HashSet<string> _activeUIComponents = new HashSet<string>();
        private bool _isUIFocused = false;

        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _jumpAction;
        private InputAction _sprintAction;
        private InputAction _crouchAction;
        private InputAction _flyAction;
        private InputAction _interactAction;
        private InputAction _removeAction;
        private InputAction _zoomAction;
        private InputAction _teleportAction;
        private InputAction _perspectiveSwitchAction;
        private InputAction _pauseAction;
        private InputAction _escapeAction;
        private InputAction _mouseLeftAction;
        private InputAction _mouseRightAction;
        private InputAction _strafeLeftAction;
        private InputAction _strafeRightAction;
        private InputAction _turnLeftAction;
        private InputAction _turnRightAction;
        private InputAction _autoRunToggleAction;

        private Vector2 _cachedMovementInput;
        private Vector2 _cachedLookInput;
        private bool _jumpPressed;
        private bool _sprintPressed;
        private bool _crouchPressed;
        private bool _flyPressed;
        private bool _interactPressed;
        private bool _removePressed;
        private bool _zoomHeld;
        private bool _jumpHeld;
        private bool _crouchHeld;
        private bool _teleportPressed;
        private float _perspectiveScrollValue;

        private bool _leftMouseHeld = false;
        private bool _rightMouseHeld = false;
        private bool _bothMouseHeld = false;

        private bool _strafeLeftPressed = false;
        private bool _strafeRightPressed = false;
        private bool _turnLeftPressed = false;
        private bool _turnRightPressed = false;
        private bool _autoRunTogglePressed = false;

        private float _lastTeleportClickTime = -999f;
        private const float DOUBLE_CLICK_WINDOW = 0.5f;

        private U3DSimpleTouchZones touchZones;

        private U3D.XR.U3DWebXRManager _webXRManager;
        private bool _isVRModeActive = false;
        private bool _vrSprintTriggerWasDown = false;
        private bool _vrPerspectiveToggleState = true;

        private U3DWebGLCursorManager _cursorManager;
        private float _lastCursorManagerSearch = -999f;
        private const float CURSOR_MANAGER_SEARCH_INTERVAL = 1f;

        public bool IsUIFocused => _isUIFocused;
        public int ActiveUICount => _uiInputHandlers.Count(h => h != null && h.IsUIFocused());

        public InputActionAsset ActionAsset => inputActionAsset;
        public InputAction InteractAction => _interactAction;
        public InputAction PauseAction => _pauseAction;
        public InputAction EscapeAction => _escapeAction;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            SetupInputActions();

            // The touch zone component handles its own platform detection internally, so this
            // only needs to skip platforms where touch can never appear. Application.platform
            // reports WebGLPlayer on mobile browsers, so the WebGL check covers mobile.
            if (Application.isEditor || Application.platform == RuntimePlatform.WebGLPlayer)
            {
                SetupTouchControls();
            }

            U3D.XR.U3DWebXRManager.OnVRModeChanged += OnVRModeChanged;
        }

        void SetupInputActions()
        {
            if (inputActionAsset == null)
            {
                Debug.LogError("U3DPlayerInput: Input Action Asset not assigned. Assign U3DInputActions in the Inspector.");
                return;
            }

            var actionMap = inputActionAsset.FindActionMap("Player");
            if (actionMap == null)
            {
                Debug.LogError("U3DPlayerInput: 'Player' action map not found in the Input Actions asset.");
                return;
            }

            _moveAction = actionMap.FindAction("Move");
            _lookAction = actionMap.FindAction("Look");
            _jumpAction = actionMap.FindAction("Jump");
            _sprintAction = actionMap.FindAction("Sprint");
            _crouchAction = actionMap.FindAction("Crouch");
            _flyAction = actionMap.FindAction("Fly");
            _interactAction = actionMap.FindAction("Interact");
            _zoomAction = actionMap.FindAction("Zoom");
            _teleportAction = actionMap.FindAction("Teleport");
            _perspectiveSwitchAction = actionMap.FindAction("PerspectiveSwitch");
            _pauseAction = actionMap.FindAction("Pause");
            _escapeAction = actionMap.FindAction("Escape");

            _mouseLeftAction = actionMap.FindAction("MouseLeft");
            _mouseRightAction = actionMap.FindAction("MouseRight");
            _strafeLeftAction = actionMap.FindAction("StrafeLeft");
            _strafeRightAction = actionMap.FindAction("StrafeRight");
            _turnLeftAction = actionMap.FindAction("TurnLeft");
            _turnRightAction = actionMap.FindAction("TurnRight");
            _autoRunToggleAction = actionMap.FindAction("AutoRunToggle");
            _removeAction = actionMap.FindAction("Remove");

            actionMap.Enable();
        }

        void SetupTouchControls()
        {
            touchZones = UnityEngine.Object.FindFirstObjectByType<U3DSimpleTouchZones>();
            if (touchZones == null)
            {
                GameObject touchControllerObj = new GameObject("TouchZoneController");
                touchZones = touchControllerObj.AddComponent<U3DSimpleTouchZones>();
                DontDestroyOnLoad(touchControllerObj);
            }
        }

        /// <summary>
        /// Returns true if touch zones are active and providing input. Uses the touch zone's own
        /// detection rather than Application.isMobilePlatform, which returns false on WebGL even
        /// when running in a mobile browser.
        /// </summary>
        private bool IsTouchInputActive()
        {
            return touchZones != null && touchZones.IsTouchEnabled;
        }

        private void OnVRModeChanged(bool isVRActive)
        {
            _isVRModeActive = isVRActive;
            _webXRManager = U3D.XR.U3DWebXRManager.Instance;

            // Disable the UI action map entirely during VR so the InputSystemUIInputModule
            // stops reading actions that share physical bindings with player movement.
            // In VR, all UI interaction is driven by the gaze pointer firing Player/Interact
            // through ExecuteEvents directly — the UI module's action pipeline isn't needed
            // and only causes input arbitration on the left stick / arrow keys. On desktop
            // exit from VR, restore the UI map so keyboard-arrow accessibility navigation
            // works again.
            if (inputActionAsset != null)
            {
                var uiMap = inputActionAsset.FindActionMap("UI");
                if (uiMap != null)
                {
                    if (isVRActive && uiMap.enabled) uiMap.Disable();
                    else if (!isVRActive && !uiMap.enabled) uiMap.Enable();
                }
            }
        }

        void Update()
        {
            if (_moveAction == null) return;

            if (_cursorManager == null && Time.time - _lastCursorManagerSearch >= CURSOR_MANAGER_SEARCH_INTERVAL)
            {
                _cursorManager = FindAnyObjectByType<U3DWebGLCursorManager>();
                _lastCursorManagerSearch = Time.time;
            }

            bool shouldProcessInput = _cursorManager == null || _cursorManager.ShouldProcessGameInput();
            bool isUIInteracting = CheckUIInteraction();

            if (!shouldProcessInput || isUIInteracting)
            {
                ClearInputCache();
                return;
            }

            if (_isVRModeActive && _webXRManager != null)
            {
                PollVRInput();
                return;
            }

            // Touch and keyboard/mouse run in parallel on touch-capable devices. Touch
            // takes over only once the touch zones have observed an actual touch this
            // session (IsTouchEnabled flips true on first touch and stays true). Until
            // then, keyboard/mouse handles everything — including in the editor and on
            // desktop WebGL.
            if (IsTouchInputActive())
            {
                _cachedMovementInput = touchZones.MovementInput;
                _cachedLookInput = touchZones.LookInput;

                if (touchZones.JumpRequested)
                    _jumpPressed = true;
                if (touchZones.SprintActive)
                    _sprintPressed = true;
                if (touchZones.CrouchRequested)
                    _crouchPressed = true;
                if (touchZones.FlyRequested)
                    _flyPressed = true;
                if (touchZones.InteractRequested)
                    _interactPressed = true;

                // Touch exposes one-shot requests only, so there is no level state to read
                // for fly ascend/descend. Held stays false; touch fly-vertical is a known gap.
                _jumpHeld = false;
                _crouchHeld = false;

                // Center-zone vertical pinch feeds the same one-shot scroll value the mouse
                // wheel uses; the controller already gates it on SmoothScroll perspective mode.
                if (Mathf.Abs(touchZones.PerspectiveScrollInput) > 0.1f)
                    _perspectiveScrollValue = touchZones.PerspectiveScrollInput;

                // Tell the touch zones we've consumed its one-frame flags this frame.
                // Lifecycle is owned in one place: set in the touch zone, read and clear here.
                touchZones.ConsumeOneFrameInputs();
            }
            else
            {
                _cachedMovementInput = _moveAction.ReadValue<Vector2>();

                if (_lookAction != null)
                    _cachedLookInput = _lookAction.ReadValue<Vector2>();

                if (_jumpAction != null)
                {
                    if (_jumpAction.WasPressedThisFrame())
                        _jumpPressed = true;
                    _jumpHeld = _jumpAction.IsPressed();
                }

                if (_sprintAction != null && _sprintAction.WasPressedThisFrame())
                    _sprintPressed = true;

                if (_crouchAction != null)
                {
                    if (_crouchAction.WasPressedThisFrame())
                        _crouchPressed = true;
                    _crouchHeld = _crouchAction.IsPressed();
                }

                if (_flyAction != null && _flyAction.WasPressedThisFrame())
                    _flyPressed = true;

                if (_interactAction != null && _interactAction.WasPressedThisFrame())
                    _interactPressed = true;

                if (_removeAction != null && _removeAction.WasPressedThisFrame())
                    _removePressed = true;

                // Desktop teleport is a double click. 
                if (_teleportAction != null && _teleportAction.WasPressedThisFrame())
                {
                    float currentTime = Time.time;
                    if (currentTime - _lastTeleportClickTime < DOUBLE_CLICK_WINDOW)
                    {
                        _teleportPressed = true;
                        _lastTeleportClickTime = -999f;
                    }
                    else
                    {
                        _lastTeleportClickTime = currentTime;
                    }
                }

                if (_zoomAction != null)
                    _zoomHeld = _zoomAction.IsPressed();

                if (_perspectiveSwitchAction != null)
                {
                    float scroll = _perspectiveSwitchAction.ReadValue<float>();
                    if (Mathf.Abs(scroll) > 0.1f)
                        _perspectiveScrollValue = scroll;
                }

                if (_mouseLeftAction != null)
                    _leftMouseHeld = _mouseLeftAction.IsPressed();
                if (_mouseRightAction != null)
                    _rightMouseHeld = _mouseRightAction.IsPressed();
                _bothMouseHeld = _leftMouseHeld && _rightMouseHeld;

                if (_strafeLeftAction != null)
                    _strafeLeftPressed = _strafeLeftAction.IsPressed();
                if (_strafeRightAction != null)
                    _strafeRightPressed = _strafeRightAction.IsPressed();
                if (_turnLeftAction != null)
                    _turnLeftPressed = _turnLeftAction.IsPressed();
                if (_turnRightAction != null)
                    _turnRightPressed = _turnRightAction.IsPressed();

                if (_autoRunToggleAction != null && _autoRunTogglePressed == false && _autoRunToggleAction.WasPressedThisFrame())
                    _autoRunTogglePressed = true;
            }
        }

        private void PollVRInput()
        {
            Vector2 moveValue = Vector2.zero;
            Vector2 lookValue = Vector2.zero;

            if (_moveAction != null)
                moveValue = _moveAction.ReadValue<Vector2>();

            if (_lookAction != null)
                lookValue = _lookAction.ReadValue<Vector2>();

            // Read raw stick values directly. The previous lerp-toward-zero smoothing
            // caused phantom release events when input frames were briefly missed,
            // which broke the teleport gesture's release-to-fire detection. Walking
            // already feels fine without smoothing — the stick is held continuously
            // and reads stable per-frame values.
            _cachedMovementInput = moveValue;
            _cachedLookInput = lookValue;

            if (_jumpAction != null)
            {
                if (_jumpAction.WasPressedThisFrame())
                    _jumpPressed = true;
                _jumpHeld = _jumpAction.IsPressed();
            }

            if (_sprintAction != null)
            {
                float triggerValue = _sprintAction.ReadValue<float>();
                bool triggerDown = triggerValue > 0.5f;
                if (triggerDown && !_vrSprintTriggerWasDown)
                    _sprintPressed = true;
                _vrSprintTriggerWasDown = triggerDown;
            }

            if (_crouchAction != null)
            {
                if (_crouchAction.WasPressedThisFrame())
                    _crouchPressed = true;
                _crouchHeld = _crouchAction.IsPressed();
            }

            if (_flyAction != null && _flyAction.WasPressedThisFrame())
                _flyPressed = true;

            if (_interactAction != null && _interactAction.WasPressedThisFrame())
                _interactPressed = true;

            if (_teleportAction != null && _teleportAction.WasPressedThisFrame())
                _teleportPressed = true;

            // X button (left controller primary). Auto-run's former VR binding — auto-run
            // is desktop-only now; VR forward motion is the stick. Any auto-run state
            // carried in from desktop is cleared on VR entry by the player controller.
            if (_removeAction != null && _removeAction.WasPressedThisFrame())
                _removePressed = true;

            // Zoom is a hold in VR, mirroring the non-VR middle-mouse Hold behavior.
            // Unconditional level read each poll: held B button = true every tick =
            // isZooming true downstream; release = false = un-zoomed. The unconditional
            // assignment self-clears, so it needs no entry in ConsumeInput's clear block.
            if (_zoomAction != null)
                _zoomHeld = _zoomAction.IsPressed();

            // Perspective switch translator. The VR binding is the right-stick CLICK
            // (Primary2DAxisClick), which can only ever read +1 — it can never produce
            // the negative the directional consumer needs to return to third person.
            // So we treat each discrete click as a stateless toggle, exactly like the
            // left-stick teleport click: WasPressedThisFrame gives one press edge per
            // physical click (correct even on a Value action), and we alternate the
            // signed one-shot we feed into the existing PerspectiveScroll consumer.
            // false = send negative = go to third person; true = send positive = back
            // to first. ConsumeInput clears _perspectiveScrollValue every tick, so
            // this is a one-shot delta with the same lifetime as the non-VR scroll.
            if (_perspectiveSwitchAction != null && _perspectiveSwitchAction.WasPressedThisFrame())
            {
                _vrPerspectiveToggleState = !_vrPerspectiveToggleState;
                _perspectiveScrollValue = _vrPerspectiveToggleState ? 10f : -10f;
            }

            _leftMouseHeld = false;
            _rightMouseHeld = false;
            _bothMouseHeld = false;
            _strafeLeftPressed = false;
            _strafeRightPressed = false;
            _turnLeftPressed = false;
            _turnRightPressed = false;
        }

        private bool CheckUIInteraction()
        {
            _isUIFocused = false;
            _activeUIComponents.Clear();

            foreach (var handler in _uiInputHandlers)
            {
                if (handler != null && handler.IsUIFocused())
                {
                    _isUIFocused = true;
                    _activeUIComponents.Add(handler.GetHandlerName());
                }
            }

            // Skip the mouse-over-UI check when touch is driving input. Touch has its own
            // UI avoidance — U3DSimpleTouchZones drops any touch that begins over UI, and
            // worldspace UI on touch devices is operated by the gaze pointer plus Interact,
            // not by direct touches on the panel. A move/look finger dragging across the
            // on-screen projection of a worldspace panel is normal looking-around, not UI
            // use; consulting the pointer check there froze that valid movement (the mobile
            // equivalent of the VR gaze-freeze). Explicit UI focus from a registered handler
            // above still counts on every platform.
            if (!_isUIFocused && !IsTouchInputActive())
            {
                _isUIFocused = IsMousePointerOverUI();
            }

            return _isUIFocused;
        }

        // Desktop-mouse-only UI check. Runs solely on the non-touch path (see the
        // IsTouchInputActive guard in CheckUIInteraction), so its only job is "is the mouse
        // over UI." Pointer id -1 is the mouse; querying it specifically excludes the gaze
        // pointer's synthetic pointer (id -10), which would otherwise report over-UI whenever
        // the player looks at worldspace UI and freeze movement.
        private bool IsMousePointerOverUI()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null) return false;

            return eventSystem.IsPointerOverGameObject(-1);
        }

        /// <summary>
        /// Blanks every field the snapshot carries. Runs whenever input is suppressed by a
        /// cursor release or a UI grab. Held and level-state fields are included: leaving a
        /// held strafe key or mouse button at its last value made the controller keep acting
        /// on it after movement had already been zeroed.
        /// </summary>
        private void ClearInputCache()
        {
            _cachedMovementInput = Vector2.zero;
            _cachedLookInput = Vector2.zero;
            _jumpPressed = false;
            _jumpHeld = false;
            _sprintPressed = false;
            _crouchPressed = false;
            _crouchHeld = false;
            _flyPressed = false;
            _interactPressed = false;
            _removePressed = false;
            _teleportPressed = false;
            _autoRunTogglePressed = false;
            _zoomHeld = false;
            _perspectiveScrollValue = 0f;
            _leftMouseHeld = false;
            _rightMouseHeld = false;
            _bothMouseHeld = false;
            _strafeLeftPressed = false;
            _strafeRightPressed = false;
            _turnLeftPressed = false;
            _turnRightPressed = false;
        }

        public void RegisterUIInputHandler(IUIInputHandler handler)
        {
            if (!_uiInputHandlers.Contains(handler))
            {
                _uiInputHandlers.Add(handler);
            }
        }

        public void UnregisterUIInputHandler(IUIInputHandler handler)
        {
            _uiInputHandlers.Remove(handler);
        }

        /// <summary>
        /// Fills a snapshot from the current cache without changing any state.
        /// Shared by ConsumeInput and PeekInput so the two can never disagree
        /// about what the snapshot contains.
        /// </summary>
        private U3DPlayerInputState BuildSnapshot()
        {
            var data = new U3DPlayerInputState();

            data.MovementInput = _cachedMovementInput;
            data.LookInput = _cachedLookInput;
            data.PerspectiveScroll = _perspectiveScrollValue;

            data.JumpPressed = _jumpPressed;
            data.JumpHeld = _jumpHeld;
            data.SprintPressed = _sprintPressed;
            data.CrouchPressed = _crouchPressed;
            data.CrouchHeld = _crouchHeld;
            data.FlyPressed = _flyPressed;
            data.InteractPressed = _interactPressed;
            data.TeleportPressed = _teleportPressed;
            data.AutoRunTogglePressed = _autoRunTogglePressed;
            data.RemovePressed = _removePressed;
            data.ZoomHeld = _zoomHeld;

            data.LeftMouseHeld = _leftMouseHeld;
            data.RightMouseHeld = _rightMouseHeld;
            data.BothMouseHeld = _bothMouseHeld;

            data.StrafeLeft = _strafeLeftPressed;
            data.StrafeRight = _strafeRightPressed;
            data.TurnLeft = _turnLeftPressed;
            data.TurnRight = _turnRightPressed;

            return data;
        }

        /// <summary>
        /// Returns the current local input snapshot and clears one-shot presses.
        /// Called once per frame from the player controller's Update, which is where
        /// every press edge is now handled. Axes and Held fields are not cleared here —
        /// Update overwrites them every frame, and clearing them would blank continuous
        /// state that the fixed step reads through PeekInput.
        /// </summary>
        public U3DPlayerInputState ConsumeInput()
        {
            var data = BuildSnapshot();

            _jumpPressed = false;
            _sprintPressed = false;
            _crouchPressed = false;
            _flyPressed = false;
            _interactPressed = false;
            _teleportPressed = false;
            _autoRunTogglePressed = false;
            _removePressed = false;
            _perspectiveScrollValue = 0f;

            return data;
        }

        /// <summary>
        /// Returns the current local input snapshot without clearing anything.
        /// Called from the player controller's FixedUpdate, which may run zero, one or
        /// two times per frame and must therefore never consume a press edge. Read only
        /// the level and rate fields from the result — the press flags it carries are
        /// whatever Update has not yet consumed and are not meaningful here.
        /// </summary>
        public U3DPlayerInputState PeekInput()
        {
            return BuildSnapshot();
        }

        void OnDestroy()
        {
            if (Instance != this) return;

            U3D.XR.U3DWebXRManager.OnVRModeChanged -= OnVRModeChanged;

            if (inputActionAsset != null)
            {
                var actionMap = inputActionAsset.FindActionMap("Player");
                if (actionMap != null && actionMap.enabled)
                {
                    actionMap.Disable();
                }
            }

            Instance = null;
        }
    }
}