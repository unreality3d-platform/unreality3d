using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Represents a single quest with objectives and rewards.
    /// Place this component on a GameObject and configure your quest details in the Inspector.
    /// </summary>
    [AddComponentMenu("U3D/Quest System/U3D Quest")]
    public class U3DQuest : MonoBehaviour
    {
        [Header("Quest Information")]
        [Tooltip("Display name for this quest shown to players")]
        public string questTitle = "New Quest";

        [Tooltip("Description explaining what the player needs to do")]
        [TextArea(3, 5)]
        public string questDescription = "Complete the objectives to finish this quest.";

        [Tooltip("Should this quest start automatically when the scene loads?")]
        [SerializeField] private bool startAutomatically = false;

        [Tooltip("Can this quest be restarted after completion?")]
        [SerializeField] private bool isRepeatable = false;

        [Header("Quest Objectives")]
        [Tooltip("All objectives that must be completed for this quest. These will auto-populate from child U3DQuestObjective components")]
        [SerializeField] private List<U3DQuestObjective> objectives = new List<U3DQuestObjective>();

        [Header("Quest Rewards")]
        [Tooltip("Called when this quest is completed - use this to give rewards, unlock areas, etc.")]
        public UnityEvent OnQuestCompleted;

        [Tooltip("Called when this quest is started")]
        public UnityEvent OnQuestStarted;

        [Tooltip("Called when quest progress changes")]
        public UnityEvent<float> OnProgressChanged;

        [Header("Visual Feedback")]
        [Tooltip("GameObject to enable when quest is active (like a quest marker)")]
        [SerializeField] private GameObject questActiveIndicator;

        [Tooltip("GameObject to enable when quest is completed (like a completion effect)")]
        [SerializeField] private GameObject questCompleteIndicator;

        private U3DQuestManager questManager;
        private bool isActive = false;
        private bool isCompleted = false;
        private bool hasAttemptedAutoStart = false; // Prevent double auto-start

        public bool IsActive => isActive;
        public bool IsCompleted => isCompleted;
        public float Progress => CalculateProgress();

        private void Awake()
        {
            // Auto-discover objectives in children
            RefreshObjectives();
        }

        private void Start()
        {
            // Prevent double auto-start and ensure proper manager connection
            if (startAutomatically && !hasAttemptedAutoStart && !isActive)
            {
                hasAttemptedAutoStart = true;

                // Try to use the already-initialized quest manager first
                if (questManager != null)
                {
                    questManager.StartQuest(this);
                }
                else
                {
                    // Fallback: find QuestManager instance
                    U3DQuestManager manager = U3DQuestManager.Instance;
                    if (manager != null)
                    {
                        manager.StartQuest(this);
                    }
                    else
                    {
                        Debug.LogWarning($"No QuestManager found for auto-starting quest '{questTitle}'. Quest will not start automatically.");
                    }
                }
            }
        }

        /// <summary>
        /// Initialize this quest with a quest manager
        /// </summary>
        public void Initialize(U3DQuestManager manager)
        {
            questManager = manager;

            // Set up objective event listeners
            foreach (U3DQuestObjective objective in objectives)
            {
                objective.OnObjectiveCompleted.AddListener(OnObjectiveCompleted);
                objective.OnObjectiveProgress.AddListener(OnObjectiveProgress);
            }
        }

        /// <summary>
        /// Start this quest
        /// </summary>
        public void StartQuest()
        {           
            if (isActive || (isCompleted && !isRepeatable))
                return;

            isActive = true;
            isCompleted = false;

            // Reset all objectives if restarting
            foreach (U3DQuestObjective objective in objectives)
            {
                objective.ResetObjective();
                objective.SetActive(true);
            }

            UpdateVisualIndicators();
            OnQuestStarted?.Invoke();
            OnProgressChanged?.Invoke(Progress);

            // Register with quest manager if available
            if (questManager == null)
                questManager = U3DQuestManager.Instance;
        }

        /// <summary>
        /// Complete this quest
        /// </summary>
        public void CompleteQuest()
        {
            if (!isActive || isCompleted)
                return;

            isActive = false;
            isCompleted = true;

            // Deactivate all objectives
            foreach (U3DQuestObjective objective in objectives)
            {
                objective.SetActive(false);
            }

            UpdateVisualIndicators();
            OnQuestCompleted?.Invoke();
            OnProgressChanged?.Invoke(1.0f);
        }

        /// <summary>
        /// Reset this quest to its initial state
        /// </summary>
        public void ResetQuest()
        {
            isActive = false;
            isCompleted = false;
            hasAttemptedAutoStart = false; // Allow auto-start again

            foreach (U3DQuestObjective objective in objectives)
            {
                objective.ResetObjective();
            }

            UpdateVisualIndicators();
            OnProgressChanged?.Invoke(0f);
        }

        /// <summary>
        /// Calculate current quest progress (0 to 1)
        /// </summary>
        private float CalculateProgress()
        {
            if (objectives.Count == 0) return 0f;

            float totalProgress = 0f;
            foreach (U3DQuestObjective objective in objectives)
            {
                totalProgress += objective.Progress;
            }

            return totalProgress / objectives.Count;
        }

        /// <summary>
        /// Check if all objectives are completed
        /// </summary>
        private bool AllObjectivesCompleted()
        {
            foreach (U3DQuestObjective objective in objectives)
            {
                if (!objective.IsCompleted)
                    return false;
            }
            return objectives.Count > 0;
        }

        /// <summary>
        /// Called when any objective is completed
        /// </summary>
        private void OnObjectiveCompleted(U3DQuestObjective objective)
        {
            OnProgressChanged?.Invoke(Progress);

            if (AllObjectivesCompleted())
            {
                CompleteQuest();
            }
        }

        /// <summary>
        /// Called when objective progress changes
        /// </summary>
        private void OnObjectiveProgress(U3DQuestObjective objective)
        {
            OnProgressChanged?.Invoke(Progress);

            if (questManager != null)
                questManager.OnObjectiveProgress(objective);
        }

        /// <summary>
        /// Update visual indicators based on quest state
        /// </summary>
        private void UpdateVisualIndicators()
        {
            if (questActiveIndicator != null)
                questActiveIndicator.SetActive(isActive);

            if (questCompleteIndicator != null)
                questCompleteIndicator.SetActive(isCompleted);
        }

        /// <summary>
        /// Refresh the objectives list from child components
        /// </summary>
        [ContextMenu("Refresh Objectives")]
        public void RefreshObjectives()
        {
            objectives.Clear();
            U3DQuestObjective[] childObjectives = GetComponentsInChildren<U3DQuestObjective>();
            objectives.AddRange(childObjectives);
        }

        /// <summary>
        /// Get current quest status as a formatted string
        /// </summary>
        public string GetQuestStatus()
        {
            if (isCompleted)
                return "Completed";
            else if (isActive)
                return $"In Progress ({Mathf.RoundToInt(Progress * 100)}%)";
            else
                return "Available";
        }

        /// <summary>
        /// Start this quest (can be called from UI buttons)
        /// </summary>
        public void StartQuestFromUI()
        {
            if (questManager != null)
                questManager.StartQuest(this);
            else
                StartQuest();
        }
    }
}