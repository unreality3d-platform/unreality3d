using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.InputSystem.Utilities;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace U3D.Input
{
    /// <summary>
    /// Zone-based touch controller that provides raw input values.
    /// Feeds U3DPlayerInput's polling system via public properties.
    ///
    /// Built on the Input System's EnhancedTouch API. The legacy
    /// UnityEngine.Input touch API was abandoned here because its phase
    /// reporting is unreliable on iOS Safari/WebKit WebGL (continuous drags
    /// misreported as repeated Began events). EnhancedTouch flushes its event
    /// queue later in the frame and reports phase/position correctly on that
    /// platform. This is the project's touch input path; it runs polled,
    /// parallel to the .inputactions-driven desktop/VR input, and shares no
    /// state with it.
    ///
    /// Zones:
    ///   Left half (continuous move stick) and right half (continuous look
    ///   stick) — both run as virtual analog sticks that respond every frame
    ///   the finger is down, not just while dragging. Two fingers can coexist
    ///   so move + look works simultaneously.
    ///
    ///   Middle-middle band (35-65% width, 35-65% height) — double-tap fires
    ///   Interact. Aligned with the gaze pointer reticle so "tap what you're
    ///   looking at" is the mental model.
    ///
    ///   Bottom-middle band (35-65% width, 0-25% height) — double-tap fires
    ///   Jump.
    ///
    /// Touches that land in the middle-middle or bottom-middle zones are
    /// provisionally action-candidates. If the touch drags significantly or
    /// stays down past the double-tap window, it converts to a look/move
    /// stick touch. This is the cost of overlapping action zones with the
    /// stick zones: a brief tap in those zones won't move the camera/avatar.
    /// </summary>
    public class U3DSimpleTouchZones : MonoBehaviour
    {
        [Header("Zone Configuration")]
        [SerializeField] private float screenDivider = 0.5f;
        [SerializeField] private float movementSensitivity = 1.0f;
        [SerializeField] private float lookSensitivity = 1.0f;

        [Tooltip("Pixels of thumb travel that maps to maximum look speed. Smaller = more sensitive, larger = more precise. Constant in pixels, so it feels the same in portrait and landscape.")]
        [SerializeField] private float lookMaxTravel = 200f;

        [Tooltip("Tuning multiplier so 'fully deflected' actually moves the camera at a reasonable speed. The old code used screen-normalized delta * 100f; this replaces that magic 100.")]
        [SerializeField] private float lookSpeedMultiplier = 4f;

        [Header("Action Zone Bounds (normalized 0-1)")]
        [SerializeField] private Vector2 middleZoneX = new Vector2(0.35f, 0.65f);
        [SerializeField] private Vector2 middleZoneY = new Vector2(0.35f, 0.65f);
        [SerializeField] private Vector2 bottomZoneX = new Vector2(0.35f, 0.65f);
        [SerializeField] private Vector2 bottomZoneY = new Vector2(0.00f, 0.25f);

        [Header("Gesture Timing")]
        [SerializeField] private float doubleTapWindow = 0.3f;
        [SerializeField] private float longPressTime = 0.5f;

        [Header("Dead Zones")]
        [SerializeField] private float movementDeadZone = 20f;
        [SerializeField] private float lookDeadZone = 2f;
        [Tooltip("Drag distance (pixels) past which an action-zone touch converts to a stick touch.")]
        [SerializeField] private float actionConvertDistance = 30f;

        [Header("Perspective Pinch")]
        [Tooltip("Pixels the gap between the two center-zone pinch fingers must change before the perspective flips. Larger = fingers must travel farther, fewer accidental switches. Smaller = more sensitive.")]
        [SerializeField] private float pinchTriggerDistance = 40f;

        [Tooltip("How diagonal a center two-finger pinch may be and still count: the largest allowed horizontal-to-vertical ratio of the gap between the two fingers. 1 = up to 45 degrees off vertical. Raise toward 2 to accept more diagonal pinches (more forgiving); lower toward 0 to require a near-vertical pinch. Keeps the gesture distinct from the side-by-side motion of the move and look thumbs.")]
        [SerializeField] private float pinchMaxDiagonalRatio = 1.0f;

        private Dictionary<int, TouchData> activeTouches = new Dictionary<int, TouchData>();
        private TouchData movementTouch;
        private TouchData lookTouch;

        // Pending action-candidate touches — touches that landed in an action zone
        // and haven't yet been classified as a tap (released quickly, no drag) or
        // converted to a stick touch (dragged or held).
        private TouchData pendingInteractTouch;
        private TouchData pendingJumpTouch;
        private TouchData _pinchTouchA;
        private TouchData _pinchTouchB;
        private float _pinchStartSeparation;
        private bool _pinchFired;

        private float lastInteractTapTime;
        private Vector2 lastInteractTapPosition;
        private float lastJumpTapTime;
        private Vector2 lastJumpTapPosition;

        private float longPressStartTime;
        private bool isLongPressing;

        private bool _isTouchEnabled;
        private bool _enhancedTouchEnabled;
        private bool _touchObservedThisSession;

        public Vector2 MovementInput { get; private set; }
        public Vector2 LookInput { get; private set; }
        public bool JumpRequested { get; private set; }
        public bool SprintActive { get; private set; }
        public bool InteractRequested { get; private set; }
        public bool CrouchRequested { get; private set; }
        public bool FlyRequested { get; private set; }
        public float ZoomInput { get; private set; }
        public bool PerspectiveSwitchRequested { get; private set; }
        public float PerspectiveScrollInput { get; private set; }

        public static U3DSimpleTouchZones Instance { get; private set; }

        /// <summary>
        /// Raised when ShouldShowTouchHints may have changed: when this component
        /// starts, and the frame a touch is first observed this session.
        /// </summary>
        public static event System.Action TouchHintsVisibilityChanged;

        private static bool _touchPrimaryChecked;
        private static bool _isTouchPrimaryDevice;

        /// <summary>
        /// True when the browser reports touch as its primary pointer, which covers
        /// phones and tablets, including iPads that send a desktop user agent.
        /// Read once from U3DTouchDetection.jslib. Always false in the Editor.
        /// </summary>
        public static bool IsTouchPrimaryDevice
        {
            get
            {
                if (!_touchPrimaryChecked)
                {
                    _isTouchPrimaryDevice = QueryTouchPrimaryDevice();
                    _touchPrimaryChecked = true;
                }
                return _isTouchPrimaryDevice;
            }
        }

        /// <summary>
        /// Whether on-screen touch control hints should be visible. True once touch
        /// controls exist, on a touch-primary device from the start, or on any device
        /// after its first touch. Visual only: the input path still switches to touch
        /// on the first observed touch.
        /// </summary>
        public static bool ShouldShowTouchHints =>
            Instance != null && (IsTouchPrimaryDevice || Instance.IsTouchEnabled);

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern int U3D_IsTouchPrimaryDevice();
#endif

        private static bool QueryTouchPrimaryDevice()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return U3D_IsTouchPrimaryDevice() == 1;
#else
            return false;
#endif
        }

        private enum TouchRole { Unassigned, Move, Look, PendingInteract, PendingJump, Pinch }

        private class TouchData
        {
            public int touchId;
            public Vector2 startPosition;
            public Vector2 currentPosition;
            public Vector2 frameDelta;
            public float startTime;
            public bool isLeftSide;
            public TouchRole role;
        }

        void Awake()
        {
            Instance = this;
            // Touch capability now starts false and flips true the first frame an
            // actual touch is observed. This means keyboard/mouse work in editor
            // and on desktop WebGL by default; touch takes over only when the
            // user actually uses it.
            _isTouchEnabled = false;
            _touchObservedThisSession = false;
            TouchHintsVisibilityChanged?.Invoke();
        }

        void OnEnable()
        {
            if (!_enhancedTouchEnabled)
            {
                EnhancedTouchSupport.Enable();
                _enhancedTouchEnabled = true;
            }
        }

        void OnDisable()
        {
            if (_enhancedTouchEnabled)
            {
                EnhancedTouchSupport.Disable();
                _enhancedTouchEnabled = false;
            }
        }

        /// <summary>
        /// Returns true once a touch has been observed this session. Lets
        /// U3DPlayerInput uses keyboard/mouse by default and switches to
        /// touch the moment the user actually touches the screen. No platform
        /// or build-target sniffing — pure runtime observation.
        /// </summary>
        public bool IsTouchEnabled => _isTouchEnabled;

        void Update()
        {
            // Observe whether any touch is currently active. The first time we
            // see one this session, flip the touch-enabled flag on permanently.
            if (!_touchObservedThisSession && ETouch.activeTouches.Count > 0)
            {
                _touchObservedThisSession = true;
                _isTouchEnabled = true;
                TouchHintsVisibilityChanged?.Invoke();
            }

            if (!_isTouchEnabled)
                return;

            ProcessTouches();
        }

        /// <summary>
        /// Per-frame clear of one-shot inputs. Called by U3DPlayerInput
        /// after it reads them, so the request flags survive until consumed
        /// rather than being cleared on the same frame they're set.
        /// </summary>
        public void ConsumeOneFrameInputs()
        {
            JumpRequested = false;
            InteractRequested = false;
            CrouchRequested = false;
            FlyRequested = false;
            PerspectiveSwitchRequested = false;
            PerspectiveScrollInput = 0f;
        }

        void ProcessTouches()
        {
            MovementInput = Vector2.zero;
            LookInput = Vector2.zero;
            ZoomInput = 0f;

            var touches = ETouch.activeTouches;

            // Pinch is handled by UpdatePinch below, scoped to two fingers inside the
            // center action zone arranged more vertically than horizontally. It never
            // touches the global Movement/Look values and only reclaims center-zone
            // fingers, which are never the move/look thumbs — so it cannot regress the
            // move-and-look-at-the-same-time pattern the way the old blanket pinch did.

            for (int i = 0; i < touches.Count; i++)
            {
                ETouch touch = touches[i];
                int id = touch.touchId;

                if (touch.began || !activeTouches.ContainsKey(id))
                {
                    HandleTouchBegan(touch);
                }
                else if (touch.ended)
                {
                    HandleTouchEnded(id);
                }
                else
                {
                    HandleTouchMoved(touch);
                }
            }

            // Convert pending action-zone touches to stick touches if they've
            // dragged far enough or been held past the double-tap window. This
            // is what makes "tap in middle = interact, but slow drag in middle
            // becomes look" work without the player thinking about it.
            ResolvePendingTouch(pendingInteractTouch);
            ResolvePendingTouch(pendingJumpTouch);

            // Runs after pending resolution and before the sticks are driven, so it can
            // reclaim any center-zone finger that briefly grabbed a stick this frame
            // before that stick produces input.
            UpdatePinch();

            // Drive the move stick.
            if (movementTouch != null)
            {
                Vector2 delta = movementTouch.currentPosition - movementTouch.startPosition;

                if (delta.magnitude > movementDeadZone)
                {
                    delta /= Screen.width * 0.3f;
                    delta = Vector2.ClampMagnitude(delta, 1f);
                    MovementInput = delta * movementSensitivity;
                }

                if (isLongPressing && Time.time - longPressStartTime > longPressTime)
                {
                    SprintActive = true;
                    isLongPressing = false;
                }
            }
            else
            {
                SprintActive = false;
            }

            // Drive the look stick as a virtual analog stick. Reads displacement
            // from the touch's start point (like the move stick), not per-frame
            // delta — so holding the thumb parked off-center keeps the camera
            // turning at a steady rate, matching the mobile FPS convention.
            // Normalizing against a fixed pixel travel distance (not Screen.width)
            // keeps the gesture's physical feel consistent across portrait and
            // landscape orientations.
            if (lookTouch != null)
            {
                Vector2 delta = lookTouch.currentPosition - lookTouch.startPosition;

                if (delta.magnitude > lookDeadZone)
                {
                    delta /= lookMaxTravel;
                    delta = Vector2.ClampMagnitude(delta, 1f);

                    // Y is passed through positive-up, matching Input System mouse delta.
                    // U3DPlayerController converts screen-up to pitch-up with its
                    // "cameraPitch -= lookInput.y" subtraction. Negating here as well
                    // would apply that conversion twice and invert touch look.
                    LookInput = new Vector2(delta.x, delta.y) * lookSensitivity * lookSpeedMultiplier;
                }
            }
        }

        /// <summary>
        /// If a pending action-zone touch has dragged past the convert distance
        /// or been held past the double-tap window without lifting, demote it to
        /// a stick touch on whichever side it sits on. Called every frame for
        /// each pending touch.
        /// </summary>
        private void ResolvePendingTouch(TouchData pending)
        {
            if (pending == null) return;

            Vector2 delta = pending.currentPosition - pending.startPosition;
            bool draggedTooFar = delta.magnitude > actionConvertDistance;
            bool heldTooLong = (Time.time - pending.startTime) > doubleTapWindow;

            if (!draggedTooFar && !heldTooLong) return;

            // Convert to a stick touch based on which side the touch is on.
            bool toLeft = pending.currentPosition.x < Screen.width * screenDivider;

            if (toLeft && movementTouch == null)
            {
                pending.role = TouchRole.Move;
                pending.startPosition = pending.currentPosition; // recenter the virtual stick
                movementTouch = pending;
                longPressStartTime = Time.time;
                isLongPressing = true;
            }
            else if (!toLeft && lookTouch == null)
            {
                pending.role = TouchRole.Look;
                lookTouch = pending;
            }
            else
            {
                // The side this touch is on already has a stick assigned. Just
                // mark it unassigned so it stops being a pending candidate; it
                // won't drive anything but will still get cleaned up on lift.
                pending.role = TouchRole.Unassigned;
            }

            if (pending == pendingInteractTouch) pendingInteractTouch = null;
            if (pending == pendingJumpTouch) pendingJumpTouch = null;
        }

        /// <summary>
        /// Detects a vertical two-finger pinch inside the center action zone and emits a
        /// one-shot signed PerspectiveScrollInput that mirrors the desktop scroll wheel:
        /// spread (gap grows) = positive = first person, squeeze = negative = third person.
        /// Requires exactly two center-zone fingers stacked more vertically than side by
        /// side, which is what separates a deliberate pinch from the horizontally separated
        /// move/look thumbs. Reclaims both fingers from any stick or tap role every frame
        /// so the pinch never drives movement, look, or interact.
        /// </summary>
        void UpdatePinch()
        {
            TouchData first = null;
            TouchData second = null;
            int centerCount = 0;

            foreach (var kvp in activeTouches)
            {
                TouchData t = kvp.Value;
                if (!IsInZone(t.currentPosition, middleZoneX, middleZoneY)) continue;
                centerCount++;
                if (first == null) first = t;
                else if (second == null) second = t;
            }

            bool valid = centerCount == 2 && first != null && second != null;

            if (valid)
            {
                Vector2 sep = second.currentPosition - first.currentPosition;
                if (Mathf.Abs(sep.x) > Mathf.Abs(sep.y) * pinchMaxDiagonalRatio)
                    valid = false;
            }

            if (!valid)
            {
                _pinchTouchA = null;
                _pinchTouchB = null;
                _pinchFired = false;
                return;
            }

            if (movementTouch == first || movementTouch == second)
            {
                movementTouch = null;
                isLongPressing = false;
            }
            if (lookTouch == first || lookTouch == second)
                lookTouch = null;
            if (pendingInteractTouch == first || pendingInteractTouch == second)
            {
                pendingInteractTouch = null;
                lastInteractTapTime = 0f;
            }
            if (pendingJumpTouch == first || pendingJumpTouch == second)
                pendingJumpTouch = null;

            first.role = TouchRole.Pinch;
            second.role = TouchRole.Pinch;

            if (_pinchTouchA == null)
            {
                _pinchTouchA = first;
                _pinchTouchB = second;
                _pinchStartSeparation = (second.currentPosition - first.currentPosition).magnitude;
                _pinchFired = false;
            }

            if (_pinchFired) return;

            float currentSeparation = (second.currentPosition - first.currentPosition).magnitude;
            float change = currentSeparation - _pinchStartSeparation;

            if (Mathf.Abs(change) > pinchTriggerDistance)
            {
                PerspectiveScrollInput = change > 0f ? 10f : -10f;
                _pinchFired = true;
            }
        }

        void HandleTouchBegan(ETouch touch)
        {
            int id = touch.touchId;

            if (activeTouches.ContainsKey(id))
            {
                // Spurious re-Began for an already-tracked touch: treat as a move
                // so we never lose continuity or reassign the touch's role.
                HandleTouchMoved(touch);
                return;
            }

            Vector2 position = touch.screenPosition;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(id))
                return;

            bool isLeftSide = position.x < Screen.width * screenDivider;
            bool inInteractZone = IsInZone(position, middleZoneX, middleZoneY);
            bool inJumpZone = IsInZone(position, bottomZoneX, bottomZoneY);

            TouchData data = new TouchData
            {
                touchId = id,
                startPosition = position,
                currentPosition = position,
                frameDelta = Vector2.zero,
                startTime = Time.time,
                isLeftSide = isLeftSide,
                role = TouchRole.Unassigned
            };

            activeTouches[id] = data;

            // Action zones take priority over stick zones for the initial touch.
            // The pending touch gets resolved into a stick later if it doesn't
            // turn out to be a tap.
            if (inInteractZone && pendingInteractTouch == null)
            {
                data.role = TouchRole.PendingInteract;
                pendingInteractTouch = data;
                CheckDoubleTap(position, ref lastInteractTapTime, ref lastInteractTapPosition, isInteract: true);
                return;
            }

            if (inJumpZone && pendingJumpTouch == null)
            {
                data.role = TouchRole.PendingJump;
                pendingJumpTouch = data;
                CheckDoubleTap(position, ref lastJumpTapTime, ref lastJumpTapPosition, isInteract: false);
                return;
            }

            // Outside action zones: assign to the appropriate stick if free.
            if (isLeftSide && movementTouch == null)
            {
                data.role = TouchRole.Move;
                movementTouch = data;
                longPressStartTime = Time.time;
                isLongPressing = true;
            }
            else if (!isLeftSide && lookTouch == null)
            {
                data.role = TouchRole.Look;
                lookTouch = data;
            }
        }

        /// <summary>
        /// Records the tap time/position. If this tap landed within doubleTapWindow
        /// and close enough to the previous tap of the same kind, fire the
        /// corresponding request. Otherwise just store this as the latest tap.
        /// </summary>
        private void CheckDoubleTap(Vector2 position, ref float lastTapTime, ref Vector2 lastTapPosition, bool isInteract)
        {
            float timeSince = Time.time - lastTapTime;
            float distance = Vector2.Distance(position, lastTapPosition);

            if (timeSince < doubleTapWindow && distance < 50f)
            {
                if (isInteract)
                    InteractRequested = true;
                else
                    JumpRequested = true;

                // Consume the first tap so a triple tap doesn't fire twice.
                lastTapTime = 0f;
            }
            else
            {
                lastTapTime = Time.time;
                lastTapPosition = position;
            }
        }

        private bool IsInZone(Vector2 screenPos, Vector2 xRange, Vector2 yRange)
        {
            float nx = screenPos.x / Screen.width;
            float ny = screenPos.y / Screen.height;
            return nx >= xRange.x && nx <= xRange.y && ny >= yRange.x && ny <= yRange.y;
        }

        void HandleTouchMoved(ETouch touch)
        {
            if (activeTouches.TryGetValue(touch.touchId, out TouchData data))
            {
                Vector2 position = touch.screenPosition;
                data.frameDelta += position - data.currentPosition;
                data.currentPosition = position;

                if (data == movementTouch && isLongPressing)
                {
                    Vector2 delta = data.currentPosition - data.startPosition;
                    if (delta.magnitude > movementDeadZone * 2)
                    {
                        isLongPressing = false;
                    }
                }
            }
        }

        void HandleTouchEnded(int touchId)
        {
            if (activeTouches.TryGetValue(touchId, out TouchData data))
            {
                if (data == movementTouch)
                {
                    movementTouch = null;
                    isLongPressing = false;
                    SprintActive = false;
                }
                else if (data == lookTouch)
                {
                    lookTouch = null;
                }
                else if (data == pendingInteractTouch)
                {
                    pendingInteractTouch = null;
                }
                else if (data == pendingJumpTouch)
                {
                    pendingJumpTouch = null;
                }

                activeTouches.Remove(touchId);
            }
        }
    }
}