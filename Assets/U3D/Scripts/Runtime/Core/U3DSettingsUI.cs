using TMPro;
using UnityEngine;
using UnityEngine.UI;
using U3D.Input;
using U3D.XR;

namespace U3D.UI
{
    /// <summary>
    /// Root of the Settings UI. Opens and closes the settings panel with UI mode, so Tab frees
    /// the cursor and shows Settings in one press. On touch devices the chip and panel move to
    /// top centre below any notch, the chip reads "Settings" and mouse-only controls are hidden.
    /// A Remove / Exit button gives every player a second way to send the Remove action, and the
    /// only way on a phone. Touch is detected the same way the touch control hints are, so the
    /// layout is right before the first touch.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DSettingsUI : MonoBehaviour
    {
        [Header("Parts")]
        [Tooltip("The closed-state chip that opens the panel.")]
        [SerializeField] private RectTransform chip;
        [Tooltip("The settings panel.")]
        [SerializeField] private RectTransform panel;
        [SerializeField] private TextMeshProUGUI chipLabel;
        [SerializeField] private TextMeshProUGUI titleLabel;

        [Header("Look Controls")]
        [SerializeField] private TextMeshProUGUI lookSectionLabel;
        [Tooltip("Hidden on touch devices, where touch look skips mouse smoothing.")]
        [SerializeField] private GameObject mouseSmoothingRow;
        [Tooltip("Moved up into the smoothing row's place on touch devices.")]
        [SerializeField] private RectTransform invertYRow;

        [Header("Touch")]
        [Tooltip("Sends the Remove action: takes off the last worn item, or leaves a steerable. The same as the Remove key, for players who prefer a button or have no key for it.")]
        [SerializeField] private Button removeButton;
        [Tooltip("Distance, in CSS pixels, from the top of the screen to the chip and panel on touch devices, below any notch or camera cutout.")]
        [SerializeField] private float touchTopMargin = 12f;

        [Header("Text")]
        [SerializeField] private string desktopChipText = "Tab: Settings";
        [SerializeField] private string compactChipText = "Settings";
        [SerializeField] private string titleText = "Settings";
        [SerializeField] private string desktopLookSectionText = "MOUSE CONTROLS";
        [SerializeField] private string touchLookSectionText = "LOOK CONTROLS";

        private struct RectPose
        {
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;

            public static RectPose From(RectTransform rect)
            {
                return new RectPose
                {
                    AnchorMin = rect.anchorMin,
                    AnchorMax = rect.anchorMax,
                    Pivot = rect.pivot,
                    AnchoredPosition = rect.anchoredPosition
                };
            }

            public void ApplyTo(RectTransform rect)
            {
                rect.anchorMin = AnchorMin;
                rect.anchorMax = AnchorMax;
                rect.pivot = Pivot;
                rect.anchoredPosition = AnchoredPosition;
            }
        }

        private RectPose _chipDesktopPose;
        private RectPose _panelDesktopPose;
        private Vector2 _invertYDesktopPosition;
        private Vector2 _smoothingPosition;
        private bool _inVR;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _lastSafeAreaTop = -1f;

        void Awake()
        {
            if (chip != null) _chipDesktopPose = RectPose.From(chip);
            if (panel != null) _panelDesktopPose = RectPose.From(panel);
            if (invertYRow != null) _invertYDesktopPosition = invertYRow.anchoredPosition;
            if (mouseSmoothingRow != null)
                _smoothingPosition = ((RectTransform)mouseSmoothingRow.transform).anchoredPosition;

            if (removeButton != null)
                removeButton.onClick.AddListener(OnRemovePressed);
        }

        void OnEnable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged += Refresh;
            U3DWebXRManager.OnVRModeChanged += OnVRModeChanged;
            U3DWebGLCursorManager.OnUIModeChanged += SetOpen;
            Refresh();
        }

        void OnDisable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged -= Refresh;
            U3DWebXRManager.OnVRModeChanged -= OnVRModeChanged;
            U3DWebGLCursorManager.OnUIModeChanged -= SetOpen;
        }

        void Update()
        {
            float safeAreaTop = U3DCanvasCssScaler.SafeAreaTopCss;
            if (Screen.width == _lastScreenWidth && Screen.height == _lastScreenHeight
                && Mathf.Approximately(safeAreaTop, _lastSafeAreaTop)) return;
            _lastScreenWidth = Screen.width;
            _lastScreenHeight = Screen.height;
            _lastSafeAreaTop = safeAreaTop;
            Refresh();
        }

        /// <summary>Shows the panel and hides the chip, or the reverse.</summary>
        public void SetOpen(bool open)
        {
            if (panel != null) panel.gameObject.SetActive(open);
            if (chip != null) chip.gameObject.SetActive(!open);
        }

        private void OnVRModeChanged(bool inVR)
        {
            _inVR = inVR;
            Refresh();
        }

        private void OnRemovePressed()
        {
            if (U3DPlayerInput.Instance != null)
                U3DPlayerInput.Instance.RequestRemove();
        }

        private void Refresh()
        {
            bool touch = U3DSimpleTouchZones.ShouldShowTouchHints;

            if (chipLabel != null)
                chipLabel.text = (touch || _inVR) ? compactChipText : desktopChipText;
            if (titleLabel != null)
                titleLabel.text = titleText;
            if (lookSectionLabel != null)
                lookSectionLabel.text = touch ? touchLookSectionText : desktopLookSectionText;

            if (mouseSmoothingRow != null)
                mouseSmoothingRow.SetActive(!touch);
            if (invertYRow != null)
                invertYRow.anchoredPosition = touch && mouseSmoothingRow != null ? _smoothingPosition : _invertYDesktopPosition;

            ApplyLayout(touch);
        }

        private void ApplyLayout(bool touch)
        {
            if (touch)
            {
                PlaceTopCentre(chip);
                PlaceTopCentre(panel);
            }
            else
            {
                if (chip != null) _chipDesktopPose.ApplyTo(chip);
                if (panel != null) _panelDesktopPose.ApplyTo(panel);
            }
        }

        private void PlaceTopCentre(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -(touchTopMargin + U3DCanvasCssScaler.SafeAreaTopCss));
        }
    }
}
