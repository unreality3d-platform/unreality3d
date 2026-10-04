using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using U3D;
using U3D.Editor;
using UnityEditor;
using UnityEngine;

public static class U3DAuthenticator
{
    private static string _idToken;
    private static string _refreshToken;
    private static string _userEmail;
    private static string _displayName;
    private static string _creatorUsername;
    private static string _userId;
    private static string _paypalEmail;
    private static bool _stayLoggedIn = true;
    private static HttpClient _sharedHttpClient;
    private static bool _networkConfigured = false;
    private static bool _credentialsLoaded = false;

    // Static credential keys
    private const string ID_TOKEN_KEY = "U3D_IdToken";
    private const string REFRESH_TOKEN_KEY = "U3D_RefreshToken";
    private const string USER_EMAIL_KEY = "U3D_UserEmail";
    private const string DISPLAY_NAME_KEY = "U3D_DisplayName";
    private const string CREATOR_USERNAME_KEY = "U3D_CreatorUsername";
    private const string USER_ID_KEY = "U3D_UserId";
    private const string PAYPAL_EMAIL_KEY = "U3D_PayPalEmail";
    private const string STAY_LOGGED_IN_KEY = "U3D_StayLoggedIn";

    public static bool IsLoggedIn => !string.IsNullOrEmpty(_idToken);
    public static string UserEmail => _userEmail;
    public static string DisplayName => _displayName;
    public static string UserId => _userId ?? "";
    public static string CreatorUsername => _creatorUsername;
    public static string PayPalEmail => _paypalEmail;

    private static readonly string[] RESERVED_PATHS = {
    "api", "docs", "admin", "support", "help", "about", "legal",
    "template", "download", "signup", "login", "dashboard", "creator-dashboard",
    "js", "css", "assets", "images", "favicon", "robots", "sitemap",
    "www", "mail", "ftp", "blog", "shop", "store", "app", "mobile",
    "auth", "oauth", "callback", "webhook", "health", "status"
};

    public static bool IsValidCreatorUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        // Existing validation rules
        if (username.Length < 3 || username.Length > 30)
            return false;

        // Alphanumeric and hyphens only
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-zA-Z0-9-]+$"))
            return false;

        // Can't start or end with hyphen
        if (username.StartsWith("-") || username.EndsWith("-"))
            return false;

        // Check against reserved paths (using Array.IndexOf for compatibility)
        if (System.Array.IndexOf(RESERVED_PATHS, username.ToLower()) >= 0)
            return false;

        return true;
    }

    public static class CurrentUser
    {
        public static string UserId => U3DAuthenticator.UserId;
        public static string Email => U3DAuthenticator.UserEmail;
        public static string DisplayName => U3DAuthenticator.DisplayName;
        public static string CreatorUsername => U3DAuthenticator.CreatorUsername;
        public static string PayPalEmail => U3DAuthenticator.PayPalEmail;
        public static string UserType => "creator";
    }

    public static string GetIdToken() => _idToken ?? "";

    public static bool StayLoggedIn
    {
        get => _stayLoggedIn;
        set
        {
            _stayLoggedIn = value;
            EditorPrefs.SetBool(STAY_LOGGED_IN_KEY, value);

            if (!value)
            {
                ClearCredentials();
            }
        }
    }

    // PayPal email management
    public static void SetPayPalEmail(string email)
    {
        _paypalEmail = email;
        SaveCredentials();
        SyncPayPalToScriptableObject(email);
    }

    public static string GetPayPalEmail()
    {
        return _paypalEmail ?? "";
    }

    public static bool HasPayPalEmail()
    {
        return !string.IsNullOrEmpty(_paypalEmail);
    }

    private static void SyncPayPalToScriptableObject(string email)
    {
        try
        {
            var assetPath = "Assets/U3D/Resources/U3DCreatorData.asset";

            // Ensure the Resources folder structure exists
            var resourcesPath = "Assets/U3D/Resources";
            if (!AssetDatabase.IsValidFolder(resourcesPath))
            {
                // Create the full path structure
                if (!AssetDatabase.IsValidFolder("Assets/U3D"))
                {
                    AssetDatabase.CreateFolder("Assets", "U3D");
                }
                AssetDatabase.CreateFolder("Assets/U3D", "Resources");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            var data = AssetDatabase.LoadAssetAtPath<U3DCreatorData>(assetPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<U3DCreatorData>();
                AssetDatabase.CreateAsset(data, assetPath);
            }

            data.PayPalEmail = email;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Verify the asset was created and saved
            var verifyData = AssetDatabase.LoadAssetAtPath<U3DCreatorData>(assetPath);
            if (verifyData == null || verifyData.PayPalEmail != email)
            {
                Debug.LogError($"Failed to save U3DCreatorData asset. Expected PayPal email: '{email}', Got: '{verifyData?.PayPalEmail ?? "null"}'");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not sync PayPal email to ScriptableObject: {ex.Message}");
        }
    }

    static U3DAuthenticator()
    {
        ConfigureNetworking();
        LoadCredentials();
        MigrateFromOldKeys();
    }

    private static void ConfigureNetworking()
    {
        if (_networkConfigured) return;

        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.DefaultConnectionLimit = 100;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.MaxServicePointIdleTime = 90000;
            ServicePointManager.DnsRefreshTimeout = 60000;

            var allEndpoints = new[]
            {
                "https://unreality3d.web.app",
                "https://unreality3d2025.web.app",
                "https://identitytoolkit.googleapis.com",
                "https://us-central1-unreality3d.cloudfunctions.net",
                "https://us-central1-unreality3d2025.cloudfunctions.net"
            };

            foreach (var endpoint in allEndpoints)
            {
                try
                {
                    var servicePoint = ServicePointManager.FindServicePoint(new Uri(endpoint));
                    servicePoint.ConnectionLeaseTimeout = 60000;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Could not set ConnectionLeaseTimeout for {endpoint}: {ex.Message}");
                }
            }

            var handler = new HttpClientHandler()
            {
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _sharedHttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };

            _sharedHttpClient.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK/1.0");
            _sharedHttpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");

            _networkConfigured = true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Network configuration failed: {ex.Message}");
        }
    }

    public static async Task<bool> TryAutoLogin()
    {
        if (!_credentialsLoaded)
        {
            LoadCredentials();
        }

        if (!_stayLoggedIn || string.IsNullOrEmpty(_idToken))
        {
            return false;
        }

        try
        {
            bool isValid = await ValidateStoredToken();

            if (isValid)
            {
                await LoadUserProfile();
                return true;
            }
            else
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Auto-login failed gracefully: {ex.Message}");
            ClearCredentials();
            return false;
        }
    }

    public static async Task<bool> LoginWithEmailPassword(string email, string password)
    {
        try
        {
            await EnsureFirebaseConfiguration();

            var loginData = new
            {
                email = email,
                password = password,
                returnSecureToken = true
            };

            var json = JsonConvert.SerializeObject(loginData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _sharedHttpClient.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={FirebaseConfigManager.CurrentConfig.apiKey}",
                content);

            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                if (result.ContainsKey("idToken"))
                {
                    _idToken = result["idToken"].ToString();
                    _refreshToken = result.ContainsKey("refreshToken") ? result["refreshToken"].ToString() : "";
                    _userEmail = email;

                    SaveCredentials();
                    await LoadUserProfile();

                    return true;
                }
            }
            else
            {
                var error = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                var errorMessage = "Login failed";

                if (error.ContainsKey("error"))
                {
                    var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(error["error"].ToString());
                    if (errorData.ContainsKey("message"))
                    {
                        var message = errorData["message"].ToString();
                        errorMessage = message switch
                        {
                            "INVALID_LOGIN_CREDENTIALS" => "Invalid email or password",
                            "USER_DISABLED" => "Account has been disabled",
                            "TOO_MANY_ATTEMPTS_TRY_LATER" => "Too many failed attempts. Please try again later.",
                            _ => $"Login failed: {message}"
                        };
                    }
                }

                throw new Exception(errorMessage);
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Login error: {ex.Message}");
            throw;
        }
    }

    public static async Task<bool> RegisterWithEmailPassword(string email, string password)
    {
        try
        {
            await EnsureFirebaseConfiguration();

            var registerData = new
            {
                email = email,
                password = password,
                returnSecureToken = true
            };

            var registerJson = JsonConvert.SerializeObject(registerData);
            var registerContent = new StringContent(registerJson, Encoding.UTF8, "application/json");

            var response = await _sharedHttpClient.PostAsync(
                $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseConfigManager.CurrentConfig.apiKey}",
                registerContent);

            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                if (result.ContainsKey("idToken"))
                {
                    _idToken = result["idToken"].ToString();
                    _refreshToken = result.ContainsKey("refreshToken") ? result["refreshToken"].ToString() : "";
                    _userEmail = email;

                    SaveCredentials();
                    await LoadUserProfile();

                    return true;
                }
            }
            else
            {
                var error = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                var errorMessage = "Registration failed";

                if (error.ContainsKey("error"))
                {
                    var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(error["error"].ToString());
                    if (errorData.ContainsKey("message"))
                    {
                        var message = errorData["message"].ToString();
                        errorMessage = message switch
                        {
                            "EMAIL_EXISTS" => "Account already exists",
                            "WEAK_PASSWORD" => "Password should be at least 6 characters",
                            "INVALID_EMAIL" => "Invalid email address",
                            _ => "Registration failed"
                        };
                    }
                }

                throw new Exception(errorMessage);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Registration error: {ex.Message}");
            throw;
        }

        return false;
    }

    public static async Task<bool> CheckUsernameAvailability(string username)
    {
        try
        {
            // First validate locally against reserved paths
            if (!IsValidCreatorUsername(username))
            {
                return false;
            }

            // Then check server availability
            var result = await CallFirebaseFunction("checkUsernameAvailability", new { username = username });
            return result.ContainsKey("available") && (bool)result["available"];
        }
        catch (Exception ex)
        {
            Debug.LogError($"Username check error: {ex.Message}");
            return false;
        }
    }

    public static async Task<string[]> GetUsernameSuggestions(string username)
    {
        try
        {
            var result = await CallFirebaseFunction("checkUsernameAvailability", new { username = username });
            if (result.ContainsKey("suggestions"))
            {
                var suggestions = JsonConvert.DeserializeObject<string[]>(result["suggestions"].ToString());
                return suggestions ?? new string[0];
            }
            return new string[0];
        }
        catch (Exception ex)
        {
            Debug.LogError($"Username suggestions error: {ex.Message}");
            return new string[0];
        }
    }

    // === FIX: Reservation now caches both the lookup form AND the display form
    // === from the server response, instead of the raw input typed by the user.
    // === This is the Unity-side half of Rei's 404 root cause — Unity had been
    // === caching "MakerComet" as _creatorUsername, but Firestore stored
    // === "makercomet". The server's new response contract (file 2) returns
    // === both forms; we cache both.
    public static async Task<bool> ReserveUsername(string username)
    {
        try
        {
            var result = await CallFirebaseFunction("reserveUsername", new { username = username });

            if (result.ContainsKey("success") && (bool)result["success"])
            {
                // Prefer server-normalized creatorUsername (lookup form).
                // Fall back to legacy "username" field for compatibility with
                // older server responses, and ultimately to the raw input as
                // a last resort.
                if (result.ContainsKey("creatorUsername") && result["creatorUsername"] != null)
                {
                    _creatorUsername = result["creatorUsername"].ToString();
                }
                else if (result.ContainsKey("username") && result["username"] != null)
                {
                    _creatorUsername = result["username"].ToString();
                }
                else
                {
                    _creatorUsername = username;
                }

                // Cache the server-returned displayName (trimmed raw input).
                // Falls back to the raw input if the server didn't return it.
                if (result.ContainsKey("displayName") && result["displayName"] != null)
                {
                    _displayName = result["displayName"].ToString();
                }
                else
                {
                    _displayName = username?.Trim() ?? "";
                }

                SaveCredentials();
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Username reservation error: {ex.Message}");
            throw;
        }
    }

    public static async Task ForceProfileReload()
    {
        if (string.IsNullOrEmpty(_idToken))
        {
            Debug.LogWarning("Cannot reload profile - no authentication token");
            return;
        }

        try
        {
            await LoadUserProfile();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Force profile reload failed: {ex.Message}");
        }
    }

    public static void Logout()
    {
        var currentConfig = FirebaseConfigManager.CurrentConfig;

        ClearCredentials();

        if (_sharedHttpClient != null)
        {
            _sharedHttpClient.DefaultRequestHeaders.Remove("Authorization");
        }

        if (currentConfig != null && !string.IsNullOrEmpty(currentConfig.apiKey))
        {
            FirebaseConfigManager.SetEnvironmentConfig("production", currentConfig);
        }
    }

    private static async Task<Dictionary<string, object>> CallFirebaseFunction(string functionName, object data)
    {
        await EnsureFirebaseConfiguration();

        const int maxRetries = 3;
        const int baseDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var requestData = new { data = data };
                var json = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _sharedHttpClient.DefaultRequestHeaders.Remove("Authorization");

                if (!string.IsNullOrEmpty(_idToken))
                {
                    _sharedHttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_idToken}");
                }

                var functionEndpoint = FirebaseConfigManager.CurrentConfig.GetFunctionEndpoint(functionName);
                var response = await _sharedHttpClient.PostAsync(functionEndpoint, content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                    return result.ContainsKey("result") ?
                        JsonConvert.DeserializeObject<Dictionary<string, object>>(result["result"].ToString()) :
                        result;
                }
                else
                {
                    var error = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                    var errorMessage = "Function call failed";

                    if (error.ContainsKey("error"))
                    {
                        var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(error["error"].ToString());
                        if (errorData.ContainsKey("message"))
                        {
                            errorMessage = errorData["message"].ToString();
                        }
                    }

                    throw new Exception(errorMessage);
                }
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("An error occurred while sending the request") && attempt < maxRetries)
            {
                RecreateHttpClient();
                var delay = (int)(baseDelayMs * Math.Pow(2, attempt - 1));
                await Task.Delay(delay);
                continue;
            }
            catch (Exception ex) when (attempt < maxRetries && IsRetriableNetworkError(ex))
            {
                var delay = (int)(baseDelayMs * Math.Pow(2, attempt - 1));
                await Task.Delay(delay);
                continue;
            }
        }

        throw new Exception($"Failed to call {functionName} after {maxRetries} attempts");
    }

    private static void RecreateHttpClient()
    {
        try
        {
            _sharedHttpClient?.Dispose();

            var handler = new HttpClientHandler()
            {
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            _sharedHttpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };

            _sharedHttpClient.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK/1.0");
            _sharedHttpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to recreate HttpClient: {ex.Message}");
        }
    }

    public static void Cleanup()
    {
        _sharedHttpClient?.Dispose();
        _sharedHttpClient = null;
        _networkConfigured = false;
    }

    private static bool IsRetriableNetworkError(Exception ex)
    {
        var message = ex.Message?.ToLower() ?? "";
        return message.Contains("connection") ||
               message.Contains("timeout") ||
               message.Contains("network") ||
               message.Contains("dns") ||
               message.Contains("socket") ||
               message.Contains("established connection was aborted") ||
               message.Contains("unable to read data from the transport connection");
    }

    private static async Task EnsureFirebaseConfiguration()
    {
        if (FirebaseConfigManager.IsConfigurationComplete() &&
            FirebaseConfigManager.CurrentConfig.apiKey != "setup-required")
        {
            return;
        }

        await EstablishDynamicConfiguration();

        if (!FirebaseConfigManager.IsConfigurationComplete())
        {
            throw new Exception("Failed to establish Firebase configuration.");
        }
    }

    private static async Task EstablishDynamicConfiguration()
    {
        try
        {
            var configUrl = "https://unreality3d.web.app/api/config";
            var response = await _sharedHttpClient.GetAsync(configUrl);

            if (response.IsSuccessStatusCode)
            {
                var configJson = await response.Content.ReadAsStringAsync();
                var firebaseConfig = JsonConvert.DeserializeObject<Dictionary<string, object>>(configJson);

                var productionConfig = new FirebaseEnvironmentConfig
                {
                    apiKey = firebaseConfig.ContainsKey("apiKey") ? firebaseConfig["apiKey"].ToString() : "",
                    authDomain = firebaseConfig.ContainsKey("authDomain") ? firebaseConfig["authDomain"].ToString() : "",
                    projectId = firebaseConfig.ContainsKey("projectId") ? firebaseConfig["projectId"].ToString() : "",
                    storageBucket = firebaseConfig.ContainsKey("storageBucket") ? firebaseConfig["storageBucket"].ToString() : "",
                    messagingSenderId = firebaseConfig.ContainsKey("messagingSenderId") ? firebaseConfig["messagingSenderId"].ToString() : "",
                    appId = firebaseConfig.ContainsKey("appId") ? firebaseConfig["appId"].ToString() : "",
                    measurementId = firebaseConfig.ContainsKey("measurementId") ? firebaseConfig["measurementId"].ToString() : ""
                };

                var developmentConfig = new FirebaseEnvironmentConfig
                {
                    apiKey = "setup-required",
                    authDomain = "unreality3d2025.firebaseapp.com",
                    projectId = "unreality3d2025",
                    storageBucket = "unreality3d2025.firebasestorage.app",
                    messagingSenderId = "244081840635",
                    appId = "1:244081840635:web:71c37efb6b172a706dbb5e",
                    measurementId = "G-YXC3XB3PFL"
                };

                FirebaseConfigManager.SetEnvironmentConfig("production", productionConfig);
                FirebaseConfigManager.SetEnvironmentConfig("development", developmentConfig);
            }
            else
            {
                Debug.LogError($"Failed to fetch secure configuration: {response.StatusCode}");
                throw new Exception("Unable to establish Firebase configuration securely");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Secure configuration establishment failed: {ex.Message}");
            throw;
        }
    }

    private static async Task<bool> ValidateStoredToken()
    {
        try
        {
            if (string.IsNullOrEmpty(_idToken))
            {
                return false;
            }

            try
            {
                var result = await CallFirebaseFunction("getUserProfile", new { });

                if (result.ContainsKey("userId"))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                var message = ex.Message?.ToLower() ?? "";

                if (message.Contains("unauthenticated") || message.Contains("unauthorized"))
                {
                    bool refreshed = await RefreshIdTokenIfNeeded();
                    if (refreshed)
                    {
                        try
                        {
                            var retryResult = await CallFirebaseFunction("getUserProfile", new { });
                            if (retryResult.ContainsKey("userId"))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // Silent failure on retry
                        }
                    }

                    ClearCredentials();
                    return false;
                }

                if (IsRetriableNetworkError(ex))
                {
                    return false;
                }

                ClearCredentials();
                return false;
            }

            return false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Token validation error: {ex.Message}");
            ClearCredentials();
            return false;
        }
    }

    /// <summary>
    /// Improved token refresh with better error handling and logging
    /// Replace the existing RefreshIdTokenIfNeeded method
    /// </summary>
    private static async Task<bool> RefreshIdTokenIfNeeded()
    {
        if (string.IsNullOrEmpty(_refreshToken))
        {
            Debug.LogWarning("No refresh token available");
            return false;
        }

        try
        {
            var formData = new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", _refreshToken }
            };

            var content = new FormUrlEncodedContent(formData);

            // CRITICAL: Remove any existing Authorization header before token refresh
            // The securetoken.googleapis.com endpoint rejects requests with Bearer tokens
            _sharedHttpClient.DefaultRequestHeaders.Remove("Authorization");

            var response = await _sharedHttpClient.PostAsync(
                $"https://securetoken.googleapis.com/v1/token?key={FirebaseConfigManager.CurrentConfig.apiKey}",
                content);

            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                if (result.ContainsKey("id_token"))
                {
                    var oldToken = _idToken?.Substring(0, Math.Min(20, _idToken.Length));
                    _idToken = result["id_token"].ToString();

                    if (result.ContainsKey("refresh_token"))
                    {
                        _refreshToken = result["refresh_token"].ToString();
                    }

                    SaveCredentials();

                    var newToken = _idToken?.Substring(0, Math.Min(20, _idToken.Length));

                    return true;
                }
            }
            else
            {
                Debug.LogError($"Token refresh failed: {response.StatusCode} - {responseText}");

                if (response.StatusCode == HttpStatusCode.BadRequest ||
                    response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Debug.LogError("❌ Refresh token is invalid or expired - user must log in again");
                    ClearCredentials();
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Token refresh error: {ex.Message}");
            return false;
        }

        return false;
    }

    /// <summary>
    /// NEW ADDITION: Smart token freshness checking
    /// Add this new private method to U3DAuthenticator
    /// </summary>
    private static async Task EnsureTokenFreshness()
    {
        if (string.IsNullOrEmpty(_idToken))
        {
            Debug.LogWarning("No ID token available for freshness check");
            return;
        }

        try
        {
            // Parse the JWT token to check expiry (without external dependencies)
            var tokenParts = _idToken.Split('.');
            if (tokenParts.Length != 3)
            {
                Debug.LogWarning("Invalid JWT token format");
                return;
            }

            // Decode the payload (simple base64 decode)
            var payload = tokenParts[1];
            // Add padding if needed for base64 decoding
            while (payload.Length % 4 != 0)
            {
                payload += "=";
            }

            var payloadBytes = Convert.FromBase64String(payload);
            var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);
            var payloadData = JsonConvert.DeserializeObject<Dictionary<string, object>>(payloadJson);

            if (payloadData.ContainsKey("exp"))
            {
                var expTime = long.Parse(payloadData["exp"].ToString());
                var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var timeRemaining = expTime - currentTime;

                // Refresh if less than 10 minutes remaining (600 seconds)
                if (timeRemaining < 600)
                {
                    Debug.Log($"🔄 U3D: Token expires in {timeRemaining} seconds, refreshing proactively...");
                    bool refreshed = await RefreshIdTokenIfNeeded();

                    if (refreshed)
                    {
                        Debug.Log("✅ U3D: Token refreshed proactively");
                    }
                    else
                    {
                        Debug.LogWarning("⚠️ U3D: Proactive token refresh failed");
                    }
                }
                else
                {
                    Debug.Log($"🕐 U3D: Token valid for {timeRemaining / 60} more minutes");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Token freshness check failed: {ex.Message}");
            // Continue without refresh - not critical if this fails
        }
    }


    /// <summary>
    /// Ensures authentication is valid before starting deployment operations
    /// Now includes proactive token refresh and better error handling
    /// </summary>
    public static async Task<bool> PrepareForDeployment()
    {
        try
        {
            // First check if we're even logged in
            if (string.IsNullOrEmpty(_idToken))
            {
                Debug.LogError("❌ No authentication token available. Please log in first.");
                return false;
            }

            // ENHANCEMENT: Always refresh token before long operations
            // This ensures we start with maximum time (1 hour) available
            bool refreshed = await RefreshIdTokenIfNeeded();

            if (!refreshed && !string.IsNullOrEmpty(_refreshToken))
            {
                Debug.LogWarning("⚠️ U3D: Token refresh failed, but will continue with existing token");
                // Continue anyway - might still be valid
            }
            else if (refreshed)
            {
                Debug.Log("✅ U3D: Token refreshed successfully - maximum time available");
            }

            // Validate the token (refreshed or existing)
            bool isValid = await ValidateStoredToken();

            if (isValid)
            {
                Debug.Log("✅ U3D: Authentication ready for deployment");
                return true;
            }

            // If validation fails, we have a more serious auth issue
            Debug.LogError("❌ U3D: Authentication validation failed after refresh attempt");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ U3D: Deployment authentication preparation failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Deployment operations with automatic authentication retry and proactive refresh
    /// Replaces CallFirebaseFunctionWithAuthRetry with better token management
    /// </summary>
    public static async Task<Dictionary<string, object>> CallFirebaseFunctionWithAuthRetry(string functionName, object data)
    {
        const int maxRetries = 3;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                // ENHANCEMENT: Check if token might be close to expiry before each call
                await EnsureTokenFreshness();

                // Call the existing private method using reflection
                var method = typeof(U3DAuthenticator).GetMethod("CallFirebaseFunction",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

                if (method == null)
                {
                    throw new Exception("CallFirebaseFunction method not found");
                }

                var task = method.Invoke(null, new object[] { functionName, data }) as Task<Dictionary<string, object>>;
                var result = await task;

                if (attempt > 1)
                {
                    Debug.Log($"✅ U3D: {functionName} succeeded after {attempt} attempts");
                }

                return result;
            }
            catch (Exception ex)
            {
                var message = ex.Message?.ToLower() ?? "";
                bool isAuthError = message.Contains("unauthenticated") ||
                                  message.Contains("unauthorized") ||
                                  message.Contains("invalid token") ||
                                  message.Contains("token expired");

                if (isAuthError && attempt < maxRetries)
                {
                    Debug.LogWarning($"⚠️ U3D: Authentication error on {functionName} (attempt {attempt}), refreshing and retrying...");

                    // Force refresh token for next attempt
                    bool refreshed = await RefreshIdTokenIfNeeded();
                    if (!refreshed)
                    {
                        Debug.LogError("❌ U3D: Token refresh failed during retry");
                        throw new Exception("Authentication refresh failed during operation");
                    }

                    continue;
                }

                // Non-auth error or final attempt - rethrow
                if (attempt == maxRetries)
                {
                    Debug.LogError($"❌ U3D: {functionName} failed after {maxRetries} attempts: {ex.Message}");
                }
                throw;
            }
        }

        throw new Exception($"Failed to call {functionName} after {maxRetries} attempts");
    }

    private static async Task LoadUserProfile()
    {
        try
        {
            var result = await CallFirebaseFunctionWithAuthRetry("getUserProfile", new { });

            _userId = result.ContainsKey("userId") && result["userId"] != null
                ? result["userId"].ToString()
                : _userId;

            _creatorUsername = result.ContainsKey("creatorUsername") && result["creatorUsername"] != null
                ? result["creatorUsername"].ToString()
                : "";

            _userEmail = result.ContainsKey("email") && result["email"] != null
                ? result["email"].ToString()
                : _userEmail;

            _displayName = result.ContainsKey("displayName") && result["displayName"] != null
                ? result["displayName"].ToString()
                : _displayName;

            SaveCredentials();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Profile load failed: {ex.Message}");
        }
    }

    public static async Task<bool> VerifyUsernameExists()
    {
        if (string.IsNullOrEmpty(_idToken))
            return false;

        try
        {
            var result = await CallFirebaseFunction("getUserProfile", new { });

            bool hasUsername = result.ContainsKey("creatorUsername") &&
                              !string.IsNullOrEmpty(result["creatorUsername"]?.ToString());

            return hasUsername;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Username verification failed: {ex.Message}");
            return false;
        }
    }

    private static void SaveCredentials()
    {
        if (_stayLoggedIn)
        {
            if (!string.IsNullOrEmpty(_idToken))
                EditorPrefs.SetString(ID_TOKEN_KEY, _idToken);
            if (!string.IsNullOrEmpty(_refreshToken))
                EditorPrefs.SetString(REFRESH_TOKEN_KEY, _refreshToken);
            if (!string.IsNullOrEmpty(_userEmail))
                EditorPrefs.SetString(USER_EMAIL_KEY, _userEmail);
            if (!string.IsNullOrEmpty(_displayName))
                EditorPrefs.SetString(DISPLAY_NAME_KEY, _displayName);

            EditorPrefs.SetString(USER_ID_KEY, _userId ?? "");
            EditorPrefs.SetString(CREATOR_USERNAME_KEY, _creatorUsername ?? "");
            EditorPrefs.SetString(PAYPAL_EMAIL_KEY, _paypalEmail ?? "");
        }
        EditorPrefs.SetBool(STAY_LOGGED_IN_KEY, _stayLoggedIn);
    }

    private static void LoadCredentials()
    {
        if (_credentialsLoaded) return;

        _idToken = EditorPrefs.GetString(ID_TOKEN_KEY, "");
        _refreshToken = EditorPrefs.GetString(REFRESH_TOKEN_KEY, "");
        _userEmail = EditorPrefs.GetString(USER_EMAIL_KEY, "");
        _displayName = EditorPrefs.GetString(DISPLAY_NAME_KEY, "");
        _userId = EditorPrefs.GetString(USER_ID_KEY, "");
        _creatorUsername = EditorPrefs.GetString(CREATOR_USERNAME_KEY, "");
        _paypalEmail = EditorPrefs.GetString(PAYPAL_EMAIL_KEY, "");
        _stayLoggedIn = EditorPrefs.GetBool(STAY_LOGGED_IN_KEY, true);
        _credentialsLoaded = true;
    }

    private static void MigrateFromOldKeys()
    {
        var possiblePrefixes = new[]
        {
            $"U3D_{PlayerSettings.companyName}.{PlayerSettings.productName}_",
            $"U3D_Creator_{PlayerSettings.companyName}_"
        };

        foreach (var oldPrefix in possiblePrefixes)
        {
            if (EditorPrefs.HasKey(oldPrefix + "idToken") && string.IsNullOrEmpty(_idToken))
            {
                _idToken = EditorPrefs.GetString(oldPrefix + "idToken", "");
                _refreshToken = EditorPrefs.GetString(oldPrefix + "refreshToken", "");
                _userEmail = EditorPrefs.GetString(oldPrefix + "userEmail", "");
                _displayName = EditorPrefs.GetString(oldPrefix + "displayName", "");
                _creatorUsername = EditorPrefs.GetString(oldPrefix + "creatorUsername", "");
                _stayLoggedIn = EditorPrefs.GetBool(oldPrefix + "stayLoggedIn", true);

                SaveCredentials();

                EditorPrefs.DeleteKey(oldPrefix + "idToken");
                EditorPrefs.DeleteKey(oldPrefix + "refreshToken");
                EditorPrefs.DeleteKey(oldPrefix + "userEmail");
                EditorPrefs.DeleteKey(oldPrefix + "displayName");
                EditorPrefs.DeleteKey(oldPrefix + "creatorUsername");
                EditorPrefs.DeleteKey(oldPrefix + "paypalConnected");

                break;
            }
        }

        MigratePayPalEmailKeys();
    }

    private static void MigratePayPalEmailKeys()
    {
        if (!string.IsNullOrEmpty(_paypalEmail))
            return;

        var possibleKeys = new[]
        {
            "U3D_PayPalEmail",
            $"U3D_PayPalEmail_{_userEmail}",
            EditorPrefs.HasKey("U3D_UserEmail") ? $"U3D_PayPalEmail_{EditorPrefs.GetString("U3D_UserEmail", "")}" : "",
            $"U3D_PayPalEmail_{PlayerSettings.companyName}_{PlayerSettings.productName}"
        };

        foreach (var key in possibleKeys)
        {
            if (!string.IsNullOrEmpty(key) && EditorPrefs.HasKey(key))
            {
                string paypalEmail = EditorPrefs.GetString(key, "");
                if (!string.IsNullOrEmpty(paypalEmail))
                {
                    _paypalEmail = paypalEmail;
                    SaveCredentials();

                    if (key != PAYPAL_EMAIL_KEY)
                    {
                        EditorPrefs.DeleteKey(key);
                    }
                    break;
                }
            }
        }
    }

    private static void ClearCredentials()
    {
        EditorPrefs.DeleteKey(ID_TOKEN_KEY);
        EditorPrefs.DeleteKey(REFRESH_TOKEN_KEY);
        EditorPrefs.DeleteKey(USER_EMAIL_KEY);
        EditorPrefs.DeleteKey(DISPLAY_NAME_KEY);
        EditorPrefs.DeleteKey(USER_ID_KEY);
        EditorPrefs.DeleteKey(CREATOR_USERNAME_KEY);
        EditorPrefs.DeleteKey(PAYPAL_EMAIL_KEY);

        _idToken = "";
        _refreshToken = "";
        _userEmail = "";
        _displayName = "";
        _userId = "";
        _creatorUsername = "";
        _paypalEmail = "";
    }
}

[System.Serializable]
public class U3DConfiguration
{
    public string productionApiKey;
    public string productionAuthDomain;
    public string productionProjectId;
    public string productionStorageBucket;
    public string productionMessagingSenderId;
    public string productionAppId;

    public string developmentApiKey;
    public string developmentAuthDomain;
    public string developmentProjectId;
    public string developmentStorageBucket;
    public string developmentMessagingSenderId;
    public string developmentAppId;
}