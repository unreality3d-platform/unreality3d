using UnityEngine;

namespace U3D.Input
{
    /// <summary>
    /// Shows on-screen touch control hints, such as Move and Look circles, only when
    /// U3DSimpleTouchZones.ShouldShowTouchHints is true. Place this on a GameObject
    /// that contains only the hint graphics. Its CanvasGroup is forced to let touches
    /// pass through, because U3DSimpleTouchZones ignores any touch that begins over a
    /// UI raycast target.
    /// </summary>
    [RequireComponent(typeof(CanvasGroup))]
    [DisallowMultipleComponent]
    public class U3DTouchControlHints : MonoBehaviour
    {
        [Tooltip("Opacity of the hints while they are shown.")]
        [SerializeField, Range(0f, 1f)] private float visibleAlpha = 1f;

        private CanvasGroup _canvasGroup;

        void Reset()
        {
            ConfigureCanvasGroup(GetComponent<CanvasGroup>());
        }

        void Awake()
        {
            _canvasGroup = GetComponent<CanvasGroup>();
            ConfigureCanvasGroup(_canvasGroup);
        }

        void OnEnable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged += Refresh;
            Refresh();
        }

        void OnDisable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged -= Refresh;
        }

        private void Refresh()
        {
            _canvasGroup.alpha = U3DSimpleTouchZones.ShouldShowTouchHints ? visibleAlpha : 0f;
        }

        private static void ConfigureCanvasGroup(CanvasGroup group)
        {
            if (group == null) return;
            group.blocksRaycasts = false;
            group.interactable = false;
        }
    }
}
