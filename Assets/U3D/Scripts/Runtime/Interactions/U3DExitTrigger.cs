using U3D.Net;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Fires an event on every player's machine when something leaves this trigger volume.
    ///
    /// Trigger Once is per person: each player may trip the trigger once, and the event
    /// reaches everyone each time. The cooldown is the local player's own rate limit.
    /// Neither travels on the wire.
    ///
    /// Multiple instances on one entity are expected — the same stacking pattern as
    /// U3DEnterTrigger. Each instance computes its own index in OnNetSpawn and only the
    /// instance whose index matches the broadcast fires its event.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetEntity))]
    public class U3DExitTrigger : NetComponent
    {
        [Header("Trigger Configuration")]
        [Tooltip("Only fire for objects with a specific tag")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag required to fire this trigger")]
        [SerializeField] private string requiredTag = "Player";

        [Tooltip("Should this trigger only work once?")]
        [SerializeField] private bool triggerOnce = false;

        [Tooltip("Delay before trigger can fire again (seconds)")]
        [SerializeField] private float cooldownTime = 0f;

        [Header("Events")]
        public UnityEvent OnExitTrigger;

        private bool hasTriggered = false;
        private float lastTriggerTime = 0f;

        private NetMessage _message;
        private int _instanceIndex;

        private void Awake()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        protected override void OnNetSpawn()
        {
            _instanceIndex = 0;
            var siblings = Entity.Components;
            for (int i = 0; i < siblings.Count; i++)
            {
                if (siblings[i] == this) break;
                if (siblings[i] is U3DExitTrigger) _instanceIndex++;
            }

            _message = RegisterMessage(NetKeys.ExitTriggered, OnExitReceived);
        }

        private void OnTriggerExit(Collider other)
        {
            if (requireTag && !other.CompareTag(requiredTag)) return;

            if (IsOnCooldown) return;
            if (triggerOnce && hasTriggered) return;

            if (triggerOnce) hasTriggered = true;
            lastTriggerTime = Time.time;

            if (IsLive && _message != null)
            {
                _message.SendToAll((ushort)_instanceIndex);
            }
            else
            {
                OnExitTrigger?.Invoke();
            }
        }

        /// <summary>
        /// Fires on every machine, including the sender's through the local delivery
        /// inside Send. The instance index in the payload selects which handler fires:
        /// every instance on the entity receives the message, and only the one whose
        /// index matches invokes its event.
        /// </summary>
        private void OnExitReceived(PeerId sender, object[] args)
        {
            if (args.Length > 0 && (ushort)args[0] != _instanceIndex) return;
            OnExitTrigger?.Invoke();
        }

        /// <summary>
        /// Clears the fired-once flag and the cooldown so the trigger can fire again.
        /// </summary>
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
        /// The cooldown gate itself, not a separate reading of it. OnTriggerExit tests this
        /// property rather than restating the condition, so the two cannot drift apart.
        /// </summary>
        public bool IsOnCooldown => cooldownTime > 0f && Time.time - lastTriggerTime < cooldownTime;

        private void OnValidate()
        {
            if (cooldownTime < 0f) cooldownTime = 0f;
        }
    }
}