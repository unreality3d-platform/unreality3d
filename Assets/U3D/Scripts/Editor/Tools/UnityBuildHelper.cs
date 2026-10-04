using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace U3D.Editor
{
    public static class UnityBuildHelper
    {
        private static bool _isBuildInProgress = false;

        public static async Task<UnityBuildResult> BuildWebGL(string outputPath, System.Action<string> onProgress = null, string[] scenePaths = null)
        {
            // Prevent multiple simultaneous builds
            if (_isBuildInProgress)
            {
                return new UnityBuildResult
                {
                    Success = false,
                    ErrorMessage = "Another build is already in progress. Please wait for it to complete."
                };
            }

            _isBuildInProgress = true;
            var buildStartTime = DateTime.Now;

            try
            {
                onProgress?.Invoke("🔍 Validating build requirements...");

                // Enhanced build validation — pass scenePaths so validation checks the
                // explicit list when provided instead of reading EditorBuildSettings
                var validationResult = ValidateExtendedBuildRequirements(scenePaths);
                if (!validationResult.IsValid)
                {
                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = validationResult.ErrorMessage
                    };
                }

                onProgress?.Invoke("🧹 Preparing build directory...");

                if (string.IsNullOrEmpty(outputPath))
                {
                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = "Output path cannot be null or empty."
                    };
                }

                try
                {
                    var fullPath = Path.GetFullPath(outputPath);
                    outputPath = fullPath;
                }
                catch (Exception pathEx)
                {
                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = $"Invalid output path: {pathEx.Message}"
                    };
                }

                // Clean and prepare output directory
                await PrepareOutputDirectory(outputPath);

                // Use explicit scene list if provided, otherwise fall back to Build Settings
                string[] scenes;
                if (scenePaths != null && scenePaths.Length > 0)
                {
                    scenes = scenePaths;
                }
                else
                {
                    scenes = EditorBuildSettings.scenes
                        .Where(scene => scene.enabled)
                        .Select(scene => scene.path)
                        .ToArray();
                }

                if (scenes.Length == 0)
                {
                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = "No scenes enabled in Build Settings. Please add at least one scene."
                    };
                }

                onProgress?.Invoke($"🎬 Found {scenes.Length} scene(s) to build");
                onProgress?.Invoke("⚙️ Applying Unity 6+ WebGL optimizations...");

                var buildPlayerOptions = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = BuildTarget.WebGL,
                    options = BuildOptions.None,
                    targetGroup = BuildTargetGroup.WebGL
                };

                onProgress?.Invoke("🔨 Starting Unity WebGL build process...");
                onProgress?.Invoke("⏱️ This may take 30-40 minutes in Unity 6 - please be patient");

                // Ensure Assets/Samples exists so Unity doesn't log a warning
                // during the package sample cache update. Not tracked in version control.
                var samplesPath = Path.Combine(Application.dataPath, "Samples");
                if (!Directory.Exists(samplesPath))
                {
                    Directory.CreateDirectory(samplesPath);
                }

                // Start build with enhanced progress tracking
                StartBuildProgressTracking(onProgress);

                // Execute BuildPlayer via delayCall to escape the player loop.
                // BuildPipeline.BuildPlayer throws "cannot be executed while inside the player loop"
                // when called from an async continuation that resumes during the loop.
                // EditorApplication.delayCall runs between loop iterations, which is safe.
                var report = await BuildPlayerOutsidePlayerLoop(buildPlayerOptions);

                // Stop progress tracking
                StopBuildProgressTracking();

                var buildTime = DateTime.Now - buildStartTime;

                if (report.summary.result == BuildResult.Succeeded)
                {
                    onProgress?.Invoke("✅ Build completed successfully!");

                    var validationMessage = await ValidateGitHubPagesCompatibility(outputPath);
                    if (!string.IsNullOrEmpty(validationMessage))
                    {
                        onProgress?.Invoke($"⚠️ {validationMessage}");
                    }

                    var buildReport = GenerateBuildReport(report, outputPath, buildTime);
                    onProgress?.Invoke(buildReport);

                    return new UnityBuildResult
                    {
                        Success = true,
                        BuildPath = outputPath,
                        BuildSize = (long)report.summary.totalSize,
                        BuildTime = buildTime,
                        Message = $"Build completed successfully in {buildTime.TotalMinutes:F1} minutes"
                    };
                }
                else
                {
                    var errorMessage = GetDetailedBuildErrorMessage(report.summary, report);
                    var troubleshootingInfo = GetBuildTroubleshootingInfo(report.summary.result);

                    return new UnityBuildResult
                    {
                        Success = false,
                        ErrorMessage = $"{errorMessage}\n\n{troubleshootingInfo}",
                        BuildTime = buildTime
                    };
                }
            }
            catch (Exception ex)
            {
                var buildTime = DateTime.Now - buildStartTime;

                return new UnityBuildResult
                {
                    Success = false,
                    ErrorMessage = $"Build process failed: {ex.Message}\n\nPlease check the Unity Console for detailed error information.",
                    BuildTime = buildTime
                };
            }
            finally
            {
                _isBuildInProgress = false;
            }
        }

        /// <summary>
        /// Schedules BuildPipeline.BuildPlayer via EditorApplication.delayCall so it executes
        /// between player loop iterations, avoiding the "cannot be executed while inside the
        /// player loop" error that occurs when BuildPlayer is called from async continuations.
        /// </summary>
        private static Task<BuildReport> BuildPlayerOutsidePlayerLoop(BuildPlayerOptions options)
        {
            var tcs = new TaskCompletionSource<BuildReport>();

            EditorApplication.delayCall += () =>
            {
                try
                {
                    // Additional safety: wait for compilation/play mode transitions
                    if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        // Re-schedule if editor is busy
                        EditorApplication.delayCall += () =>
                        {
                            try
                            {
                                var report = BuildPipeline.BuildPlayer(options);
                                tcs.TrySetResult(report);
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetException(ex);
                            }
                        };
                        return;
                    }

                    var report = BuildPipeline.BuildPlayer(options);
                    tcs.TrySetResult(report);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };

            return tcs.Task;
        }

        private static async Task PrepareOutputDirectory(string outputPath)
        {
            await Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(outputPath))
                    {
                        var directory = new DirectoryInfo(outputPath);
                        foreach (var file in directory.GetFiles())
                        {
                            try
                            {
                                file.IsReadOnly = false;
                                file.Delete();
                            }
                            catch (Exception fileEx)
                            {
                                Debug.LogWarning($"Could not delete file {file.Name}: {fileEx.Message}");
                            }
                        }
                        foreach (var dir in directory.GetDirectories())
                        {
                            try
                            {
                                dir.Delete(true);
                            }
                            catch (Exception dirEx)
                            {
                                Debug.LogWarning($"Could not delete directory {dir.Name}: {dirEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        Directory.CreateDirectory(outputPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Could not fully clean output directory: {ex.Message}");

                    try
                    {
                        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        var fallbackPath = Path.Combine(outputPath, timestamp);

                        var parentDir = Path.GetDirectoryName(fallbackPath);
                        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                        {
                            Directory.CreateDirectory(parentDir);
                        }

                        Directory.CreateDirectory(fallbackPath);
                    }
                    catch (Exception fallbackEx)
                    {
                        Debug.LogError($"Failed to create fallback directory: {fallbackEx.Message}");
                        throw new Exception($"Cannot prepare build directory: {ex.Message}", ex);
                    }
                }
            });
        }

        private static BuildValidationResult ValidateExtendedBuildRequirements(string[] scenePaths = null)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.WebGL, BuildTarget.WebGL))
            {
                return new BuildValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "WebGL build support is not installed. Please install WebGL Build Support through Unity Hub."
                };
            }

            // When explicit scene paths are provided, validate those instead of Build Settings
            if (scenePaths != null && scenePaths.Length > 0)
            {
                // Verify all provided scene paths actually exist on disk
                var missingScenes = scenePaths.Where(p => !File.Exists(p)).ToArray();
                if (missingScenes.Length > 0)
                {
                    return new BuildValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Scene file(s) not found: {string.Join(", ", missingScenes)}"
                    };
                }
            }
            else
            {
                var enabledScenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).ToArray();
                if (enabledScenes.Length == 0)
                {
                    return new BuildValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "No scenes are enabled in Build Settings. Please add at least one scene to build."
                    };
                }
            }

            if (PlayerSettings.WebGL.memorySize < 128)
            {
                Debug.LogWarning("WebGL memory size is very low. Consider increasing to at least 256MB for better performance.");
            }

            return new BuildValidationResult { IsValid = true };
        }

        private static System.Threading.CancellationTokenSource _progressCancellation;

        private static void StartBuildProgressTracking(System.Action<string> onProgress)
        {
            _progressCancellation = new System.Threading.CancellationTokenSource();
            _ = TrackBuildProgressAsync(onProgress, _progressCancellation.Token);
        }

        private static void StopBuildProgressTracking()
        {
            _progressCancellation?.Cancel();
            _progressCancellation?.Dispose();
            _progressCancellation = null;
        }

        private static async Task TrackBuildProgressAsync(System.Action<string> onProgress, System.Threading.CancellationToken cancellationToken)
        {
            var startTime = DateTime.Now;
            var lastProgressUpdate = DateTime.Now;
            var progressMessages = new[]
            {
                "🔄 Compiling scripts...",
                "📦 Processing assets...",
                "🎨 Optimizing textures...",
                "🎵 Compressing audio...",
                "⚡ Applying code stripping...",
                "🌐 Generating WebGL code...",
                "📋 Creating build manifest...",
                "🗜️ Compressing build files..."
            };
            var messageIndex = 0;

            try
            {
                while (_isBuildInProgress && !cancellationToken.IsCancellationRequested)
                {
                    var elapsed = DateTime.Now - startTime;
                    var timeSinceLastUpdate = DateTime.Now - lastProgressUpdate;

                    if (timeSinceLastUpdate.TotalSeconds >= 30)
                    {
                        var currentMessage = progressMessages[messageIndex % progressMessages.Length];
                        onProgress?.Invoke($"{currentMessage} (Elapsed: {elapsed.TotalMinutes:F1} min)");

                        messageIndex++;
                        lastProgressUpdate = DateTime.Now;
                    }

                    if (elapsed.TotalMinutes >= 10 && elapsed.TotalMinutes < 20)
                    {
                        onProgress?.Invoke("⏱️ Build progressing normally (Unity 6 builds typically take 30-40 minutes)");
                    }
                    else if (elapsed.TotalMinutes >= 45)
                    {
                        onProgress?.Invoke("⚠️ Build taking longer than expected - check Unity Console for any errors");
                    }

                    await Task.Delay(5000, cancellationToken);
                }
            }
            catch (System.OperationCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        public static void SetOptimalWebGLSettingsUnity6()
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;

            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.nameFilesAsHashes = true;

            PlayerSettings.stripEngineCode = true;
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
            PlayerSettings.WebGL.threadsSupport = false;

            PlayerSettings.WebGL.showDiagnostics = false;
            PlayerSettings.WebGL.analyzeBuildSize = false;

            PlayerSettings.WebGL.webAssemblyTable = false;

            PlayerSettings.productName = Application.productName;
            PlayerSettings.companyName = !string.IsNullOrEmpty(PlayerSettings.companyName) ?
            PlayerSettings.companyName : "Unreality3D Creator";
        }

        private static async Task<string> ValidateGitHubPagesCompatibility(string buildPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var requiredFiles = new[]
                    {
                        "index.html",
                        Path.Combine("Build", $"{Application.productName}.loader.js"),
                        Path.Combine("Build", $"{Application.productName}.wasm"),
                        Path.Combine("Build", $"{Application.productName}.data")
                    };

                    var missingFiles = requiredFiles.Where(file => !File.Exists(Path.Combine(buildPath, file))).ToArray();

                    if (missingFiles.Length > 0)
                    {
                        return $"Missing required files: {string.Join(", ", missingFiles)}";
                    }

                    return string.Empty;
                }
                catch (Exception ex)
                {
                    return $"Could not validate build: {ex.Message}";
                }
            });
        }

        private static string GenerateBuildReport(BuildReport report, string buildPath, TimeSpan buildTime)
        {
            try
            {
                var summary = report.summary;
                var buildSizeMB = summary.totalSize / (1024.0 * 1024.0);

                var reportText = $"📊 Build Report:\n";
                reportText += $"• Build time: {buildTime.TotalMinutes:F1} minutes\n";
                reportText += $"• Total size: {buildSizeMB:F1} MB\n";
                reportText += $"• Platform: WebGL\n";
                reportText += $"• Compression: {PlayerSettings.WebGL.compressionFormat}\n";

                if (Directory.Exists(buildPath))
                {
                    var fileCount = Directory.GetFiles(buildPath, "*", SearchOption.AllDirectories).Length;
                    reportText += $"• Files generated: {fileCount}\n";
                }

                return reportText;
            }
            catch
            {
                return "📊 Build completed successfully";
            }
        }

        private static string GetDetailedBuildErrorMessage(BuildSummary summary, BuildReport report)
        {
            var baseMessage = summary.result switch
            {
                BuildResult.Failed => "Build failed",
                BuildResult.Cancelled => "Build was cancelled",
                BuildResult.Unknown => "Build completed with unknown status",
                _ => "Build did not succeed"
            };

            try
            {
                var steps = report.steps;
                var failedSteps = steps?.Where(step => step.messages.Any(msg => msg.type == LogType.Error)).ToArray();

                if (failedSteps?.Length > 0)
                {
                    var errorMessages = failedSteps
                        .SelectMany(step => step.messages.Where(msg => msg.type == LogType.Error))
                        .Take(3)
                        .Select(msg => msg.content)
                        .ToArray();

                    if (errorMessages.Length > 0)
                    {
                        baseMessage += ":\n• " + string.Join("\n• ", errorMessages);
                    }
                }
            }
            catch
            {
                // If we can't get detailed errors, just return the base message
            }

            return baseMessage + "\n\nPlease check the Unity Console for complete error details.";
        }

        private static string GetBuildTroubleshootingInfo(BuildResult result)
        {
            return result switch
            {
                BuildResult.Failed => "💡 Troubleshooting Tips:\n" +
                                    "• Check Unity Console for specific error messages\n" +
                                    "• Ensure all scripts compile without errors\n" +
                                    "• Try reducing texture sizes or audio quality\n" +
                                    "• Consider switching to Built-in Render Pipeline for smaller builds",

                BuildResult.Cancelled => "💡 Build was cancelled. You can retry when ready.",

                _ => "💡 Check Unity Console for details and try building again."
            };
        }

        public static async Task<bool> CopyBuildToRepository(string buildPath, string repositoryPath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var sourceDirectory = new DirectoryInfo(buildPath);
                    var targetDirectory = new DirectoryInfo(repositoryPath);

                    if (!sourceDirectory.Exists)
                    {
                        Debug.LogError($"Build source directory does not exist: {buildPath}");
                        return false;
                    }

                    CopyDirectoryRecursively(sourceDirectory, targetDirectory);

                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to copy build to repository: {ex.Message}");
                    return false;
                }
            });
        }

        public static bool ValidateBuildRequirements()
        {
            var result = ValidateExtendedBuildRequirements();
            if (!result.IsValid)
            {
                Debug.LogError(result.ErrorMessage);
            }
            return result.IsValid;
        }

        public static string GetDefaultBuildPath()
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath);
            return Path.Combine(projectPath, "WebGLBuild");
        }

        public static long GetBuildSize(string buildPath)
        {
            try
            {
                if (!Directory.Exists(buildPath))
                {
                    return 0;
                }

                var directory = new DirectoryInfo(buildPath);
                return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
            }
            catch
            {
                return 0;
            }
        }

        private static void CopyDirectoryRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            if (!target.Exists)
            {
                target.Create();
            }

            foreach (var file in source.GetFiles())
            {
                var targetFile = Path.Combine(target.FullName, file.Name);
                file.CopyTo(targetFile, true);
            }

            foreach (var subdir in source.GetDirectories())
            {
                var targetSubdir = target.CreateSubdirectory(subdir.Name);
                CopyDirectoryRecursively(subdir, targetSubdir);
            }
        }
    }

    [System.Serializable]
    public class UnityBuildResult
    {
        public bool Success { get; set; }
        public string BuildPath { get; set; }
        public long BuildSize { get; set; }
        public TimeSpan BuildTime { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
    }

    public class BuildValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
    }
}