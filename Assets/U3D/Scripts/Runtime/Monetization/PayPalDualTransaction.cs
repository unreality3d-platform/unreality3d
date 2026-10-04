using System;
using System.Runtime.InteropServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace U3D
{
    /// <summary>
    /// Core component for PayPal dual transaction processing.
    /// DIRECT PayPal Orders v2 API integration - bypasses Firebase Functions entirely.
    /// Handles automatic 95%/5% payment splitting between creator and platform.
    /// </summary>
    public class PayPalDualTransaction : MonoBehaviour
    {
        [Header("Transaction Configuration")]
        [SerializeField] private string itemName = "Premium Content";
        [SerializeField] private string itemDescription = "Creator content purchase";
        [SerializeField] private float itemPrice = 5.00f;
        [SerializeField] private bool allowVariableAmount = false;
        [SerializeField] private float minimumAmount = 1.00f;
        [SerializeField] private float maximumAmount = 100.00f;

        [Header("UI References")]
        [SerializeField] private Button paymentButton;
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private TMP_Text priceText;
        [SerializeField] private TMP_InputField amountInputField;
        [SerializeField] private GameObject loadingIndicator;

        [Header("Events")]
        public UnityEngine.Events.UnityEvent OnPaymentSuccess = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent OnPaymentFailed = new UnityEngine.Events.UnityEvent();
        public UnityEngine.Events.UnityEvent<string> OnStatusChanged = new UnityEngine.Events.UnityEvent<string>();

        [Header("Network Input Coordination")]
        [SerializeField] private bool useNetworkInputCoordination = true;

        private bool _uiInputManagerEnsured = false;

        // DIRECT PayPal API JavaScript bridge imports - NO Firebase Functions
        [DllImport("__Internal")]
        private static extern void UnityStartDirectPayPalTransaction(string gameObjectName, string itemName, string itemDescription, string price, string creatorEmail, string transactionId);

        [DllImport("__Internal")]
        private static extern void UnityTestDirectPayPalConnection(string gameObjectName);

        // Keep legacy methods for backward compatibility (will be deprecated)
        [DllImport("__Internal")]
        private static extern void UnityStartDualTransactionWithGameObject(string gameObjectName, string itemName, string itemDescription, string price, string transactionId);

        [DllImport("__Internal")]
        private static extern void UnityCheckAuthenticationStatusWithGameObject(string gameObjectName);

        private string currentTransactionId;
        private bool isProcessing = false;
        private string creatorPayPalEmail;

        private void Start()
        {
            InitializeComponent();
            ValidateSetup();

            if (useNetworkInputCoordination)
                EnsureUIInputManager();
        }

        /// <summary>
        /// Makes sure this panel carries a U3DUIInputManager so player input is suppressed
        /// while the payment UI has focus. The handler registers and unregisters itself with
        /// U3DPlayerInput in its own lifecycle, so nothing here needs an input reference and
        /// this no longer silently does nothing when input has not initialized yet.
        /// </summary>
        private void EnsureUIInputManager()
        {
            if (_uiInputManagerEnsured) return;

            var uiManager = GetComponent<U3DUIInputManager>();
            if (uiManager == null)
            {
                uiManager = gameObject.AddComponent<U3DUIInputManager>();
                uiManager.componentName = "PayPal Transaction UI";
                uiManager.inputPriority = 10;
                uiManager.blockAllInput = true;
            }

            _uiInputManagerEnsured = true;
        }

        private void InitializeComponent()
        {
            LoadCreatorPayPalEmail();

            if (paymentButton != null)
                paymentButton.onClick.AddListener(StartPayment);

            if (amountInputField != null)
            {
                amountInputField.gameObject.SetActive(allowVariableAmount);
                amountInputField.onValueChanged.AddListener(OnAmountChanged);
                amountInputField.text = itemPrice.ToString("F2");
            }

            UpdateUI();
        }

        private void LoadCreatorPayPalEmail()
        {
            string paypalEmail = "";

            var creatorData = Resources.Load<U3DCreatorData>("U3DCreatorData");
            if (creatorData != null && !string.IsNullOrEmpty(creatorData.PayPalEmail))
            {
                paypalEmail = creatorData.PayPalEmail;
            }

#if UNITY_EDITOR
            if (string.IsNullOrEmpty(paypalEmail))
            {
                paypalEmail = EditorPrefs.GetString("U3D_PayPalEmail", "");
                if (!string.IsNullOrEmpty(paypalEmail))
                    SyncPayPalEmailToScriptableObject(paypalEmail);
            }

            if (string.IsNullOrEmpty(paypalEmail))
            {
                try
                {
                    var authenticatorType = System.Type.GetType("U3DAuthenticator");
                    if (authenticatorType != null)
                    {
                        var getPayPalMethod = authenticatorType.GetMethod("GetPayPalEmail", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (getPayPalMethod != null)
                        {
                            var authEmail = (string)getPayPalMethod.Invoke(null, null);
                            if (!string.IsNullOrEmpty(authEmail))
                            {
                                paypalEmail = authEmail;
                                SyncPayPalEmailToScriptableObject(paypalEmail);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Could not access U3DAuthenticator via reflection: {ex.Message}");
                }
            }
#endif

            this.creatorPayPalEmail = paypalEmail;

            if (string.IsNullOrEmpty(this.creatorPayPalEmail))
                Debug.LogWarning("PayPal email not found - direct PayPal integration requires creator email for dual transactions");
        }

#if UNITY_EDITOR
        private void SyncPayPalEmailToScriptableObject(string email)
        {
            try
            {
                var assetPath = "Assets/U3D/Resources/U3DCreatorData.asset";

                var resourcesPath = "Assets/U3D/Resources";
                if (!AssetDatabase.IsValidFolder(resourcesPath))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/U3D"))
                        AssetDatabase.CreateFolder("Assets", "U3D");

                    AssetDatabase.CreateFolder("Assets/U3D", "Resources");
                }

                var data = AssetDatabase.LoadAssetAtPath<U3DCreatorData>(assetPath);
                if (data == null)
                {
                    data = ScriptableObject.CreateInstance<U3DCreatorData>();
                    AssetDatabase.CreateAsset(data, assetPath);
                }

                data.PayPalEmail = email;
                EditorUtility.SetDirty(data);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Could not sync PayPal email to ScriptableObject: {ex.Message}");
            }
        }
#endif

        private void ValidateSetup()
        {
            if (string.IsNullOrEmpty(creatorPayPalEmail))
                LoadCreatorPayPalEmail();

            if (string.IsNullOrEmpty(creatorPayPalEmail))
            {
                SetStatus("PayPal email required. Please complete setup first.");
                if (paymentButton != null)
                    paymentButton.interactable = false;
                return;
            }

            if (itemPrice < 0.01f)
            {
                SetStatus("Invalid price configuration.");
                if (paymentButton != null)
                    paymentButton.interactable = false;
                return;
            }

            if (allowVariableAmount)
                SetStatus("Ready to send tip (Direct PayPal, 95% Creator, 5% Platform)");
            else
                SetStatus("Ready to accept payments (Direct PayPal, 95% Creator, 5% Platform)");

            if (paymentButton != null)
                paymentButton.interactable = true;
        }

        public void RefreshPayPalEmail()
        {
            LoadCreatorPayPalEmail();
            ValidateSetup();
        }

        private void UpdateUI()
        {
            if (priceText != null)
            {
                if (allowVariableAmount)
                    priceText.text = $"${minimumAmount:F2} - ${maximumAmount:F2}";
                else
                    priceText.text = $"${itemPrice:F2}";
            }

            if (paymentButton != null)
            {
                var buttonText = paymentButton.GetComponentInChildren<TMP_Text>();
                if (buttonText != null)
                {
                    if (allowVariableAmount)
                        buttonText.text = "Send Payment";
                    else
                        buttonText.text = $"Pay ${itemPrice:F2}";
                }
            }
        }

        public void StartPayment()
        {
            if (isProcessing)
                return;

            float finalAmount = GetFinalAmount();
            if (!ValidateAmount(finalAmount))
                return;

            if (string.IsNullOrEmpty(creatorPayPalEmail))
            {
                SetStatus("Creator PayPal email not configured");
                return;
            }

            currentTransactionId = Guid.NewGuid().ToString();

            SetProcessingState(true);
            SetStatus("Initializing direct PayPal payment...");

            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                UnityStartDirectPayPalTransaction(
                    gameObject.name,
                    itemName,
                    itemDescription,
                    finalAmount.ToString("F2"),
                    creatorPayPalEmail,
                    currentTransactionId
                );
#else
                StartCoroutine(SimulateEditorPayment());
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError($"Direct PayPal payment initialization failed: {ex.Message}");
                SetStatus("Payment initialization failed");
                SetProcessingState(false);
            }
        }

        public void TestDirectPayPalConnection()
        {
            if (string.IsNullOrEmpty(creatorPayPalEmail))
            {
                SetStatus("PayPal email required for connection test");
                return;
            }

            SetStatus("Testing direct PayPal connection...");

            try
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                UnityTestDirectPayPalConnection(gameObject.name);
#else
                StartCoroutine(SimulateConnectionTest());
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError($"Connection test failed: {ex.Message}");
                SetStatus("Connection test failed");
            }
        }

        private System.Collections.IEnumerator SimulateEditorPayment()
        {
            yield return new UnityEngine.WaitForSeconds(2f);
            OnPaymentComplete("true");
        }

        private System.Collections.IEnumerator SimulateConnectionTest()
        {
            yield return new UnityEngine.WaitForSeconds(1f);
            OnConnectionTestComplete("true");
        }

        private float GetFinalAmount()
        {
            if (allowVariableAmount && amountInputField != null)
            {
                if (float.TryParse(amountInputField.text, out float userAmount))
                    return userAmount;

                return minimumAmount;
            }
            return itemPrice;
        }

        private bool ValidateAmount(float amount)
        {
            if (allowVariableAmount)
            {
                if (amount < minimumAmount)
                {
                    SetStatus($"Minimum tip amount is ${minimumAmount:F2}");
                    return false;
                }
                if (amount > maximumAmount)
                {
                    SetStatus($"Maximum tip amount is ${maximumAmount:F2}");
                    return false;
                }
            }

            if (amount < 0.01f)
            {
                SetStatus("Amount must be at least $0.01");
                return false;
            }

            if (allowVariableAmount)
                SetStatus("Ready to send tip (Direct PayPal, 95% Creator, 5% Platform)");
            else
                SetStatus("Ready to purchase (Direct PayPal, 95% Creator, 5% Platform)");

            return true;
        }

        private void OnAmountChanged(string value)
        {
            if (float.TryParse(value, out float amount))
            {
                ValidateAmount(amount);
                UpdateUI();
            }
            else if (!string.IsNullOrEmpty(value))
            {
                SetStatus("Please enter a valid amount");
            }
        }

        public void OnPaymentComplete(string success)
        {
            SetProcessingState(false);

            if (success == "true")
            {
                if (allowVariableAmount)
                    SetStatus("Tip sent successfully! Thank you!");
                else
                    SetStatus("Payment successful!");

                OnPaymentSuccess?.Invoke();

                if (!allowVariableAmount && paymentButton != null)
                {
                    paymentButton.interactable = false;
                    var buttonText = paymentButton.GetComponentInChildren<TMP_Text>();
                    if (buttonText != null)
                        buttonText.text = "Paid";
                }
                else if (allowVariableAmount)
                {
                    StartCoroutine(ResetTipJarAfterDelay());
                }
            }
            else
            {
                SetStatus("Payment failed. Please try again.");
                OnPaymentFailed?.Invoke();
                Debug.LogWarning($"Direct PayPal dual transaction failed for {itemName}");
            }
        }

        public void OnConnectionTestComplete(string success)
        {
            if (success == "true")
                SetStatus("Direct PayPal connection successful!");
            else
            {
                SetStatus("Direct PayPal connection failed");
                Debug.LogWarning("Direct PayPal connection test failed");
            }
        }

        public void OnTransactionDetails(string transactionData)
        {
            try
            {
                JsonUtility.FromJson<TransactionDetails>(transactionData);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to parse transaction details: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator ResetTipJarAfterDelay()
        {
            yield return new UnityEngine.WaitForSeconds(3f);

            if (allowVariableAmount)
            {
                SetStatus("Ready to send tip (Direct PayPal)");

                if (amountInputField != null)
                    amountInputField.text = itemPrice.ToString("F2");
            }
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
                statusText.text = message;

            OnStatusChanged?.Invoke(message);
        }

        private void SetProcessingState(bool processing)
        {
            isProcessing = processing;

            if (paymentButton != null)
                paymentButton.interactable = !processing && !string.IsNullOrEmpty(creatorPayPalEmail);

            if (loadingIndicator != null)
                loadingIndicator.SetActive(processing);

            if (amountInputField != null)
                amountInputField.interactable = !processing;
        }

        public void SetItemDetails(string name, string description, float price)
        {
            itemName = name;
            itemDescription = description;
            itemPrice = price;
            UpdateUI();
        }

        public void SetVariableAmount(bool enabled, float min = 1.00f, float max = 100.00f)
        {
            allowVariableAmount = enabled;
            minimumAmount = min;
            maximumAmount = max;

            if (amountInputField != null)
                amountInputField.gameObject.SetActive(enabled);

            UpdateUI();
        }

        public void SetCreatorPayPalEmail(string email)
        {
            creatorPayPalEmail = email;
            ValidateSetup();
        }

        public string GetCreatorPayPalEmail()
        {
            return creatorPayPalEmail;
        }

        public float GetCreatorAmount(float totalAmount)
        {
            return totalAmount * 0.95f;
        }

        public float GetPlatformAmount(float totalAmount)
        {
            return totalAmount * 0.05f;
        }

        public void AssignUIReferences(Button payButton, TMP_Text statusTextComponent, TMP_Text priceTextComponent = null, TMP_InputField amountInput = null)
        {
            if (paymentButton != null)
                paymentButton.onClick.RemoveAllListeners();

            if (amountInputField != null)
                amountInputField.onValueChanged.RemoveAllListeners();

            paymentButton = payButton;
            statusText = statusTextComponent;
            priceText = priceTextComponent;
            amountInputField = amountInput;

            if (paymentButton != null)
                paymentButton.onClick.AddListener(StartPayment);

            if (amountInputField != null)
            {
                amountInputField.gameObject.SetActive(allowVariableAmount);
                amountInputField.onValueChanged.AddListener(OnAmountChanged);
                amountInputField.text = itemPrice.ToString("F2");
                amountInputField.characterLimit = 7;
                amountInputField.contentType = TMP_InputField.ContentType.DecimalNumber;
            }

            UpdateUI();
            ValidateSetup();
        }

        public void SetupAsTipJar(float minAmount = 1.00f, float maxAmount = 100.00f, string message = "Thank you for supporting my work!")
        {
            SetItemDetails("Creator Tip", message, 5.00f);
            SetVariableAmount(true, minAmount, maxAmount);
        }

        public bool ValidateUIComponents()
        {
            bool isValid = true;
            System.Text.StringBuilder issues = new System.Text.StringBuilder();

            if (paymentButton == null)
            {
                issues.AppendLine("• Payment Button is not assigned");
                isValid = false;
            }

            if (statusText == null)
            {
                issues.AppendLine("• Status Text is not assigned");
                isValid = false;
            }

            if (allowVariableAmount && amountInputField == null)
            {
                issues.AppendLine("• Amount Input Field is required for variable amounts");
                isValid = false;
            }

            if (!isValid)
            {
                Debug.LogWarning($"PayPal UI Validation Issues:\n{issues}");
                SetStatus("UI components not properly configured");
            }

            return isValid;
        }

        public void AssignEssentialReferences(Button payButton, TMP_Text statusTextComponent)
        {
            AssignUIReferences(payButton, statusTextComponent, null, null);
        }

        public void AutoAssignUIReferences()
        {
            var foundButton = GetComponentInChildren<Button>();
            var foundStatusText = transform.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
            var foundPriceText = transform.Find("PriceText")?.GetComponent<TextMeshProUGUI>();
            var foundAmountInput = GetComponentInChildren<TMP_InputField>();

            AssignUIReferences(foundButton, foundStatusText, foundPriceText, foundAmountInput);
        }

        public bool HasValidUIReferences()
        {
            return paymentButton != null && statusText != null;
        }

        [Serializable]
        private class TransactionDetails
        {
            public string creatorTransactionId;
            public string platformTransactionId;
            public float creatorAmount;
            public float platformAmount;
            public float totalAmount;
        }

        private void OnValidate()
        {
            if (itemPrice < 0)
                itemPrice = 0;

            if (minimumAmount < 0.01f)
                minimumAmount = 0.01f;

            if (maximumAmount < minimumAmount)
                maximumAmount = minimumAmount;
        }

        private void OnEnable()
        {
            if (Application.isPlaying)
                return;

#if UNITY_EDITOR
            ValidateSetupInEditor();
#endif
        }

#if UNITY_EDITOR
        private void ValidateSetupInEditor()
        {
            var creatorData = Resources.Load<U3DCreatorData>("U3DCreatorData");
            bool hasPayPalEmail = creatorData != null && !string.IsNullOrEmpty(creatorData.PayPalEmail);

            if (statusText != null)
            {
                if (!hasPayPalEmail)
                    statusText.text = "PayPal email required for direct integration";
                else if (allowVariableAmount)
                    statusText.text = "Ready to send tip (Direct PayPal, 95% Creator, 5% Platform)";
                else
                    statusText.text = "Ready to accept payments (Direct PayPal, 95% Creator, 5% Platform)";
            }
        }
#endif
    }
}