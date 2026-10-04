using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Represents a single objective within a quest.
    /// This component integrates with existing U3D interaction systems like triggers and collectibles.
    /// </summary>
    [AddComponentMenu("U3D/Quest System/U3D Quest Objective")]
    public class U3DQuestObjective : MonoBehaviour
    {
        [Header("Objective Configuration")]
        [Tooltip("Name of this objective shown to the player")]
        public string objectiveDescription = "Complete this objective";

        [Tooltip("Type of objective - determines how progress is tracked")]
        public ObjectiveType objectiveType = ObjectiveType.Trigger;

        [Tooltip("For collection/interaction objectives: how many times must this be triggered?")]
        [SerializeField] private int targetCount = 1;

        [Tooltip("Current progress toward completing this objective")]
        [SerializeField] private int currentCount = 0;

        [Header("Target Configuration")]
        [Tooltip("For Location objectives: player must reach this transform's position")]
        [SerializeField] private Transform targetLocation;

        [Tooltip("For Location objectives: how close the player needs to be")]
        [SerializeField] private float locationRadius = 2f;

        [Tooltip("For Collection objectives: objects that count toward this objective when destroyed/collected")]
        [SerializeField] private GameObject[] collectibleObjects;

        [Header("Integration with Existing Systems")]
        [Tooltip("Connect to existing trigger components - when they activate, this objective progresses")]
        [SerializeField] private MonoBehaviour[] connectedTriggers;

        [Header("Visual Feedback")]
        [Tooltip("GameObject to show when objective is active (like a waypoint marker)")]
        [SerializeField] private GameObject activeIndicator;

        [Tooltip("GameObject to show when objective is completed (like a checkmark)")]
        [SerializeField] private GameObject completeIndicator;

        [Header("Events")]
        [Tooltip("Called when this objective is completed")]
        public UnityEvent<U3DQuestObjective> OnObjectiveCompleted;

        [Tooltip("Called when objective progress changes")]
        public UnityEvent<U3DQuestObjective> OnObjectiveProgress;

        private bool isActive = false;
        private bool isCompleted = false;
        private Transform playerTransform;

        public bool IsActive => isActive;
        public bool IsCompleted => isCompleted;
        public float Progress => targetCount > 0 ? (float)currentCount / targetCount : 0f;
        public string ProgressText => $"{currentCount}/{targetCount}";

        public enum ObjectiveType
        {
            [Tooltip("Complete when triggered once (integrates with existing trigger systems)")]
            Trigger,

            [Tooltip("Collect or destroy specified objects")]
            Collection,

            [Tooltip("Reach a specific location")]
            Location,

            [Tooltip("Interact with objects multiple times")]
            Interaction
        }

        private void Start()
        {
            // Find player for location objectives
            if (objectiveType == ObjectiveType.Location)
            {
                GameObject player = GameObject.FindGameObjectWithTag("Player");
                if (player != null)
                    playerTransform = player.transform;
            }

            // Set up integration with existing trigger systems
            SetupTriggerIntegration();
        }

        private void Update()
        {
            if (!isActive) return;

            // Check location objectives
            if (objectiveType == ObjectiveType.Location && playerTransform != null && targetLocation != null)
            {
                float distance = Vector3.Distance(playerTransform.position, targetLocation.position);
                if (distance <= locationRadius && currentCount < targetCount)
                {
                    ProgressObjective();
                }
            }
        }

        /// <summary>
        /// Activate this objective
        /// </summary>
        public void SetActive(bool active)
        {
            isActive = active;
            UpdateVisualIndicators();

            if (active)
            {
                // Set up collectible object monitoring for collection objectives
                if (objectiveType == ObjectiveType.Collection)
                {
                    SetupCollectionMonitoring();
                }
            }
        }

        /// <summary>
        /// Progress this objective by one step
        /// </summary>
        public void ProgressObjective()
        {
            if (!isActive || isCompleted) return;

            currentCount++;
            OnObjectiveProgress?.Invoke(this);

            if (currentCount >= targetCount)
            {
                CompleteObjective();
            }

            Debug.Log($"Objective progress: {objectiveDescription} ({currentCount}/{targetCount})");
        }

        /// <summary>
        /// Progress this objective by a specific amount
        /// </summary>
        public void ProgressObjective(int amount)
        {
            if (!isActive || isCompleted) return;

            currentCount += amount;
            if (currentCount > targetCount) currentCount = targetCount;

            OnObjectiveProgress?.Invoke(this);

            if (currentCount >= targetCount)
            {
                CompleteObjective();
            }
        }

        /// <summary>
        /// Complete this objective
        /// </summary>
        private void CompleteObjective()
        {
            if (isCompleted) return;

            isCompleted = true;
            currentCount = targetCount;

            UpdateVisualIndicators();
            OnObjectiveCompleted?.Invoke(this);

            Debug.Log($"Objective completed: {objectiveDescription}");
        }

        /// <summary>
        /// Reset this objective to its initial state
        /// </summary>
        public void ResetObjective()
        {
            isCompleted = false;
            currentCount = 0;
            UpdateVisualIndicators();
        }

        /// <summary>
        /// Set up integration with existing trigger components
        /// </summary>
        private void SetupTriggerIntegration()
        {
            foreach (MonoBehaviour trigger in connectedTriggers)
            {
                if (trigger == null) continue;

                // Try to connect to common trigger types
                // This integrates with your existing interaction systems
                if (trigger.GetType().Name.Contains("Trigger"))
                {
                    // Use reflection to find and connect to trigger events
                    var triggerEvent = trigger.GetType().GetField("OnTriggered");
                    if (triggerEvent != null && triggerEvent.FieldType == typeof(UnityEvent))
                    {
                        UnityEvent triggerUnityEvent = (UnityEvent)triggerEvent.GetValue(trigger);
                        triggerUnityEvent?.AddListener(() => ProgressObjective());
                    }
                }
            }
        }

        /// <summary>
        /// Set up monitoring for collection objectives
        /// </summary>
        private void SetupCollectionMonitoring()
        {
            foreach (GameObject collectible in collectibleObjects)
            {
                if (collectible == null) continue;

                // Check if object has a destroy trigger component (your existing system)
                MonoBehaviour destroyTrigger = collectible.GetComponent<MonoBehaviour>();
                if (destroyTrigger != null)
                {
                    // Monitor for object destruction
                    StartCoroutine(MonitorObjectDestruction(collectible));
                }
            }
        }

        /// <summary>
        /// Monitor when collectible objects are destroyed
        /// </summary>
        private System.Collections.IEnumerator MonitorObjectDestruction(GameObject obj)
        {
            while (obj != null && isActive && !isCompleted)
            {
                yield return new WaitForSeconds(0.1f);
            }

            // Object was destroyed while objective was active
            if (isActive && !isCompleted)
            {
                ProgressObjective();
            }
        }

        /// <summary>
        /// Update visual indicators based on objective state
        /// </summary>
        private void UpdateVisualIndicators()
        {
            if (activeIndicator != null)
                activeIndicator.SetActive(isActive && !isCompleted);

            if (completeIndicator != null)
                completeIndicator.SetActive(isCompleted);
        }

        /// <summary>
        /// Called by external trigger systems (for integration)
        /// </summary>
        public void OnTriggerActivated()
        {
            if (objectiveType == ObjectiveType.Trigger || objectiveType == ObjectiveType.Interaction)
            {
                ProgressObjective();
            }
        }

        /// <summary>
        /// Called when a collectible is collected (for integration)
        /// </summary>
        public void OnCollectibleCollected()
        {
            if (objectiveType == ObjectiveType.Collection)
            {
                ProgressObjective();
            }
        }

        /// <summary>
        /// Get objective status for UI display
        /// </summary>
        public string GetObjectiveStatus()
        {
            if (isCompleted)
                return "✓ " + objectiveDescription;
            else if (isActive)
                return "○ " + objectiveDescription + " (" + ProgressText + ")";
            else
                return "◯ " + objectiveDescription;
        }

        private void OnDrawGizmosSelected()
        {
            // Draw location radius for location objectives
            if (objectiveType == ObjectiveType.Location && targetLocation != null)
            {
                Gizmos.color = isCompleted ? Color.green : (isActive ? Color.yellow : Color.gray);
                Gizmos.DrawWireSphere(targetLocation.position, locationRadius);
            }
        }
    }
}