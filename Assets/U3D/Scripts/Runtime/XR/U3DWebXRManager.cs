using UnityEngine;
#if WEBXR_ENABLED
using WebXR;
#endif

namespace U3D.XR
{
    /// <summary>
    /// WebXR Manager for Unreality3D - Bridges WebXR Export package with U3D player system.
    /// Handles VR session start/end and notifies U3DPlayerController of mode changes.
    /// </summary>
    public class U3DWebXRManager : MonoBehaviour
    {
        [Header("WebXR Configuration")]
        [SerializeField] private bool autoFindLocalPlayer = true;
        [SerializeField] private bool verboseLogging = false;

        public static U3DWebXRManager Instance { get; private set; }

        private bool _isVRActive = false;
        private bool _isVRSupported = false;
        private U3DPlayerController _localPlayerController;

#if WEBXR_ENABLED
        private WebXRState _currentXRState = WebXRState.NORMAL;
#endif

        public delegate void VRModeChanged(bool isVRActive);
        public static event VRModeChanged OnVRModeChanged;

        public delegate void VRSupportDetected(bool isSupported);
#pragma warning disable 0067 // Event is invoked only in WEBXR_ENABLED WebGL builds; editor compile sees no invocation.
        public static event VRSupportDetected OnVRSupportDetected;
#pragma warning restore 0067

        public bool IsVRActive => _isVRActive;
        public bool IsVRSupported => _isVRSupported;
        public U3DPlayerController LocalPlayer => _localPlayerController;

        void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (Instance != this)
            {
                Destroy(gameObject);
                return;
            }
        }

        void Start()
        {
            InitializeWebXR();
        }

        void InitializeWebXR()
        {
#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            // OnEnable owns the subscription to OnXRChange. Subscribing here as well attached
            // the handler twice, harmless only because the second call finds the state unchanged.
            StartCoroutine(CheckVRSupportDelayed());
#else
            _isVRSupported = false;
#endif
        }

#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
        private System.Collections.IEnumerator CheckVRSupportDelayed()
        {
            yield return new WaitForSeconds(0.5f);
            
            if (WebXRManager.Instance != null)
            {
                _isVRSupported = WebXRManager.Instance.isSupportedVR;
                OnVRSupportDetected?.Invoke(_isVRSupported);
            }
            else
            {
                _isVRSupported = false;
                OnVRSupportDetected?.Invoke(false);
            }
        }

        private void OnXRChange(WebXRState state, int viewsCount, Rect leftRect, Rect rightRect)
        {    
            _currentXRState = state;
            bool wasVRActive = _isVRActive;
            _isVRActive = (state == WebXRState.VR);

            if (_isVRActive != wasVRActive)
            {
                HandleVRModeChange(_isVRActive);
            }
        }
#endif

        private void HandleVRModeChange(bool enteringVR)
        {
            if (_localPlayerController == null && autoFindLocalPlayer)
            {
                FindLocalPlayer();
            }

            if (_localPlayerController != null)
            {
                _localPlayerController.SetVRMode(enteringVR);
            }
            else
            {
                Debug.LogWarning("[U3DWebXRManager] No local player found to notify of VR mode change");
            }

            OnVRModeChanged?.Invoke(enteringVR);
        }

        public void FindLocalPlayer()
        {
            var allPlayers = FindObjectsByType<U3DPlayerController>(FindObjectsSortMode.None);

            foreach (var player in allPlayers)
            {
                if (player.IsLocalPlayer)
                {
                    _localPlayerController = player;
                    return;
                }
            }
        }

        public void RegisterLocalPlayer(U3DPlayerController player)
        {
            _localPlayerController = player;

            if (_isVRActive)
            {
                player.SetVRMode(true);
            }
        }

        public void UnregisterLocalPlayer(U3DPlayerController player)
        {
            if (_localPlayerController == player)
            {
                _localPlayerController = null;
            }
        }

        void OnDestroy()
        {
#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            WebXRManager.OnXRChange -= OnXRChange;
#endif

            if (Instance == this)
            {
                Instance = null;
            }
        }

        void OnDisable()
        {
#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            WebXRManager.OnXRChange -= OnXRChange;
#endif
        }

        void OnEnable()
        {
#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            WebXRManager.OnXRChange -= OnXRChange;
            WebXRManager.OnXRChange += OnXRChange;
#endif
        }
    }
}