using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class ProjectStartupConfiguration
{
    private const string STARTUP_SCENE_PATH = "Assets/Scenes/_MyScene.unity";
    private const string BUILD_TARGET_KEY = "HasSetWebGLTarget";
    private const string TEMPLATE_WEBGL_CHECK_KEY = "U3D_TemplateWebGLCheck";

    private static string BUILD_TARGET_SPECIFIC_KEY => $"{BUILD_TARGET_KEY}_{Application.dataPath.GetHashCode()}";
    private static string TEMPLATE_CHECK_KEY => $"{TEMPLATE_WEBGL_CHECK_KEY}_{Application.dataPath.GetHashCode()}";

    static ProjectStartupConfiguration()
    {
        // Single-shot registration - delayCall already waits for editor readiness
        EditorApplication.delayCall += ConfigureProjectStartup;
    }

    private static void ConfigureProjectStartup()
    {
        // Skip if build is in progress - but do NOT retry
        if (BuildPipeline.isBuildingPlayer || EditorApplication.isCompiling)
            return;

        bool hasCheckedTemplate = EditorPrefs.GetBool(TEMPLATE_CHECK_KEY, false);

        try
        {
            bool webglSupported = BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL);

            if (!webglSupported)
            {
                Debug.LogError("❌ U3D SDK: WEBGL BUILD SUPPORT NOT INSTALLED");
                Debug.LogError("📋 TO FIX: Unity Hub → Installs → Your Unity Version → Add Modules → WebGL Build Support");

                EditorUtility.DisplayDialog(
                    "WebGL Build Support Required",
                    "This Unreality3D template requires WebGL Build Support to function properly.\n\n" +
                    "To install:\n" +
                    "1. Open Unity Hub\n" +
                    "2. Go to Installs tab\n" +
                    "3. Click the gear icon next to your Unity version\n" +
                    "4. Select 'Add Modules'\n" +
                    "5. Check 'WebGL Build Support'\n" +
                    "6. Install and restart Unity\n\n" +
                    "Note: The template will function but builds will fail until WebGL support is installed.",
                    "OK"
                );

                EditorPrefs.SetBool(TEMPLATE_CHECK_KEY, true);
                return;
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.WebGL)
            {
                if (!hasCheckedTemplate)
                {
                    Debug.Log("🔄 U3D SDK: Template opened with non-WebGL build target. Switching to WebGL...");

                    bool success = EditorUserBuildSettings.SwitchActiveBuildTarget(
                        BuildTargetGroup.WebGL,
                        BuildTarget.WebGL
                    );

                    if (success)
                    {
                        Debug.Log("✅ U3D SDK: Build target switched to WebGL successfully");
                        Debug.Log("💡 U3D SDK: Template is now configured for WebGL deployment");
                    }
                    else
                    {
                        Debug.LogWarning("⚠️ U3D SDK: Failed to switch to WebGL. Please switch manually via Build Settings.");
                    }
                }
                else
                {
                    Debug.LogWarning($"⚠️ U3D SDK: Build target is {EditorUserBuildSettings.activeBuildTarget}, but template expects WebGL");
                    Debug.LogWarning("💡 U3D SDK: Switch to WebGL in Build Settings for proper deployment");
                }
            }
            else
            {
                if (!hasCheckedTemplate)
                {
                    Debug.Log("✅ U3D SDK: Template opened with WebGL build target (correct configuration)");
                }
            }

            EditorPrefs.SetBool(TEMPLATE_CHECK_KEY, true);

            string PROJECT_STARTUP_LOADED_KEY = $"U3D_ProjectStartupLoaded_{Application.dataPath.GetHashCode()}";
            bool hasLoadedStartupForThisProject = EditorPrefs.GetBool(PROJECT_STARTUP_LOADED_KEY, false);

            var currentScene = EditorSceneManager.GetActiveScene();
            bool isUntitledOnProjectOpen = currentScene.name == "Untitled" && string.IsNullOrEmpty(currentScene.path);

            if (!hasLoadedStartupForThisProject &&
                isUntitledOnProjectOpen &&
                System.IO.File.Exists(STARTUP_SCENE_PATH))
            {
                EditorSceneManager.OpenScene(STARTUP_SCENE_PATH);
                EditorPrefs.SetBool(PROJECT_STARTUP_LOADED_KEY, true);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ U3D SDK: Error in ProjectStartupConfiguration: {ex.Message}");
        }
    }

    public static void ResetTemplateConfiguration()
    {
        string PROJECT_STARTUP_LOADED_KEY = $"U3D_ProjectStartupLoaded_{Application.dataPath.GetHashCode()}";

        EditorPrefs.DeleteKey(TEMPLATE_CHECK_KEY);
        EditorPrefs.DeleteKey(BUILD_TARGET_SPECIFIC_KEY);
        EditorPrefs.DeleteKey(PROJECT_STARTUP_LOADED_KEY);
        Debug.Log("🔄 U3D SDK: Template configuration reset. Restart Unity to test first-time setup.");
    }
}