using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using System.Globalization;

namespace U3D.Editor
{
    public static class U3DTemplateUpdateChecker
    {
        private const string RELEASES_API_URL =
            "https://api.github.com/repos/unreality3d-platform/unreality3d/releases/latest";

        private const string VERSION_FORMAT = "MM-dd-yy-HH-mm-ss";
        private static bool _hasCheckedThisSession;
        private static bool _isCheckingForUpdate;
        private static bool _isDownloading;

        public enum UpdateStatus
        {
            Unknown,
            Checking,
            UpToDate,
            UpdateAvailable,
            CheckFailed
        }

        public static UpdateStatus CurrentStatus { get; private set; } = UpdateStatus.Unknown;
        public static string LatestVersion { get; private set; } = "";
        public static string LatestReleaseName { get; private set; } = "";
        public static string DownloadUrl { get; private set; } = "";
        public static string ErrorMessage { get; private set; } = "";
        public static bool IsDownloading => _isDownloading;

        public static string GetLocalVersion()
        {
            try
            {
                TextAsset versionAsset = Resources.Load<TextAsset>("version");
                if (versionAsset != null)
                    return versionAsset.text.Trim();

                string versionPath = "Assets/U3D/Resources/version.txt";
                if (File.Exists(versionPath))
                    return File.ReadAllText(versionPath).Trim();
            }
            catch (Exception) { }

            return "";
        }

        public static bool IsNewerVersion(string remote, string local)
        {
            if (string.IsNullOrEmpty(remote))
                return false;

            if (string.IsNullOrEmpty(local))
                return true;

            string normalizedRemote = remote.Replace("u3d-update-", "").Trim();
            string normalizedLocal = local.Replace("u3d-update-", "").Trim();

            bool remoteParsed = DateTime.TryParseExact(
                normalizedRemote, VERSION_FORMAT, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime remoteDate);

            bool localParsed = DateTime.TryParseExact(
                normalizedLocal, VERSION_FORMAT, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime localDate);

            if (remoteParsed && localParsed)
                return remoteDate > localDate;

            return !string.Equals(normalizedRemote, normalizedLocal, StringComparison.OrdinalIgnoreCase);
        }

        public static void CheckForUpdateIfNeeded()
        {
            if (_isCheckingForUpdate || _isDownloading)
                return;

            if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling)
                return;

            if (_hasCheckedThisSession)
                return;

            _ = CheckForUpdate();
        }

        public static async Task CheckForUpdate()
        {
            if (_isCheckingForUpdate)
                return;

            _isCheckingForUpdate = true;
            CurrentStatus = UpdateStatus.Checking;
            ErrorMessage = "";

            try
            {
                using (var client = CreateUnauthenticatedClient())
                {
                    var response = await client.GetAsync(RELEASES_API_URL);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        CurrentStatus = UpdateStatus.CheckFailed;
                        ErrorMessage = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                            ? "GitHub API rate limit reached. Try again later."
                            : $"GitHub API returned {(int)response.StatusCode}.";
                        return;
                    }

                    var release = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                    string tagName = release.ContainsKey("tag_name")
                        ? release["tag_name"].ToString()
                        : "";

                    string releaseName = release.ContainsKey("name")
                        ? release["name"].ToString()
                        : tagName;

                    string downloadUrl = "";
                    if (release.ContainsKey("assets"))
                    {
                        var assets = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(
                            release["assets"].ToString());

                        foreach (var asset in assets)
                        {
                            string assetName = asset.ContainsKey("name") ? asset["name"].ToString() : "";
                            if (assetName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.ContainsKey("browser_download_url")
                                    ? asset["browser_download_url"].ToString()
                                    : "";
                                break;
                            }
                        }
                    }

                    LatestVersion = tagName.Replace("u3d-update-", "");
                    LatestReleaseName = releaseName;
                    DownloadUrl = downloadUrl;

                    _hasCheckedThisSession = true;

                    string local = GetLocalVersion();
                    CurrentStatus = IsNewerVersion(LatestVersion, local)
                        ? UpdateStatus.UpdateAvailable
                        : UpdateStatus.UpToDate;
                }
            }
            catch (HttpRequestException)
            {
                CurrentStatus = UpdateStatus.CheckFailed;
                ErrorMessage = "Couldn't reach GitHub. Check your internet connection.";
            }
            catch (TaskCanceledException)
            {
                CurrentStatus = UpdateStatus.CheckFailed;
                ErrorMessage = "Request timed out. Try again later.";
            }
            catch (Exception ex)
            {
                CurrentStatus = UpdateStatus.CheckFailed;
                ErrorMessage = $"Update check failed: {ex.Message}";
            }
            finally
            {
                _isCheckingForUpdate = false;
            }
        }

        public static async Task DownloadAndInstallUpdate()
        {
            if (_isDownloading || string.IsNullOrEmpty(DownloadUrl))
                return;

            _isDownloading = true;

            string tempDir = Path.Combine(Application.temporaryCachePath, "U3DUpdates");
            string tempPath = Path.Combine(tempDir, $"u3d-update-{LatestVersion}.unitypackage");

            try
            {
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                EditorUtility.DisplayProgressBar("U3D Template Update", "Downloading update package...", 0f);

                using (var client = CreateUnauthenticatedClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);

                    using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();

                        long totalBytes = response.Content.Headers.ContentLength ?? -1;
                        long receivedBytes = 0;

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            int bytesRead;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                receivedBytes += bytesRead;

                                if (totalBytes > 0)
                                {
                                    float progress = (float)receivedBytes / totalBytes;
                                    string sizeInfo = $"{receivedBytes / 1024}KB / {totalBytes / 1024}KB";
                                    EditorUtility.DisplayProgressBar(
                                        "U3D Template Update",
                                        $"Downloading update package... {sizeInfo}",
                                        progress);
                                }
                            }
                        }
                    }
                }

                EditorUtility.ClearProgressBar();

                if (!File.Exists(tempPath))
                {
                    EditorUtility.DisplayDialog(
                        "Update Failed",
                        "Download completed but the file wasn't saved correctly. Please try again.",
                        "OK");
                    return;
                }

                long fileSize = new FileInfo(tempPath).Length;
                if (fileSize < 1024)
                {
                    EditorUtility.DisplayDialog(
                        "Update Failed",
                        "Downloaded file appears to be corrupt (too small). Please try again.",
                        "OK");
                    File.Delete(tempPath);
                    return;
                }

                bool proceed = EditorUtility.DisplayDialog(
                    "Import U3D Template Update",
                    $"Update package downloaded ({fileSize / 1024}KB).\n\n" +
                    "The import dialog will open next. Please review carefully:\n" +
                    "• KEEP checked: all U3D core files (Assets/U3D/, Assets/U3D_SDK/)\n" +
                    "• UNCHECK: your custom scenes, materials, textures, models, and scripts\n\n" +
                    "It's recommended to back up your project folder first.",
                    "Open Import Dialog",
                    "Cancel");

                if (!proceed)
                {
                    File.Delete(tempPath);
                    return;
                }

                AssetDatabase.ImportPackage(tempPath, true);
            }
            catch (HttpRequestException ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(
                    "Download Failed",
                    $"Couldn't download the update package.\n\n{ex.Message}",
                    "OK");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(
                    "Update Failed",
                    $"Something went wrong during the update.\n\n{ex.Message}",
                    "OK");
            }
            finally
            {
                _isDownloading = false;
                EditorUtility.ClearProgressBar();
            }
        }

        public static void ForceRecheck()
        {
            _hasCheckedThisSession = false;
            CurrentStatus = UpdateStatus.Unknown;
            _ = CheckForUpdate();
        }

        private static HttpClient CreateUnauthenticatedClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }
    }
}