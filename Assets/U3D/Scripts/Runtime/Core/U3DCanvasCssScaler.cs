using UnityEngine;
using UnityEngine.UI;

namespace U3D.UI
{
    /// <summary>
    /// Sizes a screen-space canvas in screen points, the device-independent unit a phone or
    /// desktop uses for physical size, so one unit in the canvas looks the same size on every
    /// device. Two things vary underneath it: how many canvas pixels the page renders per CSS
    /// pixel (the page's pixel ratio), and how far the browser zooms the page (an installed
    /// home-screen app on iOS zooms by 1.5; Request Desktop Site zooms out). The ratio is the
    /// first divided by the second, read from the page and re-read twice a second, because the
    /// canvas can still be changing size when the experience starts.
    ///
    /// Also reports the top safe-area inset (a notch or camera cutout), so UI placed at the top
    /// of the screen can sit below it.
    ///
    /// In the Editor both values are emulated from Screen.dpi and Screen.safeArea, so the
    /// Device Simulator previews a phone at roughly the size it appears in the browser. A normal
    /// Game view on a standard-density monitor gets a ratio of 1 and no inset.
    /// </summary>
    [RequireComponent(typeof(CanvasScaler))]
    [DisallowMultipleComponent]
    public class U3DCanvasCssScaler : MonoBehaviour
    {
        private const float EditorReferenceDpi = 160f;
        private const float RefreshInterval = 0.5f;

        private static int _cachedScreenWidth = -1;
        private static int _cachedScreenHeight = -1;
        private static float _nextRefreshTime = -1f;
        private static float _cachedRatio = 1f;
        private static float _cachedSafeAreaTop = 0f;

        private CanvasScaler _scaler;

#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern float U3D_GetCssPixelRatio();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern float U3D_GetSafeAreaTopCss();
#endif

        /// <summary>Canvas pixels per screen point. 1 when unknown.</summary>
        public static float CssPixelRatio
        {
            get
            {
                RefreshCache();
                return _cachedRatio;
            }
        }

        /// <summary>Height of the top safe-area inset (notch or camera cutout) in canvas units. 0 when there is none.</summary>
        public static float SafeAreaTopCss
        {
            get
            {
                RefreshCache();
                return _cachedSafeAreaTop;
            }
        }

        private static void RefreshCache()
        {
            bool sizeChanged = Screen.width != _cachedScreenWidth || Screen.height != _cachedScreenHeight;
            if (!sizeChanged && Time.unscaledTime < _nextRefreshTime) return;

            _cachedScreenWidth = Screen.width;
            _cachedScreenHeight = Screen.height;
            _nextRefreshTime = Time.unscaledTime + RefreshInterval;

#if UNITY_WEBGL && !UNITY_EDITOR
            float ratio = U3D_GetCssPixelRatio();
            _cachedRatio = ratio > 0.01f ? ratio : 1f;
            _cachedSafeAreaTop = Mathf.Max(0f, U3D_GetSafeAreaTopCss());
#else
            _cachedRatio = Mathf.Max(1f, Screen.dpi / EditorReferenceDpi);
            float topInsetPixels = Screen.height - Screen.safeArea.yMax;
            _cachedSafeAreaTop = Mathf.Max(0f, topInsetPixels / _cachedRatio);
#endif
        }

        void Awake()
        {
            _scaler = GetComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _scaler.scaleFactor = CssPixelRatio;
        }

        void Update()
        {
            float ratio = CssPixelRatio;
            if (!Mathf.Approximately(_scaler.scaleFactor, ratio))
                _scaler.scaleFactor = ratio;
        }
    }
}
