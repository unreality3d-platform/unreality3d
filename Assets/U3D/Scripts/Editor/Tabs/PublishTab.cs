using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Editor
{
    public class PublishTab : ICreatorTab
    {
        public string TabName => "Publish";
        public bool IsComplete { get; private set; }
        public System.Action<int> OnRequestTabSwitch { get; set; }

        private PublishStep currentStep = PublishStep.Ready;
        private string publishUrl = "";
        private string cachedProductName;
        private bool githubConnected = false;
        private bool projectBuilt = false;
        private bool deploymentComplete = false;
        private Vector2 scrollPosition;
        private string currentStatus = "";
        private bool isPublishing = false;
        private bool shouldCreateNewRepository = false;
        private PublishMode currentMode = PublishMode.PublishExperience;
        private MarketPackagerView marketView = new MarketPackagerView();

        private List<ProjectOption> availableOptions = new List<ProjectOption>();
        private bool optionsLoaded = false;
        private bool loadingOptions = false;
        private int selectedOptionIndex = -1;
        private int previousSelectedOptionIndex = -1; // Track previous selection for sync logic

        //Thumbnail checking cache - only check when repository is selected
        private Dictionary<string, bool> thumbnailCheckCache = new Dictionary<string, bool>();
        private string lastSelectedRepositoryForThumbnailCheck = "";

        // Scene confirmation state — build-scoped toggles, never writes to EditorBuildSettings
        private string activeScenePath = "";
        private string activeSceneName = "";
        private Dictionary<string, bool> buildSceneToggles = new Dictionary<string, bool>();

        // GitHub Actions polling state — used by the WaitingForGitHub step to
        // confirm the published URL is actually live before showing success.
        private bool githubActionsComplete = false;
        private string githubActionsRunHtmlUrl = "";
        private const string CHUNK_WORKFLOW_FILENAME = "reassemble-chunks.yml";
        private const int GITHUB_ACTIONS_POLL_INTERVAL_MS = 8000;
        private const int GITHUB_ACTIONS_FIND_RUN_TIMEOUT_MS = 60000;
        private const int GITHUB_ACTIONS_TOTAL_TIMEOUT_MS = 480000;
        // Stage A safety net: a run is only claimed if it's still actively
        // running when we first observe it. A completed run we discover
        // instantly is definitionally a stale run from a previous publish.
        private const int GITHUB_ACTIONS_MIN_RUN_AGE_SEC = 5;

        private enum PublishStep
        {
            Ready,
            BuildingLocally,
            CreatingRepository,
            DeployingToGitHub,
            WaitingForGitHub,
            Complete
        }
        private enum PublishMode
        {
            PublishExperience,
            PackageForMarket
        }

        /// <summary>
        /// CRITICAL: Check if we should skip operations during builds (same as GitHubTokenManager/ProjectStartupConfiguration)
        /// </summary>
        private static bool ShouldSkipDuringBuild()
        {
            return BuildPipeline.isBuildingPlayer ||
                   EditorApplication.isCompiling ||
                   EditorApplication.isUpdating;
        }

        public void Initialize()
        {
            if (ShouldSkipDuringBuild())
            {
                return;
            }

            cachedProductName = Application.productName;

            if (!ShouldSkipDuringBuild())
            {
                var justPublished = EditorPrefs.GetBool("U3D_JustPublished", false);
                if (justPublished)
                {
                    EditorPrefs.DeleteKey("U3D_JustPublished");
                }
            }

            currentStep = PublishStep.Ready;
            IsComplete = false;
            publishUrl = "";

            githubConnected = false;
            projectBuilt = false;
            deploymentComplete = false;
            githubActionsComplete = false;
            githubActionsRunHtmlUrl = "";
            isPublishing = false;

            // Force repository options to reload on initialization
            optionsLoaded = false;
            loadingOptions = false;
            availableOptions.Clear();
            selectedOptionIndex = -1;
            previousSelectedOptionIndex = -1;
            currentStatus = "";

            RefreshBuildSceneList();
        }

        private void MarkPublishSuccess(string successUrl, string repositoryName)
        {
            // Set success state
            publishUrl = successUrl;
            IsComplete = true;
            currentStep = PublishStep.Complete;
            githubConnected = true;
            projectBuilt = true;
            deploymentComplete = true;
            githubActionsComplete = true;

            // Mark that we just published (for this session only)
            EditorPrefs.SetBool("U3D_JustPublished", true);
            EditorPrefs.SetString("U3D_PublishedURL", successUrl);
            EditorPrefs.SetString("U3D_LastRepositoryName", repositoryName);
        }

        private bool ValidateProductName(string productName, out string error)
        {
            error = null;

            // Handle null/empty
            if (string.IsNullOrWhiteSpace(productName))
            {
                error = "Product Name cannot be empty for New Repository";
                return false;
            }

            // Handle length (GitHub repo limit is 100, leave room for sanitization)
            if (productName.Length > 80)
            {
                error = "Product Name too long (max 80 characters)";
                return false;
            }

            // Handle reserved names that conflict with infrastructure
            var reservedNames = new[] { "admin", "api", "www", "test", "app", "web", "dev", "staging" };
            if (reservedNames.Contains(productName.ToLower()))
            {
                error = "Product Name conflicts with reserved words";
                return false;
            }

            // Handle characters that cause encoding issues
            if (productName.Any(c => c > 127)) // Non-ASCII characters
            {
                error = "Product Name should use only standard English characters for best compatibility";
                return false;
            }

            return true;
        }

        private void ResetPublishState()
        {
            EditorPrefs.DeleteKey("U3D_PublishedURL");
            publishUrl = "";
            githubConnected = false;
            projectBuilt = false;
            deploymentComplete = false;
            githubActionsComplete = false;
            githubActionsRunHtmlUrl = "";
            currentStep = PublishStep.Ready;
            IsComplete = false;
            currentStatus = "";
            isPublishing = false;
            shouldCreateNewRepository = true;
        }

        /// <summary>
        /// Reads the active scene and EditorBuildSettings to populate the scene confirmation UI.
        /// Does NOT modify EditorBuildSettings. Toggle state persists within the editor session
        /// so creators don't have to re-deselect scenes they already excluded.
        /// </summary>
        private void RefreshBuildSceneList()
        {
            var scene = SceneManager.GetActiveScene();
            activeScenePath = scene.path;
            activeSceneName = scene.name;

            // Build the toggle dictionary from EditorBuildSettings.scenes
            // Preserve existing toggle state for scenes we've already seen
            var freshToggles = new Dictionary<string, bool>();
            foreach (var buildScene in EditorBuildSettings.scenes)
            {
                if (!buildScene.enabled)
                    continue;

                if (buildSceneToggles.TryGetValue(buildScene.path, out bool existingState))
                {
                    // Preserve the creator's previous toggle choice
                    freshToggles[buildScene.path] = existingState;
                }
                else
                {
                    // New scene — default to on
                    freshToggles[buildScene.path] = true;
                }
            }

            // If the active scene isn't in Build Settings at all, add it as toggled on
            if (!freshToggles.ContainsKey(activeScenePath) && !string.IsNullOrEmpty(activeScenePath))
            {
                freshToggles[activeScenePath] = true;
            }

            // The active scene is always included — force it on
            if (!string.IsNullOrEmpty(activeScenePath))
            {
                freshToggles[activeScenePath] = true;
            }

            buildSceneToggles = freshToggles;
        }

        /// <summary>
        /// Returns the scene paths the creator has chosen to include in this build.
        /// The active scene is always first (Unity treats index 0 as the entry scene).
        /// </summary>
        private string[] GetSelectedScenePaths()
        {
            var selected = new List<string>();

            // Active scene is always first
            if (!string.IsNullOrEmpty(activeScenePath))
            {
                selected.Add(activeScenePath);
            }

            // Add other toggled-on scenes
            foreach (var kvp in buildSceneToggles)
            {
                if (kvp.Value && kvp.Key != activeScenePath)
                {
                    selected.Add(kvp.Key);
                }
            }

            return selected.ToArray();
        }

        public void DrawTab()
        {
            if (EditorPrefs.GetBool("U3D_NavigateToMarketPackager", false))
            {
                EditorPrefs.DeleteKey("U3D_NavigateToMarketPackager");
                currentMode = PublishMode.PackageForMarket;
            }

            EditorGUILayout.Space(10);

            DrawModeToggle();

            EditorGUILayout.Space(10);

            if (currentMode == PublishMode.PackageForMarket)
            {
                marketView.Draw();
                return;
            }

            DrawPublishExperienceMode();
        }

        private void DrawModeToggle()
        {
            var modeLabels = new[] { "Publish Experience", "Package for Market" };

            // Lock the toggle while a publish is running so an in-progress deploy
            // can't be hidden behind a mode switch.
            EditorGUI.BeginDisabledGroup(isPublishing);
            int newMode = GUILayout.Toolbar((int)currentMode, modeLabels, GUILayout.Height(28));
            EditorGUI.EndDisabledGroup();

            if (newMode != (int)currentMode)
            {
                currentMode = (PublishMode)newMode;
                GUI.FocusControl(null);
            }
        }

        private void DrawPublishExperienceMode()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Publish Your Content", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Share your creation with the world! This will make your content live on the internet.", MessageType.Info);
            EditorGUILayout.Space(15);

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            // Check prerequisites
            if (!CanPublish())
            {
                DrawPrerequisites();
            }
            else if (currentStep == PublishStep.Ready && !isPublishing)
            {
                DrawReadyToPublish();
            }

            EditorGUILayout.Space(10);

            if (isPublishing || currentStep != PublishStep.Ready)
            {
                DrawPublishingSteps();
            }

            if (!string.IsNullOrEmpty(currentStatus))
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(currentStatus, MessageType.Info);
            }

            if (currentStep == PublishStep.Complete)
            {
                DrawSuccessSection();
            }

            EditorGUILayout.EndScrollView();
        }

        private bool CanPublish()
        {
            // CRITICAL: Return false if we're in compilation/build state
            if (ShouldSkipDuringBuild())
            {
                return false;
            }

            // 🆕 AUTHENTICATION CHECK: Must be logged in to publish
            if (!U3DAuthenticator.IsLoggedIn)
            {
                return false;
            }

            // 🆕 SETUP COMPLETION CHECK: Must have completed basic setup
            bool hasUsername = !string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername);
            bool hasGitHubToken = GitHubTokenManager.HasValidToken;

            // Allow publishing if user has username and GitHub token
            // PayPal is optional for publishing
            return hasUsername && hasGitHubToken;
        }

        private void DrawPrerequisites()
        {
            EditorGUILayout.LabelField("Setup Required", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            // CRITICAL: Show compilation message if we're in that state
            if (ShouldSkipDuringBuild())
            {
                EditorGUILayout.HelpBox("⏳ Waiting for script compilation to complete...", MessageType.Info);
                return;
            }

            // 🆕 SPECIFIC GUIDANCE: Tell user exactly what's missing
            if (!U3DAuthenticator.IsLoggedIn)
            {
                EditorGUILayout.HelpBox("🔐 Please log in to your Unreality3D account first.", MessageType.Warning);
            }
            else if (string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername))
            {
                EditorGUILayout.HelpBox("🎯 Please reserve your creator username first.", MessageType.Warning);
            }
            else if (!GitHubTokenManager.HasValidToken)
            {
                EditorGUILayout.HelpBox("🔗 Please connect to GitHub first to enable publishing.", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("📋 Setup verification in progress...", MessageType.Info);
            }

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Complete Setup", GUILayout.Height(30)))
            {
                OnRequestTabSwitch?.Invoke(0); // Switch to Setup tab
            }
        }

        private void DrawReadyToPublish()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Load repository options if not loaded
            if (!optionsLoaded && !loadingOptions)
            {
                EditorGUILayout.LabelField("Analyzing your public GitHub repositories for Unreality3D content...", EditorStyles.centeredGreyMiniLabel);
                loadingOptions = true;
                _ = LoadRepositoryOptionsAsync();
            }
            else if (loadingOptions)
            {
                EditorGUILayout.LabelField("🔍 Finding your public Unreality3D GitHub repositories...", EditorStyles.centeredGreyMiniLabel);
            }
            else if (optionsLoaded)
            {
                DrawRepositoryOptions();
            }

            EditorGUILayout.EndVertical();
        }

        private async Task LoadRepositoryOptionsAsync()
        {
            try
            {
                availableOptions.Clear();

                var repoResult = await GitHubAPI.GetUserRepositories("", 100);

                if (!repoResult.Success)
                {
                    Debug.LogError($"Failed to load repositories: {repoResult.ErrorMessage}");
                    optionsLoaded = true;
                    loadingOptions = false;
                    return;
                }

                // Clean processing without spam
                foreach (var repo in repoResult.Repositories)
                {
                    repo.IsUnreality3DProject = await GitHubAPI.WasCreatedWithUnreality3D(repo.Name);

                    if (repo.IsUnreality3DProject)
                    {
                        if (repo.HasPages)
                        {
                            repo.GitHubPagesUrl = await GitHubAPI.GetGitHubPagesUrl(repo.Name);
                        }

                        availableOptions.Add(new ProjectOption
                        {
                            Type = ProjectOption.OptionType.UpdateExisting,
                            RepositoryName = repo.Name,
                            DisplayName = $"Update \"{repo.Name}\"",
                            Description = "Unreality3D project",
                            ProfessionalUrl = U3DUrlUtility.ProfessionalUrl(U3DAuthenticator.CreatorUsername, repo.Name),
                            GitHubPagesUrl = repo.GitHubPagesUrl,
                            LastUpdated = repo.UpdatedAt,
                            IsUnreality3DProject = repo.IsUnreality3DProject
                        });
                    }
                }

                // Always add "Create New Repository" option
                availableOptions.Add(new ProjectOption
                {
                    Type = ProjectOption.OptionType.CreateNew,
                    RepositoryName = "new-repository",
                    DisplayName = "Create New Repository",
                    Description = "New Unreality3D project",
                    ProfessionalUrl = U3DUrlUtility.CreatorUrl(U3DAuthenticator.CreatorUsername) + "[product-name]/",
                    GitHubPagesUrl = null,
                    LastUpdated = null,
                    IsUnreality3DProject = false
                });

                selectedOptionIndex = DetermineDefaultSelection();
                previousSelectedOptionIndex = selectedOptionIndex; // Initialize tracking
                optionsLoaded = true;
                loadingOptions = false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error loading repository options: {ex.Message}");
                optionsLoaded = true;
                loadingOptions = false;
            }
        }

        // Smart default selection logic
        private int DetermineDefaultSelection()
        {
            if (availableOptions.Count == 0)
                return -1;

            var sanitizedCurrentProduct = GitHubAPI.SanitizeRepositoryName(cachedProductName);

            // Look for exact match with current Product Name
            for (int i = 0; i < availableOptions.Count; i++)
            {
                var option = availableOptions[i];

                if (option.Type == ProjectOption.OptionType.UpdateExisting &&
                    string.Equals(option.RepositoryName, sanitizedCurrentProduct, StringComparison.OrdinalIgnoreCase))
                {
                    return i; // Found matching repository
                }
            }

            // Default to "Create New Repository" (last option)
            return availableOptions.Count - 1;
        }

        /// <summary>
        /// NEW METHOD: Handles Product Name synchronization when repository selection changes
        /// </summary>
        private void HandleRepositorySelectionChange(int newSelectedIndex)
        {
            if (newSelectedIndex < 0 || newSelectedIndex >= availableOptions.Count)
                return;

            var selectedOption = availableOptions[newSelectedIndex];

            // Only sync Product Name for existing repositories (not "Create New")
            if (selectedOption.Type == ProjectOption.OptionType.UpdateExisting)
            {
                // Get the repository name (this is what should match Product Name)
                var repositoryName = selectedOption.RepositoryName;

                // Convert repository name back to a proper Product Name format
                var suggestedProductName = ConvertRepositoryNameToProductName(repositoryName);

                // Only update if it's actually different to avoid unnecessary changes
                if (!string.Equals(cachedProductName, suggestedProductName, StringComparison.OrdinalIgnoreCase))
                {
                    // Update Unity's PlayerSettings and our cached value immediately
                    // This ensures deployment pipeline gets correct Product Name
                    PlayerSettings.productName = suggestedProductName;
                    cachedProductName = suggestedProductName;
                }
            }

            // Update tracking variable
            previousSelectedOptionIndex = newSelectedIndex;
        }

        /// <summary>
        /// NEW METHOD: Converts repository name back to a user-friendly Product Name
        /// Reverses the GitHubAPI.SanitizeRepositoryName() process where possible
        /// </summary>
        private string ConvertRepositoryNameToProductName(string repositoryName)
        {
            if (string.IsNullOrEmpty(repositoryName))
                return repositoryName;

            // Convert hyphens back to spaces and title case the result
            var productName = repositoryName.Replace("-", " ");

            // Simple title case: capitalize first letter of each word
            var words = productName.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length > 0)
                {
                    words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
                }
            }

            return string.Join(" ", words);
        }

        //Simple method to check if thumbnail.jpg exists in repository root
        private async Task<bool> CheckRepositoryHasThumbnail(string repositoryName)
        {
            try
            {
                // Use your existing GitHubAPI pattern
                if (!GitHubTokenManager.HasValidToken)
                {
                    return false;
                }

                using (var client = GitHubAPI.CreateAuthenticatedClient()) // Use existing method
                {
                    var url = $"https://api.github.com/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/contents/thumbnail.jpg";
                    var response = await client.GetAsync(url);

                    return response.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error checking thumbnail in {repositoryName}: {ex.Message}");
                return false;
            }
        }

        //Cache thumbnail check results to avoid repeated API calls
        private async Task CheckAndCacheThumbnail(string repositoryName)
        {
            try
            {
                bool hasThumbnail = await CheckRepositoryHasThumbnail(repositoryName);
                thumbnailCheckCache[repositoryName] = hasThumbnail;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error caching thumbnail check for {repositoryName}: {ex.Message}");
                thumbnailCheckCache[repositoryName] = false;
            }
        }

        /// <summary>
        /// Draws the scene confirmation section showing the active scene and toggles for
        /// other enabled scenes. This never modifies EditorBuildSettings — toggles only
        /// affect which scenes are passed to BuildWebGL for this specific build.
        /// </summary>
        private void DrawSceneConfirmation()
        {
            // Refresh in case the creator switched scenes since last draw
            var currentActiveScene = SceneManager.GetActiveScene();
            if (currentActiveScene.path != activeScenePath)
            {
                RefreshBuildSceneList();
            }

            EditorGUILayout.Space(5);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var headerStyle = new GUIStyle(EditorStyles.boldLabel);
            headerStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.3f, 0.7f, 1f)
                : new Color(0.1f, 0.35f, 0.6f);
            EditorGUILayout.LabelField("🎬 Build Scenes", headerStyle);

            // Active scene — always included, shown prominently
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.Toggle(true, GUILayout.Width(20));
            EditorGUI.EndDisabledGroup();

            var activeStyle = new GUIStyle(EditorStyles.boldLabel);
            activeStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? Color.green
                : new Color(0f, 0.5f, 0f);

            if (!string.IsNullOrEmpty(activeSceneName))
            {
                EditorGUILayout.LabelField($"{activeSceneName}  (active scene — entry point)", activeStyle);
            }
            else
            {
                EditorGUILayout.LabelField("No scene open", EditorStyles.boldLabel);
            }
            EditorGUILayout.EndHorizontal();

            // Other enabled scenes with toggles
            var otherScenes = buildSceneToggles.Keys
                .Where(path => path != activeScenePath)
                .OrderBy(path => path)
                .ToList();

            if (otherScenes.Count > 0)
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Other enabled scenes in Build Settings:", EditorStyles.miniLabel);

                foreach (var scenePath in otherScenes)
                {
                    EditorGUILayout.BeginHorizontal();
                    var sceneName = Path.GetFileNameWithoutExtension(scenePath);
                    bool included = buildSceneToggles[scenePath];
                    bool newValue = EditorGUILayout.Toggle(included, GUILayout.Width(20));
                    if (newValue != included)
                    {
                        buildSceneToggles[scenePath] = newValue;
                    }
                    EditorGUILayout.LabelField(sceneName, EditorStyles.label);
                    EditorGUILayout.EndHorizontal();
                }
            }

            // Warning if the active scene wasn't in Build Settings
            bool activeSceneInBuildSettings = EditorBuildSettings.scenes
                .Any(s => s.enabled && s.path == activeScenePath);
            if (!activeSceneInBuildSettings && !string.IsNullOrEmpty(activeScenePath))
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.HelpBox(
                    $"💡 \"{activeSceneName}\" is not in your Build Settings. It will be included automatically for this build.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawRepositoryOptions()
        {
            if (availableOptions.Count == 0)
            {
                EditorGUILayout.HelpBox("No GitHub repositories found. A new repository will be created.", MessageType.Info);

                // Show scene confirmation even with no repos
                DrawSceneConfirmation();

                EditorGUILayout.Space(5);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Make It Live!", GUILayout.Height(50)))
                {
                    shouldCreateNewRepository = true;
                    _ = StartFirebasePublishProcess(GitHubAPI.SanitizeRepositoryName(cachedProductName));
                }
                if (GUILayout.Button("🔄 Refresh", GUILayout.Height(50), GUILayout.Width(80)))
                {
                    optionsLoaded = false;
                    loadingOptions = false;
                    availableOptions.Clear();
                    selectedOptionIndex = -1;
                    thumbnailCheckCache.Clear();
                    lastSelectedRepositoryForThumbnailCheck = "";
                }
                EditorGUILayout.EndHorizontal();
                return;
            }

            EditorGUILayout.LabelField("Choose your publishing option:", EditorStyles.boldLabel);

            // Refresh button for repository list
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("🔄 Refresh Repos", EditorStyles.miniButton, GUILayout.Width(140)))
            {
                optionsLoaded = false;
                loadingOptions = false;
                availableOptions.Clear();
                selectedOptionIndex = -1;
                previousSelectedOptionIndex = -1;
                thumbnailCheckCache.Clear();
                lastSelectedRepositoryForThumbnailCheck = "";
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Check if current Product Name would create a repository that conflicts with existing ones
            var sanitizedCurrentProduct = GitHubAPI.SanitizeRepositoryName(cachedProductName);
            var matchingRepo = availableOptions.FirstOrDefault(opt =>
                opt.Type == ProjectOption.OptionType.UpdateExisting &&
                string.Equals(opt.RepositoryName, sanitizedCurrentProduct, StringComparison.OrdinalIgnoreCase));

            bool hasMatchingRepo = matchingRepo != null;

            // Show helpful context about current selection
            if (hasMatchingRepo)
            {
                EditorGUILayout.HelpBox(
                    $"💡 Your current Product Name \"{cachedProductName}\" matches the repository \"{matchingRepo.RepositoryName}\". " +
                    "The update option is selected by default, but you can choose 'Create New Repository' to make a separate project.",
                    MessageType.Info);
                EditorGUILayout.Space(5);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"📝 Your current Product Name \"{cachedProductName}\" will create a new repository. " +
                    "You can also choose to update any of your existing Unity projects below.",
                    MessageType.Info);
                EditorGUILayout.Space(5);
            }

            // Validation state computed once for use inside the loop
            bool canPublish = selectedOptionIndex >= 0;
            string validationError = null;
            ProjectOption selectedOption = null;

            if (selectedOptionIndex >= 0 && selectedOptionIndex < availableOptions.Count)
            {
                selectedOption = availableOptions[selectedOptionIndex];
            }

            if (canPublish && selectedOption != null)
            {
                if (selectedOption.Type == ProjectOption.OptionType.CreateNew)
                {
                    if (!ValidateProductName(cachedProductName, out validationError))
                    {
                        canPublish = false;
                    }
                    else
                    {
                        var hasConflictingRepo = availableOptions.Any(opt =>
                            opt.Type == ProjectOption.OptionType.UpdateExisting &&
                            string.Equals(opt.RepositoryName, GitHubAPI.SanitizeRepositoryName(cachedProductName), StringComparison.OrdinalIgnoreCase));

                        if (hasConflictingRepo)
                        {
                            validationError = $"Repository '{GitHubAPI.SanitizeRepositoryName(cachedProductName)}' already exists. Use the Update option above or change the Product Name.";
                            canPublish = false;
                        }
                    }
                }
                else if (selectedOption.Type == ProjectOption.OptionType.UpdateExisting)
                {
                    canPublish = true;
                }
            }

            // Also block publish if no scenes are selected
            if (canPublish && GetSelectedScenePaths().Length == 0)
            {
                canPublish = false;
                validationError = "No scenes selected for build. At least one scene must be included.";
            }

            // Draw radio button options
            for (int i = 0; i < availableOptions.Count; i++)
            {
                var option = availableOptions[i];
                var isSelected = selectedOptionIndex == i;

                EditorGUILayout.BeginVertical(isSelected ? EditorStyles.helpBox : EditorStyles.textArea);

                EditorGUILayout.BeginHorizontal();

                // Visual-only checkbox indicator (not the click target)
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Toggle(isSelected, GUILayout.Width(20));
                EditorGUI.EndDisabledGroup();

                // Option details
                EditorGUILayout.BeginVertical();

                var style = new GUIStyle(EditorStyles.boldLabel);

                // Color logic with enhanced matching indication
                // Colors darken in light mode for readability on light backgrounds
                bool isDark = EditorGUIUtility.isProSkin;
                if (option.Type == ProjectOption.OptionType.CreateNew)
                {
                    style.normal.textColor = isDark ? Color.yellow : new Color(0.6f, 0.45f, 0f);
                }
                else if (hasMatchingRepo && option.RepositoryName == matchingRepo.RepositoryName)
                {
                    style.normal.textColor = isDark ? Color.cyan : new Color(0f, 0.45f, 0.55f);
                }
                else
                {
                    style.normal.textColor = isDark ? Color.green : new Color(0f, 0.5f, 0f);
                }

                // Display text with enhanced matching indication
                string displayText;
                if (option.Type == ProjectOption.OptionType.UpdateExisting)
                {
                    displayText = option.DisplayName;
                    if (hasMatchingRepo && option.RepositoryName == matchingRepo.RepositoryName)
                    {
                        displayText += " (matches current Product Name)";
                    }
                }
                else
                {
                    displayText = "Create New Repository";
                }

                EditorGUILayout.LabelField(displayText, style);
                EditorGUILayout.LabelField(option.Description, EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"URL: {option.ProfessionalUrl}", EditorStyles.miniLabel);

                if (option.LastUpdated.HasValue)
                {
                    EditorGUILayout.LabelField($"Last updated: {option.LastUpdated.Value:MMM dd, yyyy}", EditorStyles.miniLabel);
                }

                if (!string.IsNullOrEmpty(option.GitHubPagesUrl))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Currently live at:", EditorStyles.miniLabel, GUILayout.Width(90));
                    if (GUILayout.Button(option.GitHubPagesUrl, EditorStyles.linkLabel))
                    {
                        Application.OpenURL(option.GitHubPagesUrl);
                    }
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("Thumbnail:", EditorStyles.miniLabel, GUILayout.Width(90));

                    // Only check thumbnail when this repository is selected
                    if (isSelected)
                    {
                        // Check if we need to update the cache for this repository
                        if (lastSelectedRepositoryForThumbnailCheck != option.RepositoryName)
                        {
                            lastSelectedRepositoryForThumbnailCheck = option.RepositoryName;
                            // Start async check and cache result
                            _ = CheckAndCacheThumbnail(option.RepositoryName);
                        }

                        // Display cached result or loading state
                        if (thumbnailCheckCache.TryGetValue(option.RepositoryName, out bool hasThumbnail))
                        {
                            if (hasThumbnail)
                            {
                                EditorGUILayout.LabelField("✅ Found", EditorStyles.miniLabel);
                                if (GUILayout.Button("View", EditorStyles.miniButton, GUILayout.Width(50)))
                                {
                                    var thumbnailUrl = $"{option.GitHubPagesUrl.TrimEnd('/')}/thumbnail.jpg";
                                    Application.OpenURL(thumbnailUrl);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField("📷 None", EditorStyles.miniLabel);
                            }
                        }
                        else
                        {
                            EditorGUILayout.LabelField("Checking...", EditorStyles.miniLabel);
                        }
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Select to check", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // Special handling for "Create New Repository" option
                if (option.Type == ProjectOption.OptionType.CreateNew && option.RepositoryName == "new-repository")
                {
                    EditorGUILayout.Space(5);

                    // Show editable Product Name field with Update button
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    EditorGUILayout.LabelField("Product Name:", EditorStyles.miniLabel, GUILayout.Width(80));

                    // Only allow editing when "Create New Repository" is selected
                    bool isCreateNewSelected = selectedOptionIndex == i;

                    GUI.enabled = isCreateNewSelected;
                    EditorGUI.BeginChangeCheck();
                    var newProductName = EditorGUILayout.TextField(cachedProductName);
                    bool nameChangedInField = EditorGUI.EndChangeCheck();
                    GUI.enabled = true;

                    // Update button logic - enabled when field differs from PlayerSettings AND Create New is selected
                    bool namesDiffer = !string.IsNullOrEmpty(newProductName) && newProductName != PlayerSettings.productName;

                    GUI.enabled = namesDiffer && isCreateNewSelected;
                    if (GUILayout.Button("Update", GUILayout.Width(60)))
                    {
                        // Update Unity's PlayerSettings
                        PlayerSettings.productName = newProductName;
                        cachedProductName = newProductName;

                        // Recalculate default selection based on new Product Name
                        var previousSelection = selectedOptionIndex;
                        selectedOptionIndex = DetermineDefaultSelection();

                        // UX IMPROVEMENT: If selection changed to an existing repo, show warning and scroll behavior
                        if (selectedOptionIndex != previousSelection && selectedOptionIndex < availableOptions.Count - 1)
                        {
                            var matchedRepo = availableOptions[selectedOptionIndex];
                            EditorUtility.DisplayDialog("Existing Repository Found",
                                $"Your Product Name '{newProductName}' matches an existing Unreality3D project '{matchedRepo.RepositoryName}'.\n\n" +
                                "The matching repository has been automatically selected above. If you want to create a new repository instead, " +
                                "choose a different Product Name.",
                                "OK");
                        }
                    }
                    GUI.enabled = true;

                    // Update cached name for real-time feedback
                    if (nameChangedInField && isCreateNewSelected)
                    {
                        cachedProductName = newProductName;
                    }

                    EditorGUILayout.EndHorizontal();

                    // Show sync status for existing repositories
                    if (!isCreateNewSelected)
                    {
                        EditorGUILayout.LabelField("⚠️ Product Name synced to selected repository above", EditorStyles.miniLabel);
                    }
                    else
                    {
                        // Smart repository name preview
                        var wouldCreateRepo = GitHubAPI.SanitizeRepositoryName(cachedProductName);
                        var wouldConflict = availableOptions.Any(opt =>
                            opt.Type == ProjectOption.OptionType.UpdateExisting &&
                            string.Equals(opt.RepositoryName, wouldCreateRepo, StringComparison.OrdinalIgnoreCase));

                        if (wouldConflict)
                        {
                            EditorGUILayout.LabelField($"⚠️ Repository '{wouldCreateRepo}' already exists above", EditorStyles.miniLabel);
                        }
                        else
                        {
                            EditorGUILayout.LabelField($"Will create repository: {wouldCreateRepo}", EditorStyles.miniLabel);
                        }
                    }
                    EditorGUILayout.Space(5);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                    var thumbnailHeaderStyle = new GUIStyle(EditorStyles.boldLabel);
                    thumbnailHeaderStyle.normal.textColor = EditorGUIUtility.isProSkin
                        ? new Color(0.3f, 0.7f, 1f)
                        : new Color(0.1f, 0.35f, 0.6f);
                    EditorGUILayout.LabelField("📷 Optional: Add a Thumbnail", thumbnailHeaderStyle);

                    EditorGUILayout.LabelField("If you want to include a thumbnail for unreality3d.com to display:", EditorStyles.wordWrappedLabel);
                    EditorGUILayout.Space(3);

                    EditorGUILayout.LabelField("• Save your image as 'thumbnail.jpg' in Assets/_MyAssets folder", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("• Recommended: 480×270 pixels (16:9 ratio), under 50KB", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField("• This will be included automatically when you publish", EditorStyles.miniLabel);

                    EditorGUILayout.Space(5);

                    // Check if thumbnail.jpg already exists in Assets/_MyAssets
                    var projectThumbnailPath = Path.Combine(Application.dataPath, "_MyAssets", "thumbnail.jpg");
                    if (File.Exists(projectThumbnailPath))
                    {
                        var successStyle = new GUIStyle(EditorStyles.miniLabel);
                        successStyle.normal.textColor = EditorGUIUtility.isProSkin
                            ? Color.green
                            : new Color(0f, 0.5f, 0f);
                        EditorGUILayout.LabelField("✅ Found: thumbnail.jpg in Assets/_MyAssets", successStyle);

                        if (GUILayout.Button("View Current Thumbnail", GUILayout.Height(25)))
                        {
                            EditorUtility.RevealInFinder(projectThumbnailPath);
                        }
                    }
                    else
                    {
                        // Let Unity's default miniLabel handle theming
                        EditorGUILayout.LabelField("💡 No thumbnail.jpg found in Assets/_MyAssets", EditorStyles.miniLabel);

                        if (GUILayout.Button("Open _MyAssets Folder", GUILayout.Height(25)))
                        {
                            var myAssetsPath = Path.Combine(Application.dataPath, "_MyAssets");
                            if (!Directory.Exists(myAssetsPath))
                            {
                                Directory.CreateDirectory(myAssetsPath);
                            }
                            EditorUtility.RevealInFinder(myAssetsPath);
                        }
                    }

                    EditorGUILayout.Space(3);
                    EditorGUILayout.LabelField("You can always add or update thumbnails later through your unreality3d.com/creator-dashboard.html page",
                        EditorStyles.wordWrappedMiniLabel);

                    EditorGUILayout.EndVertical();
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                // Scene confirmation + Make It Live button — only under the selected option
                if (isSelected)
                {
                    DrawSceneConfirmation();

                    EditorGUILayout.Space(5);

                    // Validation error shown right above the button
                    if (!canPublish && !string.IsNullOrEmpty(validationError))
                    {
                        EditorGUILayout.HelpBox($"⚠️ {validationError}", MessageType.Warning);
                    }

                    GUI.enabled = canPublish;
                    if (GUILayout.Button("Make It Live!", GUILayout.Height(50)))
                    {
                        if (selectedOption.Type == ProjectOption.OptionType.CreateNew)
                        {
                            shouldCreateNewRepository = true;
                            _ = StartFirebasePublishProcess(GitHubAPI.SanitizeRepositoryName(cachedProductName));
                        }
                        else
                        {
                            var targetRepositoryName = selectedOption.RepositoryName;
                            shouldCreateNewRepository = false;

                            if (EditorUtility.DisplayDialog("Confirm Repository Update",
                                $"You are about to update the existing repository '{targetRepositoryName}'.\n\n" +
                                "This will overwrite the current content with your new build.\n\n" +
                                "Are you sure you want to continue?",
                                "Yes, Update", "Cancel"))
                            {
                                _ = StartFirebasePublishProcess(targetRepositoryName);
                            }
                        }
                    }
                    GUI.enabled = true;
                }

                EditorGUILayout.EndVertical();

                // Make entire row clickable for selection
                var rowRect = GUILayoutUtility.GetLastRect();
                if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
                {
                    if (selectedOptionIndex != i)
                    {
                        selectedOptionIndex = i;
                        HandleRepositorySelectionChange(i);
                    }
                    Event.current.Use();
                }

                // Show pointer cursor on hover to indicate clickability
                EditorGUIUtility.AddCursorRect(rowRect, MouseCursor.Link);

                EditorGUILayout.Space(3);
            }
        }

        private bool IsRelatedRepository(string repoName, string productName)
        {
            var sanitizedProduct = GitHubAPI.SanitizeRepositoryName(productName);
            var lowerRepo = repoName.ToLower();
            var lowerProduct = sanitizedProduct.ToLower();

            // Exact match
            if (lowerRepo == lowerProduct)
                return true;

            // Product name is contained in repo name
            if (lowerRepo.Contains(lowerProduct))
                return true;

            return false;
        }

        // Visual feedback when Product Name matches existing repo
        private void HandleProductNameMatchFeedback()
        {
            var sanitizedCurrentProduct = GitHubAPI.SanitizeRepositoryName(cachedProductName);
            var matchingRepoIndex = -1;

            for (int i = 0; i < availableOptions.Count; i++)
            {
                var option = availableOptions[i];
                if (option.Type == ProjectOption.OptionType.UpdateExisting &&
                    string.Equals(option.RepositoryName, sanitizedCurrentProduct, StringComparison.OrdinalIgnoreCase))
                {
                    matchingRepoIndex = i;
                    break;
                }
            }

            // If we found a match and it's not currently selected, provide feedback
            if (matchingRepoIndex >= 0 && selectedOptionIndex != matchingRepoIndex)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(
                    $"💡 Note: Your Product Name '{cachedProductName}' matches the repository '{availableOptions[matchingRepoIndex].RepositoryName}' shown above. " +
                    "You may want to select that option to update your existing project, or change the Product Name to create something new.",
                    MessageType.Info);
            }
        }

        private void DrawPublishingSteps()
        {
            EditorGUILayout.LabelField("Publishing Progress", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            DrawStep("Build",
                projectBuilt,
                currentStep == PublishStep.BuildingLocally,
                "✓ Project built",
                "🔨 Building your project...");

            DrawStep("Upload",
                deploymentComplete,
                currentStep == PublishStep.DeployingToGitHub,
                "✓ Project uploaded",
                "📤 Uploading your project...");

            DrawStep("Go Live",
                githubActionsComplete,
                currentStep == PublishStep.WaitingForGitHub,
                "✓ Your URL is live!",
                "🌐 Finalizing — your URL goes live in 1–2 minutes...");
        }

        private void DrawStep(string stepName, bool isComplete, bool isActive, string completeMessage, string activeMessage)
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            if (isComplete)
            {
                EditorGUILayout.LabelField("🟢", GUILayout.Width(25));
                EditorGUILayout.LabelField(completeMessage, EditorStyles.boldLabel);
            }
            else if (isActive)
            {
                EditorGUILayout.LabelField("⏳", GUILayout.Width(25));
                EditorGUILayout.LabelField(activeMessage);
            }
            else
            {
                EditorGUILayout.LabelField("⏸️", GUILayout.Width(25));
                EditorGUILayout.LabelField($"Waiting: {stepName}");
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(3);
        }

        /// <summary>
        /// Runs a publish to one repository. The name is chosen once, by the button that was
        /// pressed, and handed to both the publish walk and the deploy, so the room name the
        /// build carries and the repository it deploys to are the same value.
        /// </summary>
        /// <summary>
        /// Runs a publish to one repository. The name is chosen once, by the button that was
        /// pressed, and handed to both the publish walk and the deploy, so the room name the
        /// build carries and the repository it deploys to are the same value.
        /// </summary>
        private async System.Threading.Tasks.Task StartFirebasePublishProcess(string targetRepositoryName)
        {
            if (!NetPublishWalk.Run(GetSelectedScenePaths(), U3DAuthenticator.CreatorUsername, targetRepositoryName))
            {
                return;
            }

            isPublishing = true;
            githubActionsComplete = false;
            githubActionsRunHtmlUrl = "";

            try
            {
                // Validate authentication BEFORE starting any deployment operations
                currentStatus = "Preparing authentication for deployment...";
                bool authReady = await U3DAuthenticator.PrepareForDeployment();

                if (!authReady)
                {
                    throw new System.Exception("Authentication preparation failed. Please log out and log back in, then try again.");
                }

                currentStatus = "Authentication validated - proceeding with deployment";

                // Step 1: Build Unity WebGL locally
                currentStep = PublishStep.BuildingLocally;
                currentStatus = "Building your project...";

                var buildResult = await BuildUnityProjectLocally();
                if (!buildResult.Success)
                {
                    throw new System.Exception(buildResult.ErrorMessage);
                }

                projectBuilt = true;
                currentStatus = "Project built successfully";

                // Re-validate authentication before the deployment step
                // (Build process can take 30+ minutes, token might expire)
                currentStatus = "Re-validating authentication for deployment...";
                authReady = await U3DAuthenticator.PrepareForDeployment();

                if (!authReady)
                {
                    throw new System.Exception("Authentication expired during build. Please log out and log back in, then try deploying again.");
                }

                // Step 2: Upload + push to GitHub via Firebase Cloud Functions.
                // After this step returns success, the build IS pushed to GitHub
                // but GitHub Actions still has to assemble and publish to Pages.
                currentStep = PublishStep.DeployingToGitHub;
                currentStatus = "Uploading your project...";

                // Capture the moment we kicked off the deploy. We use this as a
                // floor when searching GitHub Actions for the workflow run we
                // just triggered, so we don't accidentally match an older run.
                // Only used if the function didn't return a pre-resolved run ID.
                var publishStartUtc = DateTime.UtcNow.AddSeconds(-30); // small buffer for clock skew

                var deployResult = await DeployViaFirebaseStorage(buildResult.BuildPath, targetRepositoryName);
                if (!deployResult.Success)
                {
                    throw new System.Exception(deployResult.ErrorMessage);
                }

                deploymentComplete = true;

                var creatorUsername = U3DAuthenticator.CreatorUsername;
                var repositoryName = deployResult.RepositoryName ?? deployResult.ProjectName ?? targetRepositoryName;
                var successUrl = deployResult.ProfessionalUrl ?? U3DUrlUtility.ProfessionalUrl(creatorUsername, repositoryName);

                // Step 3: Wait for GitHub Actions to finish so we only show
                // "Live!" when the URL is actually serving the new build.
                // If the function pre-resolved the run ID for us, pass it through
                // so we can poll that exact run and skip Stage A search.
                currentStep = PublishStep.WaitingForGitHub;
                currentStatus = "Project uploaded — waiting for GitHub to finalize your deployment...";

                Debug.Log($"[U3D Publish] About to wait for GitHub Actions. knownRunId={deployResult.GitHubActionsRunId}, knownRunHtmlUrl={deployResult.GitHubActionsRunHtmlUrl}");

                var actionsResult = await WaitForGitHubActionsCompletion(
                    GitHubTokenManager.GitHubUsername,
                    repositoryName,
                    publishStartUtc,
                    deployResult.GitHubActionsRunId,
                    deployResult.GitHubActionsRunHtmlUrl);

                Debug.Log($"[U3D Publish] WaitForGitHubActionsCompletion returned. Success={actionsResult.Success}, error={actionsResult.ErrorMessage}");

                if (!actionsResult.Success)
                {
                    githubActionsRunHtmlUrl = actionsResult.RunHtmlUrl;
                    throw new System.Exception(actionsResult.ErrorMessage);
                }

                githubActionsRunHtmlUrl = actionsResult.RunHtmlUrl;
                MarkPublishSuccess(successUrl, repositoryName);
                currentStatus = "Your URL is live!";
                ShowDeploymentSummary(repositoryName);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Publishing failed: {ex.Message}");

                // 🆕 ENHANCED ERROR HANDLING: Provide specific guidance for authentication errors
                string userMessage = ex.Message;
                if (ex.Message.Contains("Authentication") || ex.Message.Contains("unauthenticated") || ex.Message.Contains("unauthorized"))
                {
                    userMessage = "Authentication error occurred during deployment.\n\n" +
                                 "This happens when your login session expires during the build process.\n\n" +
                                 "To fix this:\n" +
                                 "1. Log out from the Unreality3D Creator Dashboard\n" +
                                 "2. Clear the Unity Console\n" +
                                 "3. Close and reopen this Unity project\n" +
                                 "4. Log back in\n" +
                                 "5. Try publishing again\n\n" +
                                 "Technical details: " + ex.Message;
                }

                // If the failure was specifically the GitHub Actions wait, offer
                // the link to the Actions tab so the creator can check status.
                if (currentStep == PublishStep.WaitingForGitHub && !string.IsNullOrEmpty(githubActionsRunHtmlUrl))
                {
                    if (EditorUtility.DisplayDialog("Deployment Status Unclear",
                        userMessage + "\n\nWould you like to open the GitHub Actions page to check the status yourself?",
                        "Open Actions Page", "Close"))
                    {
                        Application.OpenURL(githubActionsRunHtmlUrl);
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Publishing Failed", userMessage, "OK");
                }

                currentStep = PublishStep.Ready;
                currentStatus = $"Publishing failed: {ex.Message}";
                githubConnected = false;
                projectBuilt = false;
                deploymentComplete = false;
                githubActionsComplete = false;
            }
            finally
            {
                isPublishing = false;
            }
        }

        private async Task<UnityBuildResult> BuildUnityProjectLocally()
        {
            try
            {
                if (!UnityBuildHelper.ValidateBuildRequirements())
                {
                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = "Build requirements not met. Please check the Console for details."
                    };
                }

                var buildPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), "WebGL");
                currentStatus = $"Building to: {buildPath}";

                // Pass the creator's scene selection to BuildWebGL
                var scenePaths = GetSelectedScenePaths();

                var buildResult = await UnityBuildHelper.BuildWebGL(buildPath, (status) =>
                {
                    currentStatus = status;
                }, scenePaths);

                //Copy thumbnail from Assets/_MyAssets to build output if it exists
                if (buildResult.Success)
                {
                    CopyThumbnailToBuildOutput(buildPath);
                }

                return buildResult;
            }
            catch (System.Exception ex)
            {
                return new UnityBuildResult
                {
                    Success = false,
                    ErrorMessage = $"Local build failed: {ex.Message}"
                };
            }
        }

        //Copy thumbnail from Unity project to build output
        private void CopyThumbnailToBuildOutput(string buildPath)
        {
            try
            {
                var sourceThumbnailPath = Path.Combine(Application.dataPath, "_MyAssets", "thumbnail.jpg");
                var destThumbnailPath = Path.Combine(buildPath, "thumbnail.jpg");

                if (File.Exists(sourceThumbnailPath))
                {
                    File.Copy(sourceThumbnailPath, destThumbnailPath, true);
                }
                else
                {
                    Debug.Log("💡 No thumbnail found in Assets/_MyAssets - skipping thumbnail copy");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"⚠️ Could not copy thumbnail to build output: {ex.Message}");
            }
        }

        private async Task<FirebaseDeployResult> DeployViaFirebaseStorage(string buildPath, string targetRepositoryName)
        {
            try
            {
                var deploymentIntent = shouldCreateNewRepository ? "create_new" : "update_existing";
                currentStatus = "Uploading build to Firebase Storage...";

                var storageBucket = FirebaseConfigManager.CurrentConfig?.storageBucket ?? "unreality3d.firebasestorage.app";
                if (string.IsNullOrEmpty(storageBucket) || storageBucket == "setup-required")
                {
                    storageBucket = "unreality3d.firebasestorage.app";
                }

                var uploader = new FirebaseStorageUploader(storageBucket, U3DAuthenticator.GetIdToken());

                // === CHANGED: Pass the raw, case-preserving Product Name to the
                // === server as productDisplayName. cachedProductName is the
                // === canonical reference in this file's logic and equals
                // === PlayerSettings.productName / Application.productName at
                // === publish time.
                var result = await uploader.UploadBuildToStorageWithIntent(
                    buildPath,
                    U3DAuthenticator.CreatorUsername,
                    targetRepositoryName,
                    deploymentIntent,
                    cachedProductName
                );

                uploader.Dispose();

                if (result.Success)
                {
                    var actualRepositoryName = result.ActualProjectName ?? targetRepositoryName;

                    if (!ShouldSkipDuringBuild())
                    {
                        EditorPrefs.SetString("U3D_LastRepositoryName", actualRepositoryName);
                    }

                    shouldCreateNewRepository = false;

                    return new FirebaseDeployResult
                    {
                        Success = true,
                        RepositoryName = actualRepositoryName,
                        ProjectName = actualRepositoryName,
                        ProfessionalUrl = U3DUrlUtility.ProfessionalUrl(U3DAuthenticator.CreatorUsername, actualRepositoryName),
                        Message = "Deployment successful via Firebase Storage",
                        GitHubActionsRunId = result.GitHubActionsRunId,
                        GitHubActionsRunHtmlUrl = result.GitHubActionsRunHtmlUrl
                    };
                }
                else
                {
                    return new FirebaseDeployResult
                    {
                        Success = false,
                        ErrorMessage = result.ErrorMessage ?? "Firebase Storage upload failed"
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Firebase Storage deployment error: {ex.Message}");
                return new FirebaseDeployResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static async Task<Dictionary<string, object>> CallFirebaseFunction(string functionName, Dictionary<string, object> data)
        {
            // Use reflection to access the private method
            var method = typeof(U3DAuthenticator).GetMethod("CallFirebaseFunction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (method == null)
            {
                throw new Exception("CallFirebaseFunction method not found in U3DAuthenticator");
            }

            var task = method.Invoke(null, new object[] { functionName, data }) as Task<Dictionary<string, object>>;
            return await task;
        }

        [System.Serializable]
        public class FirebaseDeployResult
        {
            public bool Success { get; set; }
            public string RepositoryName { get; set; }
            public string ProjectName { get; set; }
            public string ProfessionalUrl { get; set; }
            public string Message { get; set; }
            public string ErrorMessage { get; set; }
            // Pre-resolved GitHub Actions run info from deployFromStorage.
            // When present, Unity polls this run directly and skips Stage A
            // search entirely — no time-floor heuristics, no risk of latching
            // onto a stale prior run. Null when the function couldn't resolve
            // the run, in which case Unity falls back to Stage A.
            public long? GitHubActionsRunId { get; set; }
            public string GitHubActionsRunHtmlUrl { get; set; }
        }

        // === Added credit line using U3DAuthenticator.DisplayName so the
        // === user's name feature is visible in the success dialog. Falls back
        // === to CreatorUsername if DisplayName is empty.
        private void ShowDeploymentSummary(string repositoryName)
        {
            var displayName = !string.IsNullOrEmpty(U3DAuthenticator.DisplayName)
                ? U3DAuthenticator.DisplayName
                : U3DAuthenticator.CreatorUsername;

            string summaryMessage = "🎉 Publishing completed successfully!\n\n";
            summaryMessage += $"🌐 Your URL: {publishUrl}\n\n";
            if (!string.IsNullOrEmpty(displayName))
            {
                summaryMessage += $"👤 Published by: {displayName}\n\n";
            }
            summaryMessage += "🔧 Build Configuration:\n";
            summaryMessage += "• Unity build: Local (using your Unity license)\n";
            summaryMessage += "• Repository: Creator-owned GitHub repository\n";
            summaryMessage += "• Hosting: GitHub Pages (unlimited bandwidth)\n";
            summaryMessage += "• Professional URL: Unreality3D routing\n";
            summaryMessage += "\n💡 Next steps:\n";
            summaryMessage += "• Your content is live and accessible\n";
            summaryMessage += "• Push changes to trigger new deployments\n";
            summaryMessage += "• Share your professional URL with anyone\n";

            EditorUtility.DisplayDialog("Publishing Success", summaryMessage, "Great!");
        }

        private void DrawSuccessSection()
        {
            EditorGUILayout.Space(20);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            var successStyle = new GUIStyle(EditorStyles.boldLabel);
            successStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? Color.green
                : new Color(0f, 0.5f, 0f);
            successStyle.fontSize = 16;

            EditorGUILayout.LabelField("🎉 Success! Your content is live!", successStyle);
            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Your URL:", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(publishUrl, EditorStyles.textField, GUILayout.Height(30));

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open in Browser", GUILayout.Height(35)))
            {
                Application.OpenURL(publishUrl);
            }

            if (GUILayout.Button("Copy URL", GUILayout.Height(35)))
            {
                EditorGUIUtility.systemCopyBuffer = publishUrl;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // Return to options view for updates
            if (GUILayout.Button("Update Repository", GUILayout.Height(35)))
            {
                // Reset to Ready state to show repository options again
                currentStep = PublishStep.Ready;
                deploymentComplete = false;
                projectBuilt = false;
                githubConnected = false;
                isPublishing = false;
                IsComplete = false;
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Share this link with anyone to let them play your creation!", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndVertical();
        }
        /// <summary>
        /// Result of waiting for the GitHub Actions workflow that finalizes the
        /// deployment to GitHub Pages. Success means the workflow completed
        /// successfully and the published URL should now serve the new build.
        /// </summary>
        private class GitHubActionsWaitResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; }
            public string RunHtmlUrl { get; set; }
        }

        /// <summary>
        /// Polls the GitHub Actions API for the chunk-reassembly workflow run
        /// that was just triggered by deployFromStorage, and waits for it to
        /// complete. The workflow's deploy job is what actually publishes the
        /// build to GitHub Pages, so we can only honestly report "live" once
        /// this finishes successfully.
        ///
        /// If knownRunId is provided (resolved server-side by deployFromStorage),
        /// Stage A is skipped entirely and we go straight to Stage B polling.
        /// This is the preferred path — no time-floor heuristics, no risk of
        /// latching onto a stale prior run.
        ///
        /// If knownRunId is null, Stage A falls back to listing recent runs and
        /// matching one that's currently active (queued or in_progress). A
        /// completed run we discover instantly is by definition a previous
        /// publish, so we never claim it.
        ///
        /// Stage A: find the run created at or after publishStartUtc that's
        ///          still actively running (up to ~60s).
        /// Stage B: wait for status == completed (up to total timeout).
        ///
        /// Total budget: GITHUB_ACTIONS_TOTAL_TIMEOUT_MS (8 minutes).
        /// </summary>
        private async Task<GitHubActionsWaitResult> WaitForGitHubActionsCompletion(
            string owner,
            string repo,
            DateTime publishStartUtc,
            long? knownRunId,
            string knownRunHtmlUrl)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitHubActionsWaitResult
                {
                    Success = false,
                    ErrorMessage = "GitHub token is not available — cannot check deployment status."
                };
            }

            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

            long? runId = knownRunId;
            string runHtmlUrl = knownRunHtmlUrl ?? "";

            // ---- Stage A: only runs if the server didn't pre-resolve the run ----
            if (!runId.HasValue)
            {
                currentStatus = "Project uploaded — looking for GitHub deployment...";

                var findRunStopwatch = System.Diagnostics.Stopwatch.StartNew();

                while (findRunStopwatch.ElapsedMilliseconds < GITHUB_ACTIONS_FIND_RUN_TIMEOUT_MS)
                {
                    try
                    {
                        using (var client = GitHubAPI.CreateAuthenticatedClient())
                        {
                            var url = $"https://api.github.com/repos/{owner}/{repo}/actions/workflows/{CHUNK_WORKFLOW_FILENAME}/runs?per_page=5&event=workflow_dispatch";
                            var response = await client.GetAsync(url);

                            if (response.IsSuccessStatusCode)
                            {
                                var responseText = await response.Content.ReadAsStringAsync();
                                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                                if (data.ContainsKey("workflow_runs") && data["workflow_runs"] != null)
                                {
                                    var runs = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(data["workflow_runs"].ToString());

                                    foreach (var run in runs)
                                    {
                                        if (!run.ContainsKey("created_at") || run["created_at"] == null)
                                            continue;

                                        var createdAtString = run["created_at"].ToString();
                                        if (!DateTime.TryParse(createdAtString, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime createdAt))
                                            continue;

                                        var createdAtUtc = createdAt.ToUniversalTime();
                                        if (createdAtUtc < publishStartUtc)
                                            continue;

                                        // Tightened filter: only claim a run that is still actively
                                        // running. A completed run we discover instantly is from a
                                        // previous publish, even if its created_at passes the time
                                        // floor (the time floor is fuzzy by ~30s).
                                        var status = run.ContainsKey("status") && run["status"] != null
                                            ? run["status"].ToString()
                                            : "";
                                        if (status != "queued" && status != "in_progress")
                                            continue;

                                        // Match — this is the run our deploy triggered.
                                        if (run.ContainsKey("id") && run["id"] != null)
                                        {
                                            runId = long.Parse(run["id"].ToString());
                                        }
                                        if (run.ContainsKey("html_url") && run["html_url"] != null)
                                        {
                                            runHtmlUrl = run["html_url"].ToString();
                                        }
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                Debug.LogWarning($"GitHub Actions API returned {response.StatusCode} while looking for workflow run.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Error querying GitHub Actions for workflow run: {ex.Message}");
                    }

                    if (runId.HasValue)
                        break;

                    await Task.Delay(GITHUB_ACTIONS_POLL_INTERVAL_MS);
                }

                if (!runId.HasValue)
                {
                    return new GitHubActionsWaitResult
                    {
                        Success = false,
                        ErrorMessage = "Could not find the GitHub deployment workflow run. Your build was uploaded successfully, but we couldn't confirm GitHub started the deployment."
                    };
                }
            }
            else
            {
                // Server pre-resolved the run for us — go straight to polling it.
                currentStatus = "GitHub is finalizing your deployment — this usually takes 1–2 minutes...";
            }

            // ---- Stage B: wait for the run to complete ----
            currentStatus = "GitHub is finalizing your deployment — this usually takes 1–2 minutes...";

            while (totalStopwatch.ElapsedMilliseconds < GITHUB_ACTIONS_TOTAL_TIMEOUT_MS)
            {
                try
                {
                    using (var client = GitHubAPI.CreateAuthenticatedClient())
                    {
                        var url = $"https://api.github.com/repos/{owner}/{repo}/actions/runs/{runId.Value}";
                        var response = await client.GetAsync(url);

                        if (response.IsSuccessStatusCode)
                        {
                            var responseText = await response.Content.ReadAsStringAsync();
                            var run = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                            var status = run.ContainsKey("status") && run["status"] != null
                                ? run["status"].ToString()
                                : "";
                            var conclusion = run.ContainsKey("conclusion") && run["conclusion"] != null
                                ? run["conclusion"].ToString()
                                : "";

                            // Capture the html_url here too in case Stage A didn't populate it
                            // (or in case the server-provided URL was null).
                            if (string.IsNullOrEmpty(runHtmlUrl) && run.ContainsKey("html_url") && run["html_url"] != null)
                            {
                                runHtmlUrl = run["html_url"].ToString();
                            }

                            // Update friendly status as the workflow progresses
                            if (status == "queued")
                            {
                                currentStatus = "GitHub deployment queued — starting shortly...";
                            }
                            else if (status == "in_progress")
                            {
                                currentStatus = "GitHub is finalizing your deployment — almost there...";
                            }

                            if (status == "completed")
                            {
                                if (conclusion == "success")
                                {
                                    return new GitHubActionsWaitResult
                                    {
                                        Success = true,
                                        RunHtmlUrl = runHtmlUrl
                                    };
                                }

                                // Completed with a non-success conclusion (failure, cancelled, timed_out, etc.)
                                return new GitHubActionsWaitResult
                                {
                                    Success = false,
                                    ErrorMessage = $"GitHub deployment finished with status \"{conclusion}\". Your build was uploaded but the final publish step did not succeed.",
                                    RunHtmlUrl = runHtmlUrl
                                };
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"GitHub Actions API returned {response.StatusCode} while polling run {runId.Value}.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Error polling GitHub Actions run: {ex.Message}");
                }

                await Task.Delay(GITHUB_ACTIONS_POLL_INTERVAL_MS);
            }

            // Total timeout exhausted
            return new GitHubActionsWaitResult
            {
                Success = false,
                ErrorMessage = "GitHub deployment is taking longer than expected. Your build was uploaded successfully, but we couldn't confirm it finished publishing within 8 minutes.",
                RunHtmlUrl = runHtmlUrl
            };
        }
    }

    [System.Serializable]
    public class ProjectOption
    {
        public enum OptionType
        {
            UpdateExisting,
            CreateNew
        }

        public OptionType Type { get; set; }
        public string RepositoryName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string ProfessionalUrl { get; set; }
        public string GitHubPagesUrl { get; set; }
        public DateTime? LastUpdated { get; set; }
        public bool IsUnreality3DProject { get; set; }
    }

    [System.Serializable]
    public class GitHubRepositoryCreationResult
    {
        public bool Success { get; set; }
        public string RepositoryName { get; set; }
        public string CloneUrl { get; set; }
        public string LocalPath { get; set; }
        public string ErrorMessage { get; set; }
    }
}
