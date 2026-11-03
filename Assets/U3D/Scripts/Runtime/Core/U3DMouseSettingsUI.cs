using UnityEngine;
using UnityEngine.UI;

namespace U3D.UI
{
    /// <summary>
    /// Look settings: sensitivity, mouse smoothing and Y invert. Finds the local player each
    /// time its panel is shown, because the player spawns at runtime and may not exist yet
    /// the first time the panel opens.
    /// </summary>
    public class U3DMouseSettingsUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField] private Slider sensitivitySlider;
        [SerializeField] private Toggle mouseSmoothingToggle;
        [SerializeField] private Toggle invertYToggle;

        private U3DPlayerController _playerController;
        private bool _listenersAdded = false;

        void OnEnable()
        {
            if (!_listenersAdded)
            {
                InitializeUI();
                SetupEventHandlers();
                _listenersAdded = true;
            }

            _playerController = U3DPlayerController.FindLocalPlayer();
            RefreshUI();
        }

        bool EnsurePlayerController()
        {
            if (_playerController == null)
                _playerController = U3DPlayerController.FindLocalPlayer();
            return _playerController != null;
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
            if (invertYToggle != null)
            {
                bool isInverted = PlayerPrefs.GetInt("U3D_LookInverted", 0) == 1;
                invertYToggle.SetIsOnWithoutNotify(isInverted);
            }

            if (_playerController == null) return;

            if (sensitivitySlider != null)
                sensitivitySlider.SetValueWithoutNotify(_playerController.GetUserSensitivity());

            if (mouseSmoothingToggle != null)
                mouseSmoothingToggle.SetIsOnWithoutNotify(_playerController.GetMouseSmoothing());
        }

        void OnSensitivityChanged(float value)
        {
            if (!EnsurePlayerController()) return;
            _playerController.SetUserSensitivity(value);
        }

        void OnMouseSmoothingChanged(bool enabled)
        {
            if (!EnsurePlayerController()) return;
            _playerController.SetMouseSmoothing(enabled);
        }

        void OnInvertYChanged(bool inverted)
        {
            PlayerPrefs.SetInt("U3D_LookInverted", inverted ? 1 : 0);
            PlayerPrefs.Save();

            if (EnsurePlayerController())
                _playerController.SetLookInverted(inverted);
        }
    }
}
