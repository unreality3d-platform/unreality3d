using UnityEngine;
using UnityEditor;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;

namespace U3D.Editor
{
    public static class GitHubTokenManager
    {
        private static string _githubToken;
        private static string _githubUsername;
        private static bool _tokenValidated = false;

        private const string TOKEN_KEY = "U3D_GitHubToken";
        private const string USERNAME_KEY = "U3D_GitHubUsername";
        private const string VALIDATED_KEY = "U3D_GitHubTokenValidated";

        public static bool HasValidToken => !string.IsNullOrEmpty(_githubToken) && _tokenValidated;
        public static string GitHubUsername => _githubUsername;
        public static string Token => _githubToken;

        private static bool ShouldSkipDuringBuild()
        {
            return BuildPipeline.isBuildingPlayer ||
                   EditorApplication.isCompiling ||
                   EditorApplication.isUpdating;
        }

        static GitHubTokenManager()
        {
            if (ShouldSkipDuringBuild())
                return;

            LoadCredentials();
        }

        public static void SetToken(string token)
        {
            _githubToken = token;
            _tokenValidated = false;
            SaveCredentials();
        }

        public static void RefreshFromEditorPrefs()
        {
            LoadCredentials();
        }

        public static async Task<GitHubValidationResult> ValidateAndSetToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return new GitHubValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Token cannot be empty"
                };
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"token {token}");
                    client.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK");
                    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                    var response = await client.GetAsync("https://api.github.com/user");
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var userData = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                        if (userData.ContainsKey("login"))
                        {
                            _githubToken = token;
                            _githubUsername = userData["login"].ToString();
                            _tokenValidated = true;
                            SaveCredentials();

                            return new GitHubValidationResult
                            {
                                IsValid = true,
                                Username = _githubUsername,
                                Message = $"Token validated for user: {_githubUsername}"
                            };
                        }
                    }
                    else
                    {
                        var errorMessage = "Invalid token";

                        try
                        {
                            var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                            if (errorData.ContainsKey("message"))
                            {
                                errorMessage = errorData["message"].ToString();
                            }
                        }
                        catch { }

                        return new GitHubValidationResult
                        {
                            IsValid = false,
                            ErrorMessage = errorMessage
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub token validation error: {ex.Message}");
                return new GitHubValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Validation failed: {ex.Message}"
                };
            }

            return new GitHubValidationResult
            {
                IsValid = false,
                ErrorMessage = "Unexpected validation result"
            };
        }

        public static async Task<bool> CheckRepositoryPermissions()
        {
            if (!HasValidToken)
                return false;

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("Authorization", $"token {_githubToken}");
                    client.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK");
                    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

                    var response = await client.GetAsync("https://api.github.com/user/repos?per_page=1");
                    return response.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub permissions check error: {ex.Message}");
                return false;
            }
        }

        public static void ClearToken()
        {
            _githubToken = "";
            _githubUsername = "";
            _tokenValidated = false;

            if (!ShouldSkipDuringBuild())
            {
                EditorPrefs.DeleteKey(TOKEN_KEY);
                EditorPrefs.DeleteKey(USERNAME_KEY);
                EditorPrefs.DeleteKey(VALIDATED_KEY);
            }
        }

        private static void SaveCredentials()
        {
            if (ShouldSkipDuringBuild())
                return;

            if (!string.IsNullOrEmpty(_githubToken))
                EditorPrefs.SetString(TOKEN_KEY, _githubToken);

            if (!string.IsNullOrEmpty(_githubUsername))
                EditorPrefs.SetString(USERNAME_KEY, _githubUsername);

            EditorPrefs.SetBool(VALIDATED_KEY, _tokenValidated);
        }

        private static void LoadCredentials()
        {
            if (ShouldSkipDuringBuild())
            {
                _githubToken = "";
                _githubUsername = "";
                _tokenValidated = false;
                return;
            }

            _githubToken = EditorPrefs.GetString(TOKEN_KEY, "");
            _githubUsername = EditorPrefs.GetString(USERNAME_KEY, "");
            _tokenValidated = EditorPrefs.GetBool(VALIDATED_KEY, false);
        }
    }

    [System.Serializable]
    public class GitHubValidationResult
    {
        public bool IsValid { get; set; }
        public string Username { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
    }
}