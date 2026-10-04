using UnityEngine;
using UnityEngine.InputSystem;

namespace U3D
{
    /// <summary>
    /// WebGL-specific cursor management. Tab frees the cursor for UI, Esc releases it
    /// entirely, click re-captures it for FPS controls.
    ///
    /// The browser can take the pointer lock away at any time without telling C# —
    /// Esc, opening dev tools, alt-tab, window blur. This component watches for that
    /// and stands down rather than assuming its own last request is still in effect.
    /// After a browser-initiated release, browsers refuse a new lock request for a
    /// short period even if a click occurs, so requests are held off until that
    /// window passes and a real click arrives.
    ///
    /// VR/WebXR: cursor lock is left alone entirely during immersive sessions.
    /// </summary>
    public class U3DWebGLCursorManager : MonoBehaviour
    {
        [Header("WebGL Cursor Configuration")]
        [SerializeField] private bool enableWebGLCursorManagement = true;
        [SerializeField] private bool startWithLockedCursor = true;

        [Tooltip("How long, in seconds, to stop asking for the mouse after the browser takes the lock away on its own. Browsers refuse requests made too soon after their own release gesture. Raise this if you still see pointer lock errors in the browser console.")]
        [SerializeField] private float relockCooldown = 1.5f;

        [Header("UI References")]
        [SerializeField] private GameObject pauseMenu;
        [SerializeField] private Canvas gameUI;

        // Cursor state management
        private bool _isCursorLocked = false;
        private bool _isUIMode = false; // Tab mode - cursor free but WebGL has focus
        private bool _isEscapedMode = false; // Esc mode - cursor free and WebGL lost focus
        private bool _isInVRMode = false; // VR mode - cursor lock disabled entirely
        private bool _hasReceivedUserGesture = false;

        // Browser lock-state tracking
        private CursorLockMode _lastObservedLockState = CursorLockMode.None;
        private float _relockBlockedUntil = 0f;
        private float _lockRequestGraceUntil = 0f;
        private const float LOCK_REQUEST_GRACE = 0.5f;

        // Player input reference, resolved lazily
        private U3D.Input.U3DPlayerInput _playerInput;
        private float _lastInputSearchTime = -999f;
        private const float INPUT_SEARCH_INTERVAL = 1f;

        // Events
        public static event System.Action<bool> OnCursorLockStateChanged;

        // Public properties
        // In VR mode, report as "locked" so input code proceeds normally
        public bool IsCursorLocked => _isInVRMode || _isCursorLocked;
        public bool IsUIMode => _isUIMode;
        public bool IsEscapedMode => _isEscapedMode;
        public bool IsInVRMode => _isInVRMode;

        void Awake()
        {
            bool isWebGLOrEditor = Application.platform == RuntimePlatform.WebGLPlayer ||
                                   Application.platform == RuntimePlatform.WindowsEditor ||
                                   Application.platform == RuntimePlatform.OSXEditor ||
                                   Application.platform == RuntimePlatform.LinuxEditor;

            if (!isWebGLOrEditor)
            {
                enableWebGLCursorManagement = false;
                enabled = false;
                return;
            }

            // Start with no pending lock request. Asking before the page has had a
            // click is guaranteed to be refused.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _isCursorLocked = false;
            _lastObservedLockState = CursorLockMode.None;
        }

        void Update()
        {
            if (!enableWebGLCursorManagement) return;

            // Skip cursor management input during VR - VR controllers handle everything
            if (_isInVRMode) return;

            PollBrowserLockState();

            // Resolved lazily rather than in Awake. Awake order between separate scene objects
            // is undefined, and click-to-lock must keep working even if player input is absent.
            if (_playerInput == null && Time.time - _lastInputSearchTime >= INPUT_SEARCH_INTERVAL)
            {
                _playerInput = U3D.Input.U3DPlayerInput.Instance;
                _lastInputSearchTime = Time.time;
            }

            if (_playerInput != null)
            {
                var pauseAction = _playerInput.PauseAction;
                if (pauseAction != null && pauseAction.WasPressedThisFrame())
                {
                    OnTabPressed();
                }

                var escapeAction = _playerInput.EscapeAction;
                if (escapeAction != null && escapeAction.WasPressedThisFrame())
                {
                    OnEscapePressed();
                }
            }

            // Monitor mouse clicks for returning to game mode or initial lock
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                OnClickPressed();
            }
        }

        /// <summary>
        /// Detects the browser releasing or granting the pointer lock behind our back.
        /// On release we explicitly clear the lock request so the engine stops retrying
        /// it against a browser that will refuse, and start the cooldown before we are
        /// willing to ask again.
        /// </summary>
        void PollBrowserLockState()
        {
            CursorLockMode actual = Cursor.lockState;
            if (actual == _lastObservedLockState) return;

            _lastObservedLockState = actual;

            if (actual != CursorLockMode.Locked && _isCursorLocked)
            {
                // A request we made moments ago may not be granted yet. Don't mistake
                // that for the browser refusing us.
                if (Time.unscaledTime < _lockRequestGraceUntil) return;

                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _isCursorLocked = false;
                _relockBlockedUntil = Time.unscaledTime + relockCooldown;
                _lastObservedLockState = CursorLockMode.None;

                OnCursorLockStateChanged?.Invoke(false);
            }
            else if (actual == CursorLockMode.Locked && !_isCursorLocked)
            {
                Cursor.visible = false;
                _isCursorLocked = true;

                OnCursorLockStateChanged?.Invoke(true);
            }
        }

        /// <summary>
        /// True when the browser is willing to consider a lock request: the page has
        /// had at least one real interaction, and we are past the cooldown following
        /// any browser-initiated release.
        /// </summary>
        bool CanRequestLock()
        {
            if (_isInVRMode) return false;
            if (!_hasReceivedUserGesture) return false;
            return Time.unscaledTime >= _relockBlockedUntil;
        }

        void OnTabPressed()
        {
            _hasReceivedUserGesture = true;
            if (_isEscapedMode) return; // Can't toggle while escaped
            ToggleUIMode();
        }

        void OnEscapePressed()
        {
            _hasReceivedUserGesture = true;

            // The browser handles Esc itself and releases the lock before this runs.
            // Esc is the one release gesture browsers penalise hardest, so hold off
            // asking again for the full cooldown.
            _relockBlockedUntil = Time.unscaledTime + relockCooldown;
            SetEscapedMode(true);
        }

        void OnClickPressed()
        {
            _hasReceivedUserGesture = true;

            // Skip if in VR mode
            if (_isInVRMode) return;

            if (_isEscapedMode)
            {
                // Click detected - WebGL regaining focus
                OnWebGLWindowRegainedFocus();
                return;
            }

            if (_isUIMode)
            {
                // Check if we clicked on UI elements
                if (!IsPointerOverUI())
                {
                    // Clicked on game area - return to game mode
                    SetUIMode(false);
                }
            }
            else if (!_isCursorLocked && startWithLockedCursor)
            {
                // First click, or re-capture after the browser took the lock away.
                TrySetCursorLocked(true);
            }
        }

        public void ToggleUIMode()
        {
            SetUIMode(!_isUIMode);
        }

        public void SetUIMode(bool uiMode)
        {
            if (_isEscapedMode) return; // Can't change UI mode while escaped
            if (_isInVRMode) return; // UI mode not applicable in VR

            _isUIMode = uiMode;

            if (_isUIMode)
            {
                // Entering UI mode - unlock cursor but keep WebGL focus
                TrySetCursorLocked(false);

                // Show pause menu
                if (pauseMenu != null)
                    pauseMenu.SetActive(true);
            }
            else
            {
                // Exiting UI mode - lock cursor
                TrySetCursorLocked(true);

                // Hide pause menu
                if (pauseMenu != null)
                    pauseMenu.SetActive(false);
            }
        }

        public void SetEscapedMode(bool escapedMode)
        {
            if (_isInVRMode) return; // Escape mode not applicable in VR

            _isEscapedMode = escapedMode;

            if (_isEscapedMode)
            {
                // Esc pressed - release cursor completely
                _isUIMode = false; // Clear UI mode
                TrySetCursorLocked(false);

                // Hide all game UI
                if (pauseMenu != null)
                    pauseMenu.SetActive(false);
                if (gameUI != null)
                    gameUI.enabled = false;
            }
            else
            {
                // Returning from escape - restore game UI
                if (gameUI != null)
                    gameUI.enabled = true;
            }
        }

        /// <summary>
        /// Called by U3DWebXRManager or U3DPlayerController when VR session starts/ends.
        /// Cursor lock is left untouched during VR, and on exit we wait for a click
        /// rather than requesting the lock ourselves — leaving a headset is not a page
        /// interaction, so the browser would refuse the request.
        /// </summary>
        public void SetVRMode(bool enabled)
        {
            bool wasInVR = _isInVRMode;
            _isInVRMode = enabled;

            if (enabled && !wasInVR)
            {
                // Entering VR - just update internal state
                // Don't touch Cursor.lockState at all - browser handles VR input exclusively
                _isCursorLocked = false;

                // Clear UI/escape modes
                _isUIMode = false;
                _isEscapedMode = false;
            }
            else if (!enabled && wasInVR)
            {
                // Exiting VR - clear any stale lock request and wait for the next click
                // to re-capture the mouse for FPS controls.
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _isCursorLocked = false;
                _lastObservedLockState = CursorLockMode.None;
                _relockBlockedUntil = Time.unscaledTime + relockCooldown;

                OnCursorLockStateChanged?.Invoke(false);
            }
        }

        void OnWebGLWindowRegainedFocus()
        {
            if (_isEscapedMode)
            {
                // Player clicked back into WebGL window
                SetEscapedMode(false);
                TrySetCursorLocked(true); // Resume FPS mode
            }
        }

        /// <summary>
        /// Sets the cursor lock state. Lock requests are dropped rather than attempted
        /// when the browser would refuse them, because a refused request surfaces as a
        /// page-level error the engine turns into a modal dialog over the content.
        /// </summary>
        void TrySetCursorLocked(bool locked)
        {
            if (_isInVRMode)
            {
                _isCursorLocked = false;
                return;
            }

            if (locked)
            {
                if (!CanRequestLock()) return;

                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                _isCursorLocked = true;
                _lastObservedLockState = CursorLockMode.Locked;
                _lockRequestGraceUntil = Time.unscaledTime + LOCK_REQUEST_GRACE;
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                _isCursorLocked = false;
                _lastObservedLockState = CursorLockMode.None;
            }

            OnCursorLockStateChanged?.Invoke(_isCursorLocked);
        }

        bool IsPointerOverUI()
        {
            // Check if mouse is over UI elements
            return UnityEngine.EventSystems.EventSystem.current != null &&
                   UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        }

        // Public method to check if game input should be processed
        public bool ShouldProcessGameInput()
        {
            // In VR mode, always process game input
            if (_isInVRMode) return true;

            return !_isEscapedMode;
        }
    }
}