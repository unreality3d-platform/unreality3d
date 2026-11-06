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
    /// In VR the canvas is held on the left wrist by U3DWristMount, above the player menu, with the
    /// chip and panel at the top of the canvas; pressing the chip opens the panel in place and
    /// pressing its title closes it, without UI mode, so an open panel never takes UI focus.
    /// PanelOpenChanged follows the panel's active state, so it is raised however the panel opens
    /// or closes. A Remove / Exit button gives every player a second way to send the Remove action,
    /// and the only way on a phone. Touch is detected the same way the touch control hints are, so
    /// the layout is right before the first touch.
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

        private const float VRCanvasMargin = 8f;

        /// <summary>
        /// Raised when the panel opens or closes, by any route. The float is how far the open panel
        /// reaches down from the top of the screen, in screen points, on the touch layout; 0 when closed.
        /// </summary>
        public static event System.Action<bool, float> PanelOpenChanged;

        private float ChipHeight => chip != null ? chip.rect.height : 0f;

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

        private Canvas _canvas;
        private RectPose _chipDesktopPose;
        private RectPose _panelDesktopPose;
        private Vector2 _invertYDesktopPosition;
        private Vector2 _smoothingPosition;
        private bool _inVR;
        private bool _hasReported;
        private bool _reportedOpen;
        private int _lastScreenWidth = -1;
        private int _lastScreenHeight = -1;
        private float _lastSafeAreaTop = -1f;

        void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();

            if (chip != null) _chipDesktopPose = RectPose.From(chip);
            if (panel != null) _panelDesktopPose = RectPose.From(panel);
            if (invertYRow != null) _invertYDesktopPosition = invertYRow.anchoredPosition;
            if (mouseSmoothingRow != null)
                _smoothingPosition = ((RectTransform)mouseSmoothingRow.transform).anchoredPosition;

            if (removeButton != null)
                removeButton.onClick.AddListener(OnRemovePressed);

            if (chip != null)
                AddRelay(chip.gameObject).Clicked += OnChipPressed;
            if (titleLabel != null)
            {
                titleLabel.raycastTarget = true;
                AddRelay(titleLabel.gameObject).Clicked += OnTitlePressed;
            }
        }

        void OnEnable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged += Refresh;
            U3DWebXRManager.OnVRModeChanged += OnVRModeChanged;
            U3DWebGLCursorManager.OnUIModeChanged += SetOpen;
            if (U3DWebXRManager.Instance != null && U3DWebXRManager.Instance.IsVRActive)
                OnVRModeChanged(true);
            Refresh();
        }

        void OnDisable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged -= Refresh;
            U3DWebXRManager.OnVRModeChanged -= OnVRModeChanged;
            U3DWebGLCursorManager.OnUIModeChanged -= SetOpen;
            if (_inVR)
            {
                _inVR = false;
                U3DWristMount.Release(RootCanvas);
            }
        }

        void Update()
        {
            ReportPanelState();

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
            ReportPanelState();
        }

        /// <summary>
        /// Raises PanelOpenChanged, and moves the player menu down on the wrist, when the panel's
        /// active state differs from the last one reported. The chip's own Button and the panel's
        /// close button switch the panel directly, so the state is read rather than assumed.
        /// </summary>
        private void ReportPanelState()
        {
            bool open = panel != null && panel.gameObject.activeInHierarchy;
            if (_hasReported && open == _reportedOpen) return;
            _hasReported = true;
            _reportedOpen = open;

            if (open) LayoutRebuilder.ForceRebuildLayoutImmediate(panel);

            if (_inVR)
            {
                float shown = open ? panel.rect.height : ChipHeight;
                U3DWristMount.SetHeader(RootCanvas, VRCanvasMargin + shown);
            }

            float panelBottom = open
                ? touchTopMargin + U3DCanvasCssScaler.SafeAreaTopCss + panel.rect.height
                : 0f;
            PanelOpenChanged?.Invoke(open, panelBottom);
        }

        private Canvas RootCanvas => _canvas != null ? _canvas.rootCanvas : null;

        private void OnVRModeChanged(bool inVR)
        {
            if (inVR == _inVR) return;
            _inVR = inVR;

            if (inVR)
            {
                Vector2 size = VRCanvasSize();
                SetOpen(false);
                U3DWristMount.Hold(RootCanvas, U3DWristMount.Slot.Upper, size, VRCanvasMargin + ChipHeight);
            }
            else
            {
                U3DWristMount.Release(RootCanvas);
                SetOpen(false);
            }

            Refresh();
        }

        private Vector2 VRCanvasSize()
        {
            float width = 0f;
            float height = 0f;
            if (panel != null)
            {
                width = Mathf.Max(width, panel.rect.width);
                height = Mathf.Max(height, panel.rect.height);
            }
            if (chip != null)
            {
                width = Mathf.Max(width, chip.rect.width);
                height = Mathf.Max(height, chip.rect.height);
            }
            return new Vector2(width + 2f * VRCanvasMargin, height + 2f * VRCanvasMargin);
        }

        private void OnChipPressed()
        {
            if (_inVR) SetOpen(true);
        }

        private void OnTitlePressed()
        {
            if (_inVR) SetOpen(false);
        }

        private static U3DClickRelay AddRelay(GameObject target)
        {
            U3DClickRelay relay = target.GetComponent<U3DClickRelay>();
            return relay != null ? relay : target.AddComponent<U3DClickRelay>();
        }

        private void OnRemovePressed()
        {
            if (U3DPlayerInput.Instance != null)
                U3DPlayerInput.Instance.RequestRemove();
        }

        private void Refresh()
        {
            bool touch = !_inVR && U3DSimpleTouchZones.ShouldShowTouchHints;

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
            if (_inVR)
            {
                PlaceTopOfVRCanvas(chip);
                PlaceTopOfVRCanvas(panel);
            }
            else if (touch)
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

        private static void PlaceTopOfVRCanvas(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -VRCanvasMargin);
        }
    }
}