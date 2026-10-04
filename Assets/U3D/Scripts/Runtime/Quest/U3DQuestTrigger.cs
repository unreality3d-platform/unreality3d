using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Simple trigger component that integrates quest objectives with Unity's built-in Collider system.
    /// This extends your existing trigger functionality to work with the quest system.
    /// </summary>
    [AddComponentMenu("U3D/Quest System/U3D Quest Trigger")]
    [RequireComponent(typeof(Collider))]
    public class U3DQuestTrigger : MonoBehaviour
    {
        [Header("Quest Trigger Configuration")]
        [Tooltip("Quest objective that will progress when this trigger is activated")]
        [SerializeField] private U3DQuestObjective targetObjective;

        [Tooltip("How much progress to add to the objective when triggered")]
        [SerializeField] private int progressAmount = 1;

        [Tooltip("Should this trigger only work once?")]
        [SerializeField] private bool triggerOnce = true;

        [Tooltip("Tag that must touch this trigger (leave empty for any object)")]
        [SerializeField] private string requiredTag = "Player";

        [Header("Visual Feedback")]
        [Tooltip("GameObject to disable when trigger is activated (like removing a collectible)")]
        [SerializeField] private GameObject objectToDisable;

        [Tooltip("Particle effect to play when triggered")]
        [SerializeField] private ParticleSystem triggerEffect;

        [Tooltip("Sound to play when triggered")]
        [SerializeField] private AudioClip triggerSound;

        private bool hasTriggered = false;
        private AudioSource audioSource;
        private Collider triggerCollider;

        private void Awake()
        {
            triggerCollider = GetComponent<Collider>();
            triggerCollider.isTrigger = true;

            audioSource = GetComponent<AudioSource>();
            if (audioSource == null && triggerSound != null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                audioSource.playOnAwake = false;
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            // Check if already triggered and set to trigger once
            if (hasTriggered && triggerOnce) return;

            // Check required tag
            if (!string.IsNullOrEmpty(requiredTag) && !other.CompareTag(requiredTag)) return;

            // Progress the quest objective
            if (targetObjective != null && targetObjective.IsActive)
            {
                targetObjective.ProgressObjective(progressAmount);
                OnTriggerActivated();

                if (triggerOnce)
                    hasTriggered = true;

                Debug.Log($"Quest trigger activated: {gameObject.name}");
            }
        }

        /// <summary>
        /// Handle trigger activation effects
        /// </summary>
        private void OnTriggerActivated()
        {
            // Play sound effect
            if (triggerSound != null && audioSource != null)
            {
                audioSource.PlayOneShot(triggerSound);
            }

            // Play particle effect
            if (triggerEffect != null)
            {
                triggerEffect.Play();
            }

            // Disable object if specified
            if (objectToDisable != null)
            {
                objectToDisable.SetActive(false);
            }

            // Disable trigger if set to trigger once
            if (triggerOnce)
            {
                triggerCollider.enabled = false;
            }
        }

        /// <summary>
        /// Manually activate this trigger (for external systems)
        /// </summary>
        public void ActivateTrigger()
        {
            if (hasTriggered && triggerOnce) return;

            if (targetObjective != null && targetObjective.IsActive)
            {
                targetObjective.ProgressObjective(progressAmount);
                OnTriggerActivated();

                if (triggerOnce)
                    hasTriggered = true;
            }
        }

        /// <summary>
        /// Reset this trigger (useful for testing or repeatable quests)
        /// </summary>
        public void ResetTrigger()
        {
            hasTriggered = false;
            triggerCollider.enabled = true;

            if (objectToDisable != null)
                objectToDisable.SetActive(true);
        }

        /// <summary>
        /// Set the target objective for this trigger
        /// </summary>
        public void SetTargetObjective(U3DQuestObjective objective)
        {
            targetObjective = objective;
        }

        private void OnDrawGizmosSelected()
        {
            // Draw trigger bounds
            Collider col = GetComponent<Collider>();
            if (col != null)
            {
                Gizmos.color = hasTriggered ? Color.gray : Color.green;

                if (col is BoxCollider box)
                {
                    Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                    Gizmos.DrawWireCube(box.center, box.size);
                }
                else if (col is SphereCollider sphere)
                {
                    Gizmos.DrawWireSphere(transform.position + sphere.center, sphere.radius * transform.lossyScale.x);
                }
                else if (col is CapsuleCollider capsule)
                {
                    Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.lossyScale);
                    Gizmos.DrawWireCube(capsule.center, new Vector3(capsule.radius * 2, capsule.height, capsule.radius * 2));
                }
            }
        }
    }
}