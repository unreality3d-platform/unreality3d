using UnityEngine;
using TMPro;

namespace U3D
{
    /// <summary>
    /// Controller for Scene Gate monetization tool.
    /// Manages full-screen overlay that blocks access until payment is made.
    /// </summary>
    public class SceneGateController : MonoBehaviour
    {
        [Header("Gate Configuration")]
        [SerializeField] private bool isOpen = false;
        [SerializeField] private bool blockGameplayInput = true;

        [Header("Scene Control")]
        [SerializeField] private string blockedMessage = "Payment required to access this content";

        private Canvas gateCanvas;

        private void Start()
        {
            gateCanvas = GetComponentInParent<Canvas>();

            // Ensure gate starts closed and blocks everything
            if (gateCanvas != null)
            {
                // Set canvas to highest sort order to block everything
                gateCanvas.sortingOrder = 1000;
                gateCanvas.gameObject.SetActive(!isOpen);
            }

            // Block input if specified
            if (blockGameplayInput && !isOpen)
            {
                BlockGameplayInput();
            }
        }

        /// <summary>
        /// Opens the gate after successful payment
        /// </summary>
        public void OpenGate()
        {
            isOpen = true;

            Debug.Log("Scene gate opened - full access granted!");

            // Hide the gate overlay
            if (gateCanvas != null)
            {
                gateCanvas.gameObject.SetActive(false);
            }

            // Re-enable gameplay input
            if (blockGameplayInput)
            {
                EnableGameplayInput();
            }

            // Update status text if available
            var statusText = GetComponentInChildren<TextMeshProUGUI>();
            if (statusText != null && statusText.name == "StatusText")
            {
                statusText.text = "Access granted! Welcome!";
            }

            // Optional: Add a brief success message before hiding
            StartCoroutine(ShowSuccessMessage());
        }

        /// <summary>
        /// Closes the gate (for testing or reset purposes)
        /// </summary>
        public void CloseGate()
        {
            isOpen = false;

            if (gateCanvas != null)
            {
                gateCanvas.gameObject.SetActive(true);
                gateCanvas.sortingOrder = 1000;
            }

            if (blockGameplayInput)
            {
                BlockGameplayInput();
            }

            var statusText = GetComponentInChildren<TextMeshProUGUI>();
            if (statusText != null && statusText.name == "StatusText")
            {
                statusText.text = "Ready to accept payment (95% Creator, 5% Platform)";
            }

            var unlockButton = GetComponentInChildren<UnityEngine.UI.Button>();
            if (unlockButton != null)
            {
                unlockButton.interactable = true;
                var buttonText = unlockButton.GetComponentInChildren<TextMeshProUGUI>();
                if (buttonText != null)
                {
                    buttonText.text = "Unlock Scene - $3.00";
                }
            }
        }

        /// <summary>
        /// Check if the gate is currently open
        /// </summary>
        public bool IsOpen() => isOpen;

        /// <summary>
        /// Block all gameplay input (you can expand this based on your input system)
        /// </summary>
        private void BlockGameplayInput()
        {
            // Disable common input components - expand based on your game's input system
            var playerInputs = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var input in playerInputs)
            {
                // You can add specific input component disabling here
                // For example: if (input is PlayerController) input.enabled = false;
            }

            // Set time scale to 0 to pause the game (optional)
            // Time.timeScale = 0f;
        }

        /// <summary>
        /// Re-enable gameplay input
        /// </summary>
        private void EnableGameplayInput()
        {
            // Re-enable input components
            var playerInputs = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            foreach (var input in playerInputs)
            {
                // Re-enable specific input components
                // For example: if (input is PlayerController) input.enabled = true;
            }

            // Restore time scale
            // Time.timeScale = 1f;
        }

        /// <summary>
        /// Show a brief success message before hiding the gate
        /// </summary>
        private System.Collections.IEnumerator ShowSuccessMessage()
        {
            // Update button to show success
            var unlockButton = GetComponentInChildren<UnityEngine.UI.Button>();
            if (unlockButton != null)
            {
                unlockButton.interactable = false;
                var buttonText = unlockButton.GetComponentInChildren<TextMeshProUGUI>();
                if (buttonText != null)
                {
                    buttonText.text = "Access Granted!";
                }
            }

            // Wait 2 seconds then hide the gate
            yield return new WaitForSeconds(2f);

            if (gateCanvas != null)
            {
                gateCanvas.gameObject.SetActive(false);
            }
        }

        /// <summary>
        /// Set custom blocked message
        /// </summary>
        public void SetBlockedMessage(string message)
        {
            blockedMessage = message;

            var messageText = transform.Find("ContentPanel/MessageText")?.GetComponent<TextMeshProUGUI>();
            if (messageText != null)
            {
                messageText.text = message;
            }
        }

        /// <summary>
        /// Set whether to block gameplay input
        /// </summary>
        public void SetBlockGameplayInput(bool block)
        {
            blockGameplayInput = block;
        }

        // Editor validation
        private void OnValidate()
        {
            if (Application.isPlaying && gateCanvas != null)
            {
                gateCanvas.gameObject.SetActive(!isOpen);
            }
        }
    }
}