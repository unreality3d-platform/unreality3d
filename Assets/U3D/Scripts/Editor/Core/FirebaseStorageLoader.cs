using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using U3D.Editor;
using UnityEngine;

public class FirebaseStorageUploader
{
    private readonly string _storageBucket;
    private readonly string _idToken;
    private readonly HttpClient _httpClient;

    public FirebaseStorageUploader(string storageBucket, string idToken)
    {
        _storageBucket = storageBucket;
        _idToken = idToken;
        _httpClient = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
    }
    public class MarketUploadResult
    {
        public bool Success { get; set; }
        public string StoragePath { get; set; }
        public string ErrorMessage { get; set; }
    }

    public async Task<MarketUploadResult> UploadMarketPackage(string localFilePath, string uid, string assetId)
    {
        try
        {
            if (string.IsNullOrEmpty(uid))
                return new MarketUploadResult { Success = false, ErrorMessage = "Missing user id. Log in again before uploading." };
            if (string.IsNullOrEmpty(assetId))
                return new MarketUploadResult { Success = false, ErrorMessage = "Missing asset id." };
            if (!File.Exists(localFilePath))
                return new MarketUploadResult { Success = false, ErrorMessage = $"Package file not found: {localFilePath}" };

            var fileBytes = await File.ReadAllBytesAsync(localFilePath);

            var storagePath = $"market/{uid}/{assetId}/package.unitypackage";
            var storageUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o?name={storagePath}";

            // Dedicated long-timeout client: a 1 GiB single-request upload on a slow
            // connection can run ~30 minutes, well past the shared client's limit.
            using var marketClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            using var content = new ByteArrayContent(fileBytes);
            content.Headers.Add("Content-Type", "application/octet-stream");

            using var request = new HttpRequestMessage(HttpMethod.Post, storageUrl);
            request.Content = content;
            request.Headers.Add("Authorization", $"Bearer {_idToken}");

            var response = await marketClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                return new MarketUploadResult { Success = true, StoragePath = storagePath };
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new MarketUploadResult
            {
                Success = false,
                ErrorMessage = $"Upload failed ({(int)response.StatusCode}): {errorText}"
            };
        }
        catch (TaskCanceledException)
        {
            return new MarketUploadResult { Success = false, ErrorMessage = "Upload timed out. Check your connection and retry." };
        }
        catch (Exception ex)
        {
            return new MarketUploadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<bool> UploadBuildToStorage(string buildPath, string creatorUsername, string projectName)
    {
        try
        {
            var buildFiles = CollectBuildFiles(buildPath);

            const int maxConcurrentUploads = 3;
            var semaphore = new SemaphoreSlim(maxConcurrentUploads, maxConcurrentUploads);
            var uploadTasks = new List<Task<bool>>();

            foreach (var file in buildFiles)
            {
                var uploadTask = UploadFileWithThrottling(file, creatorUsername, projectName, semaphore);
                uploadTasks.Add(uploadTask);
            }

            var results = await Task.WhenAll(uploadTasks);

            var failedCount = results.Count(r => !r);
            if (failedCount > 0)
            {
                Debug.LogError($"❌ {failedCount} out of {buildFiles.Count} file uploads failed");
                return false;
            }

            return await TriggerGitHubDeployment(creatorUsername, projectName, buildFiles);
        }
        catch (Exception ex)
        {
            Debug.LogError($"🚨 Firebase Storage upload failed: {ex.Message}");
            return false;
        }
    }

    // === Added productDisplayName parameter so PublishTab can send
    // === PlayerSettings.productName (the raw, case-preserving typed value) to
    // === the server, which stores it as creator_projects.productDisplayName
    // === and uses it in the Unity loading-screen title and generated README.
    public async Task<DeploymentResult> UploadBuildToStorageWithIntent(string buildPath, string creatorUsername, string baseProjectName, string deploymentIntent, string productDisplayName)
    {
        try
        {
            await ClearExistingBuildFolder(creatorUsername, baseProjectName);

            var buildFiles = CollectBuildFiles(buildPath);

            const int maxConcurrentUploads = 3;
            var semaphore = new SemaphoreSlim(maxConcurrentUploads, maxConcurrentUploads);
            var uploadTasks = new List<Task<bool>>();

            foreach (var file in buildFiles)
            {
                var uploadTask = UploadFileWithThrottling(file, creatorUsername, baseProjectName, semaphore);
                uploadTasks.Add(uploadTask);
            }

            var results = await Task.WhenAll(uploadTasks);
            var failedCount = results.Count(r => !r);

            if (failedCount > 0)
            {
                Debug.LogError($"❌ {failedCount} out of {buildFiles.Count} file uploads failed");
                return new DeploymentResult { Success = false, ErrorMessage = "File upload failed" };
            }

            return await TriggerGitHubDeploymentWithIntent(creatorUsername, baseProjectName, deploymentIntent, buildFiles, productDisplayName);
        }
        catch (Exception ex)
        {
            Debug.LogError($"🚨 Firebase Storage upload failed: {ex.Message}");
            return new DeploymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    // === Added productDisplayName parameter and included it in the
    // === deployment request payload. Server-side deployFromStorage (file 5)
    // === destructures this field and threads it through the deploy chain.
    private async Task<DeploymentResult> TriggerGitHubDeploymentWithIntent(string creatorUsername, string baseProjectName, string deploymentIntent, List<BuildFileInfo> files, string productDisplayName)
    {
        try
        {
            var fileList = files.ConvertAll(f => f.StoragePath);
            var paypalEmail = U3DAuthenticator.GetPayPalEmail();

            var deploymentRequest = new Dictionary<string, object>
        {
            { "project", baseProjectName },
            { "creatorUsername", creatorUsername },
            { "githubOwner", GitHubTokenManager.GitHubUsername },
            { "fileList", fileList },
            { "githubToken", GitHubTokenManager.Token },
            { "deploymentIntent", deploymentIntent },
            { "creatorPayPalEmail", paypalEmail ?? "" },
            { "productDisplayName", productDisplayName ?? "" }
        };

            var result = await U3DAuthenticator.CallFirebaseFunctionWithAuthRetry("deployFromStorage", deploymentRequest);

            if (result.ContainsKey("success") && (bool)result["success"])
            {
                var actualProjectName = result.ContainsKey("actualProjectName") ? result["actualProjectName"].ToString() : baseProjectName;
                var liveUrl = result.ContainsKey("url") ? result["url"].ToString() : "";

                // Pull the pre-resolved GitHub Actions run info out of the response
                // if deployFromStorage was able to find it. Both fields are nullable
                // server-side; PublishTab falls back to Stage A search when they're
                // missing or null.
                long? actionsRunId = null;
                string actionsRunHtmlUrl = null;
                if (result.ContainsKey("githubActionsRunId") && result["githubActionsRunId"] != null)
                {
                    if (long.TryParse(result["githubActionsRunId"].ToString(), out long parsedRunId))
                    {
                        actionsRunId = parsedRunId;
                    }
                }
                if (result.ContainsKey("githubActionsRunHtmlUrl") && result["githubActionsRunHtmlUrl"] != null)
                {
                    actionsRunHtmlUrl = result["githubActionsRunHtmlUrl"].ToString();
                }

                return new DeploymentResult
                {
                    Success = true,
                    ActualProjectName = actualProjectName,
                    Url = liveUrl,
                    GitHubActionsRunId = actionsRunId,
                    GitHubActionsRunHtmlUrl = actionsRunHtmlUrl
                };
            }
            else
            {
                // Surface the full server error including any registration-failure
                // guidance (file 5's deployFromStorage returns a descriptive message
                // when registration throws, including the working GitHub Pages URL
                // so the user isn't stranded).
                var errorMessage = result.ContainsKey("message") && result["message"] != null
                    ? result["message"].ToString()
                    : (result.ContainsKey("error") ? result["error"].ToString() : "Unknown deployment error");

                Debug.LogError($"❌ Deployment failed: {errorMessage}");

                // If registration failed but the build IS live on GitHub Pages,
                // return the fallback URL so the UI can still offer it to the user.
                var fallbackUrl = result.ContainsKey("githubPagesUrl") && result["githubPagesUrl"] != null
                    ? result["githubPagesUrl"].ToString()
                    : "";
                var fallbackProject = result.ContainsKey("actualProjectName") && result["actualProjectName"] != null
                    ? result["actualProjectName"].ToString()
                    : baseProjectName;

                return new DeploymentResult
                {
                    Success = false,
                    ErrorMessage = errorMessage,
                    ActualProjectName = fallbackProject,
                    Url = fallbackUrl
                };
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ GitHub deployment trigger failed: {ex.Message}");
            return new DeploymentResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private async Task ClearExistingBuildFolder(string creatorUsername, string projectName)
    {
        try
        {
            var listUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o?" +
                          $"prefix=creators/{creatorUsername}/builds/{projectName}/Build/";

            using var listRequest = new HttpRequestMessage(HttpMethod.Get, listUrl);
            listRequest.Headers.Add("Authorization", $"Bearer {_idToken}");

            var listResponse = await _httpClient.SendAsync(listRequest);

            if (!listResponse.IsSuccessStatusCode)
                return;

            var listContent = await listResponse.Content.ReadAsStringAsync();
            var listData = JsonConvert.DeserializeObject<Dictionary<string, object>>(listContent);

            if (!listData.ContainsKey("items"))
                return;

            var items = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(listData["items"].ToString());

            foreach (var item in items)
            {
                var fileName = item["name"].ToString();
                var encodedFileName = Uri.EscapeDataString(fileName);
                var deleteUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o/{encodedFileName}";

                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, deleteUrl);
                deleteRequest.Headers.Add("Authorization", $"Bearer {_idToken}");

                var deleteResponse = await _httpClient.SendAsync(deleteRequest);

                if (!deleteResponse.IsSuccessStatusCode)
                    Debug.Log($"[U3D] Could not delete existing build file (non-fatal): {fileName} — {(int)deleteResponse.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"⚠️ Could not clear existing Build folder (non-fatal): {ex.Message}");
        }
    }

    public class DeploymentResult
    {
        public bool Success { get; set; }
        public string ActualProjectName { get; set; }
        public string Url { get; set; }
        public string ErrorMessage { get; set; }
        // Pre-resolved GitHub Actions run info from deployFromStorage.
        // Null when the function couldn't resolve the run within its lookup
        // window — PublishTab falls back to Stage A search when these are
        // null. See WaitForGitHubActionsCompletion in PublishTab.cs.
        public long? GitHubActionsRunId { get; set; }
        public string GitHubActionsRunHtmlUrl { get; set; }
    }

    private async Task<bool> UploadFileWithThrottling(BuildFileInfo file, string creatorUsername, string projectName, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            return await UploadFileToStorage(file, creatorUsername, projectName);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private List<BuildFileInfo> CollectBuildFiles(string buildPath)
    {
        var files = new List<BuildFileInfo>();
        var buildDirectory = new DirectoryInfo(buildPath);

        foreach (var file in buildDirectory.GetFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(buildPath, file.FullName).Replace('\\', '/');

            files.Add(new BuildFileInfo
            {
                LocalPath = file.FullName,
                StoragePath = relativePath,
                Size = file.Length,
                ContentType = GetContentType(file.Extension)
            });
        }

        return files;
    }

    private async Task<bool> UploadFileToStorage(BuildFileInfo file, string creatorUsername, string projectName)
    {
        try
        {
            var fileBytes = await File.ReadAllBytesAsync(file.LocalPath);

            var storageUrl = $"https://firebasestorage.googleapis.com/v0/b/{_storageBucket}/o" +
                           $"?name=creators/{creatorUsername}/builds/{projectName}/{file.StoragePath}";

            using var content = new ByteArrayContent(fileBytes);
            content.Headers.Add("Content-Type", file.ContentType);

            using var request = new HttpRequestMessage(HttpMethod.Post, storageUrl);
            request.Content = content;
            request.Headers.Add("Authorization", $"Bearer {_idToken}");

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
                return true;

            var errorText = await response.Content.ReadAsStringAsync();
            Debug.LogError($"❌ Upload failed for {file.StoragePath}: {response.StatusCode} - {errorText}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ Exception uploading {file.StoragePath}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TriggerGitHubDeployment(string creatorUsername, string projectName, List<BuildFileInfo> files)
    {
        try
        {
            var fileList = files.ConvertAll(f => f.StoragePath);
            var paypalEmail = U3DAuthenticator.GetPayPalEmail();

            var deploymentRequest = new Dictionary<string, object>
        {
            { "project", projectName },
            { "creatorUsername", creatorUsername },
            { "githubOwner", GitHubTokenManager.GitHubUsername },
            { "fileList", fileList },
            { "githubToken", GitHubTokenManager.Token },
            { "creatorPayPalEmail", paypalEmail ?? "" }
        };

            var result = await U3DAuthenticator.CallFirebaseFunctionWithAuthRetry("deployFromStorage", deploymentRequest);

            if (result.ContainsKey("url"))
                return true;

            Debug.LogError("❌ Deployment failed - no URL returned");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ GitHub deployment trigger failed: {ex.Message}");
            return false;
        }
    }

    private string GetContentType(string extension)
    {
        return extension.ToLower() switch
        {
            ".html" => "text/html",
            ".js" => "application/javascript",
            ".css" => "text/css",
            ".wasm" => "application/wasm",
            ".data" => "application/octet-stream",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

public class BuildFileInfo
{
    public string LocalPath { get; set; }
    public string StoragePath { get; set; }
    public long Size { get; set; }
    public string ContentType { get; set; }
}