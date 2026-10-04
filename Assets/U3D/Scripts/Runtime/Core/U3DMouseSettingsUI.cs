using UnityEngine;
using UnityEngine.UI;

namespace U3D.UI
{
    /// <summary>
    /// Simplified mouse settings UI with only essential controls
    /// </summary>
    public class U3DMouseSettingsUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private Toggle mouseSmoothingToggle;
        [SerializeField] private Toggle invertYToggle;

        private U3DPlayerController _playerController;
        private bool _isInitialized = false;

        void Start()
        {
            FindPlayerController();
            InitializeUI();
            SetupEventHandlers();
            RefreshUI();
            _isInitialized = true;
        }

        void FindPlayerController()
        {
            U3DPlayerController[] controllers = Object.FindObjectsByType<U3DPlayerController>(FindObjectsSortMode.None);

            foreach (var controller in controllers)
            {
                if (controller.IsLocalPlayer)
                {
                    _playerController = controller;
                    break;
                }
            }
        }

        void InitializeUI()
        {
            if (sensitivitySlider != null)
            {
                sensitivitySlider.minValue = 0.1f;
                sensitivitySlider.maxValue = 3.0f;
                sensitivitySlider.wholeNumbers = false;
            }
        }

        void SetupEventHandlers()
        {
            if (sensitivitySlider != null)
                sensitivitySlider.onValueChanged.AddListener(OnSensitivityChanged);

            if (mouseSmoothingToggle != null)
                mouseSmoothingToggle.onValueChanged.AddListener(OnMouseSmoothingChanged);

            if (invertYToggle != null)
                invertYToggle.onValueChanged.AddListener(OnInvertYChanged);
        }

        void RefreshUI()
        {
            if (_playerController == null) return;

            if (sensitivitySlider != null)
                sensitivitySlider.SetValueWithoutNotify(_playerController.GetUserSensitivity());

            if (mouseSmoothingToggle != null)
                mouseSmoothingToggle.SetIsOnWithoutNotify(_playerController.GetMouseSmoothing());

            if (invertYToggle != null)
            {
                bool isInverted = PlayerPrefs.GetInt("U3D_LookInverted", 0) == 1;
                invertYToggle.SetIsOnWithoutNotify(isInverted);
            }
        }

        void OnSensitivityChanged(float value)
        {
            if (!_isInitialized || _playerController == null) return;
            _playerController.SetUserSensitivity(value);
        }

        void OnMouseSmoothingChanged(bool enabled)
        {
            if (!_isInitialized || _playerController == null) return;
            _playerController.SetMouseSmoothing(enabled);
        }

        void OnInvertYChanged(bool inverted)
        {
            if (!_isInitialized) return;

            // Save to PlayerPrefs
            PlayerPrefs.SetInt("U3D_LookInverted", inverted ? 1 : 0);
            PlayerPrefs.Save();

            // Update the PlayerController's lookInverted field immediately
            if (_playerController != null)
            {
                _playerController.SetLookInverted(inverted);
            }
        }
    }
}