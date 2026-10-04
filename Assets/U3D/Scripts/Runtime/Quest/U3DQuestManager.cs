using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

namespace U3D
{
    /// <summary>
    /// Manages all quests in the scene. Handles quest progression, completion, and UI updates.
    /// This component should be placed on a single GameObject in your scene.
    /// </summary>
    [AddComponentMenu("U3D/Quest System/U3D Quest Manager")]
    public class U3DQuestManager : MonoBehaviour
    {
        [Header("Quest System Configuration")]
        [Tooltip("Automatically assign available quests to this manager when they're added to the scene")]
        [SerializeField] private bool autoDiscoverQuests = true;

        [Tooltip("List of all quests in the scene. Quests will auto-populate here if Auto Discover is enabled")]
        [SerializeField] private List<U3DQuest> availableQuests = new List<U3DQuest>();

        [Tooltip("Currently active quests that players can work on")]
        [SerializeField] private List<U3DQuest> activeQuests = new List<U3DQuest>();

        [Header("UI References")]
        [Tooltip("Canvas that displays the quest log UI. Leave empty to use default quest UI")]
        [SerializeField] private Canvas questLogCanvas;

        [Tooltip("Parent transform where quest UI elements will be created")]
        [SerializeField] private Transform questListParent;

        [Header("Quest Events")]
        [Tooltip("Called when any quest is started")]
        public UnityEvent<U3DQuest> OnQuestStarted;

        [Tooltip("Called when any quest is completed")]
        public UnityEvent<U3DQuest> OnQuestCompleted;

        [Tooltip("Called when any objective is updated")]
        public UnityEvent<U3DQuestObjective> OnObjectiveUpdated;

        [Header("Audio (Optional)")]
        [Tooltip("Sound played when a quest is started")]
        [SerializeField] private AudioClip questStartSound;

        [Tooltip("Sound played when a quest is completed")]
        [SerializeField] private AudioClip questCompleteSound;

        [Tooltip("Sound played when an objective is completed")]
        [SerializeField] private AudioClip objectiveCompleteSound;

        private AudioSource audioSource;
        private static U3DQuestManager instance;
        private Dictionary<U3DQuest, GameObject> questUIElements = new Dictionary<U3DQuest, GameObject>();

        public static U3DQuestManager Instance
        {
            get
            {
                if (instance == null)
                    instance = FindAnyObjectByType<U3DQuestManager>();
                return instance;
            }
        }

        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
                audioSource = GetComponent<AudioSource>();
                if (audioSource == null)
                    audioSource = gameObject.AddComponent<AudioSource>();

                // CRITICAL: Discover quests in Awake to fix execution order
                if (autoDiscoverQuests)
                    DiscoverQuests();
            }
            else if (instance != this)
            {
                Debug.LogWarning("Multiple QuestManagers found. Destroying duplicate on: " + gameObject.name);
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            InitializeQuestUI();
        }

        /// <summary>
        /// Automatically finds all U3DQuest components in the scene
        /// </summary>
        private void DiscoverQuests()
        {
            U3DQuest[] allQuests = FindObjectsByType<U3DQuest>(FindObjectsSortMode.None);
            foreach (U3DQuest quest in allQuests)
            {
                if (!availableQuests.Contains(quest))
                {
                    availableQuests.Add(quest);
                    quest.Initialize(this);
                }
            }
        }

        /// <summary>
        /// Start a specific quest
        /// </summary>
        public void StartQuest(U3DQuest quest)
        {
            if (quest == null || activeQuests.Contains(quest))
                return;

            activeQuests.Add(quest);
            quest.StartQuest();

            PlaySound(questStartSound);
            OnQuestStarted?.Invoke(quest);

            CreateQuestUI(quest);
        }

        /// <summary>
        /// Start a quest by name
        /// </summary>
        public void StartQuest(string questName)
        {
            U3DQuest quest = availableQuests.Find(q => q.questTitle == questName);
            if (quest != null)
                StartQuest(quest);
            else
                Debug.LogWarning($"Quest not found: {questName}");
        }

        /// <summary>
        /// Complete a quest and handle rewards
        /// </summary>
        public void CompleteQuest(U3DQuest quest)
        {
            if (quest == null || !activeQuests.Contains(quest))
                return;

            activeQuests.Remove(quest);
            quest.CompleteQuest();

            PlaySound(questCompleteSound);
            OnQuestCompleted?.Invoke(quest);

            RemoveQuestUI(quest);
        }

        /// <summary>
        /// Called when an objective is updated
        /// </summary>
        public void OnObjectiveProgress(U3DQuestObjective objective)
        {
            if (objective.IsCompleted)
                PlaySound(objectiveCompleteSound);

            OnObjectiveUpdated?.Invoke(objective);

            // Check if parent quest is complete
            U3DQuest parentQuest = objective.GetComponentInParent<U3DQuest>();
            if (parentQuest != null && activeQuests.Contains(parentQuest))
            {
                // Update UI whenever objective progress changes, not just when quest completes
                UpdateQuestUI(parentQuest);

                if (parentQuest.IsCompleted)
                {
                    CompleteQuest(parentQuest);
                }
            }
        }

        /// <summary>
        /// Get all active quests
        /// </summary>
        public List<U3DQuest> GetActiveQuests()
        {
            return new List<U3DQuest>(activeQuests);
        }

        /// <summary>
        /// Check if a quest is currently active
        /// </summary>
        public bool IsQuestActive(U3DQuest quest)
        {
            return activeQuests.Contains(quest);
        }

        /// <summary>
        /// Initialize the quest UI - Canvas is always enabled if QuestManager exists
        /// </summary>
        private void InitializeQuestUI()
        {
            if (questLogCanvas != null)
            {
                questLogCanvas.gameObject.layer = LayerMask.NameToLayer("UI");
                questLogCanvas.sortingOrder = 100;
                // Canvas is always enabled when QuestManager exists
                questLogCanvas.gameObject.SetActive(true);
                questLogCanvas.enabled = true;
            }
            else
            {
                Debug.LogError("QuestLogCanvas is not assigned in QuestManager!");
            }
        }

        /// <summary>
        /// Play audio clip if available
        /// </summary>
        private void PlaySound(AudioClip clip)
        {
            if (clip != null && audioSource != null)
            {
                audioSource.PlayOneShot(clip);
            }
        }

        /// <summary>
        /// Reset all quest progress (useful for testing)
        /// </summary>
        [ContextMenu("Reset All Quests")]
        public void ResetAllQuests()
        {
            foreach (U3DQuest quest in availableQuests)
            {
                quest.ResetQuest();
            }
            activeQuests.Clear();
        }

        /// <summary>
        /// Create UI elements for a quest using Unity 6+ TextMeshPro with MUCH larger font sizes
        /// Add layout components only when creating quest UI (not on default Content)
        /// </summary>
        private void CreateQuestUI(U3DQuest quest)
        {
            if (questListParent == null || quest == null)
            {
                Debug.LogError("questListParent is null! Cannot create quest UI.");
                return;
            }

            if (questUIElements.ContainsKey(quest))
                return;

            // Add layout components to Content object only when first quest is added
            if (questUIElements.Count == 0)
            {
                // Add VerticalLayoutGroup to organize quest elements
                VerticalLayoutGroup layoutGroup = questListParent.GetComponent<VerticalLayoutGroup>();
                if (layoutGroup == null)
                {
                    layoutGroup = questListParent.gameObject.AddComponent<VerticalLayoutGroup>();
                    layoutGroup.spacing = 10f;
                    layoutGroup.padding = new RectOffset(10, 10, 10, 10);
                    layoutGroup.childControlWidth = true;
                    layoutGroup.childControlHeight = false;
                    layoutGroup.childForceExpandWidth = true;
                    layoutGroup.childForceExpandHeight = false;
                    layoutGroup.childAlignment = TextAnchor.UpperCenter;
                }

                // Add ContentSizeFitter to expand Content as needed
                ContentSizeFitter sizeFitter = questListParent.GetComponent<ContentSizeFitter>();
                if (sizeFitter == null)
                {
                    sizeFitter = questListParent.gameObject.AddComponent<ContentSizeFitter>();
                    sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }
            }

            // Create quest container
            GameObject questContainer = new GameObject($"Quest_{quest.questTitle}");
            questContainer.transform.SetParent(questListParent, false);
            questContainer.layer = LayerMask.NameToLayer("UI");

            // Add RectTransform component
            RectTransform containerRect = questContainer.AddComponent<RectTransform>();
            containerRect.anchorMin = Vector2.zero;
            containerRect.anchorMax = Vector2.one;
            containerRect.offsetMin = Vector2.zero;
            containerRect.offsetMax = Vector2.zero;

            // Add LayoutElement for proper sizing
            LayoutElement layoutElement = questContainer.AddComponent<LayoutElement>();
            layoutElement.minHeight = 80;
            layoutElement.preferredHeight = 100;

            // Create vertical layout for quest content
            VerticalLayoutGroup questLayout = questContainer.AddComponent<VerticalLayoutGroup>();
            questLayout.spacing = 5;
            questLayout.padding = new RectOffset(10, 10, 10, 10);
            questLayout.childControlWidth = true;
            questLayout.childControlHeight = false;
            questLayout.childForceExpandWidth = true;

            // Quest title using TextMeshPro with MUCH larger font
            GameObject titleObj = new GameObject("QuestTitle");
            titleObj.transform.SetParent(questContainer.transform, false);
            titleObj.layer = LayerMask.NameToLayer("UI");

            RectTransform titleRect = titleObj.AddComponent<RectTransform>();
            titleRect.sizeDelta = new Vector2(0, 25);

            TextMeshProUGUI titleText = titleObj.AddComponent<TextMeshProUGUI>();
            titleText.text = quest.questTitle;
            titleText.fontSize = 36; // INCREASED from 16 
            titleText.fontStyle = FontStyles.Bold;
            titleText.color = Color.white;
            titleText.alignment = TextAlignmentOptions.MidlineLeft;
            titleText.raycastTarget = false;

            // Create objectives
            foreach (U3DQuestObjective objective in GetQuestObjectives(quest))
            {
                CreateObjectiveUI(objective, questContainer.transform);
            }

            questUIElements[quest] = questContainer;

            // Force layout rebuild
            LayoutRebuilder.ForceRebuildLayoutImmediate(containerRect);
        }

        /// <summary>
        /// Create UI for individual objective using TextMeshPro with larger font
        /// </summary>
        private void CreateObjectiveUI(U3DQuestObjective objective, Transform parent)
        {
            GameObject objContainer = new GameObject($"Objective_{objective.objectiveDescription}");
            objContainer.transform.SetParent(parent, false);
            objContainer.layer = LayerMask.NameToLayer("UI");

            RectTransform objRect = objContainer.AddComponent<RectTransform>();
            objRect.sizeDelta = new Vector2(0, 35);

            TextMeshProUGUI objText = objContainer.AddComponent<TextMeshProUGUI>();
            objText.fontSize = 24; // INCREASED from 12
            objText.color = Color.white;
            objText.text = objective.GetObjectiveStatus();
            objText.alignment = TextAlignmentOptions.MidlineLeft;
            objText.raycastTarget = false;

            objContainer.name = $"ObjectiveUI_{objective.GetInstanceID()}";
        }

        /// <summary>
        /// Update UI for a quest
        /// </summary>
        private void UpdateQuestUI(U3DQuest quest)
        {
            if (!questUIElements.ContainsKey(quest))
                return;

            GameObject questContainer = questUIElements[quest];
            if (questContainer == null)
                return;

            // Update all objective texts
            foreach (U3DQuestObjective objective in GetQuestObjectives(quest))
            {
                Transform objUI = questContainer.transform.Find($"ObjectiveUI_{objective.GetInstanceID()}");
                if (objUI != null)
                {
                    TextMeshProUGUI objText = objUI.GetComponent<TextMeshProUGUI>();
                    if (objText != null)
                    {
                        objText.text = objective.GetObjectiveStatus();
                        objText.color = objective.IsCompleted ? Color.green : (objective.IsActive ? Color.white : Color.gray);
                    }
                }
            }
        }

        /// <summary>
        /// Remove UI for a quest
        /// </summary>
        private void RemoveQuestUI(U3DQuest quest)
        {
            if (questUIElements.ContainsKey(quest))
            {
                if (questUIElements[quest] != null)
                    Destroy(questUIElements[quest]);
                questUIElements.Remove(quest);
            }
        }

        /// <summary>
        /// Get objectives from a quest
        /// </summary>
        private List<U3DQuestObjective> GetQuestObjectives(U3DQuest quest)
        {
            return new List<U3DQuestObjective>(quest.GetComponentsInChildren<U3DQuestObjective>());
        }
    }
}