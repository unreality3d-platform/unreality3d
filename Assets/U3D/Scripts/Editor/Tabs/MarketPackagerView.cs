using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using System.Threading.Tasks;

namespace U3D.Editor
{
    /// <summary>
    /// "Package for Market" sub-view of the Publish tab. Owns all market-packaging
    /// state and drawing so it stays completely separate from the publish/deploy
    /// flow in PublishTab — the two modes share no state.
    ///
    /// Part 2: reads the Project-window selection, resolves the full dependency
    /// closure, and reports what will be included vs. what blocks the package and
    /// why. Attestation + export to disk arrive in Part 3.
    /// </summary>
    public class MarketPackagerView
    {
        private enum MarketItemStatus { Allowed, Blocked }

        private class MarketAssetItem
        {
            public string path;
            public bool isRoot;       // explicitly selected vs. pulled in as a dependency
            public bool isExternal;   // resolved outside Assets/ (URP / built-ins) — not exported
            public MarketItemStatus status;
            public string reason;
        }

        // Market package size policy. Hard cap mirrors the storage.rules write limit
        // (1 GiB) — uploads above it are rejected server-side, so block before sending.
        // Recommended cap is a softer nudge: bigger packages cost more to deliver to
        // buyers (download egress) and are slower to upload.
        private const long RecommendedMaxBytes = 500L * 1024 * 1024;
        private const long HardMaxBytes = 1024L * 1024 * 1024;

        // Custom executable code — never allowed in a v1 (art & media) package.
        private static readonly HashSet<string> CodeExtensions = new HashSet<string>
        {
            ".cs", ".dll", ".so", ".dylib", ".bundle", ".a", ".jslib", ".asmdef", ".asmref"
        };

        // Asset types whose file lives under Assets/ but which need a non-baseline
        // package to work after import. Maps extension -> human-readable package name.
        private static readonly Dictionary<string, string> PackageRequiredExtensions =
            new Dictionary<string, string>
        {
            { ".vfx", "Visual Effect Graph" }
        };

        // Package roots a buyer's U3D-template project is guaranteed to have. Mirrors
        // the template's Packages/manifest.json, plus the render-pipeline core and
        // Shader Graph that URP pulls in transitively. All com.unity.modules.* engine
        // modules are treated as baseline separately (see ClassifyAsset), since they're
        // present in every Unity project. When you add a package to the template, add
        // its root here so art that depends on it doesn't wrongly block.
        private static readonly HashSet<string> BaselinePackageRoots = new HashSet<string>
{
    // Render pipeline (URP + what it pulls in)
    "com.unity.render-pipelines.universal",
    "com.unity.render-pipelines.core",
    "com.unity.shadergraph",

    // Template packages
    "com.de-panther.webxr",
    "com.de-panther.webxr-input-profiles-loader",
    "com.de-panther.webxr-interactions",
    "com.unity.ai.navigation",
    "com.unity.cloud.gltfast",
    "com.unity.ide.rider",
    "com.unity.ide.visualstudio",
    "com.unity.inputsystem",
    "com.unity.multiplayer.center",
    "com.unity.nuget.mono-cecil",
    "com.unity.nuget.newtonsoft-json",
    "com.unity.test-framework",
    "com.unity.timeline",
    "com.unity.ugui",
    "com.unity.visualscripting",
    "com.unity.xr.hands",
    "com.unity.xr.interaction.toolkit"
};

        private Vector2 scrollPosition;
        private bool isUploading = false;
        private bool profileLoadAttempted = false;
        private bool profileLoading = false;
        private string lastUploadAssetId = "";
        private string lastUploadStoragePath = "";
        private string lastUploadError = "";
        private List<MarketAssetItem> closure = new List<MarketAssetItem>();
        private bool analyzed = false;
        private bool hasBlocking = false;
        private bool attested = false;
        private string lastExportedPath = "";
        private string packageName = "";
        private bool isValidating = false;
        private bool validationDone = false;
        private bool validated = false;
        private string validationStatus = "";
        private List<string> validationReasons = new List<string>();
        private int validationFileCount = 0;
        private string validationError = "";

        public void Draw()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Package an Asset for the Market", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bundle art and media assets into a portable package other creators can buy and import.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            DrawSelectionSection();

            if (analyzed)
            {
                EditorGUILayout.Space(10);
                DrawReportSection();

                if (!hasBlocking)
                {
                    EditorGUILayout.Space(10);
                    DrawExportSection();

                    EditorGUILayout.Space(10);
                    DrawMarketUploadSection();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawMarketUploadSection()
        {
            var included = closure
                .Where(i => i.status == MarketItemStatus.Allowed && !i.isExternal)
                .Select(i => i.path)
                .ToList();

            if (included.Count == 0)
            {
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("4. Upload to the Market", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Sends this package to the platform as a draft listing. The platform re-checks " +
                "the package before it can be listed. You can keep working while it's reviewed.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"Recommended max {RecommendedMaxBytes / (1024 * 1024)} MB · hard limit {HardMaxBytes / (1024 * 1024)} MB. " +
                "Bigger packages upload slower and cost more to deliver to buyers.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6);

            bool loggedIn = U3DAuthenticator.IsLoggedIn;
            string uid = U3DAuthenticator.CurrentUser.UserId;
            bool haveUid = !string.IsNullOrEmpty(uid);

            string paypalEmail = U3DAuthenticator.GetPayPalEmail();
            bool havePayPal = !string.IsNullOrEmpty(paypalEmail);

            if (loggedIn && !haveUid && !profileLoading && !profileLoadAttempted)
            {
                EnsureMarketProfileLoadedAsync();
            }

            if (!loggedIn)
            {
                EditorGUILayout.HelpBox(
                    "Log in on the Setup tab before uploading. The upload needs your account id.",
                    MessageType.Warning);
            }
            else if (!haveUid)
            {
                if (profileLoading)
                {
                    EditorGUILayout.HelpBox("Loading your account details…", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Couldn't load your account details. Try Reload, or re-open the Setup tab to confirm you're signed in.",
                        MessageType.Warning);
                    if (GUILayout.Button("Reload account", GUILayout.Height(24)))
                    {
                        EnsureMarketProfileLoadedAsync();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            if (loggedIn && haveUid)
            {
                EditorGUILayout.LabelField($"Submitting as: {ResolveSubmitterIdentity()}", EditorStyles.miniLabel);
                if (havePayPal)
                {
                    EditorGUILayout.LabelField($"Payments go to: {paypalEmail}", EditorStyles.miniLabel);
                }
                EditorGUILayout.Space(4);
            }

            if (loggedIn && haveUid && !havePayPal)
            {
                EditorGUILayout.HelpBox(
                    "Add a PayPal email on the Setup tab. Buyers' payments go directly to your PayPal account — " +
                    "a payout address is required before you can upload.",
                    MessageType.Warning);
                EditorGUILayout.Space(4);
            }

            EditorGUI.BeginDisabledGroup(!attested || !loggedIn || !haveUid || !havePayPal || isUploading || isValidating);
            string uploadLabel;
            if (isUploading) uploadLabel = "Uploading…";
            else if (isValidating) uploadLabel = "Checking…";
            else if (!string.IsNullOrEmpty(lastUploadError)) uploadLabel = "Retry Upload";
            else uploadLabel = "Upload to Market";
            if (GUILayout.Button(uploadLabel, GUILayout.Height(34)))
            {
                UploadToMarketAsync(included);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();

            if (!attested)
            {
                EditorGUILayout.LabelField("Tick the rights box above to enable upload.", EditorStyles.miniLabel);
            }
            else if (loggedIn && haveUid && !havePayPal)
            {
                EditorGUILayout.LabelField("Add a PayPal email in the Setup tab to enable upload.", EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(lastUploadError))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(lastUploadError, MessageType.Error);
            }

            if (!string.IsNullOrEmpty(lastUploadStoragePath))
            {
                EditorGUILayout.Space(6);
                var okStyle = new GUIStyle(EditorStyles.boldLabel);
                okStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? Color.green
                    : new Color(0f, 0.5f, 0f);
                EditorGUILayout.LabelField("✅ Uploaded as draft", okStyle);
                EditorGUILayout.LabelField($"Asset id: {lastUploadAssetId}", EditorStyles.miniLabel);
                EditorGUILayout.SelectableLabel(lastUploadStoragePath, EditorStyles.textField, GUILayout.Height(18));
            }

            if (isValidating)
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.LabelField("Checking the package on the platform…", EditorStyles.miniLabel);
            }
            else if (!string.IsNullOrEmpty(validationError))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(validationError, MessageType.Error);
            }
            else if (validationDone)
            {
                EditorGUILayout.Space(6);
                if (validated)
                {
                    var validatedStyle = new GUIStyle(EditorStyles.boldLabel);
                    validatedStyle.normal.textColor = EditorGUIUtility.isProSkin
                        ? Color.green
                        : new Color(0f, 0.5f, 0f);
                    EditorGUILayout.LabelField("✅ Validated by the platform", validatedStyle);
                    EditorGUILayout.LabelField(
                        validationFileCount > 0
                            ? $"Checked {validationFileCount} file(s). This draft is ready for the next step."
                            : "This draft is ready for the next step.",
                        EditorStyles.wordWrappedMiniLabel);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "The platform couldn't validate this package. Fix the items below, then upload again.",
                        MessageType.Warning);

                    if (validationReasons != null && validationReasons.Count > 0)
                    {
                        foreach (var reason in validationReasons)
                        {
                            EditorGUILayout.LabelField("• " + reason, EditorStyles.wordWrappedMiniLabel);
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField(
                            "No specific reasons were returned. Check your assets and try again.",
                            EditorStyles.wordWrappedMiniLabel);
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // Triggered when the upload section is shown while signed in but without a
        // loaded account id. Fetches the profile (which populates UserId) so the upload
        // — which requires the real UserId — can proceed.
        private async void EnsureMarketProfileLoadedAsync()
        {
            if (profileLoading)
            {
                return;
            }
            profileLoading = true;
            profileLoadAttempted = true;
            try
            {
                await U3DAuthenticator.ForceProfileReload();
            }
            catch
            {
                // ForceProfileReload already logs; the section reflects the still-empty UserId.
            }
            finally
            {
                profileLoading = false;
            }
        }

        private string ResolveSubmitterIdentity()
        {
            var displayName = U3DAuthenticator.CurrentUser.DisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName.Trim();
            }
            var username = U3DAuthenticator.CurrentUser.CreatorUsername;
            return string.IsNullOrWhiteSpace(username) ? "your account" : username.Trim();
        }

        private string ResolveTitle()
        {
            return string.IsNullOrWhiteSpace(packageName)
                ? "Untitled Market Package"
                : packageName.Trim();
        }

        private static bool GetBool(Dictionary<string, object> data, string key)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw != null)
            {
                if (raw is bool b) return b;
                if (bool.TryParse(raw.ToString(), out bool parsed)) return parsed;
            }
            return false;
        }

        private static int GetInt(Dictionary<string, object> data, string key)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw != null)
            {
                try
                {
                    return System.Convert.ToInt32(raw);
                }
                catch
                {
                    if (int.TryParse(raw.ToString(), out int parsed)) return parsed;
                }
            }
            return 0;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw != null)
            {
                return raw.ToString();
            }
            return "";
        }

        private static List<string> GetStringList(Dictionary<string, object> data, string key)
        {
            var list = new List<string>();
            if (data != null && data.TryGetValue(key, out var raw) && raw != null)
            {
                if (raw is System.Collections.IEnumerable enumerable && !(raw is string))
                {
                    foreach (var element in enumerable)
                    {
                        if (element == null) continue;
                        var s = element.ToString();
                        if (!string.IsNullOrEmpty(s)) list.Add(s);
                    }
                }
                else
                {
                    var s = raw.ToString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }

        private async Task ValidateUploadedPackageAsync(string assetId, string title, string paypalEmail)
        {
            isValidating = true;
            validationDone = false;
            validated = false;
            validationStatus = "";
            validationReasons = new List<string>();
            validationFileCount = 0;
            validationError = "";

            try
            {
                var request = new Dictionary<string, object>
        {
            { "assetId", assetId },
            { "title", title },
            { "attested", true },
            { "paypalEmail", paypalEmail }
        };

                var response = await U3DAuthenticator.CallFirebaseFunctionWithAuthRetry(
                    "validateMarketPackage", request);

                if (response == null)
                {
                    validationError = "The platform didn't return a validation result. You can upload again to retry.";
                    return;
                }

                validated = GetBool(response, "validated");
                validationStatus = GetString(response, "status");
                validationReasons = GetStringList(response, "reasons");
                validationFileCount = GetInt(response, "fileCount");
                validationDone = true;
            }
            catch (System.Exception ex)
            {
                validationError = $"The platform couldn't check the package: {ex.Message}";
            }
            finally
            {
                isValidating = false;
            }
        }

        private void ResetValidationState()
        {
            isValidating = false;
            validationDone = false;
            validated = false;
            validationStatus = "";
            validationReasons = new List<string>();
            validationFileCount = 0;
            validationError = "";
        }

        private async void UploadToMarketAsync(List<string> includedPaths)
        {
            isUploading = true;
            lastUploadError = "";
            lastUploadStoragePath = "";
            lastUploadAssetId = "";
            ResetValidationState();

            string tempPackagePath = null;
            bool uploadSucceeded = false;
            string uploadedAssetId = null;
            string title = ResolveTitle();

            try
            {
                string uid = U3DAuthenticator.CurrentUser.UserId;
                string idToken = U3DAuthenticator.GetIdToken();

                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(idToken))
                {
                    lastUploadError = "Not logged in. Open the Creator Dashboard, log in, then try again.";
                    return;
                }

                bool authReady = await U3DAuthenticator.PrepareForDeployment();
                if (!authReady)
                {
                    lastUploadError = "Authentication check failed. Log out and back in, then try again.";
                    return;
                }

                // Re-fetch the token after PrepareForDeployment guarantees it's fresh
                idToken = U3DAuthenticator.GetIdToken();

                string paypalEmail = U3DAuthenticator.GetPayPalEmail();
                if (string.IsNullOrEmpty(paypalEmail))
                {
                    lastUploadError = "Add a PayPal email in the Setup tab before uploading to the market.";
                    return;
                }

                string bucket = FirebaseConfigManager.CurrentConfig != null
                    ? FirebaseConfigManager.CurrentConfig.storageBucket
                    : null;
                if (string.IsNullOrEmpty(bucket))
                {
                    lastUploadError = "Storage is not configured. Open the Creator Dashboard to finish setup, then try again.";
                    return;
                }

                string assetId = System.Guid.NewGuid().ToString("N");

                tempPackagePath = Path.Combine(Path.GetTempPath(), $"u3d_market_{assetId}.unitypackage");
                AssetDatabase.ExportPackage(includedPaths.ToArray(), tempPackagePath, ExportPackageOptions.Default);

                var info = new FileInfo(tempPackagePath);
                if (!info.Exists)
                {
                    lastUploadError = "Could not build the package for upload.";
                    return;
                }

                if (info.Length > HardMaxBytes)
                {
                    lastUploadError =
                        $"Package is {info.Length / (1024 * 1024)} MB, over the {HardMaxBytes / (1024 * 1024)} MB limit. " +
                        "Remove some assets and check again.";
                    return;
                }

                if (info.Length > RecommendedMaxBytes)
                {
                    bool proceed = EditorUtility.DisplayDialog(
                        "Large package",
                        $"This package is {info.Length / (1024 * 1024)} MB, above the recommended " +
                        $"{RecommendedMaxBytes / (1024 * 1024)} MB. Larger packages upload slower and cost more to " +
                        "deliver to buyers. Upload anyway?",
                        "Upload",
                        "Cancel");
                    if (!proceed)
                    {
                        return;
                    }
                }

                var uploader = new FirebaseStorageUploader(bucket, idToken);
                FirebaseStorageUploader.MarketUploadResult result;
                try
                {
                    result = await uploader.UploadMarketPackage(tempPackagePath, uid, assetId);
                }
                finally
                {
                    uploader.Dispose();
                }

                if (result != null && result.Success)
                {
                    lastUploadAssetId = assetId;
                    lastUploadStoragePath = result.StoragePath;
                    lastUploadError = "";
                    uploadSucceeded = true;
                    uploadedAssetId = assetId;
                }
                else
                {
                    lastUploadError = result != null && !string.IsNullOrEmpty(result.ErrorMessage)
                        ? result.ErrorMessage
                        : "Upload failed. Check your connection and retry.";
                }
            }
            catch (System.Exception ex)
            {
                lastUploadError = ex.Message;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPackagePath) && File.Exists(tempPackagePath))
                {
                    try { File.Delete(tempPackagePath); } catch { /* best-effort temp cleanup */ }
                }
                isUploading = false;
            }

            if (uploadSucceeded)
            {
                await ValidateUploadedPackageAsync(uploadedAssetId, title, U3DAuthenticator.GetPayPalEmail());
            }
        }

        private void DrawSelectionSection()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("1. Choose assets", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Select the art and media you want to package in the Project window, then check them. " +
                "You can pick individual files or whole folders.",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(5);

            int selectedCount = Selection.assetGUIDs != null ? Selection.assetGUIDs.Length : 0;
            EditorGUILayout.LabelField(
                selectedCount == 0
                    ? "Nothing selected in the Project window."
                    : $"{selectedCount} item(s) selected in the Project window.",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(selectedCount == 0);
            if (GUILayout.Button("Check Selected Assets", GUILayout.Height(30)))
            {
                AnalyzeSelection();
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();

            if (analyzed)
            {
                if (GUILayout.Button("Clear", GUILayout.Width(80), GUILayout.Height(30)))
                {
                    ResetAnalysis();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawReportSection()
        {
            var included = closure.Where(i => i.status == MarketItemStatus.Allowed && !i.isExternal).ToList();
            var blocked = closure.Where(i => i.status == MarketItemStatus.Blocked).ToList();
            var external = closure.Where(i => i.status == MarketItemStatus.Allowed && i.isExternal).ToList();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("2. Compatibility report", EditorStyles.boldLabel);

            if (hasBlocking)
            {
                EditorGUILayout.HelpBox(
                    $"{blocked.Count} item(s) can't go in a v1 market package. Remove them from your selection " +
                    "(or from the source asset that pulls them in) and check again.",
                    MessageType.Error);

                foreach (var item in blocked)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.LabelField(item.path, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(item.reason, EditorStyles.wordWrappedMiniLabel);
                    if (!item.isRoot)
                    {
                        EditorGUILayout.LabelField("(pulled in as a dependency of your selection)", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.Space(5);
            }
            else
            {
                var okStyle = new GUIStyle(EditorStyles.boldLabel);
                okStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? Color.green
                    : new Color(0f, 0.5f, 0f);
                EditorGUILayout.LabelField("✅ No compatibility problems found.", okStyle);
                EditorGUILayout.Space(5);
            }

            EditorGUILayout.LabelField($"Will be included in the package ({included.Count}):", EditorStyles.boldLabel);
            if (included.Count == 0)
            {
                EditorGUILayout.LabelField("Nothing to include yet.", EditorStyles.miniLabel);
            }
            else
            {
                foreach (var item in included)
                {
                    EditorGUILayout.LabelField("• " + item.path, EditorStyles.miniLabel);
                }
            }

            if (external.Count > 0)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField(
                    $"{external.Count} reference(s) resolve to URP or Unity built-ins — buyers' URP projects already " +
                    "have these, so they aren't included in the package.",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawExportSection()
        {
            var included = closure
                .Where(i => i.status == MarketItemStatus.Allowed && !i.isExternal)
                .Select(i => i.path)
                .ToList();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("3. Confirm and export", EditorStyles.boldLabel);

            if (included.Count == 0)
            {
                EditorGUILayout.LabelField("There are no includable assets to export yet.", EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUILayout.LabelField("Package name", EditorStyles.miniBoldLabel);
            packageName = EditorGUILayout.TextField(packageName);
            EditorGUILayout.LabelField(
                "Used as the default file name when you export. You can still change it in the save dialog.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(8);

            attested = EditorGUILayout.ToggleLeft(
                "I have the rights to distribute every asset in this package.",
                attested);
            EditorGUILayout.LabelField(
                "The platform re-checks the package before it can be listed for sale. Final wording for this " +
                "agreement is set later in the store terms.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(5);

            EditorGUI.BeginDisabledGroup(!attested);
            if (GUILayout.Button($"Export Package ({included.Count} assets)", GUILayout.Height(34)))
            {
                ExportMarketPackage(included);
                GUIUtility.ExitGUI();
            }
            EditorGUI.EndDisabledGroup();

            if (!attested)
            {
                EditorGUILayout.LabelField("Tick the box above to enable export.", EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(lastExportedPath))
            {
                EditorGUILayout.Space(8);
                var okStyle = new GUIStyle(EditorStyles.boldLabel);
                okStyle.normal.textColor = EditorGUIUtility.isProSkin
                    ? Color.green
                    : new Color(0f, 0.5f, 0f);
                EditorGUILayout.LabelField("✅ Exported", okStyle);
                EditorGUILayout.SelectableLabel(lastExportedPath, EditorStyles.textField, GUILayout.Height(18));
                if (GUILayout.Button("Show in Explorer", GUILayout.Height(24)))
                {
                    EditorUtility.RevealInFinder(lastExportedPath);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private void ExportMarketPackage(List<string> includedPaths)
        {
            string defaultName = SanitizeFileName(packageName);

            string targetPath = EditorUtility.SaveFilePanel(
                "Export Market Package",
                "",
                defaultName,
                "unitypackage");

            if (string.IsNullOrEmpty(targetPath))
            {
                return; // creator cancelled the save dialog
            }

            try
            {
                // Export EXACTLY the approved set. No IncludeDependencies — the closure
                // was already resolved during analysis, so the package contains precisely
                // what the report listed and nothing validation excluded.
                AssetDatabase.ExportPackage(includedPaths.ToArray(), targetPath, ExportPackageOptions.Default);

                lastExportedPath = targetPath;
                EditorUtility.RevealInFinder(targetPath);
            }
            catch (System.Exception ex)
            {
                lastExportedPath = "";
                EditorUtility.DisplayDialog(
                    "Export Failed",
                    $"The package couldn't be exported.\n\n{ex.Message}",
                    "OK");
            }
        }

        private void AnalyzeSelection()
        {
            var roots = ResolveSelectionToAssetPaths().Distinct().ToList();

            attested = false;
            lastExportedPath = "";

            lastUploadAssetId = "";
            lastUploadStoragePath = "";
            lastUploadError = "";
            ResetValidationState();

            if (roots.Count == 0)
            {
                closure = new List<MarketAssetItem>();
                hasBlocking = false;
                analyzed = true;
                return;
            }

            var rootSet = new HashSet<string>(roots);

            // Recursive closure; recursive=true also returns the input paths themselves.
            var deps = AssetDatabase.GetDependencies(roots.ToArray(), true);
            var allPaths = new HashSet<string>(deps);
            allPaths.UnionWith(rootSet); // safety: ensure every root is represented

            var items = new List<MarketAssetItem>();
            foreach (var path in allPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                items.Add(ClassifyAsset(path, rootSet.Contains(path)));
            }

            // Blocking first, then included, then external; alphabetical within each.
            closure = items
                .OrderBy(i => i.status == MarketItemStatus.Blocked ? 0 : (i.isExternal ? 2 : 1))
                .ThenBy(i => i.path)
                .ToList();

            hasBlocking = closure.Any(i => i.status == MarketItemStatus.Blocked);
            analyzed = true;

            // Seed a name only if the creator hasn't typed one — a re-check of a
            // different set keeps whatever they already entered. Clear resets it.
            if (string.IsNullOrEmpty(packageName))
            {
                packageName = SuggestPackageName();
            }
        }

        private MarketAssetItem ClassifyAsset(string path, bool isRoot)
        {
            var item = new MarketAssetItem { path = path, isRoot = isRoot };
            string ext = Path.GetExtension(path).ToLowerInvariant();

            // Branch on WHERE the asset lives first. Anything outside Assets/ is a
            // dependency, not the creator's own content, so the "no custom code" rule
            // doesn't apply to it — a .cs inside URP is URP's code, not the seller's.
            if (!path.StartsWith("Assets/"))
            {
                if (path.StartsWith("Packages/"))
                {
                    string pkgRoot = ExtractPackageRoot(path);

                    // Engine modules exist in every Unity project; template packages are
                    // guaranteed by the buyer's U3D-template project. Either is fine, and
                    // neither is shipped (only Assets/ files can be exported).
                    if (pkgRoot.StartsWith("com.unity.modules.") || BaselinePackageRoots.Contains(pkgRoot))
                    {
                        item.status = MarketItemStatus.Allowed;
                        item.isExternal = true;
                        item.reason = $"Provided by {pkgRoot} (template baseline).";
                        return item;
                    }

                    item.status = MarketItemStatus.Blocked;
                    item.reason = $"Depends on the {pkgRoot} package, which buyers won't have.";
                    return item;
                }

                // Built-in Unity resources (default material, built-in shaders, etc.).
                item.status = MarketItemStatus.Allowed;
                item.isExternal = true;
                item.reason = "Unity built-in resource.";
                return item;
            }

            // Below here the asset lives under Assets/ — it's the creator's own content,
            // so the v1 content rules apply.

            // Custom code — blocked in v1 (art & media only).
            if (CodeExtensions.Contains(ext))
            {
                item.status = MarketItemStatus.Blocked;
                item.reason = $"Custom code ({ext}). v1 market packages are art and media only.";
                return item;
            }

            // Asset types that need a non-baseline package to work after import.
            if (PackageRequiredExtensions.TryGetValue(ext, out string requiredPkg))
            {
                item.status = MarketItemStatus.Blocked;
                item.reason = $"Needs the {requiredPkg} package, which a package import won't install for the buyer.";
                return item;
            }

            // Normal art/media under Assets/ — included in the package.
            item.status = MarketItemStatus.Allowed;
            item.isExternal = false;
            return item;
        }

        private static string ExtractPackageRoot(string packagesPath)
        {
            // "Packages/<root>/..." -> "<root>"
            var parts = packagesPath.Split('/');
            return parts.Length >= 2 ? parts[1] : packagesPath;
        }

        private List<string> ResolveSelectionToAssetPaths()
        {
            var paths = new List<string>();
            if (Selection.assetGUIDs == null) return paths;

            foreach (var guid in Selection.assetGUIDs)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(p)) continue;

                if (AssetDatabase.IsValidFolder(p))
                {
                    // Expand a selected folder to every asset inside it (recursive).
                    foreach (var innerGuid in AssetDatabase.FindAssets("", new[] { p }))
                    {
                        var innerPath = AssetDatabase.GUIDToAssetPath(innerGuid);
                        if (string.IsNullOrEmpty(innerPath)) continue;
                        if (AssetDatabase.IsValidFolder(innerPath)) continue; // skip sub-folders
                        paths.Add(innerPath);
                    }
                }
                else
                {
                    paths.Add(p);
                }
            }

            return paths;
        }

        private string SuggestPackageName()
        {
            var firstIncluded = closure.FirstOrDefault(i => i.status == MarketItemStatus.Allowed && !i.isExternal);
            if (firstIncluded != null)
            {
                var name = Path.GetFileNameWithoutExtension(firstIncluded.path);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }
            return "U3D_MarketPackage";
        }

        private static string SanitizeFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "U3D_MarketPackage";
            }

            var cleaned = raw.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                cleaned = cleaned.Replace(c, '_');
            }

            cleaned = cleaned.Trim();
            return string.IsNullOrEmpty(cleaned) ? "U3D_MarketPackage" : cleaned;
        }

        private void ResetAnalysis()
        {
            analyzed = false;
            hasBlocking = false;
            attested = false;
            lastExportedPath = "";
            packageName = "";
            closure = new List<MarketAssetItem>();

            isUploading = false;
            lastUploadAssetId = "";
            lastUploadStoragePath = "";
            lastUploadError = "";
            ResetValidationState();
        }
    }
}