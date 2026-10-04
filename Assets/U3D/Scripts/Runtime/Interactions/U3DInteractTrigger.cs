using U3D.Net;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace U3D
{
    /// <summary>
    /// Fires an event on every player's machine when someone interacts with this object,
    /// either by pressing the Interact key while in range or by clicking it.
    ///
    /// Trigger Once is per person: each player may fire the trigger once, and the event
    /// reaches everyone each time. The cooldown is the local player's own rate limit.
    /// Neither travels on the wire.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetEntity))]
    public class U3DInteractTrigger : NetComponent, IU3DInteractable
    {
        [Header("Trigger Configuration")]
        [Tooltip("Only fire for players/objects with a specific tag")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag required to activate this trigger")]
        [SerializeField] private string requiredTag = "Player";

        [Tooltip("Should this trigger only work once per player?")]
        [SerializeField] private bool triggerOnce = false;

        [Tooltip("Delay before trigger can fire again (seconds)")]
        [SerializeField] private float cooldownTime = 0f;

        [Header("Interaction Settings")]
        [Tooltip("Maximum distance for proximity-based interaction (Interact key)")]
        [SerializeField] private float maxInteractDistance = 3f;

        [Tooltip("Maximum distance for mouse click raycast (0 = unlimited)")]
        [SerializeField] private float maxClickDistance = 10f;

        [Tooltip("Allow mouse click as an additional way to trigger (camera raycast)")]
        [SerializeField] private bool allowMouseClick = true;

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this object. Edit the text on that object directly.")]
        public U3DWorldspaceUI labelUI;

        [Header("Events")]
        [Tooltip("Called on every player's machine when someone interacts with this object. Use this for doors, buttons, switches and anything that should happen for everybody.")]
        public UnityEvent OnInteractTriggered;

        [Tooltip("Called only on this player's machine when their interaction is refused (cooldown or already triggered).")]
        public UnityEvent OnInteractFailed;
        public UnityEvent OnPlayerEnterRangeEvent;
        public UnityEvent OnPlayerExitRangeEvent;

        private bool hasTriggered = false;
        private float lastTriggerTime = 0f;

        private Collider triggerCollider;
        private Transform playerTransform;
        private bool isInRange = false;

        private NetMessage _interactMessage;

        private void Awake()
        {
            triggerCollider = GetComponent<Collider>();
        }

        protected override void OnNetSpawn()
        {
            _interactMessage = RegisterMessage(NetKeys.InteractTriggered, OnInteractReceived);
        }

        private void Update()
        {
            UpdatePlayerProximity();

            if (allowMouseClick)
                CheckMouseClick();
        }

        private void UpdatePlayerProximity()
        {
            if (playerTransform == null)
            {
                FindPlayer();
                if (playerTransform == null) return;
            }

            float distance = Vector3.Distance(transform.position, playerTransform.position);
            bool wasInRange = isInRange;
            isInRange = distance <= maxInteractDistance;

            if (isInRange && !wasInRange)
                OnPlayerEnterRangeEvent?.Invoke();
            else if (!isInRange && wasInRange)
                OnPlayerExitRangeEvent?.Invoke();
        }

        private void FindPlayer()
        {
            U3DPlayerController controller = U3DPlayerController.FindLocalPlayer();
            if (controller != null)
                playerTransform = controller.transform;
        }

        private bool PassesTagCheck()
        {
            if (!requireTag) return true;
            if (playerTransform == null) return false;
            return playerTransform.CompareTag(requiredTag);
        }

        private void CheckMouseClick()
        {
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
                return;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            if (Camera.main == null)
                return;

            Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
            float rayDistance = maxClickDistance > 0f ? maxClickDistance : Mathf.Infinity;

            if (!Physics.Raycast(ray, out RaycastHit hit, rayDistance))
                return;

            if (hit.collider != triggerCollider)
                return;

            if (!PassesTagCheck())
            {
                OnInteractFailed?.Invoke();
                return;
            }

            AttemptTrigger();
        }

        private bool AttemptTrigger()
        {
            if (IsOnCooldown)
            {
                OnInteractFailed?.Invoke();
                return false;
            }

            if (triggerOnce && hasTriggered)
            {
                OnInteractFailed?.Invoke();
                return false;
            }

            ExecuteTrigger();
            return true;
        }

        private void ExecuteTrigger()
        {
            if (triggerOnce) hasTriggered = true;
            lastTriggerTime = Time.time;

            if (IsLive && _interactMessage != null)
            {
                _interactMessage.SendToAll();
            }
            else
            {
                OnInteractTriggered?.Invoke();
            }
        }

        /// <summary>
        /// Fires on every machine, including the sender's through the local delivery
        /// inside Send. No gates: the sender already passed the tag check, the cooldown
        /// and the once-per-player test, and each of those is about the player who
        /// pressed rather than about the object.
        /// </summary>
        private void OnInteractReceived(PeerId sender, object[] args)
        {
            OnInteractTriggered?.Invoke();
        }

        public void OnInteract()
        {
            if (!PassesTagCheck())
            {
                OnInteractFailed?.Invoke();
                return;
            }

            AttemptTrigger();
        }

        public void OnPlayerEnterRange() { }

        public void OnPlayerExitRange() { }

        public bool CanInteract()
        {
            if (!PassesTagCheck()) return false;

            if (triggerOnce && hasTriggered) return false;

            if (IsOnCooldown) return false;

            return isInRange;
        }

        public string GetInteractionPrompt()
        {
            return "Interact";
        }

        public void ResetTrigger()
        {
            hasTriggered = false;
            lastTriggerTime = 0f;
        }

        public void SetCooldownTime(float newCooldownTime) => cooldownTime = Mathf.Max(0f, newCooldownTime);
        public void SetTriggerOnce(bool value) => triggerOnce = value;

        public bool HasTriggered => hasTriggered;
        public float LastTriggerTime => lastTriggerTime;

        /// <summary>
        /// The cooldown gate itself, not a separate reading of it. AttemptTrigger and
        /// CanInteract both test this property rather than restating the condition.
        /// </summary>
        public bool IsOnCooldown => cooldownTime > 0f && Time.time - lastTriggerTime < cooldownTime;

        public bool IsInRange => isInRange;

        private void OnValidate()
        {
            if (cooldownTime < 0f) cooldownTime = 0f;
            if (maxInteractDistance < 0f) maxInteractDistance = 0f;
            if (maxClickDistance < 0f) maxClickDistance = 0f;
        }
    }
}