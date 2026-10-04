using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Opens a URL in a new browser tab. Pair with U3DInteractTrigger for click-to-open,
    /// or wire Open() to any UnityEvent (enter triggers, buttons, etc).
    /// In WebGL builds, Application.OpenURL opens a new tab automatically.
    /// In the Unity Editor, it opens the system default browser.
    /// </summary>
    [AddComponentMenu("U3D/Media/U3D Open URL")]
    public class U3DOpenURL : MonoBehaviour
    {
        [Header("URL Configuration")]
        [Tooltip("The full URL to open (include https://)")]
        [SerializeField] private string url = "https://";

        [Header("Events")]
        public UnityEvent OnURLOpened;

        /// <summary>
        /// Opens the configured URL in a new browser tab.
        /// Call from any UnityEvent, trigger, or script.
        /// </summary>
        public void Open()
        {
            if (string.IsNullOrWhiteSpace(url) || url == "https://")
            {
                Debug.LogWarning($"U3DOpenURL on '{name}': No URL configured.");
                return;
            }

            Application.OpenURL(url);
            OnURLOpened?.Invoke();
        }

        /// <summary>
        /// Opens a specific URL, overriding the configured one.
        /// Useful for dynamic URL assignment from scripts.
        /// </summary>
        public void Open(string overrideUrl)
        {
            if (string.IsNullOrWhiteSpace(overrideUrl))
            {
                Debug.LogWarning($"U3DOpenURL on '{name}': Empty URL passed to Open().");
                return;
            }

            Application.OpenURL(overrideUrl);
            OnURLOpened?.Invoke();
        }

        public string URL { get => url; set => url = value; }
    }
}