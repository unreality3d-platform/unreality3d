using System;
using System.Collections;
using UnityEngine;
using UnityEditor;
using System.Threading.Tasks;
using System.IO;

namespace U3D.Editor
{
    public class SetupTab : ICreatorTab
    {
        public string TabName => "Setup";
        public bool IsComplete { get; private set; }
        public System.Action<int> OnRequestTabSwitch { get; set; }

        private enum AuthState
        {
            ManualLogin,
            ManualRegister,
            UsernameReservation,
            PayPalSetup,
            GitHubSetup,
            LoggedIn
        }

        private AuthState currentState = AuthState.LoggedIn;
        private string email = "";
        private string password = "";
        private string confirmPassword = "";

        private string desiredUsername = "";
        private bool checkingAvailability = false;
        private bool usernameAvailable = false;
        private bool usernameChecked = false;
        private string[] usernameSuggestions = new string[0];
        private bool reservingUsername = false;

        private string paypalEmail = "";
        private bool savingPayPalEmail = false;
        private bool paypalEmailSaved = false;

        private string githubToken = "";
        private bool validatingToken = false;
        private bool tokenValidated = false;
        private string validationMessage = "";

        private bool showOnStartup = true;

        private static string SETUP_INITIALIZED_KEY => $"U3D_SetupTab_Initialized_{Application.dataPath.GetHashCode()}";
        private static string SETUP_AUTH_STATE_KEY => $"U3D_SetupTab_WasAuthenticated_{Application.dataPath.GetHashCode()}";

        private static bool ShouldSkipDuringBuild()
        {
            return BuildPipeline.isBuildingPlayer ||
                   EditorApplication.isCompiling ||
                   EditorApplication.isUpdating;
        }

        private static bool SetupTabInitialized
        {
            get => !ShouldSkipDuringBuild() && EditorPrefs.GetBool(SETUP_INITIALIZED_KEY, false);
            set { if (!ShouldSkipDuringBuild()) EditorPrefs.SetBool(SETUP_INITIALIZED_KEY, value); }
        }

        private static bool WasAuthenticatedBeforeCompilation
        {
            get => !ShouldSkipDuringBuild() && EditorPrefs.GetBool(SETUP_AUTH_STATE_KEY, false);
            set { if (!ShouldSkipDuringBuild()) EditorPrefs.SetBool(SETUP_AUTH_STATE_KEY, value); }
        }

        public async void Initialize()
        {
            if (ShouldSkipDuringBuild())
            {
                return;
            }

            WasAuthenticatedBeforeCompilation = U3DAuthenticator.IsLoggedIn;

            EnsureFirebaseConfiguration();

            if (!U3DAuthenticator.IsLoggedIn)
            {
                await U3DAuthenticator.TryAutoLogin();
            }

            await DetermineInitialState();

            if (U3DAuthenticator.IsLoggedIn)
            {
                SetupTabInitialized = true;
            }
        }

        private void LogoutAndReset()
        {
            U3DAuthenticator.Logout();

            if (!ShouldSkipDuringBuild())
            {
                EditorPrefs.DeleteKey(SETUP_INITIALIZED_KEY);
                EditorPrefs.DeleteKey(SETUP_AUTH_STATE_KEY);
                EditorPrefs.DeleteKey($"U3D_UsernameCompleted_{Application.dataPath.GetHashCode()}");
            }

            currentState = AuthState.ManualLogin;
            UpdateCompletion();
        }

        private async Task DetermineInitialState()
        {
            if (!U3DAuthenticator.IsLoggedIn)
            {
                currentState = AuthState.ManualLogin;
                ResetTemporaryFields();
                UpdateCompletion();
                return;
            }

            string currentUsername = U3DAuthenticator.CreatorUsername;

            if (string.IsNullOrEmpty(currentUsername))
            {
                try
                {
                    await U3DAuthenticator.ForceProfileReload();
                    currentUsername = U3DAuthenticator.CreatorUsername;
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Unauthenticated"))
                    {
                        Debug.LogWarning("Auth token expired during profile reload");
                        currentState = AuthState.ManualLogin;
                        SetupTabInitialized = false;
                        UpdateCompletion();
                        return;
                    }
                    Debug.LogWarning($"Profile reload failed (continuing anyway): {ex.Message}");
                }
            }

            if (string.IsNullOrEmpty(currentUsername))
            {
                bool serverHasUsername = await U3DAuthenticator.VerifyUsernameExists();

                if (serverHasUsername)
                {
                    try
                    {
                        await U3DAuthenticator.ForceProfileReload();
                        currentUsername = U3DAuthenticator.CreatorUsername;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Profile reload after server check failed: {ex.Message}");
                    }
                }
            }

            if (string.IsNullOrEmpty(currentUsername))
            {
                currentState = AuthState.UsernameReservation;
            }
            else if (string.IsNullOrEmpty(GetSavedPayPalEmail()))
            {
                currentState = AuthState.PayPalSetup;
                paypalEmail = GetSavedPayPalEmail();
            }
            else if (!GitHubTokenManager.HasValidToken)
            {
                currentState = AuthState.GitHubSetup;
                ResetGitHubFields();
            }
            else
            {
                currentState = AuthState.LoggedIn;
            }

            UpdateCompletion();
        }

        private void ResetTemporaryFields()
        {
            githubToken = "";
            tokenValidated = false;
            validationMessage = "";
            paypalEmail = "";
            paypalEmailSaved = false;

            desiredUsername = "";
            usernameChecked = false;
            usernameAvailable = false;
            usernameSuggestions = new string[0];
        }

        private void ResetGitHubFields()
        {
            githubToken = "";
            tokenValidated = false;
            validationMessage = "";
        }

        public void DrawTab()
        {
            EditorGUILayout.BeginVertical();

            switch (currentState)
            {
                case AuthState.ManualLogin:
                    DrawManualLogin();
                    break;
                case AuthState.ManualRegister:
                    DrawManualRegister();
                    break;
                case AuthState.UsernameReservation:
                    DrawUsernameReservation();
                    break;
                case AuthState.PayPalSetup:
                    DrawPayPalSetup();
                    break;
                case AuthState.GitHubSetup:
                    DrawGitHubSetup();
                    break;
                case AuthState.LoggedIn:
                    DrawLoggedIn();
                    break;
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawManualLogin()
        {
            EditorGUILayout.LabelField("Email & Password Login", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            email = EditorGUILayout.TextField("Email", email);
            password = EditorGUILayout.PasswordField("Password", password);

            EditorGUILayout.Space(10);

            bool newStayLoggedIn = EditorGUILayout.ToggleLeft("Stay logged in", U3DAuthenticator.StayLoggedIn);
            if (newStayLoggedIn != U3DAuthenticator.StayLoggedIn)
            {
                U3DAuthenticator.StayLoggedIn = newStayLoggedIn;
            }

            EditorGUILayout.Space(10);

            bool canLogin = !string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password);

            EditorGUI.BeginDisabledGroup(!canLogin);
            if (GUILayout.Button("Login", GUILayout.Height(35)))
            {
                StartManualLogin();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Create New Account"))
            {
                currentState = AuthState.ManualRegister;
            }

            EditorGUILayout.Space(5);

            if (U3DAuthenticator.IsLoggedIn && GUILayout.Button("Logout", EditorStyles.miniButton))
            {
                if (EditorUtility.DisplayDialog("Logout Confirmation",
                    "This will log you out completely. Continue?",
                    "Yes, Logout", "Cancel"))
                {
                    LogoutAndReset();
                }
            }
        }

        private void DrawManualRegister()
        {
            EditorGUILayout.LabelField("Create New Account", EditorStyles.boldLabel);
            EditorGUILayout.Space(10);

            email = EditorGUILayout.TextField("Email", email);
            password = EditorGUILayout.PasswordField("Password", password);
            confirmPassword = EditorGUILayout.PasswordField("Confirm Password", confirmPassword);

            EditorGUILayout.Space(10);

            bool newStayLoggedIn = EditorGUILayout.ToggleLeft("Stay logged in", U3DAuthenticator.StayLoggedIn);
            if (newStayLoggedIn != U3DAuthenticator.StayLoggedIn)
            {
                U3DAuthenticator.StayLoggedIn = newStayLoggedIn;
            }

            EditorGUILayout.Space(10);

            bool canRegister = !string.IsNullOrWhiteSpace(email) &&
                              !string.IsNullOrWhiteSpace(password) &&
                              password == confirmPassword &&
                              password.Length >= 6;

            if (!canRegister && !string.IsNullOrWhiteSpace(password))
            {
                if (password != confirmPassword)
                {
                    EditorGUILayout.HelpBox("Passwords do not match.", MessageType.Warning);
                }
                else if (password.Length < 6)
                {
                    EditorGUILayout.HelpBox("Password must be at least 6 characters.", MessageType.Warning);
                }
            }

            EditorGUI.BeginDisabledGroup(!canRegister);
            if (GUILayout.Button("Create Account", GUILayout.Height(35)))
            {
                StartManualRegister();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Already have an account? Login"))
            {
                currentState = AuthState.ManualLogin;
            }
        }

        // === CHANGED: Labels and help text reflect the two-field architecture.
        // === Users now type a display-form Creator Name; the server computes
        // === the URL-safe lookup form. Per decision #4, the live URL preview
        // === is deferred — labels and help text only for this pass.
        private void DrawUsernameReservation()
        {
            EditorGUILayout.LabelField("Reserve Your Creator Name", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Your name is how you're known on the platform. Capitals, spaces, " +
                "hyphens, and underscores are fine. We'll create a URL-safe version " +
                "automatically for your pages: unreality3d.com/[url-safe-name]/",
                MessageType.Info);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Your Creator Name:", EditorStyles.boldLabel);
            string newUsername = EditorGUILayout.TextField(desiredUsername);

            if (newUsername != desiredUsername)
            {
                desiredUsername = newUsername;
                usernameChecked = false;
                usernameAvailable = false;
                usernameSuggestions = new string[0];
            }

            EditorGUILayout.Space(5);

            bool canCheck = !string.IsNullOrWhiteSpace(desiredUsername) && !checkingAvailability;

            EditorGUI.BeginDisabledGroup(!canCheck);
            if (GUILayout.Button("Check Availability", GUILayout.Height(30)))
            {
                CheckUsernameAvailability();
            }
            EditorGUI.EndDisabledGroup();

            if (checkingAvailability)
            {
                EditorGUILayout.LabelField("Checking availability...", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(10);

            if (usernameChecked)
            {
                if (usernameAvailable)
                {
                    EditorGUILayout.HelpBox("Name is available!", MessageType.Info);

                    EditorGUI.BeginDisabledGroup(reservingUsername);
                    if (GUILayout.Button("Reserve Name", GUILayout.Height(35)))
                    {
                        ReserveUsername();
                    }
                    EditorGUI.EndDisabledGroup();

                    if (reservingUsername)
                    {
                        EditorGUILayout.LabelField("Reserving name...", EditorStyles.miniLabel);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("Name is not available.", MessageType.Warning);

                    if (usernameSuggestions.Length > 0)
                    {
                        EditorGUILayout.LabelField("Suggestions:", EditorStyles.boldLabel);
                        foreach (var suggestion in usernameSuggestions)
                        {
                            if (GUILayout.Button(suggestion))
                            {
                                desiredUsername = suggestion;
                                usernameChecked = false;
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(15);
            if (GUILayout.Button("Logout"))
            {
                if (EditorUtility.DisplayDialog("Logout Confirmation",
                    "This will log you out and clear all stored credentials. Continue?",
                    "Yes, Logout", "Cancel"))
                {
                    LogoutAndReset();
                }
            }
        }

        private void DrawPayPalSetup()
        {
            EditorGUILayout.LabelField("PayPal Monetization Setup", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Optional: Connect PayPal to enable monetization:\n" +
                "• Sell content (scenes, avatars, props, access)\n" +
                "• Receive payments directly to your account\n" +
                "• Keep 95% of your earnings\n" +
                "• Automatic dual-transaction system\n\n" +
                "You can skip this and publish content without monetization.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("How it works:", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("1. Customer pays for your content", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("2. Payment automatically splits:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("   • 95% goes to your PayPal account", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("   • 5% covers platform costs", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("3. You receive payment notifications directly", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("PayPal Email Address:", EditorStyles.boldLabel);
            paypalEmail = EditorGUILayout.TextField(paypalEmail);
            EditorGUILayout.LabelField("(This must match your PayPal account email)", EditorStyles.miniLabel);

            EditorGUILayout.Space(10);

            bool isValidEmail = IsValidEmail(paypalEmail);
            if (!string.IsNullOrWhiteSpace(paypalEmail) && !isValidEmail)
            {
                EditorGUILayout.HelpBox("Please enter a valid email address.", MessageType.Warning);
            }

            EditorGUILayout.Space(5);

            EditorGUI.BeginDisabledGroup(!isValidEmail || savingPayPalEmail);
            if (GUILayout.Button(savingPayPalEmail ? "Saving..." : "Save PayPal Email", GUILayout.Height(35)))
            {
                SavePayPalEmail();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            if (GUILayout.Button("Skip for Now (Add Later)", EditorStyles.miniButton))
            {
                if (EditorUtility.DisplayDialog("Skip PayPal Setup?",
                    "You can add add your PayPal email here later.\n\n" +
                    "You'll still be able to publish content, but won't be able to sell it until you add PayPal.\n\n" +
                    "Continue?",
                    "Yes, Skip for Now", "Wait, Let Me Add It"))
                {
                    if (!GitHubTokenManager.HasValidToken)
                    {
                        currentState = AuthState.GitHubSetup;
                    }
                    else
                    {
                        currentState = AuthState.LoggedIn;
                    }
                    UpdateCompletion();
                }
            }

            EditorGUILayout.Space(15);
            if (GUILayout.Button("Logout"))
            {
                if (EditorUtility.DisplayDialog("Logout Confirmation",
                    "This will log you out and clear all stored credentials. Continue?",
                    "Yes, Logout", "Cancel"))
                {
                    LogoutAndReset();
                }
            }
        }

        private void DrawGitHubSetup()
        {
            EditorGUILayout.LabelField("Connect to GitHub", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.HelpBox(
                "Connect a (free) GitHub account to publish online.\n\n" +
                "New to GitHub? No problem! Just create a free account, then follow the steps below.\n\n" +
                "We'll handle the rest - you just click 'Make It Live!'",
                MessageType.Info);

            EditorGUILayout.HelpBox(
                "Already have a token? If you saved it, paste it below. If not, you'll need to create a new one. It's ok, your published products aren't affected.",
                MessageType.Info);

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Quick Setup (takes 2 minutes):", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Step 1: Go to GitHub", EditorStyles.boldLabel);
            if (GUILayout.Button("Open GitHub Settings", GUILayout.Height(35)))
            {
                Application.OpenURL("https://github.com/settings/tokens/new?scopes=repo,workflow&description=U3D");
            }
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Step 2: If you see a list of tokens", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Click 'Generate new token' (top right)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("• Choose 'Generate new token (classic)'", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("(If you went straight to a form, skip to Step 3)", EditorStyles.miniLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.LabelField("Step 3: Fill out the form", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Note: Type in 'U3D' (or any name you'll recognize)", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("• Expiration: Choose '90 days' (Recommended)", EditorStyles.miniLabel);
            if (GUILayout.Button("?", GUILayout.Width(20)))
            {
                EditorUtility.DisplayDialog("Why 90 days?",
                    "Setting an expiration date on personal access tokens is highly recommended as this helps keep your information secure.\n\n" +
                    "GitHub will send you an email when it's time to renew a token that's about to expire.\n\n" +
                    "Tokens that have expired can be regenerated, giving you a duplicate token with the same properties as the original.",
                    "Got it!");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField("• Scopes: Check these boxes:", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  repo (Create and manage repositories)", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("  workflow (Update GitHub Action workflows)", EditorStyles.miniLabel);
            EditorGUILayout.Space(3);

            EditorGUILayout.LabelField("Step 4: Create and copy", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Click 'Generate token' at the bottom", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("• Copy the long code that appears", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("• Paste it below", EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Paste your code here:", EditorStyles.boldLabel);
            githubToken = EditorGUILayout.PasswordField(githubToken);
            EditorGUILayout.LabelField("(This stays private and secure on your computer)", EditorStyles.miniLabel);

            EditorGUILayout.Space(5);

            if (!string.IsNullOrEmpty(validationMessage))
            {
                var messageType = tokenValidated ? MessageType.Info : MessageType.Warning;
                EditorGUILayout.HelpBox(validationMessage, messageType);
                EditorGUILayout.Space(5);
            }

            EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(githubToken) || validatingToken);
            if (GUILayout.Button(validatingToken ? "Checking..." : "Test Connection", GUILayout.Height(35)))
            {
                ValidateGitHubToken();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(10);

            if (tokenValidated)
            {
                if (GUILayout.Button("All Set! Ready to Publish", GUILayout.Height(40)))
                {
                    currentState = AuthState.LoggedIn;
                    UpdateCompletion();
                }
            }

            if (!tokenValidated)
            {
                if (GUILayout.Button("I'll Set This Up Later", EditorStyles.miniButton))
                {
                    if (EditorUtility.DisplayDialog("Setup Later?",
                        "No problem! You can explore other features now.\n\nJust remember you'll need to come back here before you can publish your creations online.\n\nContinue?",
                        "Yes, Skip for Now", "Wait, Let Me Finish"))
                    {
                        currentState = AuthState.LoggedIn;
                        UpdateCompletion();
                    }
                }
            }

            EditorGUILayout.Space(15);
            if (GUILayout.Button("Logout"))
            {
                if (EditorUtility.DisplayDialog("Logout Confirmation",
                    "This will log you out and clear all stored credentials. Continue?",
                    "Yes, Logout", "Cancel"))
                {
                    LogoutAndReset();
                }
            }
        }

        private void DrawLoggedIn()
        {
            EditorGUILayout.LabelField("Setup Complete", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginVertical("Box");
            EditorGUILayout.LabelField($"{U3DAuthenticator.DisplayName}", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{U3DAuthenticator.UserEmail}", EditorStyles.miniLabel);

            if (!string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername))
            {
                EditorGUILayout.LabelField($"Username: {U3DAuthenticator.CreatorUsername}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"URL: {U3DUrlUtility.CreatorUrl(U3DAuthenticator.CreatorUsername)}", EditorStyles.miniLabel);
            }

            string savedPayPalEmail = GetSavedPayPalEmail();
            if (!string.IsNullOrEmpty(savedPayPalEmail))
            {
                EditorGUILayout.LabelField($"PayPal: {savedPayPalEmail}", EditorStyles.miniLabel);
            }

            if (GitHubTokenManager.HasValidToken)
            {
                EditorGUILayout.LabelField($"GitHub: Connected ({GitHubTokenManager.GitHubUsername})", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(10);

            if (!GitHubTokenManager.HasValidToken)
            {
                EditorGUILayout.HelpBox(
                    "Almost Ready to Publish!\n\n" +
                    "Connect to GitHub so we can put your creations online automatically.",
                    MessageType.Warning);

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Connect to GitHub", GUILayout.Height(30)))
                {
                    currentState = AuthState.GitHubSetup;
                }

                EditorGUILayout.Space(10);
            }

            if (string.IsNullOrEmpty(GetSavedPayPalEmail()))
            {
                EditorGUILayout.HelpBox(
                    "Connect PayPal to enable monetization:\n" +
                    "• Sell content (scenes, avatars, props, access)\n" +
                    "• Receive payments directly to your account\n" +
                    "• Keep 95% of your earnings",
                    MessageType.Info);

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Connect PayPal Account", GUILayout.Height(30)))
                {
                    currentState = AuthState.PayPalSetup;
                    paypalEmail = GetSavedPayPalEmail();
                    paypalEmailSaved = false;
                }

                EditorGUILayout.Space(10);
            }

            EditorGUILayout.BeginHorizontal();

            if (GitHubTokenManager.HasValidToken && GUILayout.Button("Update GitHub Token"))
            {
                currentState = AuthState.GitHubSetup;
                githubToken = "";
                tokenValidated = false;
                validationMessage = "";
            }

            if (!string.IsNullOrEmpty(GetSavedPayPalEmail()) && GUILayout.Button("Update PayPal Email"))
            {
                currentState = AuthState.PayPalSetup;
                paypalEmail = GetSavedPayPalEmail();
                paypalEmailSaved = false;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            if (GUILayout.Button("Logout"))
            {
                if (EditorUtility.DisplayDialog("Logout Confirmation",
                    "This will log you out and clear all stored credentials. Continue?",
                    "Yes, Logout", "Cancel"))
                {
                    LogoutAndReset();
                }
            }
        }

        private async void StartManualLogin()
        {
            try
            {
                bool success = await U3DAuthenticator.LoginWithEmailPassword(email, password);
                if (success)
                {
                    await U3DAuthenticator.ForceProfileReload();

                    if (string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername))
                    {
                        currentState = AuthState.UsernameReservation;
                    }
                    else if (string.IsNullOrEmpty(GetSavedPayPalEmail()))
                    {
                        currentState = AuthState.PayPalSetup;
                        paypalEmail = GetSavedPayPalEmail();
                    }
                    else if (!GitHubTokenManager.HasValidToken)
                    {
                        currentState = AuthState.GitHubSetup;
                    }
                    else
                    {
                        currentState = AuthState.LoggedIn;
                    }
                    UpdateCompletion();
                }
                else
                {
                    EditorUtility.DisplayDialog("Login Failed", "Invalid email or password.", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Login Error", $"An error occurred: {ex.Message}", "OK");
            }
        }

        private async void StartManualRegister()
        {
            try
            {
                bool success = await U3DAuthenticator.RegisterWithEmailPassword(email, password);
                if (success)
                {
                    await U3DAuthenticator.ForceProfileReload();

                    if (string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername))
                    {
                        currentState = AuthState.UsernameReservation;
                    }
                    else if (string.IsNullOrEmpty(GetSavedPayPalEmail()))
                    {
                        currentState = AuthState.PayPalSetup;
                        paypalEmail = GetSavedPayPalEmail();
                    }
                    else if (!GitHubTokenManager.HasValidToken)
                    {
                        currentState = AuthState.GitHubSetup;
                    }
                    else
                    {
                        currentState = AuthState.LoggedIn;
                    }
                    UpdateCompletion();
                }
                else
                {
                    EditorUtility.DisplayDialog("Registration Failed", "Failed to create account. Please try again.", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Registration Error", $"An error occurred: {ex.Message}", "OK");
            }
        }

        private async void CheckUsernameAvailability()
        {
            checkingAvailability = true;
            usernameChecked = false;

            try
            {
                usernameAvailable = await U3DAuthenticator.CheckUsernameAvailability(desiredUsername);

                if (!usernameAvailable)
                {
                    usernameSuggestions = await U3DAuthenticator.GetUsernameSuggestions(desiredUsername);
                }

                usernameChecked = true;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Check Failed", $"Failed to check username availability: {ex.Message}", "OK");
            }
            finally
            {
                checkingAvailability = false;
            }
        }

        // === CHANGED: Success dialog shows both the display name (what the
        // === user typed) and the lookup-form professional URL. After the
        // === U3DAuthenticator.ReserveUsername fix in file 7A, DisplayName
        // === holds the trimmed raw input and CreatorUsername holds the
        // === URL-safe lookup form.
        private async void ReserveUsername()
        {
            reservingUsername = true;

            try
            {
                bool success = await U3DAuthenticator.ReserveUsername(desiredUsername);
                if (success)
                {
                    if (string.IsNullOrEmpty(GetSavedPayPalEmail()))
                    {
                        currentState = AuthState.PayPalSetup;
                        paypalEmail = "";
                    }
                    else if (!GitHubTokenManager.HasValidToken)
                    {
                        currentState = AuthState.GitHubSetup;
                    }
                    else
                    {
                        currentState = AuthState.LoggedIn;
                    }
                    UpdateCompletion();

                    var shownDisplayName = !string.IsNullOrEmpty(U3DAuthenticator.DisplayName)
                        ? U3DAuthenticator.DisplayName
                        : desiredUsername;
                    var shownUrl = U3DUrlUtility.CreatorUrl(!string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername)
                        ? U3DAuthenticator.CreatorUsername
                        : desiredUsername);

                    EditorUtility.DisplayDialog("Success!",
                        $"Name '{shownDisplayName}' reserved successfully!\n\nYour professional URL: {shownUrl}",
                        "Awesome!");
                }
                else
                {
                    EditorUtility.DisplayDialog("Reservation Failed", "Failed to reserve name. Please try again.", "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Reservation Error", $"An error occurred: {ex.Message}", "OK");
            }
            finally
            {
                reservingUsername = false;
            }
        }

        private void SavePayPalEmail()
        {
            savingPayPalEmail = true;
            try
            {
                SavePayPalEmailToPrefs(paypalEmail);
                paypalEmailSaved = true;

                UpdateRuntimeDataAsset();

                U3DAuthenticator.SetPayPalEmail(paypalEmail);

                RefreshMonetizationToolsCategory();

                EditorUtility.DisplayDialog("PayPal Email Saved!",
                    $"PayPal email '{paypalEmail}' saved successfully!\n\n" +
                    "You can now sell content and receive 95% of earnings directly to this PayPal account.",
                    "Great!");

                if (!GitHubTokenManager.HasValidToken)
                {
                    currentState = AuthState.GitHubSetup;
                }
                else
                {
                    currentState = AuthState.LoggedIn;
                }
                UpdateCompletion();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Save Error", $"Failed to save PayPal email: {ex.Message}", "OK");
            }
            finally
            {
                savingPayPalEmail = false;
            }
        }

        private void UpdateRuntimeDataAsset()
        {
            var assetPath = "Assets/U3D/Resources/U3DCreatorData.asset";

            var resourcesPath = "Assets/U3D/Resources";
            if (!AssetDatabase.IsValidFolder(resourcesPath))
            {
                if (!AssetDatabase.IsValidFolder("Assets/U3D"))
                {
                    AssetDatabase.CreateFolder("Assets", "U3D");
                }
                AssetDatabase.CreateFolder("Assets/U3D", "Resources");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            var data = AssetDatabase.LoadAssetAtPath<U3DCreatorData>(assetPath);
            if (data == null)
            {
                data = ScriptableObject.CreateInstance<U3DCreatorData>();
                AssetDatabase.CreateAsset(data, assetPath);
            }

            data.PayPalEmail = paypalEmail;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var verifyData = AssetDatabase.LoadAssetAtPath<U3DCreatorData>(assetPath);
            if (verifyData == null || verifyData.PayPalEmail != paypalEmail)
            {
                Debug.LogError($"Failed to save U3DCreatorData asset. Expected PayPal email: '{paypalEmail}', Got: '{verifyData?.PayPalEmail ?? "null"}'");
            }
        }

        private void RefreshMonetizationToolsCategory()
        {
            try
            {
                var windowType = System.Type.GetType("U3D.Editor.U3DCreatorWindow,Assembly-CSharp-Editor");
                if (windowType != null)
                {
                    var windowMethod = windowType.GetMethod("GetWindow", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (windowMethod != null)
                    {
                        var window = windowMethod.Invoke(null, new object[] { windowType, false });
                        if (window != null)
                        {
                            var refreshMethod = windowType.GetMethod("RefreshMonetizationCategory", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                            if (refreshMethod != null)
                            {
                                refreshMethod.Invoke(window, null);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not refresh MonetizationToolsCategory: {ex.Message}");
            }
        }

        private void SavePayPalEmailToPrefs(string email)
        {
            EditorPrefs.SetString("U3D_PayPalEmail", email);
        }

        private string GetSavedPayPalEmail()
        {
            return EditorPrefs.GetString("U3D_PayPalEmail", "");
        }

        private async void ValidateGitHubToken()
        {
            validatingToken = true;
            validationMessage = "Testing your connection...";

            try
            {
                var result = await GitHubTokenManager.ValidateAndSetToken(githubToken);

                if (result.IsValid)
                {
                    tokenValidated = true;
                    validationMessage = $"Connected to GitHub as {result.Username}";

                    bool hasRepoPermissions = await GitHubTokenManager.CheckRepositoryPermissions();
                    if (!hasRepoPermissions)
                    {
                        validationMessage += "\n\nHeads up: Your token might not have all the permissions needed. If publishing doesn't work, try creating a new token with the steps above.";
                    }
                }
                else
                {
                    tokenValidated = false;

                    string friendlyError = result.ErrorMessage;
                    if (friendlyError.Contains("Bad credentials"))
                    {
                        friendlyError = "The code you entered doesn't seem to work. Please double-check you copied it correctly, or try creating a new one.";
                    }
                    else if (friendlyError.Contains("rate limit"))
                    {
                        friendlyError = "GitHub is busy right now. Please wait a minute and try again.";
                    }
                    else if (friendlyError.Contains("Forbidden"))
                    {
                        friendlyError = "This code doesn't have the right permissions. Please create a new one following the steps above.";
                    }

                    validationMessage = friendlyError;
                }
            }
            catch (Exception ex)
            {
                tokenValidated = false;
                validationMessage = $"Something went wrong: {ex.Message}\n\nPlease check your internet connection and try again.";
            }
            finally
            {
                validatingToken = false;
            }
        }

        private bool IsValidEmail(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        public static string GetCreatorPayPalEmail()
        {
            return EditorPrefs.GetString("U3D_PayPalEmail", "");
        }

        private void UpdateCompletion()
        {
            IsComplete = FirebaseConfigManager.IsConfigurationComplete() &&
                        U3DAuthenticator.IsLoggedIn &&
                        !string.IsNullOrEmpty(U3DAuthenticator.CreatorUsername) &&
                        GitHubTokenManager.HasValidToken;
        }

        private void EnsureFirebaseConfiguration()
        {
        }

        private async Task QuickStateCheck()
        {
            await DetermineInitialState();
        }

        public void OnEnable()
        {
            if (ShouldSkipDuringBuild())
            {
                return;
            }

            if (WasAuthenticatedBeforeCompilation && !U3DAuthenticator.IsLoggedIn)
            {
                _ = U3DAuthenticator.TryAutoLogin();
            }

            if (!SetupTabInitialized)
            {
                Initialize();
            }
            else
            {
                _ = QuickStateCheck();
            }
        }
    }
}