using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace U3D
{
    /// <summary>
    /// Runtime owner of the Instructions canvas text. The Add Instructions editor tool bakes a
    /// preview of this text into the canvas at creation time so it isn't blank in the Scene view,
    /// but at runtime this component rebuilds it from the live input asset and the spawned local
    /// player's feature flags and overwrites the bake. That means binding changes shipped in
    /// template updates appear in every published space on its next build - no canvas
    /// regeneration - and the flag lines reflect the actual player instance in the scene,
    /// including per-scene Inspector overrides, not the prefab.
    ///
    /// The rebuild waits for the local player to spawn (flags live on the spawned instance). If
    /// no player appears within the wait window (e.g. networking failed), the text is built with
    /// every flag shown rather than leaving the stale bake. Runs once at start; bindings don't
    /// change mid-session.
    ///
    /// BuildText is the single source of truth for the text - the editor tool calls it for the
    /// preview bake, so preview and runtime can't drift.
    ///
    /// All emitted text is restricted to ASCII. The default LiberationSans SDF font asset has no
    /// glyphs beyond Latin-1, and any character outside that range renders as a missing-glyph box
    /// and logs a warning every time the text object rebuilds.
    /// </summary>
    public class U3DControlsInstructions : MonoBehaviour
    {
        [Tooltip("The input actions asset to read bindings from. Assigned automatically by the Add Instructions tool. If empty, falls back to the player input asset at runtime.")]
        [SerializeField] private InputActionAsset inputActions;

        [Tooltip("The text element this component writes the controls list into. Assigned automatically by the Add Instructions tool. If empty, falls back to a child named 'Instructions Text'.")]
        [SerializeField] private TextMeshProUGUI targetText;

        private const float PLAYER_WAIT_TIMEOUT = 15f;
        private const float PLAYER_POLL_INTERVAL = 0.5f;

        private const string SectionRule = "-----------------------------";

        /// <summary>
        /// Called by the Add Instructions editor tool at canvas creation to wire the serialized
        /// references.
        /// </summary>
        public void Configure(InputActionAsset actions, TextMeshProUGUI text)
        {
            inputActions = actions;
            targetText = text;
        }

        private void Start()
        {
            StartCoroutine(RefreshWhenPlayerReady());
        }

        private IEnumerator RefreshWhenPlayerReady()
        {
            float deadline = Time.time + PLAYER_WAIT_TIMEOUT;
            U3DPlayerController player = U3DPlayerController.FindLocalPlayer();

            while (player == null && Time.time < deadline)
            {
                yield return new WaitForSeconds(PLAYER_POLL_INTERVAL);
                player = U3DPlayerController.FindLocalPlayer();
            }

            ApplyText(player);
        }

        private void ApplyText(U3DPlayerController playerController)
        {
            InputActionAsset asset = inputActions;
            if (asset == null && U3D.Input.U3DPlayerInput.Instance != null)
                asset = U3D.Input.U3DPlayerInput.Instance.ActionAsset;

            TextMeshProUGUI text = targetText;
            if (text == null)
            {
                foreach (TextMeshProUGUI candidate in GetComponentsInChildren<TextMeshProUGUI>(true))
                {
                    if (candidate.name == "Instructions Text")
                    {
                        text = candidate;
                        break;
                    }
                }
            }
            if (text == null) return;

            text.text = BuildText(asset, playerController);
        }

        /// <summary>
        /// Builds the full controls text from the given input asset and player controller.
        /// Either argument may be null: a null controller shows every feature line; a null asset
        /// shows the intro sections with a note in place of the bindings list. Called at runtime
        /// by this component and at edit time by the Add Instructions tool for the preview bake.
        /// </summary>
        public static string BuildText(InputActionAsset inputActions, U3DPlayerController playerController)
        {
            var sb = new System.Text.StringBuilder();

            bool showMovement = playerController == null || playerController.EnableMovement;
            bool showJump = playerController == null || playerController.EnableJumping;
            bool showSprint = playerController == null || playerController.EnableSprintToggle;
            bool showCrouch = playerController == null || playerController.EnableCrouchToggle;
            bool showFly = playerController == null || playerController.EnableFlying;
            bool showAutoRun = playerController == null || playerController.EnableAutoRun;
            bool showTeleport = playerController == null || playerController.EnableTeleport;
            bool showZoom = playerController == null || playerController.EnableViewZoom;
            bool showAdvancedCam = playerController == null || playerController.EnableAdvancedCamera;

            // BASIC MOVEMENT
            var basicLines = new List<string>();
            if (showMovement) basicLines.Add("Walk: W A S D  or  Arrow Keys");
            if (showMovement && showSprint) basicLines.Add("Run: Shift (toggle)");
            if (showMovement && showJump) basicLines.Add("Jump: Space");
            if (showMovement && showCrouch) basicLines.Add("Crouch: C");

            if (basicLines.Count > 0)
            {
                sb.AppendLine("<b>BASIC MOVEMENT</b>");
                sb.AppendLine(SectionRule);
                foreach (var line in basicLines) sb.AppendLine(line);
                sb.AppendLine();
            }

            // CAMERA + UI
            var cameraLines = new List<string>();
            cameraLines.Add("Look: Right Mouse + Move");
            cameraLines.Add("Interact: R");
            cameraLines.Add("Remove: X");
            cameraLines.Add("Free Cursor (stay in game): Tab");
            cameraLines.Add("Free Cursor (return to browser): Esc");
            if (showZoom) cameraLines.Add("Zoom: Mouse Wheel");

            sb.AppendLine("<b>CAMERA + UI</b>");
            sb.AppendLine(SectionRule);
            foreach (var line in cameraLines) sb.AppendLine(line);
            sb.AppendLine();

            // SPECIAL MOVEMENT
            var specialLines = new List<string>();
            if (showFly) specialLines.Add("Fly: F (toggle)");
            if (showMovement) specialLines.Add("Strafe: Q / E");
            if (showAdvancedCam && showMovement) specialLines.Add("Move Forward: Left + Right Mouse");
            if (showAdvancedCam && showMovement) specialLines.Add("Steer: Left + Right Mouse + Move Mouse");
            if (showAutoRun && showMovement) specialLines.Add("Auto-Run: Num Lock (toggle)");
            if (showTeleport) specialLines.Add("Teleport: Double-Click");

            if (specialLines.Count > 0)
            {
                sb.AppendLine("<b>SPECIAL MOVEMENT</b>");
                sb.AppendLine(SectionRule);
                foreach (var line in specialLines) sb.AppendLine(line);
                sb.AppendLine();
            }

            // ALL INPUT BINDINGS
            sb.AppendLine("<b>ALL INPUT BINDINGS</b>");
            sb.AppendLine(SectionRule);

            if (inputActions == null)
            {
                sb.AppendLine("(Input Action asset not found)");
                return sb.ToString();
            }

            var playerMap = inputActions.FindActionMap("Player");
            if (playerMap == null)
            {
                sb.AppendLine("(Player action map not found)");
                return sb.ToString();
            }

            // Map action names to the feature flag that controls them. Null = always shown.
            System.Func<string, bool> isActionEnabled = (actionName) =>
            {
                if (playerController == null) return true;
                switch (actionName)
                {
                    case "Jump": return playerController.EnableJumping;
                    case "Sprint": return playerController.EnableSprintToggle;
                    case "Crouch": return playerController.EnableCrouchToggle;
                    case "Fly": return playerController.EnableFlying;
                    case "AutoRun":
                    case "AutoRunToggle": return playerController.EnableAutoRun;
                    case "Teleport": return playerController.EnableTeleport;
                    case "Zoom": return playerController.EnableViewZoom;
                    case "Move":
                    case "StrafeLeft":
                    case "StrafeRight":
                    case "TurnLeft":
                    case "TurnRight": return playerController.EnableMovement;
                    default: return true;
                }
            };

            // Actions whose XR bindings exist in the asset but do nothing in a VR
            // session by VR camera architecture, not by bug:
            //   Zoom - FOV change has no effect; the WebXR XR Display Subsystem
            //           supplies per-eye projection from the device every frame.
            //   PerspectiveSwitch - third-person is blocked by the head-bone camera
            //           position lock in U3DPlayerController.LateUpdate and the
            //           _isInVRMode early-return in HandleCameraPositioning.
            // Both still work on desktop, so they are only excluded from the VR
            // CONTROLS section below - not from keyboard/mouse. The input bindings
            // are intentionally retained (inert) for if/when VR camera work makes
            // these functional; remove names from this set when that happens.
            var vrNonFunctionalActions = new HashSet<string> { "Zoom", "PerspectiveSwitch" };

            // Keyboard/mouse bindings
            var keyboardLines = new List<string>();
            foreach (var action in playerMap.actions)
            {
                if (!isActionEnabled(action.name)) continue;
                string keys = GetBindingDisplayString(action, BindingDeviceFilter.KeyboardMouse);
                if (!string.IsNullOrEmpty(keys))
                    keyboardLines.Add($"{GetActionDisplayName(action.name)}: {keys}");
            }

            if (keyboardLines.Count > 0)
            {
                foreach (var line in keyboardLines)
                    sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine("(No keyboard or mouse bindings)");
            }

            // VR CONTROLS (only if XR bindings exist)
            var xrLines = new List<string>();
            foreach (var action in playerMap.actions)
            {
                if (!isActionEnabled(action.name)) continue;
                if (vrNonFunctionalActions.Contains(action.name)) continue;
                string xr = GetBindingDisplayString(action, BindingDeviceFilter.XR);
                if (!string.IsNullOrEmpty(xr))
                    xrLines.Add($"{GetActionDisplayName(action.name)}: {xr}");
            }

            if (xrLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("<b>VR CONTROLS</b>");
                sb.AppendLine(SectionRule);
                foreach (var line in xrLines)
                    sb.AppendLine(line);
            }

            return sb.ToString();
        }

        private static string GetActionDisplayName(string actionName)
        {
            // Override action names that don't self-explain to visitors.
            // The action name itself stays unchanged in the asset - this only
            // affects what the Movement Instructions UI shows.
            switch (actionName)
            {
                case "Pause": return "Free Cursor (stay in game)";
                case "Escape": return "Free Cursor (return to browser)";
                case "PerspectiveSwitch": return "Camera Perspective";
                case "AutoRunToggle": return "Auto-Run";
                case "MouseLeft": return "Primary Click";
                case "MouseRight": return "Camera Look (hold)";
                case "Remove": return "Remove Attachments and Steerables";
                default: return actionName;
            }
        }

        private enum BindingDeviceFilter
        {
            KeyboardMouse,
            XR
        }

        private static string GetBindingDisplayString(InputAction action, BindingDeviceFilter filter)
        {
            var entries = new List<string>();
            var bindings = action.bindings;

            for (int i = 0; i < bindings.Count; i++)
            {
                var binding = bindings[i];

                // Handle composites (like 2D Vector for WASD) as a single entry
                if (binding.isComposite)
                {
                    string compositeDisplay = FormatComposite(bindings, i, filter);
                    if (!string.IsNullOrEmpty(compositeDisplay) && !entries.Contains(compositeDisplay))
                        entries.Add(compositeDisplay);

                    // Skip ahead past all parts of this composite
                    int j = i + 1;
                    while (j < bindings.Count && bindings[j].isPartOfComposite)
                        j++;
                    i = j - 1;
                    continue;
                }

                // Skip orphan composite parts (shouldn't happen, but safe)
                if (binding.isPartOfComposite) continue;

                if (!BindingMatchesFilter(binding.effectivePath, filter)) continue;

                string display = FormatSingleBinding(binding.effectivePath, filter);
                if (!string.IsNullOrEmpty(display) && !entries.Contains(display))
                    entries.Add(display);
            }

            return string.Join("  |  ", entries);
        }

        private static string FormatComposite(IReadOnlyList<InputBinding> bindings, int compositeIndex, BindingDeviceFilter filter)
        {
            // Gather parts of the composite
            var partDisplays = new List<string>();
            for (int j = compositeIndex + 1; j < bindings.Count; j++)
            {
                if (!bindings[j].isPartOfComposite) break;

                string path = bindings[j].effectivePath;
                if (!BindingMatchesFilter(path, filter)) continue;

                string part = FormatSingleBinding(path, filter);
                if (!string.IsNullOrEmpty(part))
                    partDisplays.Add(part);
            }

            if (partDisplays.Count == 0) return null;

            // For 2D Vector composites (WASD, arrows), the order is Up/Down/Left/Right.
            // Render as a compact group rather than "Up | Down | Left | Right".
            if (partDisplays.Count == 4)
                return string.Join(" ", partDisplays);

            // For 1D axis or other composites, join with slashes
            return string.Join(" / ", partDisplays);
        }

        private static bool BindingMatchesFilter(string effectivePath, BindingDeviceFilter filter)
        {
            if (string.IsNullOrEmpty(effectivePath)) return false;

            bool isXR = effectivePath.Contains("<XRController>")
                     || effectivePath.Contains("<XRHMD>")
                     || effectivePath.Contains("<WebXRController>");

            if (filter == BindingDeviceFilter.XR) return isXR;

            // KeyboardMouse: exclude XR, exclude gamepad (not currently supported),
            // include keyboard and mouse
            if (isXR) return false;
            return effectivePath.Contains("<Keyboard>") || effectivePath.Contains("<Mouse>");
        }

        private static string FormatSingleBinding(string effectivePath, BindingDeviceFilter filter)
        {
            if (filter == BindingDeviceFilter.XR)
                return FormatXRBinding(effectivePath);

            string display = InputControlPath.ToHumanReadableString(
                effectivePath,
                InputControlPath.HumanReadableStringOptions.OmitDevice);

            if (string.IsNullOrEmpty(display)) return null;

            display = display
                .Replace("Up Arrow", "Up")
                .Replace("Down Arrow", "Down")
                .Replace("Left Arrow", "Left")
                .Replace("Right Arrow", "Right")
                .Replace("Left Shift", "Shift")
                .Replace("Left Ctrl", "Ctrl")
                .Replace("Mouse Delta", "Mouse")
                .Replace("Scroll Y", "Mouse Wheel");

            return display;
        }

        private static string FormatXRBinding(string effectivePath)
        {
            if (string.IsNullOrEmpty(effectivePath)) return null;

            // Extract handedness from {LeftHand} or {RightHand} usage tag
            string hand = null;
            if (effectivePath.Contains("{LeftHand}")) hand = "Left";
            else if (effectivePath.Contains("{RightHand}")) hand = "Right";

            // Extract the control name (the last path segment)
            int lastSlash = effectivePath.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= effectivePath.Length - 1) return null;

            string control = effectivePath.Substring(lastSlash + 1);

            // Map XR control names to readable labels
            string readable = PrettifyXRControl(control);
            if (string.IsNullOrEmpty(readable)) return null;

            return hand != null ? $"{hand} {readable}" : readable;
        }

        private static string PrettifyXRControl(string control)
        {
            if (string.IsNullOrEmpty(control)) return null;

            switch (control)
            {
                case "trigger":
                case "triggerButton":
                case "triggerPressed":
                    return "Trigger";
                case "grip":
                case "gripButton":
                case "gripPressed":
                    return "Grip";
                case "primaryButton":
                case "primaryPressed":
                    return "Primary Button (A/X)";
                case "secondaryButton":
                case "secondaryPressed":
                    return "Secondary Button (B/Y)";
                case "menuButton":
                    return "Menu Button";
                case "primary2DAxis":
                case "thumbstick":
                    return "Thumbstick";
                case "primary2DAxisClick":
                case "thumbstickClicked":
                    return "Thumbstick Click";
                case "secondary2DAxis":
                case "touchpad":
                    return "Touchpad";
                case "secondary2DAxisClick":
                case "touchpadClicked":
                    return "Touchpad Click";
                case "devicePosition":
                    return "Controller Position";
                case "deviceRotation":
                    return "Controller Rotation";
                case "centerEyePosition":
                    return "Headset Position";
                case "centerEyeRotation":
                    return "Headset Rotation";
                default:
                    // Fallback: insert spaces before capitals and title-case
                    return System.Text.RegularExpressions.Regex.Replace(
                        control, "([a-z])([A-Z])", "$1 $2");
            }
        }
    }
}