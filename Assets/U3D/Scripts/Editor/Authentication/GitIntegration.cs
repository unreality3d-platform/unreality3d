using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace U3D.Editor
{
    public static class GitIntegration
    {
        public static async Task<GitOperationResult> InitializeRepository(string repositoryPath)
        {
            try
            {
                var result = await RunGitCommand(repositoryPath, "init");

                if (result.Success)
                {
                    // Set up .gitignore for Unity if it doesn't exist
                    await SetupUnityGitIgnore(repositoryPath);

                    return new GitOperationResult
                    {
                        Success = true,
                        Message = "Repository initialized successfully"
                    };
                }

                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Repository initialization failed: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> CloneRepository(string remoteUrl, string localPath)
        {
            try
            {
                // Ensure the local directory doesn't exist or is empty
                if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, true);
                }

                Directory.CreateDirectory(localPath);

                // Clone the repository
                var result = await RunGitCommand("", $"clone {remoteUrl} \"{localPath}\"");

                if (result.Success)
                {
                    return new GitOperationResult
                    {
                        Success = true,
                        Message = $"Repository cloned successfully to {localPath}"
                    };
                }

                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to clone repository: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> AddRemoteOrigin(string repositoryPath, string remoteUrl)
        {
            try
            {
                // Remove existing origin if it exists
                await RunGitCommand(repositoryPath, "remote remove origin");

                // Add new origin
                var result = await RunGitCommand(repositoryPath, $"remote add origin {remoteUrl}");
                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to add remote origin: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> AddAllFiles(string repositoryPath)
        {
            try
            {
                var result = await RunGitCommand(repositoryPath, "add .");
                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to add files: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> CommitChanges(string repositoryPath, string commitMessage)
        {
            try
            {
                var sanitizedMessage = commitMessage.Replace("\"", "\\\"");
                var result = await RunGitCommand(repositoryPath, $"commit -m \"{sanitizedMessage}\"");
                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to commit changes: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> PushToRemote(string repositoryPath, string branch = "main")
        {
            try
            {
                // First attempt: try direct push
                var directPush = await RunGitCommand(repositoryPath, $"push -u origin {branch}");

                if (directPush.Success)
                {
                    return directPush;
                }

                // If rejected due to divergent histories, fetch and merge
                if (directPush.ErrorMessage.Contains("fetch first") ||
                    directPush.ErrorMessage.Contains("rejected") ||
                    directPush.ErrorMessage.Contains("Updates were rejected"))
                {
                    UnityEngine.Debug.Log("Push rejected, attempting to fetch and merge remote changes...");

                    // Fetch remote changes
                    var fetchResult = await RunGitCommand(repositoryPath, "fetch origin");
                    if (!fetchResult.Success)
                    {
                        return new GitOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to fetch from remote: {fetchResult.ErrorMessage}"
                        };
                    }

                    // Try to merge remote changes with unrelated histories flag
                    var mergeResult = await RunGitCommand(repositoryPath, $"merge origin/{branch} --allow-unrelated-histories --no-edit");
                    if (!mergeResult.Success)
                    {
                        // If merge fails, it might be due to conflicts or other issues
                        return new GitOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to merge remote changes. This usually means your project conflicts with template files. Error: {mergeResult.ErrorMessage}"
                        };
                    }

                    UnityEngine.Debug.Log("Successfully merged remote changes, attempting push again...");

                    // Now try push again
                    var finalPush = await RunGitCommand(repositoryPath, $"push -u origin {branch}");
                    return finalPush;
                }

                // Handle other push failures (like missing branch)
                if (directPush.ErrorMessage.Contains("src refspec"))
                {
                    UnityEngine.Debug.Log("Branch doesn't exist, creating and pushing...");
                    await RunGitCommand(repositoryPath, $"checkout -b {branch}");
                    var retryPush = await RunGitCommand(repositoryPath, $"push -u origin {branch}");
                    return retryPush;
                }

                // Return original error if we can't handle it
                return directPush;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to push to remote: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> SetupGitUser(string repositoryPath, string username, string email)
        {
            try
            {
                var userResult = await RunGitCommand(repositoryPath, $"config user.name \"{username}\"");
                if (!userResult.Success) return userResult;

                var emailResult = await RunGitCommand(repositoryPath, $"config user.email \"{email}\"");
                return emailResult;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to setup git user: {ex.Message}"
                };
            }
        }

        public static async Task<bool> IsGitAvailable()
        {
            try
            {
                var result = await RunGitCommand("", "--version");
                return result.Success;
            }
            catch
            {
                return false;
            }
        }

        public static async Task<GitOperationResult> CheckGitStatus(string repositoryPath)
        {
            try
            {
                var result = await RunGitCommand(repositoryPath, "status --porcelain");
                return result;
            }
            catch (Exception ex)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to check git status: {ex.Message}"
                };
            }
        }

        private static async Task<GitOperationResult> RunGitCommand(string workingDirectory, string arguments)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var processInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = arguments,
                        WorkingDirectory = workingDirectory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(processInfo))
                    {
                        if (process == null)
                        {
                            return new GitOperationResult
                            {
                                Success = false,
                                ErrorMessage = "Failed to start git process"
                            };
                        }

                        process.WaitForExit();

                        var output = process.StandardOutput.ReadToEnd();
                        var error = process.StandardError.ReadToEnd();

                        if (process.ExitCode == 0)
                        {
                            return new GitOperationResult
                            {
                                Success = true,
                                Message = output,
                                CloneUrl = output
                            };
                        }
                        else
                        {
                            var errorMessage = !string.IsNullOrEmpty(error) ? error : output;

                            // Some git commands return non-zero but are not actual errors
                            if (arguments.Contains("remote remove") && errorMessage.Contains("No such remote"))
                            {
                                return new GitOperationResult
                                {
                                    Success = true,
                                    Message = "Remote already removed or doesn't exist"
                                };
                            }

                            return new GitOperationResult
                            {
                                Success = false,
                                ErrorMessage = errorMessage,
                                CloneUrl = output
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    return new GitOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"Git command execution failed: {ex.Message}"
                    };
                }
            });
        }

        private static async Task SetupUnityGitIgnore(string repositoryPath)
        {
            var gitIgnorePath = Path.Combine(repositoryPath, ".gitignore");

            if (!File.Exists(gitIgnorePath))
            {
                var unityGitIgnore = @"# This .gitignore file should be placed at the root of your Unity project directory
#
# Get latest from https://github.com/github/gitignore/blob/master/Unity.gitignore
/[Ll]ibrary/
/[Tt]emp/
/[Oo]bj/
/[Bb]uild/
/[Bb]uilds/
/[Ll]ogs/
/[Mm]emoryCaptures/

# Asset meta data should only be ignored when the corresponding asset is also ignored
!/[Aa]ssets/**/*.meta

# Uncomment this line if you wish to ignore the asset store tools plugin
# /[Aa]ssets/AssetStoreTools*

# Autogenerated Jetbrains Rider plugin
[Aa]ssets/Plugins/Editor/JetBrains*

# Visual Studio cache directory
.vs/

# Gradle cache directory
.gradle/

# Autogenerated VS/MD/Consulo solution and project files
ExportedObj/
.consulo/
*.csproj
*.unityproj
*.sln
*.suo
*.tmp
*.user
*.userprefs
*.pidb
*.booproj
*.svd
*.pdb
*.mdb
*.opendb
*.VC.db

# Unity3D generated meta files
*.pidb.meta
*.pdb.meta
*.mdb.meta

# Unity3D generated file on crash reports
sysinfo.txt

# Builds
*.apk
*.unitypackage

# Crashlytics generated file
crashlytics-build.properties

# Packed Addressables
/[Aa]ssets/[Aa]ddressable[Aa]ssets[Dd]ata/*/*.bin*

# Temporary auto-generated Android Assets
/[Aa]ssets/[Ss]treamingAssets/aa.meta
/[Aa]ssets/[Ss]treamingAssets/aa/*

# Custom additions for WebGL builds
/WebGLBuild/
/Build/
*.data
*.wasm
*.framework.js
*.loader.js
*.symbols.json
";

                await Task.Run(() => File.WriteAllText(gitIgnorePath, unityGitIgnore));
            }
        }
    }

    [System.Serializable]
    public class GitOperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
        public string CloneUrl { get; set; }
    }
}