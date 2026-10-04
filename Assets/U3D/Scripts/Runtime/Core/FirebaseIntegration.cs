using System.Runtime.InteropServices;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// U3D Platform Integration - Handles PayPal, professional URLs, and creator identity.
/// This component works automatically. No configuration needed by creators.
///
/// The GameObject carrying this component must stay named "U3D_FirebaseIntegration".
/// FirebasePlugin.jslib targets that exact name when routing inbound callbacks.
/// </summary>
public class FirebaseIntegration : MonoBehaviour
{
    [Header("Debug Info (Runtime Only)")]
    [SerializeField, ReadOnly] private string detectedEnvironment = "Not running";

    private string contentId = "creator-content";

    // Static identity snapshot. Set on every successful profile delivery and
    // available to late subscribers (e.g. a player controller that spawns
    // after the bridge has already resolved). Subscribers should check
    // IsLocalProfileReady on subscription and apply the snapshot immediately
    // if true, then rely on OnLocalProfileReady for future updates.
    public static UserInfo LocalProfile { get; private set; }
    public static bool IsLocalProfileReady => LocalProfile != null;

    // Fires whenever a profile is delivered from the JS bridge. Fires once
    // on initial resolution and again on any subsequent re-delivery (e.g.
    // mid-session re-login). Subscribers wanting one-shot behavior can
    // unsubscribe themselves after the first call.
    public static event System.Action<UserInfo> OnLocalProfileReady;

    [DllImport("__Internal")]
    private static extern void UnityCheckContentAccess(string contentId);

    // PORT: UnityRequestPayment and UnityGetCurrentURL are declared here and called
    // by nothing. Both are private, so this file is the only possible caller.
    // Commerce path — confirm with Laurie before deleting. B20
    [DllImport("__Internal")]
    private static extern void UnityRequestPayment(string contentId, string price);

    [DllImport("__Internal")]
    private static extern System.IntPtr UnityGetCurrentURL();

    [DllImport("__Internal")]
    private static extern System.IntPtr UnityGetDeploymentInfo();

    [DllImport("__Internal")]
    private static extern void UnityReportDeploymentMetrics(string deploymentType, string loadTime);

    [DllImport("__Internal")]
    private static extern void UnityGetUserProfile();

    private UserInfo _currentUserInfo;
    private DeploymentInfo _deploymentInfo;
    private float _startTime;

    [System.Serializable]
    public class UserInfo
    {
        public string userId;
        public string displayName;
        public string userType;
        public bool paypalConnected;
        public string creatorUsername;
    }

    [System.Serializable]
    public class DeploymentInfo
    {
        public string url;
        public string hostname;
        public string pathname;
        public bool isProduction;
        public bool isProfessionalURL;
        public string creatorUsername;
        public string projectName;
        public string deploymentType;
    }

    void Awake()
    {
        _startTime = Time.time;
        DetectDeploymentEnvironment();
    }

    void Start()
    {
        CheckContentAccess();
        RequestUserProfile();
    }

    void DetectDeploymentEnvironment()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            var deploymentInfoPtr = UnityGetDeploymentInfo();
            var deploymentInfoJson = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(deploymentInfoPtr);

            if (!string.IsNullOrEmpty(deploymentInfoJson))
            {
                _deploymentInfo = JsonUtility.FromJson<DeploymentInfo>(deploymentInfoJson);
                detectedEnvironment = _deploymentInfo.deploymentType;

                if (_deploymentInfo.isProfessionalURL)
                {
                    contentId = $"{_deploymentInfo.creatorUsername}_{_deploymentInfo.projectName}";
                }

                Invoke(nameof(ReportDeploymentMetrics), 2f);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Platform detection failed: {e.Message}");
            detectedEnvironment = "Detection failed";
        }
#else
        detectedEnvironment = "Unity Editor";
        _deploymentInfo = new DeploymentInfo
        {
            deploymentType = "editor",
            isProduction = false,
            isProfessionalURL = false
        };
#endif
    }

    void ReportDeploymentMetrics()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        if (_deploymentInfo != null)
        {
            var loadTime = (Time.time - _startTime) * 1000f;
            try
            {
                UnityReportDeploymentMetrics(_deploymentInfo.deploymentType, loadTime.ToString("F0"));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Failed to report metrics: {e.Message}");
            }
        }
#endif
    }

    void RequestUserProfile()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            UnityGetUserProfile();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"User profile request failed: {e.Message}");
        }
#endif
    }

    void CheckContentAccess()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            UnityCheckContentAccess(contentId);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Access check failed: {e.Message}");
        }
#endif
    }

    public void OnAccessCheckComplete(string hasAccess)
    {
    }

    public void OnPaymentComplete(string success)
    {
        if (success == "true")
            CheckContentAccess();
    }

    public void OnUserProfileReceived(string userDataJson)
    {
        try
        {
            var parsed = JsonUtility.FromJson<UserInfo>(userDataJson);
            _currentUserInfo = parsed;

            // Update static snapshot before firing the event so late-running
            // subscribers checking IsLocalProfileReady from inside the handler
            // see the new value, not the previous one.
            LocalProfile = parsed;

            OnLocalProfileReady?.Invoke(parsed);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"User profile parsing failed: {e.Message}");
        }
    }

    // Inbound callback from content.js when the PayPal SDK script finishes
    // loading on the creator page. Currently a no-op stub — the existing
    // PayPal flow doesn't gate on this signal — but the method exists so
    // SendMessage routing doesn't log "method not found" warnings. Wire
    // real logic here later if PayPal initialization ever needs to react
    // to SDK readiness from the C# side.
    public void OnPayPalSDKReady(string ready)
    {
    }

    // Inbound callbacks from FirebasePlugin.jslib session paths. The .jslib
    // SendMessages for OnSessionCreated and OnSessionJoinResponse target
    // this GameObject by name; these stubs prevent "method not found"
    // warnings now that the GameObject-name fix routes them here correctly.
    // Wire real logic here when/if session lifecycle handling moves into
    // the C# layer.
    public void OnSessionCreated(string sessionData)
    {
    }

    public void OnSessionJoinResponse(string responseData)
    {
    }

    // Inbound callback from FirebasePlugin.jslib UnityReportBrowserInfo
    // path. Stubbed for the same reason as the session callbacks above.
    public void OnBrowserInfoReceived(string browserInfoJson)
    {
    }

    public bool IsProfessionalURL()
    {
        return _deploymentInfo != null && _deploymentInfo.isProfessionalURL;
    }

    public string GetCreatorUsername()
    {
        return _deploymentInfo?.creatorUsername ?? "";
    }

    public string GetLocalDisplayName()
    {
        return _currentUserInfo != null ? _currentUserInfo.displayName : "";
    }

    public string GetProjectName()
    {
        return _deploymentInfo?.projectName ?? "";
    }
}

#if UNITY_EDITOR
[CustomPropertyDrawer(typeof(ReadOnlyAttribute))]
public class ReadOnlyDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        GUI.enabled = false;
        EditorGUI.PropertyField(position, property, label, true);
        GUI.enabled = true;
    }
}
#endif

public class ReadOnlyAttribute : UnityEngine.PropertyAttribute { }