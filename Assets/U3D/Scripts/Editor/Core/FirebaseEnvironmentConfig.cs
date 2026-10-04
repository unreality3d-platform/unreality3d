using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace U3D.Editor
{
    [Serializable]
    public class FirebaseEnvironmentConfig
    {
        public string apiKey;
        public string authDomain;
        public string projectId;
        public string storageBucket;
        public string messagingSenderId;
        public string appId;
        public string measurementId;

        public string GetAuthEndpoint(string action)
        {
            return $"https://identitytoolkit.googleapis.com/v1/accounts:{action}?key={apiKey}";
        }

        public string GetFunctionEndpoint(string functionName)
        {
            return $"https://us-central1-{projectId}.cloudfunctions.net/{functionName}";
        }
    }

    public static class FirebaseConfigManager
    {
        private static FirebaseEnvironmentConfig _currentConfig;
        private static bool _initialized = false;

        // Default configurations (loaded at runtime, not exposed in source)
        private static readonly Dictionary<string, FirebaseEnvironmentConfig> _configs =
            new Dictionary<string, FirebaseEnvironmentConfig>();

        public static FirebaseEnvironmentConfig CurrentConfig
        {
            get
            {
                if (!_initialized)
                {
                    Initialize();
                }
                return _currentConfig;
            }
        }

        static void Initialize()
        {
            if (_initialized) return;

            // Load configuration based on environment
            LoadConfigurations();

            // Determine which environment to use
            string environment = DetermineEnvironment();

            if (_configs.ContainsKey(environment))
            {
                _currentConfig = _configs[environment];
            }
            else
            {
                Debug.LogError($"Firebase configuration not found for environment: {environment}");
            }

            _initialized = true;
        }

        static void LoadConfigurations()
        {
            // SECURITY: Load from secure EditorPrefs (local Unity storage)
            // These are set by the setup process, not stored in source code

            // Production configuration - use PRODUCTION (not PROD) to match SetEnvironmentConfig
            var prodConfig = LoadConfigFromPrefs("PRODUCTION");
            if (prodConfig != null)
            {
                _configs["production"] = prodConfig;
            }

            // Development configuration - use DEVELOPMENT (not DEV) to match SetEnvironmentConfig
            var devConfig = LoadConfigFromPrefs("DEVELOPMENT");
            if (devConfig != null)
            {
                _configs["development"] = devConfig;
            }

            // If configurations are missing, prompt for setup
            if (_configs.Count == 0)
            {
                Debug.LogWarning("Firebase backend connection unavailable. Attempting dynamic configuration...");
                // Return minimal safe config that will prompt for setup
                _configs["setup_required"] = new FirebaseEnvironmentConfig
                {
                    projectId = "setup-required",
                    apiKey = "setup-required"
                };
            }
        }

        static FirebaseEnvironmentConfig LoadConfigFromPrefs(string environment)
        {
            string prefix = $"U3D_Firebase_{environment}_";

            if (!EditorPrefs.HasKey(prefix + "ApiKey"))
            {
                return null; // Configuration not set
            }

            return new FirebaseEnvironmentConfig
            {
                apiKey = EditorPrefs.GetString(prefix + "ApiKey", ""),
                authDomain = EditorPrefs.GetString(prefix + "AuthDomain", ""),
                projectId = EditorPrefs.GetString(prefix + "ProjectId", ""),
                storageBucket = EditorPrefs.GetString(prefix + "StorageBucket", ""),
                messagingSenderId = EditorPrefs.GetString(prefix + "MessagingSenderId", ""),
                appId = EditorPrefs.GetString(prefix + "AppId", ""),
                measurementId = EditorPrefs.GetString(prefix + "MeasurementId", "")
            };
        }

        static string DetermineEnvironment()
        {
            // Environment detection logic
            string manualEnvironment = EditorPrefs.GetString("U3D_Environment", "auto");
            if (manualEnvironment != "auto")
            {
                return manualEnvironment;
            }

            // Creators always use production environment
            // This prevents the chicken-and-egg problem where login fails because
            // we use development environment before the user is logged in
            return "production";
        }

        // SECURITY: Method to safely set configuration (called by setup process)
        public static void SetEnvironmentConfig(string environment, FirebaseEnvironmentConfig config)
        {
            string prefix = $"U3D_Firebase_{environment.ToUpper()}_";

            EditorPrefs.SetString(prefix + "ApiKey", config.apiKey);
            EditorPrefs.SetString(prefix + "AuthDomain", config.authDomain);
            EditorPrefs.SetString(prefix + "ProjectId", config.projectId);
            EditorPrefs.SetString(prefix + "StorageBucket", config.storageBucket);
            EditorPrefs.SetString(prefix + "MessagingSenderId", config.messagingSenderId);
            EditorPrefs.SetString(prefix + "AppId", config.appId);
            EditorPrefs.SetString(prefix + "MeasurementId", config.measurementId);

            // Reinitialize to pick up new config
            _initialized = false;
            Initialize();
        }

        // SECURITY: Method to clear configuration (for security/logout)
        public static void ClearEnvironmentConfig(string environment)
        {
            string prefix = $"U3D_Firebase_{environment.ToUpper()}_";

            EditorPrefs.DeleteKey(prefix + "ApiKey");
            EditorPrefs.DeleteKey(prefix + "AuthDomain");
            EditorPrefs.DeleteKey(prefix + "ProjectId");
            EditorPrefs.DeleteKey(prefix + "StorageBucket");
            EditorPrefs.DeleteKey(prefix + "MessagingSenderId");
            EditorPrefs.DeleteKey(prefix + "AppId");
            EditorPrefs.DeleteKey(prefix + "MeasurementId");

            if (_configs.ContainsKey(environment))
            {
                _configs.Remove(environment);
            }
        }

        // Helper method to check if configuration is complete
        public static bool IsConfigurationComplete()
        {
            return _currentConfig != null &&
                   !string.IsNullOrEmpty(_currentConfig.apiKey) &&
                   _currentConfig.apiKey != "setup-required";
        }

        // Get available environments
        public static string[] GetAvailableEnvironments()
        {
            if (!_initialized) Initialize();

            var environments = new List<string>(_configs.Keys);
            environments.Remove("setup_required"); // Don't show setup placeholder
            return environments.ToArray();
        }
    }
}