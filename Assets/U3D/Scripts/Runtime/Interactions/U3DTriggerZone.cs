using U3D.Net;
using UnityEngine;
using UnityEngine.Events;
using System.Collections.Generic;

namespace U3D
{
    /// <summary>
    /// Fires one event on every player's machine when this volume goes from empty to
    /// occupied and another when it goes back to empty. Occupancy counts everything
    /// currently inside, so the events fire on the first thing in and the last thing out
    /// rather than once per object.
    ///
    /// An occupant destroyed inside the volume counts as leaving. Unity sends no exit
    /// callback for a collider that is destroyed or disabled while inside a trigger, so
    /// occupancy is re-checked each frame while the zone holds anything.
    ///
    /// Occupancy counting stays local: each machine counts the colliders it can see.
    /// The transition events (occupied and cleared) are what travel on the wire, so
    /// every machine fires the creator's response at the same moment even though the
    /// collider counts behind each transition are independent.
    ///
    /// Multiple instances on one entity are expected — the same stacking pattern as
    /// U3DEnterTrigger. Each instance computes its own index in OnNetSpawn and packs it
    /// into the low byte of the broadcast payload alongside the event type in the high
    /// byte.
    ///
    /// Trigger Once is per person: each player may trip the pair once, and the events
    /// reach everyone each time.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetEntity))]
    public class U3DTriggerZone : NetComponent
    {
        [Header("Zone Configuration")]
        [Tooltip("Only respond to objects with a specific tag")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag required to count as an occupant")]
        [SerializeField] private string requiredTag = "Player";

        [Tooltip("Fire the pair once, then stop. The zone fires Occupied on the first thing in and Cleared when it empties, and after that stays quiet no matter who enters. Call ResetZone() to re-arm it. Leave this off for a zone that should keep working every time, like a door that stays open while anyone is standing in it.")]
        [SerializeField] private bool triggerOnce = false;

        [Header("Events")]
        [Tooltip("Fired on every player's machine when the zone goes from empty to occupied")]
        public UnityEvent OnZoneOccupied;

        [Tooltip("Fired on every player's machine when the zone goes from occupied to empty")]
        public UnityEvent OnZoneCleared;

        private bool hasTriggered = false;

        private readonly List<Collider> _occupants = new List<Collider>();
        private bool _wasOccupied = false;

        private NetMessage _message;
        private int _instanceIndex;

        private const int EventOccupied = 0;
        private const int EventCleared = 1;

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
                if (siblings[i] is U3DTriggerZone) _instanceIndex++;
            }

            _message = RegisterMessage(NetKeys.ZoneTransition, OnZoneReceived);
        }

        private void Update()
        {
            if (_occupants.Count == 0 && !_wasOccupied) return;
            RefreshOccupancy();
        }

        private void OnTriggerEnter(Collider other)
        {
            if (requireTag && !other.CompareTag(requiredTag)) return;

            if (!_occupants.Contains(other))
                _occupants.Add(other);

            RefreshOccupancy();
        }

        private void OnTriggerExit(Collider other)
        {
            if (requireTag && !other.CompareTag(requiredTag)) return;

            _occupants.Remove(other);
            RefreshOccupancy();
        }

        /// <summary>
        /// Single place occupancy changes are turned into events. Entry, exit, and the
        /// per-frame check all route through here so they cannot disagree about what the
        /// zone's state is. Occupancy bookkeeping is updated whether or not the events are
        /// still allowed to fire, so IsOccupied and OccupantCount stay correct after a
        /// Trigger Once zone has been spent.
        /// </summary>
        private void RefreshOccupancy()
        {
            PruneDestroyedOccupants();

            bool isOccupied = _occupants.Count > 0;
            if (isOccupied == _wasOccupied) return;

            _wasOccupied = isOccupied;

            if (triggerOnce && hasTriggered) return;

            if (isOccupied)
            {
                BroadcastZoneEvent(EventOccupied);
            }
            else
            {
                BroadcastZoneEvent(EventCleared);
                if (triggerOnce) hasTriggered = true;
            }
        }

        /// <summary>
        /// Packs the event type into the high byte and the instance index into the low
        /// byte of a single UShort, then broadcasts it. When not live, fires the
        /// corresponding event directly so the zone works in an offline scene.
        /// </summary>
        private void BroadcastZoneEvent(int eventType)
        {
            if (IsLive && _message != null)
            {
                ushort packed = (ushort)((eventType << 8) | _instanceIndex);
                _message.SendToAll(packed);
            }
            else
            {
                if (eventType == EventOccupied)
                    OnZoneOccupied?.Invoke();
                else
                    OnZoneCleared?.Invoke();
            }
        }

        /// <summary>
        /// Fires on every machine, including the sender's through the local delivery
        /// inside Send. The low byte of the packed UShort selects which instance fires;
        /// the high byte selects which event.
        /// </summary>
        private void OnZoneReceived(PeerId sender, object[] args)
        {
            if (args.Length == 0) return;

            ushort packed = (ushort)args[0];
            int index = packed & 0xFF;
            int eventType = (packed >> 8) & 0xFF;

            if (index != _instanceIndex) return;

            if (eventType == EventOccupied)
                OnZoneOccupied?.Invoke();
            else
                OnZoneCleared?.Invoke();
        }

        /// <summary>
        /// Drops occupants that no longer exist. An object destroyed while inside the zone
        /// never fires OnTriggerExit, so without this its entry would remain and the zone
        /// would never report itself empty again.
        /// </summary>
        private void PruneDestroyedOccupants()
        {
            for (int i = _occupants.Count - 1; i >= 0; i--)
                if (_occupants[i] == null)
                    _occupants.RemoveAt(i);
        }

        /// <summary>
        /// Empties the occupant list and clears the fired-once flag so the zone can fire
        /// again. Does not fire OnZoneCleared — this is a reset, not a departure.
        /// </summary>
        public void ResetZone()
        {
            _occupants.Clear();
            _wasOccupied = false;
            hasTriggered = false;
        }

        public bool IsOccupied
        {
            get
            {
                PruneDestroyedOccupants();
                return _occupants.Count > 0;
            }
        }

        public int OccupantCount
        {
            get
            {
                PruneDestroyedOccupants();
                return _occupants.Count;
            }
        }

        /// <summary>
        /// Disabling the zone drops what it knows without firing anything, matching
        /// ResetZone. Re-enabling starts from empty.
        /// </summary>
        private void OnDisable()
        {
            _occupants.Clear();
            _wasOccupied = false;
        }
    }
}