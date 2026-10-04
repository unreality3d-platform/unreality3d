using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace U3D.Editor
{
    public class U3DCreatorWindow : EditorWindow
    {
        [SerializeField] private int selectedTab = 0;
        [SerializeField] private Vector2 scrollPosition;

        private List<ICreatorTab> tabs;
        private GUIStyle headerStyle;
        private GUIStyle tabButtonStyle;
        private GUIStyle activeTabButtonStyle;
        private bool stylesInitialized = false;
        private Texture2D logoTexture;

        // Index of the Project Tools tab in the tabs list. Update if tab order changes.
        private const int PROJECT_TOOLS_TAB_INDEX = 1;

        /// <summary>
        /// CRITICAL: Check if we should skip operations during actual builds (not editor startup)
        /// </summary>
        private static bool ShouldSkipDuringBuild()
        {
            // Only skip for actual build operations, not editor initialization
            return BuildPipeline.isBuildingPlayer ||
                   EditorApplication.isCompiling;
        }

        /// <summary>
        /// Check if editor is still initializing (separate from build operations)
        /// </summary>
        private static bool IsEditorInitializing()
        {
            return EditorApplication.isUpdating;
        }

        [MenuItem("U3D/Creator Dashboard")]
        public static void ShowWindow()
        {
            var window = GetWindow<U3DCreatorWindow>("U3D Creator Dashboard");
            window.minSize = new Vector2(320, 560);
            EnsureOnScreen(window);
            window.Show();
        }

        /// <summary>
        /// Unity restores floating window rects without checking them against the current
        /// display arrangement. A window last positioned on a disconnected second monitor
        /// stays off-screen and the menu item appears to do nothing. Pull it back if its
        /// saved rect does not overlap the main display.
        /// </summary>
        private static void EnsureOnScreen(EditorWindow window)
        {
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            Rect pos = window.position;

            if (pos.Overlaps(main)) return;

            float width = Mathf.Max(pos.width, 320f);
            float height = Mathf.Max(pos.height, 560f);

            window.position = new Rect(
                main.x + (main.width - width) * 0.5f,
                main.y + (main.height - height) * 0.5f,
                width,
                height);
        }

        /// <summary>
        /// Opens the Creator Dashboard, switches to the Project Tools tab, and pre-fills
        /// the search field with the provided term. Used by MissingScriptPlaceholder to
        /// route creators from cleanup placeholders to relevant tools.
        /// </summary>
        public static void OpenWithProjectToolsSearch(string searchTerm)
        {
            var window = GetWindow<U3DCreatorWindow>("U3D Creator Dashboard");
            window.minSize = new Vector2(320, 560);
            window.Show();
            window.Focus();

            window.selectedTab = PROJECT_TOOLS_TAB_INDEX;

            if (window.tabs != null && PROJECT_TOOLS_TAB_INDEX < window.tabs.Count)
            {
                var projectToolsTab = window.tabs[PROJECT_TOOLS_TAB_INDEX] as ProjectToolsTab;
                projectToolsTab?.SetSearchText(searchTerm);
            }

            window.Repaint();
        }

        [InitializeOnLoadMethod]
        static void OpenOnStartup()
        {
            // Single-shot registration - no recursion
            EditorApplication.delayCall += OnEditorReady;
        }

        static void OnEditorReady()
        {
            // Skip if build is in progress
            if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling)
                return;

            // Skip during play mode transitions
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            bool hasOpenedBefore = EditorPrefs.GetBool("U3D_HasOpenedBefore", false);
            bool showOnStartup = EditorPrefs.GetBool("U3D_ShowOnStartup", true);

            if (!hasOpenedBefore || showOnStartup)
            {
                ShowWindow();
                EditorPrefs.SetBool("U3D_HasOpenedBefore", true);
            }
        }

        void OnEnable()
        {
            InitializeTabs();
            LoadLogo();
            U3DTemplateUpdateChecker.CheckForUpdateIfNeeded();
            // DON'T initialize styles here - wait for OnGUI when Editor is ready
        }

        // Single repaint mechanism for the window. Unity fires this ~10x/sec while the
        // window is open, so async state changes in any tab (auth, publishing, uploads,
        // validation) surface without the user moving the mouse. Replaces the scattered
        // in-OnGUI Repaint() calls that previously only covered the version checker.
        void OnInspectorUpdate()
        {
            Repaint();
        }

        void LoadLogo()
        {
            string[] guids = AssetDatabase.FindAssets("U3D512Logo t:Texture2D");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
        }

        void InitializeTabs()
        {
            tabs = new List<ICreatorTab>
            {
                new SetupTab(),
                new ProjectToolsTab(),
                new CheckFixTab(),
                new PublishTab()
            };

            foreach (var tab in tabs)
            {
                tab.Initialize();
                // Add navigation callback
                tab.OnRequestTabSwitch = SwitchToTab;
            }
        }

        // Navigation method for tabs to request tab switches
        private void SwitchToTab(int tabIndex)
        {
            if (tabIndex >= 0 && tabIndex < tabs.Count)
            {
                selectedTab = tabIndex;
                Repaint(); // Refresh the window
            }
        }

        void InitializeStyles()
        {
            if (stylesInitialized) return;

            try
            {
                // Only create styles if EditorStyles is ready
                if (EditorStyles.boldLabel == null) return;

                headerStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 18,
                    normal = { textColor = new Color(0.4f, 0.5f, 0.9f) },
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true
                };

                // Let Unity's default button style handle text color for theme compatibility
                tabButtonStyle = new GUIStyle("Button")
                {
                    fontSize = 12,
                    fixedHeight = 35
                };

                activeTabButtonStyle = new GUIStyle("Button")
                {
                    fontSize = 12,
                    fixedHeight = 35,
                    fontStyle = FontStyle.Bold
                };

                stylesInitialized = true;
            }
            catch (System.Exception)
            {
                // EditorStyles not ready yet, will try again next OnGUI call
                return;
            }
        }

        void OnGUI()
        {
            // Initialize styles safely during OnGUI when Editor is ready
            InitializeStyles();

            DrawHeader();
            DrawTabNavigation();
            DrawCurrentTab();

            // Add startup preference control at bottom
            DrawStartupPreference();
        }

        void DrawStartupPreference()
        {
            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            // Use build guards for EditorPrefs access
            bool showOnStartup = true;
            if (!ShouldSkipDuringBuild())
            {
                showOnStartup = EditorPrefs.GetBool("U3D_ShowOnStartup", true);
            }

            bool newShowOnStartup = EditorGUILayout.ToggleLeft("Show dashboard when Unity starts", showOnStartup, GUILayout.Width(250));

            if (newShowOnStartup != showOnStartup)
            {
                // Use build guards for EditorPrefs access
                if (!ShouldSkipDuringBuild())
                {
                    EditorPrefs.SetBool("U3D_ShowOnStartup", newShowOnStartup);
                }
            }

            // Add Reset Startup Setup button
            GUILayout.Space(10);
            if (GUILayout.Button("Reset Startup Setup", GUILayout.Width(140)))
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Reset Startup Setup",
                    "This will reset the template startup configuration, allowing the startup scene to load again when this project is next opened.\n\n" +
                    "This is useful for testing the first-time template experience.",
                    "Reset",
                    "Cancel"
                );

                if (confirmed)
                {
                    ProjectStartupConfiguration.ResetTemplateConfiguration();
                    U3DTemplateUpdateChecker.ForceRecheck();
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        void DrawHeader()
        {
            float windowWidth = position.width;

            if (logoTexture != null)
            {
                float maxLogoWidth = windowWidth * 0.8f;
                float maxLogoHeight = 180f;

                float aspectRatio = (float)logoTexture.width / logoTexture.height;
                float logoWidth = Mathf.Min(maxLogoWidth, maxLogoHeight * aspectRatio);
                float logoHeight = logoWidth / aspectRatio;

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();

                Rect logoRect = GUILayoutUtility.GetRect(logoWidth, logoHeight,
                    GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
                GUI.DrawTexture(logoRect, logoTexture, ScaleMode.ScaleToFit);

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            DrawTemplateVersionInfo();
            EditorGUILayout.Space(8);
        }

        void DrawTabNavigation()
        {
            EditorGUILayout.BeginHorizontal();

            for (int i = 0; i < tabs.Count; i++)
            {
                var tabName = tabs[i].TabName;

                if (tabs[i].IsComplete)
                {
                    tabName = "✓ " + tabName;
                }

                bool isSelected = selectedTab == i;
                bool newSelection = GUILayout.Toggle(isSelected, tabName,
                    isSelected ? activeTabButtonStyle : tabButtonStyle);

                if (newSelection && !isSelected)
                {
                    selectedTab = i;
                }

                if (i < tabs.Count - 1)
                {
                    GUILayout.Space(1f);
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(5);
        }

        void DrawCurrentTab()
        {
            if (tabs != null && selectedTab < tabs.Count)
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
                tabs[selectedTab].DrawTab();
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// Displays template version info with update check status and download button.
        /// Replaces the original static version label.
        /// </summary>
        void DrawTemplateVersionInfo()
        {
            string templateVersion = GetTemplateVersion();

            if (string.IsNullOrEmpty(templateVersion))
                return;

            var versionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                normal = {
            textColor = EditorGUIUtility.isProSkin
                ? new Color(0.7f, 0.7f, 0.7f)
                : new Color(0.35f, 0.35f, 0.35f)
        },
                alignment = TextAnchor.MiddleCenter
            };

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"Template Version: {templateVersion}", versionStyle);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            var status = U3DTemplateUpdateChecker.CurrentStatus;

            bool isDark = EditorGUIUtility.isProSkin;
            Color checkingColor = isDark ? new Color(0.6f, 0.6f, 0.6f) : new Color(0.35f, 0.35f, 0.35f);
            Color upToDateColor = isDark ? new Color(0.4f, 0.8f, 0.4f) : new Color(0f, 0.5f, 0f);
            Color updateAvailableColor = isDark ? new Color(1f, 0.8f, 0.2f) : new Color(0.6f, 0.45f, 0f);
            Color downloadingColor = isDark ? new Color(0.6f, 0.8f, 1f) : new Color(0.1f, 0.35f, 0.6f);
            Color errorColor = isDark ? new Color(0.8f, 0.4f, 0.4f) : new Color(0.65f, 0.1f, 0.1f);

            switch (status)
            {
                case U3DTemplateUpdateChecker.UpdateStatus.Unknown:
                    U3DTemplateUpdateChecker.CheckForUpdateIfNeeded();
                    break;

                case U3DTemplateUpdateChecker.UpdateStatus.Checking:
                    DrawCenteredMiniLabel("Checking for updates...", checkingColor);
                    break;

                case U3DTemplateUpdateChecker.UpdateStatus.UpToDate:
                    DrawCenteredMiniLabel("✓ Up to date", upToDateColor);
                    break;

                case U3DTemplateUpdateChecker.UpdateStatus.UpdateAvailable:
                    DrawCenteredMiniLabel(
                        $"Update available: {U3DTemplateUpdateChecker.LatestVersion}",
                        updateAvailableColor);

                    if (!U3DTemplateUpdateChecker.IsDownloading)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.FlexibleSpace();

                        if (GUILayout.Button("Download & Install Update", GUILayout.Width(200), GUILayout.Height(24)))
                        {
                            _ = U3DTemplateUpdateChecker.DownloadAndInstallUpdate();
                        }

                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();
                    }
                    else
                    {
                        DrawCenteredMiniLabel("Downloading...", downloadingColor);
                    }
                    break;

                case U3DTemplateUpdateChecker.UpdateStatus.CheckFailed:
                    DrawCenteredMiniLabel(
                        $"Update check failed: {U3DTemplateUpdateChecker.ErrorMessage}",
                        errorColor);

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();

                    if (GUILayout.Button("Retry", GUILayout.Width(60), GUILayout.Height(20)))
                    {
                        U3DTemplateUpdateChecker.ForceRecheck();
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.EndHorizontal();
                    break;
            }

            EditorGUILayout.Space(3);
        }

        void DrawCenteredMiniLabel(string text, Color color)
        {
            var style = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 10,
                normal = { textColor = color },
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(text, style, GUILayout.MinWidth(200));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Gets the current template version from Assets/U3D/Resources/version.txt
        /// </summary>
        string GetTemplateVersion()
        {
            try
            {
                // Try to load the version file from Resources
                TextAsset versionAsset = Resources.Load<TextAsset>("version");
                if (versionAsset != null)
                {
                    return versionAsset.text.Trim();
                }

                // Fallback: try to read directly from file system
                string versionPath = "Assets/U3D/Resources/version.txt";
                if (System.IO.File.Exists(versionPath))
                {
                    return System.IO.File.ReadAllText(versionPath).Trim();
                }
            }
            catch (System.Exception ex)
            {
                // Silent fail - don't spam console during normal operation
                Debug.LogWarning($"Could not load U3D template version: {ex.Message}");
            }

            return null; // Don't show anything if version can't be determined
        }

        private Texture2D CreateRoundedTexture(Color color)
        {
            int size = 16;
            Texture2D texture = new Texture2D(size, size);
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = color;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;

            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }
    }
}