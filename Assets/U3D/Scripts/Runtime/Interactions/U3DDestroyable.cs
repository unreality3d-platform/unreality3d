using U3D.Net;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Removes an object for everyone rather than on one screen.
    ///
    /// Destruction is for objects created during play — items spawned from an Object
    /// Spawner or taken out of an inventory. An object placed in the scene cannot be
    /// destroyed across the wire: every machine builds the scene from the same file, so
    /// there is no way to tell a player who joins later that a scene object is gone. Send
    /// those home with a Trash Handler in Respawn mode instead.
    ///
    /// OnDestroyed fires on every machine immediately before the object goes, so effects,
    /// sounds and scoring hooks wired to it happen for everybody.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetEntity))]
    public class U3DDestroyable : NetComponent
    {
        [Header("Events")]
        [Tooltip("Called on every player's machine just before this object is destroyed. Use this to spawn effects, update score, play sounds, etc.")]
        public UnityEvent OnDestroyed;

        private NetMessage _despawnRequest;
        private bool _requested;
        private bool _refusesAuthored;

        protected override void OnNetSpawn()
        {
            _requested = false;
            _refusesAuthored = NetEntity.IsAuthoredId(Entity.Id);

            if (_refusesAuthored)
            {
                Debug.LogWarning($"'{name}' is placed in the scene, so it cannot be destroyed for other players and Request Destroy will do nothing. Remove the U3D Destroyable component and use a Trash Handler in Respawn mode, or create this object from an Object Spawner instead.", this);
                return;
            }

            _despawnRequest = RegisterMessage(NetKeys.SpawnedDespawn, OnDespawnRequest);
        }

        /// <summary>
        /// Fires the creator's event on every machine, immediately before the object is
        /// removed. Runs on the peer that asked and on every peer receiving the removal,
        /// because the session raises this while the object is still here.
        /// </summary>
        protected override void OnNetDespawn()
        {
            OnDestroyed?.Invoke();
        }

        /// <summary>
        /// Request that this object be destroyed for everyone. Safe to call from anywhere,
        /// on any machine.
        ///
        /// The request goes to the one peer entitled to remove this object, which the ID
        /// answers: a room prop is the reporter's to retire, and an item from a player's
        /// own range is that player's. Sending it anywhere else has it refused on arrival
        /// with nothing in the log.
        /// </summary>
        public void RequestDestroy()
        {
            if (_requested) return;

            if (_refusesAuthored) return;

            if (!IsLive || Session == null || Entity == null || _despawnRequest == null)
            {
                _requested = true;
                OnDestroyed?.Invoke();
                Destroy(gameObject);
                return;
            }

            PeerId target = ResolveDespawner(Entity.Id);
            if (!target.IsValid) return;

            _requested = true;
            _despawnRequest.SendTo(target);
        }

        /// <summary>
        /// Which peer may remove this object, from its ID alone. The same arithmetic every
        /// receiving peer runs before accepting a removal, so a request aimed anywhere else
        /// is discarded on arrival.
        /// </summary>
        private PeerId ResolveDespawner(ushort id)
        {
            if (NetEntity.IsRoomPropId(id)) return Session.Reporter;

            int block = NetEntity.PersonalBlockOf(id);
            if (block < 0) return PeerId.None;

            return PeerId.FromBlockIndex(block);
        }

        /// <summary>
        /// The request has arrived at the peer entitled to act on it. The sender is not
        /// checked: a request from anyone is legitimate — a trash zone on one machine
        /// asking the object's owner to remove it is the ordinary case — and what protects
        /// the object is that only this peer can publish the removal at all.
        /// </summary>
        private void OnDespawnRequest(PeerId sender, object[] args)
        {
            if (Session == null || Entity == null) return;
            if (ResolveDespawner(Entity.Id) != Session.LocalPeer) return;

            Session.Despawn(Entity);
        }
    }
}