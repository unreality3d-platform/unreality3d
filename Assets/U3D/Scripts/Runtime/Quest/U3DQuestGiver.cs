using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using System.Collections.Generic;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Interaction choice modes for Quest Giver - ADDED for mutually exclusive system
    /// </summary>
    public enum U3DInteractionMode
    {
        Single,     // Default: Accept [E]
        Binary,     // Accept [E], Decline [X]
        Multiple    // Option A [1], Option B [2], Option C [3]
    }

    /// <summary>
    /// NPC or object that gives quests to players.
    /// UnityEvent field names updated to avoid interface method conflicts
    /// </summary>
    [AddComponentMenu("U3D/Quest System/U3D Quest Giver")]
    public partial class U3DQuestGiver : MonoBehaviour
    {
        [Header("Quest Giver Configuration")]
        [Tooltip("Name of this quest giver (shown in dialog)")]
        public string giverName = "Quest Giver";

        [Tooltip("The quest this NPC will give to players")]
        [SerializeField] private U3DQuest questToGive;

        [Tooltip("Can this quest giver give the same quest multiple times?")]
        [SerializeField] private bool allowRepeatQuests = false;

        [Header("Interaction Settings")]
        [Tooltip("How close the player needs to be to interact (0 = no distance limit)")]
        [SerializeField] private float interactionRange = 3f;

        [Tooltip("Should interaction happen automatically when player gets close?")]
        [SerializeField] private bool autoInteract = false;

        [Header("Interaction Choices")]
        // Mutually exclusive interaction mode system
        [Tooltip("Choose the interaction style for this quest giver")]
        [SerializeField] private U3DInteractionMode interactionMode = U3DInteractionMode.Single;

        [Tooltip("Single choice configuration (Accept button)")]
        [SerializeField] private U3DInteractionChoice singleChoice = new U3DInteractionChoice(KeyCode.E, "Accept", "accept");

        [Tooltip("Binary choice configuration (Accept/Decline buttons)")]
        [SerializeField] private U3DInteractionChoice acceptChoice = new U3DInteractionChoice(KeyCode.E, "Accept", "accept");
        [SerializeField] private U3DInteractionChoice declineChoice = new U3DInteractionChoice(KeyCode.X, "Decline", "decline");

        [Tooltip("Multiple choice configuration (Quiz-style options)")]
        [SerializeField]
        private List<U3DInteractionChoice> multipleChoices = new List<U3DInteractionChoice>
        {
            new U3DInteractionChoice(KeyCode.Alpha1, "Option A", "option_1"),
            new U3DInteractionChoice(KeyCode.Alpha2, "Option B", "option_2"),
            new U3DInteractionChoice(KeyCode.Alpha3, "Option C", "option_3")
        };

        [Header("UI Dialog")]
        [Tooltip("Canvas for quest dialog UI. An unstyled Dialogue Canvas will be created at runtime if one is not assigned.")]
        [SerializeField] private Canvas dialogCanvas;

        [Tooltip("TextMeshPro component for quest giver's name. Auto-created if not assigned.")]
        [SerializeField] private TextMeshProUGUI giverNameText;

        [Tooltip("TextMeshPro component for quest description. Auto-created if not assigned.")]
        [SerializeField] private TextMeshProUGUI questDescriptionText;

        [Tooltip("Parent object for interaction choice display. Auto-created if not assigned.")]
        [SerializeField] private Transform choicesParent;

        [Tooltip("Transform that defines where the dialog will appear. Dialog will be created here if assigned, otherwise 2 units above the Quest Giver.")]
        [SerializeField] private Transform dialogPositionTransform;

        [Header("Visual Indicators")]
        [Tooltip("GameObject shown when quest is available (like an exclamation mark)")]
        [SerializeField] private GameObject questAvailableIndicator;

        [Tooltip("GameObject shown when quest has been completed")]
        [SerializeField] private GameObject questCompletedIndicator;

        [Header("Dialog Text")]
        [Tooltip("What the quest giver says when offering the quest")]
        [TextArea(2, 4)]
        [SerializeField] private string questOfferText = "I have a task for you. Are you interested?";

        [Tooltip("What the quest giver says if quest is already active")]
        [TextArea(2, 4)]
        [SerializeField] private string questActiveText = "You're already working on my quest. Good luck!";

        [Tooltip("What the quest giver says if quest is completed")]
        [TextArea(2, 4)]
        [SerializeField] private string questCompletedText = "Thank you for completing my quest!";

        [Header("Creator Events")]
        [Tooltip("Called when a choice is selected")]
        public U3DInteractionChoiceEvent OnChoiceSelected;

        [Tooltip("Called when player enters interaction range")]
        public UnityEvent OnPlayerEnterRangeEvent;

        [Tooltip("Called when player exits interaction range")]
        public UnityEvent OnPlayerExitRangeEvent;

        private Transform playerTransform;
        private bool playerInRange = false;
        private bool questGiven = false;
        private List<TextMeshProUGUI> choiceDisplays = new List<TextMeshProUGUI>();

        private void Awake()
        {
            // Updated to use new mutually exclusive system
            ValidateInteractionChoices();
        }

        // Validation for new choice system
        private void ValidateInteractionChoices()
        {
            switch (interactionMode)
            {
                case U3DInteractionMode.Single:
                    singleChoice.choiceKey = U3DKeyManager.GetSafeAlternative(singleChoice.choiceKey);
                    break;
                case U3DInteractionMode.Binary:
                    acceptChoice.choiceKey = U3DKeyManager.GetSafeAlternative(acceptChoice.choiceKey);
                    declineChoice.choiceKey = U3DKeyManager.GetSafeAlternative(declineChoice.choiceKey);
                    break;
                case U3DInteractionMode.Multiple:
                    for (int i = 0; i < multipleChoices.Count; i++)
                    {
                        multipleChoices[i].choiceKey = U3DKeyManager.GetSafeAlternative(multipleChoices[i].choiceKey);
                    }
                    break;
            }
        }

        private void Start()
        {
            playerTransform = FindFirstObjectByType<U3DPlayerController>()?.transform;
            UpdateVisualState();
        }

        private void Update()
        {
            if (playerTransform != null && interactionRange > 0)
            {
                float distance = Vector3.Distance(transform.position, playerTransform.position);
                bool nowInRange = distance <= interactionRange;

                if (nowInRange != playerInRange)
                {
                    playerInRange = nowInRange;

                    // Your working proximity detection
                    if (dialogCanvas == null && playerInRange && CanGiveQuest())
                        CreateDefaultDialogUI();

                    if (dialogCanvas != null)
                        dialogCanvas.gameObject.SetActive(playerInRange && CanGiveQuest());

                    if (playerInRange)
                    {
                        OnPlayerEnterRangeEvent?.Invoke();
                        if (autoInteract)
                            StartInteraction();
                    }
                    else
                    {
                        OnPlayerExitRangeEvent?.Invoke();
                        CloseDialog();
                    }

                    UpdateVisualState();
                }
            }

            if (playerInRange && dialogCanvas != null && dialogCanvas.gameObject.activeInHierarchy)
            {
                HandleInput();
            }
        }

        private void UpdateVisualState()
        {
            if (questAvailableIndicator != null)
                questAvailableIndicator.SetActive(!questGiven && questToGive != null && !questToGive.IsActive && !questToGive.IsCompleted);

            if (questCompletedIndicator != null)
                questCompletedIndicator.SetActive(questToGive != null && questToGive.IsCompleted);
        }

        private void HandleInput()
        {
            // Updated to use new choice system
            List<U3DInteractionChoice> availableChoices = GetAvailableChoices();

            foreach (U3DInteractionChoice choice in availableChoices)
            {
                if (choice.WasKeyPressed()) // Your working Input System
                {
                    HandleChoiceSelected(choice);
                    break;
                }
            }
        }

        private void HandleChoiceSelected(U3DInteractionChoice choice)
        {
            OnChoiceSelected?.Invoke(choice);

            switch (choice.choiceID.ToLower())
            {
                case "accept":
                    AcceptQuest();
                    break;
                case "decline":
                case "close":
                    CloseDialog();
                    break;
                default:
                    CloseDialog();
                    break;
            }
        }

        public void StartInteraction()
        {
            if (questToGive == null) return;

            string dialogText = GetAppropriateDialogText();
            ShowDialog(dialogText);
        }

        private string GetAppropriateDialogText()
        {
            if (questToGive.IsCompleted)
                return questCompletedText;
            else if (questToGive.IsActive)
                return questActiveText;
            else
                return questOfferText;
        }

        private void ShowDialog(string dialogText)
        {
            if (dialogCanvas == null)
                CreateDefaultDialogUI();

            if (dialogCanvas != null)
            {
                dialogCanvas.gameObject.SetActive(true);

                if (giverNameText != null)
                    giverNameText.text = giverName;

                if (questDescriptionText != null)
                    questDescriptionText.text = dialogText;

                UpdateChoicesDisplay();
            }
        }

        private void UpdateChoicesDisplay()
        {
            if (choicesParent == null) return;

            foreach (TextMeshProUGUI choiceDisplay in choiceDisplays)
            {
                if (choiceDisplay != null)
                    DestroyImmediate(choiceDisplay.gameObject);
            }
            choiceDisplays.Clear();

            // Updated to use new choice system
            List<U3DInteractionChoice> availableChoices = GetAvailableChoices();

            foreach (U3DInteractionChoice choice in availableChoices)
            {
                CreateChoiceDisplay(choice);
            }
        }

        // Updated to use mutually exclusive system
        private List<U3DInteractionChoice> GetAvailableChoices()
        {
            List<U3DInteractionChoice> available = new List<U3DInteractionChoice>();

            if (questToGive.IsCompleted || questToGive.IsActive)
            {
                KeyCode interactKey = U3DKeyManager.GetPlayerInteractionKey();
                available.Add(new U3DInteractionChoice(interactKey, "Close", "close"));
            }
            else
            {
                switch (interactionMode)
                {
                    case U3DInteractionMode.Single:
                        available.Add(singleChoice);
                        break;
                    case U3DInteractionMode.Binary:
                        available.Add(acceptChoice);
                        available.Add(declineChoice);
                        break;
                    case U3DInteractionMode.Multiple:
                        available.AddRange(multipleChoices);
                        break;
                }
            }

            return available;
        }

        private void CreateChoiceDisplay(U3DInteractionChoice choice)
        {
            if (choicesParent == null)
            {
                Debug.LogError("choicesParent is null! Cannot create choice display.");
                return;
            }

            GameObject choiceObj = new GameObject($"Choice_{choice.choiceLabel}");
            choiceObj.transform.SetParent(choicesParent, false);
            choiceObj.layer = LayerMask.NameToLayer("UI");

            RectTransform rect = choiceObj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(200, 30);

            TextMeshProUGUI choiceText = choiceObj.AddComponent<TextMeshProUGUI>();
            choiceText.text = $"{choice.choiceLabel} [{choice.choiceKey}]";
            choiceText.fontSize = 24; 
            choiceText.color = Color.white;
            choiceText.alignment = TextAlignmentOptions.Center;
            choiceText.raycastTarget = false;

            choiceDisplays.Add(choiceText);
        }

        // Quest acceptance to properly connect with QuestManager
        private void AcceptQuest()
        {
            if (questToGive != null && !questToGive.IsActive && !questToGive.IsCompleted)
            {
                // CRITICAL: Use QuestManager.StartQuest() to ensure UI creation
                U3DQuestManager questManager = U3DQuestManager.Instance;
                if (questManager != null)
                {
                    // This will call questManager.StartQuest() which triggers CreateQuestUI()
                    questManager.StartQuest(questToGive);
                    questGiven = true;
                }
                else
                {
                    // Fallback: Direct quest start if no QuestManager (logs warning)
                    questToGive.StartQuest();
                    questGiven = true;
                    Debug.LogWarning("No QuestManager found! Quest started without UI registration.");
                }

                UpdateVisualState();
                CloseDialog();
            }
        }

        public void CloseDialog()
        {
            if (dialogCanvas != null)
                dialogCanvas.gameObject.SetActive(false);
        }

        public bool CanGiveQuest()
        {
            if (questToGive == null) return false;

            if (questToGive.IsCompleted && !allowRepeatQuests) return false;

            if (questToGive.IsActive) return true; // Can talk to show quest progress

            return true; // Can give quest
        }

        // CreateDefaultDialogUI to use WorldSpace instead of ScreenSpace
        private void CreateDefaultDialogUI()
        {
            GameObject canvasObj = new GameObject("QuestGiver_Dialog");
            canvasObj.layer = LayerMask.NameToLayer("UI");

            dialogCanvas = canvasObj.AddComponent<Canvas>();
            dialogCanvas.renderMode = RenderMode.WorldSpace; // CHANGED FROM ScreenSpaceOverlay
            dialogCanvas.worldCamera = Camera.main; // ADDED for WorldSpace
            dialogCanvas.sortingOrder = 100;

            canvasObj.AddComponent<GraphicRaycaster>();

            // Position canvas using dialogPositionTransform if available, otherwise above quest giver
            Transform dialogTransform = transform.Find("Dialog Position");
            if (dialogTransform != null)
            {
                canvasObj.transform.position = dialogTransform.position;
                canvasObj.transform.rotation = dialogTransform.rotation;
                canvasObj.transform.localScale = dialogTransform.localScale;
            }
            else
            {
                canvasObj.transform.position = transform.position + (transform.rotation * Vector3.up * 2f);
                canvasObj.transform.rotation = transform.rotation;
                canvasObj.transform.localScale = Vector3.one * 0.01f;
            }

            GameObject panelObj = new GameObject("DialogPanel");
            panelObj.transform.SetParent(canvasObj.transform, false);
            panelObj.layer = LayerMask.NameToLayer("UI");

            RectTransform panelRect = panelObj.AddComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(400, 350); // CHANGED from anchor-based sizing to fixed size

            Image panelImage = panelObj.AddComponent<Image>();
            panelImage.color = new Color(0, 0, 0, 0.8f);
            panelImage.raycastTarget = true;

            GameObject nameObj = new GameObject("GiverName");
            nameObj.transform.SetParent(panelObj.transform, false);
            nameObj.layer = LayerMask.NameToLayer("UI");

            RectTransform nameRect = nameObj.AddComponent<RectTransform>();
            nameRect.anchorMin = new Vector2(0, 0.8f);
            nameRect.anchorMax = new Vector2(1, 1);
            nameRect.offsetMin = new Vector2(10, 0);
            nameRect.offsetMax = new Vector2(-10, 0);

            giverNameText = nameObj.AddComponent<TextMeshProUGUI>();
            giverNameText.text = giverName;
            giverNameText.fontSize = 54; // Keep existing font size
            giverNameText.color = Color.white;
            giverNameText.alignment = TextAlignmentOptions.Center;
            giverNameText.raycastTarget = false;

            GameObject descObj = new GameObject("QuestDescription");
            descObj.transform.SetParent(panelObj.transform, false);
            descObj.layer = LayerMask.NameToLayer("UI");

            RectTransform descRect = descObj.AddComponent<RectTransform>();
            descRect.anchorMin = new Vector2(0, 0.4f);
            descRect.anchorMax = new Vector2(1, 0.8f);
            descRect.offsetMin = new Vector2(10, 0);
            descRect.offsetMax = new Vector2(-10, 0);

            questDescriptionText = descObj.AddComponent<TextMeshProUGUI>();
            questDescriptionText.text = questOfferText;
            questDescriptionText.fontSize = 42; // Keep existing font size
            questDescriptionText.color = Color.white;
            questDescriptionText.alignment = TextAlignmentOptions.Center;
            questDescriptionText.textWrappingMode = TextWrappingModes.Normal;
            questDescriptionText.raycastTarget = false;

            GameObject choicesObj = new GameObject("Choices");
            choicesObj.transform.SetParent(panelObj.transform, false);
            choicesObj.layer = LayerMask.NameToLayer("UI");

            RectTransform choicesRect = choicesObj.AddComponent<RectTransform>();
            choicesRect.anchorMin = new Vector2(0, 0.05f);
            choicesRect.anchorMax = new Vector2(1, 0.4f);
            choicesRect.offsetMin = new Vector2(10, 0);
            choicesRect.offsetMax = new Vector2(-10, 0);

            choicesParent = choicesObj.transform;

            // Keep dialog visible for testing and immediate choice population
            dialogCanvas.gameObject.SetActive(true);
            UpdateChoicesDisplay(); // Immediately creates the choice displays
        }

        [ContextMenu("Reset Quest Giver")]
        public void ResetQuestGiver()
        {
            questGiven = false;
            UpdateVisualState();
            CloseDialog();
        }

        private void OnDrawGizmosSelected()
        {
            if (interactionRange > 0)
            {
                Gizmos.color = Color.blue;
                Gizmos.DrawWireSphere(transform.position, interactionRange);
            }
        }
    }
}