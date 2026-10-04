using System;
using System.Collections.Generic;
using TMPro;
using U3D;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace U3D.Editor
{
    public class MonetizationToolsCategory : IToolCategory
    {
        public string CategoryName => "Monetization";
        public System.Action<int> OnRequestTabSwitch { get; set; }

        private List<CreatorTool> communityTools;
        private List<CreatorTool> commerceTools;

        private bool paypalConfigurationChecked = false;
        private bool paypalConfigured = true;

        public MonetizationToolsCategory()
        {
            communityTools = new List<CreatorTool>
            {
                new CreatorTool("🟢 Add Tip Jar", "Accept variable donations with dual transaction splitting", CreateTipJar, false)
            };

            commerceTools = new List<CreatorTool>
            {
                new CreatorTool("🟢 Add Purchase Button", "Single item PayPal purchase with dual transaction (95% to creator)", CreatePurchaseButton, false),
                new CreatorTool("🟢 Add Scene Gate", "Scene entry payment gate with PayPal dual transaction", CreateSceneGate, false),
                new CreatorTool("🟢 Add Shop Object", "3D world PayPal shop with multiple items and dual transactions", CreateShopObject, false),
                new CreatorTool("🟢 Add Event Gate", "Timed event access with PayPal dual transaction", CreateEventGate, false),
                new CreatorTool("🟢 Add Screen Shop", "Screen overlay PayPal shop interface with dual transactions", CreateScreenShop, false)
            };
        }

        public List<CreatorTool> GetTools()
        {
            var all = new List<CreatorTool>(communityTools);
            all.AddRange(commerceTools);
            return all;
        }

        public void DrawCategory()
        {
            EditorGUILayout.LabelField("Monetization Tools", EditorStyles.boldLabel);

            if (!paypalConfigurationChecked)
            {
                CheckPayPalConfiguration();
            }

            if (paypalConfigured)
            {
                DrawConfiguredState();
            }
            else
            {
                DrawUnconfiguredState();
            }

            EditorGUILayout.Space(10);

            EditorGUILayout.LabelField("Community Support", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            foreach (var tool in communityTools)
            {
                if (!paypalConfigured)
                {
                    EditorGUI.BeginDisabledGroup(true);
                }

                ProjectToolsTab.DrawCategoryTool(tool);

                if (!paypalConfigured)
                {
                    EditorGUI.EndDisabledGroup();
                }
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("In-Scene Commerce", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "These tools work in any Unity WebGL project deployed outside of Unreality3D — " +
                "itch.io, self-hosted builds, or any other WebGL host. In-scene commerce within " +
                "Unreality3D-deployed experiences is planned for a future platform update when " +
                "creator demand justifies the hosting work.",
                MessageType.Warning);

            if (GUILayout.Button("Open U3D Market Packager →", EditorStyles.linkLabel))
            {
                EditorPrefs.SetBool("U3D_NavigateToMarketPackager", true);
                OnRequestTabSwitch?.Invoke(3);
            }

            EditorGUILayout.Space(4);

            foreach (var tool in commerceTools)
            {
                if (!paypalConfigured)
                {
                    EditorGUI.BeginDisabledGroup(true);
                }

                ProjectToolsTab.DrawCategoryTool(tool);

                if (!paypalConfigured)
                {
                    EditorGUI.EndDisabledGroup();
                }
            }

            if (!paypalConfigured)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.HelpBox(
                    "All monetization tools require PayPal email to function.",
                    MessageType.Info);
            }
        }

        private void CheckPayPalConfiguration()
        {
            string paypalEmail = "";

            paypalEmail = EditorPrefs.GetString("U3D_PayPalEmail", "");

            if (string.IsNullOrEmpty(paypalEmail))
            {
                var creatorData = Resources.Load<U3DCreatorData>("U3DCreatorData");
                if (creatorData != null)
                {
                    paypalEmail = creatorData.PayPalEmail;
                }
            }

            if (string.IsNullOrEmpty(paypalEmail))
            {
                paypalEmail = U3DAuthenticator.GetPayPalEmail();
            }

            paypalConfigured = !string.IsNullOrEmpty(paypalEmail);
            paypalConfigurationChecked = true;
        }

        private void DrawConfiguredState()
        {
            string paypalEmail = U3DAuthenticator.GetPayPalEmail();

            EditorGUILayout.HelpBox(
                $"PayPal Connected: {paypalEmail}\n\n" +
                "Dual Transaction System Ready:\n" +
                "• You keep 95% of all earnings\n" +
                "• Platform fee: 5% (for hosting & infrastructure)\n" +
                "• Automatic payment splitting\n" +
                "• Direct payments to your PayPal account",
                MessageType.Info);
        }

        private void DrawUnconfiguredState()
        {
            EditorGUILayout.HelpBox(
                "PayPal Email Not Added\n\n" +
                "To enable monetization:\n" +
                "1. Go to the Setup tab\n" +
                "2. Add your PayPal email address\n" +
                "3. Return here to add payment tools\n\n" +
                "You'll keep 95% of all earnings!",
                MessageType.Warning);

            EditorGUILayout.Space(5);

            if (GUILayout.Button("Go to Setup Tab", GUILayout.Height(30)))
            {
                OnRequestTabSwitch?.Invoke(0);
            }

            EditorGUILayout.Space(10);
        }

        public void RefreshPayPalConfiguration()
        {
            paypalConfigurationChecked = false;
            CheckPayPalConfiguration();
        }

        #region Tool Creation Methods

        private void CreatePurchaseButton()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            GameObject buttonObject = CreatePaymentUI("Purchase Button", CreatePurchaseButtonUI);

            var dualTransaction = buttonObject.AddComponent<PayPalDualTransaction>();
            dualTransaction.SetItemDetails("Premium Content", "Creator content purchase", 5.00f);
            dualTransaction.SetVariableAmount(false);

            AssignUIReferences(buttonObject, dualTransaction);

            NotifyToolCreated("Purchase Button");
        }

        private void CreateTipJar()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            Canvas canvas = CreateNewWorldSpaceCanvas("Tip Jar Canvas");

            var uiResources = new DefaultControls.Resources();
            GameObject container = DefaultControls.CreatePanel(uiResources);
            container.name = "Tip Jar";
            container.transform.SetParent(canvas.transform, false);

            U3DUIStyle.ApplyPanelStyle(container);

            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.5f, 0.5f);
            containerRect.anchorMax = new Vector2(0.5f, 0.5f);
            containerRect.sizeDelta = new Vector2(300, 200);
            containerRect.anchoredPosition = Vector2.zero;

            U3DUIStyle.CreateHeader(container, "Tip Jar");

            var tmpResources = new TMP_DefaultControls.Resources();

            GameObject inputField = TMP_DefaultControls.CreateInputField(tmpResources);
            inputField.name = "AmountInput";
            inputField.transform.SetParent(container.transform, false);

            var inputRect = inputField.GetComponent<RectTransform>();
            inputRect.anchorMin = new Vector2(0.1f, 0.6f);
            inputRect.anchorMax = new Vector2(0.9f, 0.75f);
            inputRect.offsetMin = Vector2.zero;
            inputRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyInputFieldStyle(inputField);

            var inputComponent = inputField.GetComponent<TMP_InputField>();
            inputComponent.text = "5.00";
            inputComponent.contentType = TMP_InputField.ContentType.DecimalNumber;

            var placeholder = inputComponent.placeholder as TextMeshProUGUI;
            if (placeholder != null)
            {
                placeholder.text = "Enter tip amount ($1.00 - $100.00)";
                U3DUIStyle.ApplyPlaceholderStyle(placeholder);
            }

            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = "TipButton";
            button.transform.SetParent(container.transform, false);

            var buttonRect = button.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.1f, 0.35f);
            buttonRect.anchorMax = new Vector2(0.9f, 0.55f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyButtonStyle(button, "Send Tip");

            U3DUIStyle.CreateStatusText(container, "Ready to accept payments");

            var dualTransaction = container.AddComponent<PayPalDualTransaction>();
            dualTransaction.SetItemDetails("Creator Tip", "Support this creator's work", 5.00f);
            dualTransaction.SetVariableAmount(true, 1.00f, 100.00f);

            AssignUIReferences(container, dualTransaction);

            Selection.activeGameObject = canvas.gameObject;
            NotifyToolCreated("Tip Jar");
        }

        private void CreateSceneGate()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            GameObject gateObject = CreateScreenOverlayUI("Scene Gate", CreateSceneGateOverlayUI);

            var dualTransaction = gateObject.AddComponent<PayPalDualTransaction>();
            var gateController = gateObject.AddComponent<SceneGateController>();

            dualTransaction.SetItemDetails("Scene Access", "Premium scene access required", 3.00f);
            dualTransaction.SetVariableAmount(false);
            dualTransaction.OnPaymentSuccess.AddListener(gateController.OpenGate);

            AssignUIReferences(gateObject, dualTransaction);

            NotifyToolCreated("Scene Gate");
        }

        private void CreateSceneGateOverlayUI(GameObject container)
        {
            var tmpResources = new TMP_DefaultControls.Resources();
            var uiResources = new DefaultControls.Resources();

            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = Vector2.zero;
            containerRect.offsetMax = Vector2.zero;

            var containerImage = container.GetComponent<Image>();
            if (containerImage == null)
            {
                containerImage = container.AddComponent<Image>();
            }
            U3DUIStyle.StripSprite(container);
            containerImage.color = new Color32(255, 255, 255, 100);
            containerImage.raycastTarget = true;

            GameObject contentPanel = DefaultControls.CreatePanel(uiResources);
            contentPanel.name = "ContentPanel";
            contentPanel.transform.SetParent(container.transform, false);

            U3DUIStyle.ApplyPanelStyle(contentPanel);

            var contentRect = contentPanel.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.5f, 0.5f);
            contentRect.anchorMax = new Vector2(0.5f, 0.5f);
            contentRect.sizeDelta = new Vector2(400, 300);
            contentRect.anchoredPosition = Vector2.zero;

            U3DUIStyle.CreateHeader(contentPanel, "Scene Access Required");

            GameObject messageText = TMP_DefaultControls.CreateText(tmpResources);
            messageText.name = "MessageText";
            messageText.transform.SetParent(contentPanel.transform, false);

            var messageRect = messageText.GetComponent<RectTransform>();
            messageRect.anchorMin = new Vector2(0.1f, 0.5f);
            messageRect.anchorMax = new Vector2(0.9f, 0.75f);
            messageRect.offsetMin = Vector2.zero;
            messageRect.offsetMax = Vector2.zero;

            var messageTMP = messageText.GetComponent<TextMeshProUGUI>();
            if (messageTMP != null)
            {
                messageTMP.text = "This premium content requires payment to access.\n\nSupport the creator and unlock this experience!";
                U3DUIStyle.ApplyBodyStyle(messageTMP);
                messageTMP.raycastTarget = false;
            }

            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = "UnlockButton";
            button.transform.SetParent(contentPanel.transform, false);

            var buttonRect = button.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.2f, 0.25f);
            buttonRect.anchorMax = new Vector2(0.8f, 0.4f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            U3DUIStyle.StripSprite(button);
            var buttonText = button.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                buttonText.text = "Unlock Scene - $3.00";
                U3DUIStyle.ApplyBodyStyle(buttonText);
            }

            U3DUIStyle.CreateStatusText(contentPanel, "Ready to accept payment (95% Creator, 5% Platform)");
        }

        private void CreateShopObject()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            GameObject shopObject = CreatePaymentUI("Shop Object", CreateShopObjectUI);

            var shopController = shopObject.AddComponent<U3D.ShopController>();

            NotifyToolCreated("Shop Object");
        }

        private void CreateEventGate()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            GameObject eventObject = CreatePaymentUI("Event Gate", CreateEventGateUI);

            var dualTransaction = eventObject.AddComponent<PayPalDualTransaction>();
            var eventController = eventObject.AddComponent<U3D.EventGateController>();

            dualTransaction.SetItemDetails("Event Access", "Special event ticket", 10.00f);
            dualTransaction.SetVariableAmount(false);
            dualTransaction.OnPaymentSuccess.AddListener(eventController.GrantAccess);

            AssignUIReferences(eventObject, dualTransaction);

            NotifyToolCreated("Event Gate");
        }

        private void CreateScreenShop()
        {
            if (!ValidatePayPalSetupOnDemand()) return;

            GameObject screenShop = CreateScreenOverlayUI("Screen Shop", CreateScreenShopUI);

            var screenShopController = screenShop.AddComponent<U3D.ScreenShopController>();

            NotifyToolCreated("Screen Shop");
        }

        #endregion

        #region UI Creation Helpers

        private Canvas CreateNewWorldSpaceCanvas(string canvasName)
        {
            var canvasObject = new GameObject(canvasName);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();
            canvas.transform.localScale = Vector3.one * U3DUIStyle.WorldspaceCanvasScale;

            return canvas;
        }

        private Canvas CreateNewOverlayCanvas(string canvasName)
        {
            var canvasObject = new GameObject(canvasName);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasObject.AddComponent<CanvasScaler>();
            canvasObject.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        private GameObject CreatePaymentUI(string name, System.Action<GameObject> customSetup)
        {
            Canvas canvas = CreateNewWorldSpaceCanvas($"{name} Canvas");

            var uiResources = new DefaultControls.Resources();
            GameObject container = DefaultControls.CreatePanel(uiResources);
            container.name = name;
            container.transform.SetParent(canvas.transform, false);

            U3DUIStyle.ApplyPanelStyle(container);

            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.5f, 0.5f);
            containerRect.anchorMax = new Vector2(0.5f, 0.5f);
            containerRect.sizeDelta = new Vector2(300, 200);
            containerRect.anchoredPosition = Vector2.zero;

            U3DUIStyle.CreateHeader(container, name);

            customSetup?.Invoke(container);

            Selection.activeGameObject = canvas.gameObject;
            return container;
        }

        private GameObject CreateScreenOverlayUI(string name, System.Action<GameObject> customSetup)
        {
            Canvas canvas = CreateNewOverlayCanvas($"{name} Canvas");

            var uiResources = new DefaultControls.Resources();
            GameObject container = DefaultControls.CreatePanel(uiResources);
            container.name = name;
            container.transform.SetParent(canvas.transform, false);

            U3DUIStyle.ApplyPanelStyle(container);

            var containerRect = container.GetComponent<RectTransform>();
            containerRect.anchorMin = new Vector2(0.5f, 0.5f);
            containerRect.anchorMax = new Vector2(0.5f, 0.5f);
            containerRect.sizeDelta = new Vector2(400, 300);
            containerRect.anchoredPosition = Vector2.zero;

            U3DUIStyle.CreateHeader(container, name);
            customSetup?.Invoke(container);

            Selection.activeGameObject = canvas.gameObject;
            return container;
        }

        private void CreatePurchaseButtonUI(GameObject container)
        {
            var tmpResources = new TMP_DefaultControls.Resources();

            GameObject priceText = TMP_DefaultControls.CreateText(tmpResources);
            priceText.name = "PriceText";
            priceText.transform.SetParent(container.transform, false);

            var priceRect = priceText.GetComponent<RectTransform>();
            priceRect.anchorMin = new Vector2(0, 0.5f);
            priceRect.anchorMax = new Vector2(1, 0.7f);
            priceRect.offsetMin = new Vector2(10, 0);
            priceRect.offsetMax = new Vector2(-10, 0);

            var priceTMP = priceText.GetComponent<TextMeshProUGUI>();
            if (priceTMP != null)
            {
                priceTMP.text = "$5.00";
                U3DUIStyle.ApplyBodyStyle(priceTMP);
                priceTMP.raycastTarget = false;
            }

            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = "PaymentButton";
            button.transform.SetParent(container.transform, false);

            var buttonRect = button.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.1f, 0.25f);
            buttonRect.anchorMax = new Vector2(0.9f, 0.45f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyButtonStyle(button, "Purchase");

            U3DUIStyle.CreateStatusText(container, "Ready to accept payments");
        }

        private void CreateShopObjectUI(GameObject container)
        {
            var tmpResources = new TMP_DefaultControls.Resources();
            var uiResources = new DefaultControls.Resources();

            GameObject shopTitle = TMP_DefaultControls.CreateText(tmpResources);
            shopTitle.name = "ShopTitle";
            shopTitle.transform.SetParent(container.transform, false);

            var titleRect = shopTitle.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 0.65f);
            titleRect.anchorMax = new Vector2(1, 0.78f);
            titleRect.offsetMin = new Vector2(10, 0);
            titleRect.offsetMax = new Vector2(-10, 0);

            var titleTMP = shopTitle.GetComponent<TextMeshProUGUI>();
            if (titleTMP != null)
            {
                titleTMP.text = "Creator Shop";
                U3DUIStyle.ApplyTitleStyle(titleTMP);
                titleTMP.raycastTarget = false;
            }

            GameObject scrollView = DefaultControls.CreateScrollView(uiResources);
            scrollView.name = "ItemScrollView";
            scrollView.transform.SetParent(container.transform, false);

            var scrollRect = scrollView.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.05f, 0.2f);
            scrollRect.anchorMax = new Vector2(0.95f, 0.6f);
            scrollRect.offsetMin = Vector2.zero;
            scrollRect.offsetMax = Vector2.zero;

            U3DUIStyle.ConfigureVerticalOnlyScrollView(scrollView);

            U3DUIStyle.CreateStatusText(container, "Ready to accept payments");
        }

        private void CreateEventGateUI(GameObject container)
        {
            var tmpResources = new TMP_DefaultControls.Resources();

            GameObject eventInfo = TMP_DefaultControls.CreateText(tmpResources);
            eventInfo.name = "EventInfo";
            eventInfo.transform.SetParent(container.transform, false);

            var infoRect = eventInfo.GetComponent<RectTransform>();
            infoRect.anchorMin = new Vector2(0, 0.6f);
            infoRect.anchorMax = new Vector2(1, 0.75f);
            infoRect.offsetMin = new Vector2(10, 0);
            infoRect.offsetMax = new Vector2(-10, 0);

            var infoTMP = eventInfo.GetComponent<TextMeshProUGUI>();
            if (infoTMP != null)
            {
                infoTMP.text = "Special Event Access";
                U3DUIStyle.ApplyBodyStyle(infoTMP);
                infoTMP.raycastTarget = false;
            }

            GameObject timerText = TMP_DefaultControls.CreateText(tmpResources);
            timerText.name = "TimerText";
            timerText.transform.SetParent(container.transform, false);

            var timerRect = timerText.GetComponent<RectTransform>();
            timerRect.anchorMin = new Vector2(0, 0.45f);
            timerRect.anchorMax = new Vector2(1, 0.6f);
            timerRect.offsetMin = new Vector2(10, 0);
            timerRect.offsetMax = new Vector2(-10, 0);

            var timerTMP = timerText.GetComponent<TextMeshProUGUI>();
            if (timerTMP != null)
            {
                timerTMP.text = "Event Active";
                U3DUIStyle.ApplyStatusStyle(timerTMP);
                timerTMP.raycastTarget = false;
            }

            GameObject button = TMP_DefaultControls.CreateButton(tmpResources);
            button.name = "AccessButton";
            button.transform.SetParent(container.transform, false);

            var buttonRect = button.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.1f, 0.25f);
            buttonRect.anchorMax = new Vector2(0.9f, 0.4f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyButtonStyle(button, "Buy Ticket");

            U3DUIStyle.CreateStatusText(container, "Ready to accept payments");
        }

        private void CreateScreenShopUI(GameObject container)
        {
            var tmpResources = new TMP_DefaultControls.Resources();
            var uiResources = new DefaultControls.Resources();

            GameObject closeButton = TMP_DefaultControls.CreateButton(tmpResources);
            closeButton.name = "CloseButton";
            closeButton.transform.SetParent(container.transform, false);

            var closeRect = closeButton.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(0.9f, 0.9f);
            closeRect.anchorMax = new Vector2(0.98f, 0.98f);
            closeRect.offsetMin = Vector2.zero;
            closeRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyButtonStyle(closeButton, "X");

            GameObject contentArea = DefaultControls.CreateScrollView(uiResources);
            contentArea.name = "ShopContent";
            contentArea.transform.SetParent(container.transform, false);

            var contentRect = contentArea.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0.05f, 0.1f);
            contentRect.anchorMax = new Vector2(0.95f, 0.85f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;

            U3DUIStyle.ConfigureVerticalOnlyScrollView(contentArea);

            U3DUIStyle.CreateStatusText(container, "Ready to accept payments");
        }

        private void AssignUIReferences(GameObject container, PayPalDualTransaction dualTransaction)
        {
            var paymentButton = container.transform.Find("PaymentButton")?.GetComponent<Button>();
            if (paymentButton == null)
            {
                paymentButton = container.transform.Find("TipButton")?.GetComponent<Button>();
            }
            if (paymentButton == null)
            {
                paymentButton = container.transform.Find("UnlockButton")?.GetComponent<Button>();
            }
            if (paymentButton == null)
            {
                paymentButton = container.transform.Find("AccessButton")?.GetComponent<Button>();
            }

            var statusText = container.transform.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
            var priceText = container.transform.Find("PriceText")?.GetComponent<TextMeshProUGUI>();
            var amountInput = container.transform.Find("AmountInput")?.GetComponent<TMP_InputField>();

            dualTransaction.AssignUIReferences(paymentButton, statusText, priceText, amountInput);

            string paypalEmail = U3DAuthenticator.GetPayPalEmail();
            if (!string.IsNullOrEmpty(paypalEmail))
            {
                dualTransaction.SetCreatorPayPalEmail(paypalEmail);
            }
            else
            {
                Debug.LogWarning($"No PayPal email available for {container.name}. Component will require setup before use.");
            }
        }

        #endregion

        #region Validation and Logging

        private bool ValidatePayPalSetupOnDemand()
        {
            string paypalEmail = "";

            paypalEmail = EditorPrefs.GetString("U3D_PayPalEmail", "");

            if (string.IsNullOrEmpty(paypalEmail))
            {
                var creatorData = Resources.Load<U3DCreatorData>("U3DCreatorData");
                if (creatorData != null)
                {
                    paypalEmail = creatorData.PayPalEmail;
                }
            }

            if (string.IsNullOrEmpty(paypalEmail))
            {
                paypalEmail = U3DAuthenticator.GetPayPalEmail();
            }

            if (string.IsNullOrEmpty(paypalEmail))
            {
                bool goToSetup = EditorUtility.DisplayDialog(
                    "PayPal Setup Required",
                    "Please configure your PayPal email to enable monetization tools.\n\n" +
                    "This enables the dual transaction system where you keep 95% of earnings.\n\n" +
                    "Would you like to go to the Setup tab now?",
                    "Yes, Take Me There", "Cancel"
                );

                if (goToSetup)
                {
                    OnRequestTabSwitch?.Invoke(0);
                }

                paypalConfigured = false;
                paypalConfigurationChecked = true;

                return false;
            }

            paypalConfigured = true;
            paypalConfigurationChecked = true;

            return true;
        }

        private void NotifyToolCreated(string toolName)
        {
            EditorUtility.DisplayDialog(
                "Monetization Tool Created!",
                $"{toolName} has been created successfully.\n\n" +
                "Dual Transaction System:\n" +
                "• You keep 95% of all payments\n" +
                "• Platform fee: 5%\n" +
                "• Automatic payment splitting\n" +
                "• Direct PayPal payments\n\n" +
                "Configure the component settings to customize pricing and behavior.",
                "Great!"
            );
        }

        #endregion
    }
}