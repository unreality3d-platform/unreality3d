using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace U3D.Editor
{
    public static class GitHubAPI
    {
        private const string GITHUB_API_BASE = "https://api.github.com";

        private static async Task<bool> CheckRateLimit()
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return false;
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var response = await client.GetAsync($"{GITHUB_API_BASE}/rate_limit");
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var rateLimit = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);
                        var resources = JsonConvert.DeserializeObject<Dictionary<string, object>>(rateLimit["resources"].ToString());
                        var core = JsonConvert.DeserializeObject<Dictionary<string, object>>(resources["core"].ToString());

                        var remaining = int.Parse(core["remaining"].ToString());
                        var resetTime = long.Parse(core["reset"].ToString());

                        if (remaining < 5)
                        {
                            var currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            var waitTime = resetTime - currentTime;

                            Debug.LogWarning($"GitHub API rate limit low: {remaining} requests remaining. Reset in {waitTime} seconds.");

                            if (waitTime > 300)
                            {
                                return false;
                            }
                        }

                        return remaining > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Rate limit check failed: {ex.Message}");
            }

            return true;
        }

        public static async Task<GitHubRepositoryResult> CreateRepository(string repositoryName, string description = "")
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitHubRepositoryResult
                {
                    Success = false,
                    ErrorMessage = "No valid GitHub token available"
                };
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var repoData = new
                    {
                        name = repositoryName,
                        description = string.IsNullOrEmpty(description) ? $"Unity project created with Unreality3D SDK" : description,
                        @private = false,
                        has_issues = true,
                        has_projects = false,
                        has_wiki = false,
                        auto_init = false
                    };

                    var json = JsonConvert.SerializeObject(repoData);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync($"{GITHUB_API_BASE}/user/repos", content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                        return new GitHubRepositoryResult
                        {
                            Success = true,
                            RepositoryName = repositoryName,
                            FullName = result.ContainsKey("full_name") ? result["full_name"].ToString() : $"{GitHubTokenManager.GitHubUsername}/{repositoryName}",
                            CloneUrl = result.ContainsKey("clone_url") ? result["clone_url"].ToString() : "",
                            SshUrl = result.ContainsKey("ssh_url") ? result["ssh_url"].ToString() : "",
                            HtmlUrl = result.ContainsKey("html_url") ? result["html_url"].ToString() : "",
                            Message = "Repository created successfully"
                        };
                    }
                    else
                    {
                        var errorMessage = ParseGitHubError(responseText);
                        return new GitHubRepositoryResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub repository creation error: {ex.Message}");
                return new GitHubRepositoryResult
                {
                    Success = false,
                    ErrorMessage = $"Repository creation failed: {ex.Message}"
                };
            }
        }

        public static async Task<bool> CheckRepositoryExists(string repositoryName)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return false;
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var response = await client.GetAsync($"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}");
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        public static async Task<string> GenerateUniqueRepositoryName(string baseName)
        {
            var safeName = SanitizeRepositoryName(baseName);
            var uniqueName = safeName;
            var counter = 1;

            while (await CheckRepositoryExists(uniqueName))
            {
                uniqueName = $"{safeName}-{counter}";
                counter++;

                if (counter > 100)
                {
                    uniqueName = $"{safeName}-{DateTime.Now.Ticks}";
                    break;
                }
            }

            return uniqueName;
        }

        public static string SanitizeRepositoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "unity-project";
            }

            var sanitized = name.ToLower()
                .Replace(" ", "-")
                .Replace("_", "-")
                .Replace(".", "-");

            var validChars = "abcdefghijklmnopqrstuvwxyz0123456789-";
            var result = new StringBuilder();

            foreach (char c in sanitized)
            {
                if (validChars.Contains(c))
                {
                    result.Append(c);
                }
            }

            var finalName = result.ToString().Trim('-');

            if (finalName.Length > 0 && finalName[0] == '-')
            {
                finalName = finalName.Substring(1);
            }

            if (finalName.Length < 3)
            {
                finalName = "unity-project";
            }

            if (finalName.Length > 80)
            {
                finalName = finalName.Substring(0, 80).TrimEnd('-');
            }

            return finalName;
        }

        public static async Task<GitHubRepositoryResult> CreateFreshRepository(string repositoryName, string description)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitHubRepositoryResult
                {
                    Success = false,
                    ErrorMessage = "No valid GitHub token available"
                };
            }

            if (!await CheckRateLimit())
            {
                return new GitHubRepositoryResult
                {
                    Success = false,
                    ErrorMessage = "GitHub API rate limit exceeded. Please wait and try again."
                };
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var repoData = new
                    {
                        name = repositoryName,
                        description = string.IsNullOrEmpty(description) ? $"Unity WebGL project created with Unreality3D SDK" : description,
                        @private = false,
                        auto_init = false,
                        has_issues = true,
                        has_projects = false,
                        has_wiki = false
                    };

                    var json = JsonConvert.SerializeObject(repoData);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync($"{GITHUB_API_BASE}/user/repos", content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        var result = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                        return new GitHubRepositoryResult
                        {
                            Success = true,
                            RepositoryName = repositoryName,
                            FullName = result.ContainsKey("full_name") ? result["full_name"].ToString() : $"{GitHubTokenManager.GitHubUsername}/{repositoryName}",
                            CloneUrl = result.ContainsKey("clone_url") ? result["clone_url"].ToString() : "",
                            SshUrl = result.ContainsKey("ssh_url") ? result["ssh_url"].ToString() : "",
                            HtmlUrl = result.ContainsKey("html_url") ? result["html_url"].ToString() : "",
                            Message = "Repository created successfully"
                        };
                    }
                    else
                    {
                        var errorMessage = ParseGitHubError(responseText);
                        return new GitHubRepositoryResult
                        {
                            Success = false,
                            ErrorMessage = errorMessage
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub repository creation error: {ex.Message}");
                return new GitHubRepositoryResult
                {
                    Success = false,
                    ErrorMessage = $"Repository creation failed: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> UploadFileContent(string repositoryName, string filePath, string base64Content, string commitMessage)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = "No valid GitHub token available"
                };
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var uploadRequest = new
                    {
                        message = commitMessage,
                        content = base64Content,
                        branch = "main"
                    };

                    var json = JsonConvert.SerializeObject(uploadRequest);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var url = $"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/contents/{filePath}";
                    var response = await client.PutAsync(url, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        return new GitOperationResult
                        {
                            Success = true,
                            Message = $"File uploaded successfully: {filePath}"
                        };
                    }
                    else
                    {
                        var errorMessage = ParseGitHubError(responseText);
                        return new GitOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to upload {filePath}: {errorMessage}"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub file upload error: {ex.Message}");
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"File upload failed: {ex.Message}"
                };
            }
        }

        public static async Task<GitOperationResult> EnableGitHubPages(string repositoryName)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = "No valid GitHub token available"
                };
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var pagesRequest = new
                    {
                        source = new
                        {
                            branch = "main",
                            path = "/"
                        }
                    };

                    var json = JsonConvert.SerializeObject(pagesRequest);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var url = $"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/pages";
                    var response = await client.PostAsync(url, content);
                    var responseText = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        return new GitOperationResult
                        {
                            Success = true,
                            Message = "GitHub Pages enabled successfully"
                        };
                    }
                    else
                    {
                        var errorMessage = ParseGitHubError(responseText);
                        return new GitOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Failed to enable GitHub Pages: {errorMessage}"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub Pages setup error: {ex.Message}");
                return new GitOperationResult
                {
                    Success = false,
                    ErrorMessage = $"GitHub Pages setup failed: {ex.Message}"
                };
            }
        }

        public static async Task<GitHubRepositoryListResult> GetUserRepositories(string nameFilter = null, int maxResults = 100)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return new GitHubRepositoryListResult
                {
                    Success = false,
                    ErrorMessage = "No valid GitHub token available"
                };
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var repositories = new List<GitHubRepository>();
                    var page = 1;
                    var perPage = Math.Min(maxResults, 100);

                    while (repositories.Count < maxResults)
                    {
                        var url = $"{GITHUB_API_BASE}/user/repos?page={page}&per_page={perPage}&sort=updated&direction=desc";
                        var response = await client.GetAsync(url);
                        var responseText = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorMessage = ParseGitHubError(responseText);
                            return new GitHubRepositoryListResult
                            {
                                Success = false,
                                ErrorMessage = errorMessage
                            };
                        }

                        var repoArray = JsonConvert.DeserializeObject<object[]>(responseText);

                        if (repoArray.Length == 0)
                        {
                            break;
                        }

                        foreach (var repoObj in repoArray)
                        {
                            var repoData = JsonConvert.DeserializeObject<Dictionary<string, object>>(repoObj.ToString());
                            var repoName = repoData["name"].ToString();

                            if (!string.IsNullOrEmpty(nameFilter) && !repoName.ToLower().Contains(nameFilter.ToLower()))
                            {
                                continue;
                            }

                            var repo = new GitHubRepository
                            {
                                Name = repoName,
                                FullName = repoData["full_name"].ToString(),
                                Description = repoData["description"]?.ToString() ?? "",
                                HtmlUrl = repoData["html_url"].ToString(),
                                CloneUrl = repoData["clone_url"].ToString(),
                                IsPrivate = bool.Parse(repoData["private"].ToString()),
                                UpdatedAt = DateTime.Parse(repoData["updated_at"].ToString()),
                                CreatedAt = DateTime.Parse(repoData["created_at"].ToString()),
                                HasPages = repoData.ContainsKey("has_pages") ? bool.Parse(repoData["has_pages"].ToString()) : false
                            };

                            repositories.Add(repo);

                            if (repositories.Count >= maxResults)
                            {
                                break;
                            }
                        }

                        page++;
                    }

                    return new GitHubRepositoryListResult
                    {
                        Success = true,
                        Repositories = repositories,
                        TotalFound = repositories.Count
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"GitHub repository list error: {ex.Message}");
                return new GitHubRepositoryListResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to retrieve repositories: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Checks if a repository was created with the Unreality3D platform.
        /// Uses multiple detection methods for reliability:
        /// 1. Unreality3D-specific workflow file
        /// 2. README content containing "Unreality3D"
        /// </summary>
        public static async Task<bool> WasCreatedWithUnreality3D(string repositoryName)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return false;
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    // METHOD 1: Check for Unreality3D workflow file (definitive marker)
                    var workflowUrl = $"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/contents/.github/workflows/reassemble-chunks.yml";
                    var workflowResponse = await client.GetAsync(workflowUrl);

                    if (workflowResponse.IsSuccessStatusCode)
                    {
                        Debug.Log($"✅ '{repositoryName}' detected via Unreality3D workflow");
                        return true;
                    }

                    // METHOD 2: Check README for "Unreality3D" (simple and reliable)
                    var readmeUrl = $"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/contents/README.md";
                    var readmeResponse = await client.GetAsync(readmeUrl);

                    if (readmeResponse.IsSuccessStatusCode)
                    {
                        var content = await readmeResponse.Content.ReadAsStringAsync();
                        var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(content);

                        if (data.ContainsKey("content"))
                        {
                            var encodedContent = data["content"].ToString();
                            var decodedBytes = Convert.FromBase64String(encodedContent.Replace("\n", ""));
                            var fileContent = System.Text.Encoding.UTF8.GetString(decodedBytes);

                            if (fileContent.Contains("Unreality3D"))
                            {
                                Debug.Log($"✅ '{repositoryName}' detected via README content");
                                return true;
                            }
                        }
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Repository detection failed for {repositoryName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets GitHub Pages URL for a repository if Pages is enabled
        /// </summary>
        public static async Task<string> GetGitHubPagesUrl(string repositoryName)
        {
            if (!GitHubTokenManager.HasValidToken)
            {
                return null;
            }

            try
            {
                using (var client = CreateAuthenticatedClient())
                {
                    var url = $"{GITHUB_API_BASE}/repos/{GitHubTokenManager.GitHubUsername}/{repositoryName}/pages";
                    var response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseText = await response.Content.ReadAsStringAsync();
                        var pagesData = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                        if (pagesData.ContainsKey("html_url"))
                        {
                            return pagesData["html_url"].ToString();
                        }
                    }

                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        public static HttpClient CreateAuthenticatedClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", $"token {GitHubTokenManager.Token}");
            client.DefaultRequestHeaders.Add("User-Agent", "Unreality3D-Unity-SDK");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        private static string ParseGitHubError(string responseText)
        {
            try
            {
                var errorData = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                if (errorData.ContainsKey("message"))
                {
                    var message = errorData["message"].ToString();

                    if (message.Contains("name already exists"))
                    {
                        return "Repository name already exists. Please try a different name.";
                    }
                    else if (message.Contains("Bad credentials"))
                    {
                        return "Invalid GitHub token. Please check your token and try again.";
                    }
                    else if (message.Contains("rate limit"))
                    {
                        return "GitHub API rate limit exceeded. Please wait a moment and try again.";
                    }

                    return message;
                }

                if (errorData.ContainsKey("errors"))
                {
                    var errors = JsonConvert.DeserializeObject<object[]>(errorData["errors"].ToString());
                    if (errors.Length > 0)
                    {
                        var firstError = JsonConvert.DeserializeObject<Dictionary<string, object>>(errors[0].ToString());
                        if (firstError.ContainsKey("message"))
                        {
                            return firstError["message"].ToString();
                        }
                    }
                }
            }
            catch
            {
            }

            return $"GitHub API error: {responseText}";
        }
    }

    [System.Serializable]
    public class GitHubRepositoryListResult
    {
        public bool Success { get; set; }
        public List<GitHubRepository> Repositories { get; set; } = new List<GitHubRepository>();
        public int TotalFound { get; set; }
        public string ErrorMessage { get; set; }
    }

    [System.Serializable]
    public class GitHubRepository
    {
        public string Name { get; set; }
        public string FullName { get; set; }
        public string Description { get; set; }
        public string HtmlUrl { get; set; }
        public string CloneUrl { get; set; }
        public bool IsPrivate { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool HasPages { get; set; }
        public bool IsUnreality3DProject { get; set; }
        public string GitHubPagesUrl { get; set; }
    }

    [System.Serializable]
    public class GitHubRepositoryResult
    {
        public bool Success { get; set; }
        public string RepositoryName { get; set; }
        public string FullName { get; set; }
        public string CloneUrl { get; set; }
        public string SshUrl { get; set; }
        public string HtmlUrl { get; set; }
        public string Message { get; set; }
        public string ErrorMessage { get; set; }
    }

    [System.Serializable]
    public class GitHubRepositoryResponse
    {
        public string name;
        public string full_name;
        public string clone_url;
        public string html_url;
    }
}