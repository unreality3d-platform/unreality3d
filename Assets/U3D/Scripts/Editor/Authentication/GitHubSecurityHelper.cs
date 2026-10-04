using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

namespace U3D.Editor
{
    /// <summary>
    /// GitHub secret encryption using Firebase Cloud Function
    /// Integrates with your existing Firebase infrastructure
    /// </summary>
    public static class GitHubSecurityHelper
    {
        /// <summary>
        /// Encrypts a secret value using your Firebase Cloud Function
        /// Uses the same project infrastructure you already have set up
        /// </summary>
        public static async Task<string> EncryptForGitHubSecret(string secretValue, string base64PublicKey)
        {
            try
            {
                // Use your existing Firebase project's Cloud Function endpoint
                var functionUrl = FirebaseConfigManager.CurrentConfig.GetFunctionEndpoint("encryptGitHubSecret");

                var requestData = new EncryptionRequest
                {
                    secret = secretValue,
                    publicKey = base64PublicKey
                };

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var json = JsonConvert.SerializeObject(requestData);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    Debug.Log($"Encrypting secret using Firebase Cloud Function: {functionUrl}");

                    var response = await client.PostAsync(functionUrl, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<EncryptionResponse>(responseText);

                        if (result.success && !string.IsNullOrEmpty(result.encryptedValue))
                        {
                            Debug.Log("Secret encrypted successfully using Firebase libsodium function");
                            return result.encryptedValue;
                        }
                        else
                        {
                            throw new Exception($"Firebase encryption function returned invalid response: {responseText}");
                        }
                    }
                    else
                    {
                        try
                        {
                            var errorResult = JsonConvert.DeserializeObject<EncryptionError>(responseText);
                            throw new Exception($"Firebase encryption error: {errorResult.error} - {errorResult.details}");
                        }
                        catch (JsonException)
                        {
                            throw new Exception($"Firebase function error: {response.StatusCode} - {responseText}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub secret encryption failed: {ex.Message}");
                throw new Exception($"Failed to encrypt secret for GitHub: {ex.Message}");
            }
        }
    }

    [System.Serializable]
    public class EncryptionRequest
    {
        public string secret { get; set; }
        public string publicKey { get; set; }
    }

    [System.Serializable]
    public class EncryptionResponse
    {
        public string encryptedValue { get; set; }
        public bool success { get; set; }
    }

    [System.Serializable]
    public class EncryptionError
    {
        public string error { get; set; }
        public string details { get; set; }
    }
}