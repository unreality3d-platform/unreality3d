using System;
using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    [DisallowMultipleComponent]
    public class NetEntity : MonoBehaviour
    {
        public const ushort AuthoredIdMin = 1;
        public const ushort AuthoredIdMax = 49151;
        public const ushort RoomPropIdMin = 49152;
        public const ushort RoomPropIdMax = 57343;
        public const ushort PersonalIdMin = 57344;
        public const ushort PersonalBlockSize = 128;
        public const int PersonalBlockCount = 64;

        [SerializeField] private ushort _id;
        [SerializeField] private bool _takeableWhileHeld = true;

        private PeerId _owner = PeerId.None;
        private ushort _origin;
        private ushort _prefabIndex;
        private ushort _handoff;
        private PeerId _previousWinner = PeerId.None;
        private bool _hasPreviousHandoff;
        private const float ClaimGraceSeconds = 0.4f;
        private float _claimGraceUntil;
        private bool _isLive;

        private readonly List<NetComponent> _components = new List<NetComponent>();

        public event Action<NetEntity, PeerId, PeerId> AuthorityChanged;
        public event Action<NetEntity, PeerId> ClaimRefused;

        public ushort Id => _id;
        public PeerId Owner => _owner;
        public ushort Origin => _origin;

        /// <summary>
        /// The prefab table index this object was built from, or 0 for an authored object.
        /// Kept because arrival state has to name the prefab a newcomer should build, and
        /// the registry's lookup is keyed by the prefab asset — a live object is a scene
        /// instance, so the number cannot be recovered afterwards. Runtime only, never
        /// serialized, for the same reason a prefab asset never carries a baked ID.
        /// </summary>
        public ushort PrefabIndex => _prefabIndex;
        public ushort Handoff => _handoff;
        public bool IsLive => _isLive;
        public bool IsOwned => _owner.IsValid;
        public bool TakeableWhileHeld => _takeableWhileHeld;

        public bool HasAuthority =>
            _isLive && Net.Session != null && _owner == Net.Session.LocalPeer;

        public IReadOnlyList<NetComponent> Components => _components;

        public static bool IsAuthoredId(ushort id) => id >= AuthoredIdMin && id <= AuthoredIdMax;
        public static bool IsRoomPropId(ushort id) => id >= RoomPropIdMin && id <= RoomPropIdMax;
        public static bool IsPersonalId(ushort id) => id >= PersonalIdMin;

        public static int PersonalBlockOf(ushort id)
            => IsPersonalId(id) ? (id - PersonalIdMin) / PersonalBlockSize : -1;
        public static ushort PersonalBlockBase(int blockIndex)
        {
            if (blockIndex < 0 || blockIndex >= PersonalBlockCount) return 0;
            return (ushort)(PersonalIdMin + blockIndex * PersonalBlockSize);
        }

        public static bool IsAvatarId(ushort id)
            => IsPersonalId(id) && (id - PersonalIdMin) % PersonalBlockSize == 0;

        private void OnEnable()
        {
            if (_isLive) return;
            if (!IsAuthoredId(_id)) return;
            if (Net.Session == null || !Net.Session.IsConnected) return;
            Net.Session.RegisterAuthored(this);
        }

        internal void InitializeAuthored()
        {
            if (_isLive) return;
            if (!IsAuthoredId(_id))
            {
                Debug.LogError($"NetEntity on '{name}' has ID {_id}, which is outside the authored range {AuthoredIdMin} to {AuthoredIdMax}. Registration refused.", this);
                return;
            }
            _origin = 0;
            _prefabIndex = 0;
            BeginLife(_id, PeerId.None);
        }

        internal void InitializeSpawn(ushort id, PeerId owner, Vector3 position, Quaternion rotation, ushort origin, ushort prefabIndex)
        {
            if (id == 0)
            {
                Debug.LogError($"NetEntity on '{name}' was spawned without an ID. IDs come from a baked authored value, the room prop range, or a peer's personal block. Spawn refused.", this);
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
            _origin = origin;
            _prefabIndex = prefabIndex;
            BeginLife(id, owner);
        }

        private void BeginLife(ushort id, PeerId owner)
        {
            _id = id;
            _owner = owner;
            _handoff = 0;
            _previousWinner = PeerId.None;
            _hasPreviousHandoff = false;

            _components.Clear();
            GetComponentsInChildren(true, _components);
            for (int i = 0; i < _components.Count; i++)
                _components[i].BindEntity(this);

            _isLive = true;

            for (int i = 0; i < _components.Count; i++)
                _components[i].InvokeNetSpawn();

#if UNITY_EDITOR
            ValidateMessageKeys();
#endif
        }

        internal void InvokeDespawn()
        {
            if (!_isLive) return;
            for (int i = 0; i < _components.Count; i++)
                _components[i].InvokeNetDespawn();
            _isLive = false;
        }

        public bool RequestAuthority()
        {
            if (!_isLive || Net.Session == null) return false;
            if (HasAuthority) return true;

            PeerId local = Net.Session.LocalPeer;
            if (!CanTake(local))
            {
                ClaimRefused?.Invoke(this, local);
                return false;
            }

            var claim = new AuthorityClaim(local, _handoff);
            ResolveHandoff(claim);

            // The claim has been applied here and has not reached anyone else yet, so a
            // correction composed before it landed describes a room that has not seen this
            // grab. Taking it would pull the object back out of this player's hands in
            // ordinary play rather than only in a broken room. Corrections are ignored
            // until the room has had time to answer, or until one arrives that already
            // agrees, whichever comes first.
            _claimGraceUntil = Time.fixedTime + ClaimGraceSeconds;

            Net.Session.SubmitClaim(this, claim);
            return true;
        }

        /// <summary>
        /// The owner has published a settle, so the object is at rest and unowned. Every
        /// peer runs this from that same message, which is what keeps the handoff counter
        /// in step across the room.
        ///
        /// Not the owner's private business, which is what it was and what B80 is. The
        /// counter advanced on the dropper's machine alone, so the next claim carried a
        /// number nobody else recognised and was discarded in silence — and every other
        /// machine went on naming the dropper as owner for the rest of the session.
        ///
        /// Unlike a departure, the counter is advanced here. A settle is a message every
        /// peer receives, so all of them move together; a departure is read from a roster
        /// delivery that peers process at their own moments, which is why
        /// ClearOwnerOnDeparture deliberately leaves the counter alone.
        /// </summary>
        internal void ReleaseOnSettle()
        {
            if (!_isLive || !_owner.IsValid) return;

            unchecked { _handoff = (ushort)(_handoff + 1); }
            _previousWinner = PeerId.None;
            _hasPreviousHandoff = false;
            SetOwner(PeerId.None);
        }

        /// <summary>
        /// The owner left the room. Every peer runs this from the same roster delivery, so
        /// the object becomes unowned everywhere without anything going on the wire.
        ///
        /// The handoff counter is deliberately not advanced. Nothing the departed peer sent
        /// can still arrive, and advancing here would put this machine a number ahead of a
        /// machine that has not yet processed the departure, which is a wider disagreement
        /// than the one it would close.
        ///
        /// Without this an authored object held by a departing peer keeps naming them as
        /// owner forever. A takeable object can still be claimed; one with Takeable While
        /// Held switched off is refused by CanTake to everybody for the rest of the
        /// session, with nothing in the log naming the cause.
        /// </summary>
        internal void ClearOwnerOnDeparture()
        {
            if (!_isLive || !_owner.IsValid) return;

            _previousWinner = PeerId.None;
            _hasPreviousHandoff = false;
            SetOwner(PeerId.None);
        }

        /// <summary>
        /// A claim from another peer. Three outcomes, by how the claim's counter sits
        /// against this machine's.
        ///
        /// At the same value it is an ordinary take, arbitrated by the permission test.
        ///
        /// One below, with a contested grab already recorded, is the second of two claims
        /// made at one value and is resolved by peer ID.
        ///
        /// One below with nothing recorded and nobody holding the object is a claim a
        /// settle overtook — the claimant grabbed before the drop reached them, so their
        /// counter advanced from the same value this machine advanced from, and the two
        /// agree about the number while disagreeing about who is holding it. It is taken,
        /// which leaves this machine where the claimant already is. Refusing it is B86:
        /// the grab succeeds on the claimant's screen alone and is undone a second later
        /// by the reporter's correction. The object is unowned by construction here, so
        /// there is no permission question to ask.
        /// </summary>
        internal void ApplyRemoteClaim(AuthorityClaim claim)
        {
            if (!_isLive || claim.IsEmpty) return;

            if (claim.Handoff == _handoff)
            {
                if (claim.Peer == _owner) return;
                if (!CanTake(claim.Peer))
                {
                    ClaimRefused?.Invoke(this, claim.Peer);
                    return;
                }
                ResolveHandoff(claim);
                return;
            }

            if (claim.Handoff != PreviousHandoff()) return;

            if (_hasPreviousHandoff)
            {
                if (!_previousWinner.IsValid) return;
                if (claim.Peer.Raw >= _previousWinner.Raw) return;
                CorrectHandoff(claim);
                return;
            }

            if (_owner.IsValid) return;

            CorrectHandoff(claim);
        }

        /// <summary>
        /// The reporter's account of who holds this object and how far its handoff counter
        /// has reached. Taken as given, in both directions.
        ///
        /// Downward as well as upward, deliberately. A peer that has fallen out of step
        /// keeps claiming this object and each claim advances its own counter, so an
        /// out-of-step peer can sit above the room as easily as below it. A rule that only
        /// ever moved the counter forward would make exactly those peers refuse the message
        /// that repairs them, which is the failure this exists to end.
        ///
        /// A correction matching what is already held does nothing and is the ordinary
        /// case. It must stay a no-op: clearing the contested-grab state on every pass
        /// would leave two simultaneous claims with nothing to resolve them.
        ///
        /// That state is cleared when the correction does bite, because a previous winner
        /// recorded against a counter the room disagrees with describes a contest that
        /// happened in a different version of this object's history.
        /// </summary>
        internal void ApplyAuthorityCorrection(PeerId owner, ushort handoff)
        {
            if (!_isLive) return;

            if (handoff == _handoff && owner == _owner)
            {
                _claimGraceUntil = 0f;
                return;
            }

            if (Time.fixedTime < _claimGraceUntil) return;

            _handoff = handoff;
            _previousWinner = PeerId.None;
            _hasPreviousHandoff = false;
            SetOwner(owner);
        }

        public bool CanTake(PeerId claimant)
        {
            if (!claimant.IsValid) return false;
            if (!_owner.IsValid) return true;
            if (_owner == claimant) return true;
            return _takeableWhileHeld;
        }

        private ushort PreviousHandoff()
        {
            unchecked { return (ushort)(_handoff - 1); }
        }

        private void ResolveHandoff(AuthorityClaim claim)
        {
            _previousWinner = claim.Peer;
            _hasPreviousHandoff = true;
            unchecked { _handoff = (ushort)(_handoff + 1); }
            SetOwner(claim.Peer);
        }

        private void CorrectHandoff(AuthorityClaim claim)
        {
            _previousWinner = claim.Peer;
            _hasPreviousHandoff = true;
            SetOwner(claim.Peer);
        }

        internal void SetTakeableWhileHeld(bool takeable)
        {
            _takeableWhileHeld = takeable;
        }

        private void SetOwner(PeerId next)
        {
            PeerId previous = _owner;
            if (previous == next) return;

            _owner = next;

            for (int i = 0; i < _components.Count; i++)
                _components[i].InvokeAuthorityChanged(previous, _owner);

            AuthorityChanged?.Invoke(this, previous, _owner);
        }

        internal void Tick()
        {
            if (!_isLive) return;
            for (int i = 0; i < _components.Count; i++)
                _components[i].InvokeNetTick();
        }

        internal void Render()
        {
            if (!_isLive) return;
            for (int i = 0; i < _components.Count; i++)
                _components[i].InvokeNetRender();
        }

        internal void RouteMessage(string key, PeerId sender, object[] args)
        {
            for (int i = 0; i < _components.Count; i++)
                _components[i].DeliverMessage(key, sender, args);
        }

#if UNITY_EDITOR
        private static readonly List<string> _keyScratch = new List<string>();

        private void ValidateMessageKeys()
        {
            _keyScratch.Clear();
            for (int i = 0; i < _components.Count; i++)
            {
                int before = _keyScratch.Count;
                _components[i].CollectKeys(_keyScratch);

                for (int added = before; added < _keyScratch.Count; added++)
                {
                    for (int earlier = 0; earlier < before; earlier++)
                    {
                        if (_keyScratch[earlier] != _keyScratch[added]) continue;

                        if (NetWire.TryGetTag(_keyScratch[added], out byte tag)
                            && NetWire.TryGetDescriptor(tag, out var desc)
                            && desc.InstanceKeyed)
                            continue;

                        Debug.LogError($"Two components on '{name}' both registered the message key '{_keyScratch[added]}'. Every message with that key fires both handlers. Give each component its own prefixed key in NetKeys.", this);
                    }
                }
            }
        }

        internal ushort EditorId => _id;
        internal void EditorSetId(ushort id) => _id = id;

        private void OnValidate()
        {
            if (Application.isPlaying) return;

            if (UnityEditor.EditorUtility.IsPersistent(this))
            {
                if (_id != 0) NetEntityIdAssigner.ScheduleClear(this);
                return;
            }

            NetEntityIdAssigner.Schedule();
        }
#endif
    }
}