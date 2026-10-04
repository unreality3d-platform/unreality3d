using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// Everything a session does that is not the transport: the entity table, ID
    /// allocation, the peer list, the reporter, and the per-entity Tick and Render passes.
    /// Both backends derive from this so each of those has one implementation rather than
    /// two that must be kept agreeing. Per G56.
    ///
    /// Subclasses supply Connect, Disconnect, Send and SubmitClaim, assign the locator in
    /// their own Awake, and carry their own [DefaultExecutionOrder(110)], which Unity does
    /// not inherit.
    /// </summary>
    public abstract class NetSessionBase : MonoBehaviour, INetSession
    {
        protected sealed class PersonalBlock
        {
            public ushort Min;
            public ushort Max;
            public ushort Cursor;
        }

        /// <summary>
        /// One peer already present when the local peer joins. The roster supplies these
        /// to BeginSession so the peer list and the reporter are correct before any
        /// authored object registers. Per G180.
        /// </summary>
        protected readonly struct RosterSlot
        {
            public readonly PeerId Peer;
            public readonly int BlockIndex;

            public RosterSlot(PeerId peer, int blockIndex)
            {
                Peer = peer;
                BlockIndex = blockIndex;
            }
        }

        private readonly List<PeerId> _peers = new List<PeerId>();
        private readonly Dictionary<PeerId, PersonalBlock> _blocks = new Dictionary<PeerId, PersonalBlock>();
        private readonly Dictionary<ushort, NetEntity> _entities = new Dictionary<ushort, NetEntity>();
        private readonly Dictionary<PeerId, NetEntity> _playerEntities = new Dictionary<PeerId, NetEntity>();
        private readonly List<NetEntity> _tickList = new List<NetEntity>();
        private readonly List<NetEntity> _removalScratch = new List<NetEntity>();
        private readonly List<PeerId> _openingScratch = new List<PeerId>();
        private readonly List<NetEntity> _spawnedScratch = new List<NetEntity>();
        private readonly HashSet<ushort> _unknownPrefabIndices = new HashSet<ushort>();
        private readonly List<ushort> _correctionCycle = new List<ushort>();
        private int _correctionIndex;
        private float _correctionTimer;

        private PeerId _localPeer = PeerId.None;
        private PeerId _reporter = PeerId.None;
        private ushort _roomPropCursor = NetEntity.RoomPropIdMin;
        private ushort _avatarSequence;
        private bool _isConnected;

        public PeerId LocalPeer => _localPeer;
        public PeerId Reporter => _reporter;
        public bool IsReporter => _isConnected && _reporter == _localPeer;
        public IReadOnlyList<PeerId> Peers => _peers;
        public bool IsConnected => _isConnected;

        public event Action<PeerId> PeerJoined;
        public event Action<PeerId> PeerLeft;
        public event Action<PeerId> ReporterChanged;
        public event Action<bool> ConnectionChanged;
        public event Action<PeerId> PeerReachable;

        /// <summary>
        /// The one platform test. Both backends read it, in opposite directions, so which
        /// one runs is decided in a single place. Per G161: the signaling layer is behind
        /// #if UNITY_WEBGL &amp;&amp; !UNITY_EDITOR, so the editor is permanently a room of one.
        /// </summary>
        protected static bool IsWebGLBuild
        {
            get
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public abstract Task<JoinResult> Connect(string sessionName);
        public abstract Task Disconnect();
        public abstract void SubmitClaim(NetEntity entity, AuthorityClaim claim);
        public abstract void Send(NetEntity entity, string key, PeerId target, object[] args);

        protected void ClaimLocator()
        {
            Net.Session = this;
        }

        protected virtual void OnDestroy()
        {
            if (ReferenceEquals(Net.Session, this)) Net.Session = null;
        }

        // ===== Session lifecycle =====

        /// <summary>
        /// Brings the session up with the local peer holding the named block, adds any
        /// peers already present, raises the opening events, and registers every authored
        /// object in the scene.
        ///
        /// The reporter is supplied rather than assumed, per G180. ScanSceneForAuthored
        /// reaches spawn callbacks that test IsReporter, so a peer that opened as its own
        /// reporter and corrected afterwards would first drop and settle every unowned
        /// Start Active object in the scene per G75. A caller passing no reporter is a room
        /// of one and gets the local peer.
        /// </summary>
        protected void BeginSession(PeerId localPeer, int localBlockIndex, IReadOnlyList<RosterSlot> otherPeers = null, PeerId reporter = default)
        {
            _peers.Clear();
            _blocks.Clear();
            _roomPropCursor = NetEntity.RoomPropIdMin;

            _localPeer = localPeer;
            _peers.Add(localPeer);
            AssignBlock(localPeer, localBlockIndex);

            _openingScratch.Clear();
            if (otherPeers != null)
            {
                for (int i = 0; i < otherPeers.Count; i++)
                {
                    PeerId peer = otherPeers[i].Peer;
                    if (!peer.IsValid || _peers.Contains(peer)) continue;

                    _peers.Add(peer);
                    if (!AssignBlock(peer, otherPeers[i].BlockIndex))
                    {
                        _peers.Remove(peer);
                        continue;
                    }
                    _openingScratch.Add(peer);
                }
            }

            _reporter = reporter.IsValid && _peers.Contains(reporter) ? reporter : localPeer;
            _isConnected = true;

            ConnectionChanged?.Invoke(true);
            PeerJoined?.Invoke(localPeer);
            for (int i = 0; i < _openingScratch.Count; i++)
                PeerJoined?.Invoke(_openingScratch[i]);
            _openingScratch.Clear();

            ReporterChanged?.Invoke(_reporter);

            ScanSceneForAuthored();
        }

        // PORT: EndSession invokes despawn on every entity but destroys no GameObjects, so
        // spawned objects survive as orphans holding dead entities. A real disconnect keeps
        // authored objects and destroys spawned ones. Phase 5. B20
        protected void EndSession()
        {
            foreach (var kv in _entities)
            {
                if (kv.Value != null) kv.Value.InvokeDespawn();
            }
            _entities.Clear();
            _playerEntities.Clear();
            _peers.Clear();
            _blocks.Clear();
            _localPeer = PeerId.None;
            _reporter = PeerId.None;
            _isConnected = false;
            ConnectionChanged?.Invoke(false);
        }

        // ===== Peers =====

        /// <summary>
        /// Adds a peer holding the named block and raises PeerJoined. The block index is
        /// the caller's to choose: loopback counts upward, the roster draws at random among
        /// the free ones per G158.
        /// </summary>
        protected bool AddPeer(PeerId peer, int blockIndex)
        {
            if (!peer.IsValid || _peers.Contains(peer)) return false;

            _peers.Add(peer);
            if (!AssignBlock(peer, blockIndex))
            {
                _peers.Remove(peer);
                return false;
            }

            PeerJoined?.Invoke(peer);
            return true;
        }

        /// <summary>
        /// Removes a peer, destroys every personal item in its block on this machine, frees
        /// the block, releases anything it still owned, and hands the reporter role on if
        /// the departing peer held it. Room props are untouched. Per spec section 10.
        ///
        /// Ownership is cleared after PeerLeft rather than before, so a listener reading
        /// Entity.Owner during that event still sees who was holding the object. The clear
        /// itself reaches components through the ordinary authority-changed callback.
        /// </summary>
        protected void RemovePeer(PeerId peer)
        {
            if (!_peers.Remove(peer)) return;

            if (_blocks.TryGetValue(peer, out PersonalBlock block))
            {
                _removalScratch.Clear();
                foreach (var kv in _entities)
                {
                    if (kv.Value == null) continue;
                    if (!NetEntity.IsPersonalId(kv.Key)) continue;
                    if (kv.Key >= block.Min && kv.Key <= block.Max) _removalScratch.Add(kv.Value);
                }

                // Removed locally rather than through Despawn, which publishes. Every peer
                // reaches this from the same roster delivery, so a broadcast per item per
                // peer would be traffic that says what everyone already knows — and it
                // would be refused on arrival regardless, since a despawn naming a personal
                // ID is only accepted from the peer holding that block.
                for (int i = 0; i < _removalScratch.Count; i++)
                    ApplyRemoteDespawn(_removalScratch[i].Id);
                _removalScratch.Clear();
            }

            _blocks.Remove(peer);
            PeerLeft?.Invoke(peer);

            _removalScratch.Clear();
            foreach (var kv in _entities)
            {
                if (kv.Value == null || !kv.Value.IsLive) continue;
                if (kv.Value.Owner != peer) continue;
                _removalScratch.Add(kv.Value);
            }
            for (int i = 0; i < _removalScratch.Count; i++)
                _removalScratch[i].ClearOwnerOnDeparture();
            _removalScratch.Clear();

            if (_reporter == peer)
                SetReporter(ChooseSuccessorReporter());
        }

        /// <summary>
        /// Who holds the reporter role once the previous holder has gone. Loopback has no
        /// roster and keeps insertion order; the roster derives from the join times it
        /// already holds, per G22. One rule with two answers rather than a correction
        /// applied afterwards, which would raise ReporterChanged twice. Per G180.
        /// </summary>
        protected virtual PeerId ChooseSuccessorReporter()
        {
            return _peers.Count > 0 ? _peers[0] : PeerId.None;
        }

        protected void SetReporter(PeerId peer)
        {
            if (_reporter == peer) return;
            _reporter = peer;
            ReporterChanged?.Invoke(_reporter);
        }

        /// <summary>
        /// Announces that a player can now be sent to. Raised by the backend when that
        /// player's connection actually opens, which is a later and different moment from
        /// PeerJoined: the roster names a peer before any connection to them exists, and a
        /// send in that window does nothing and says nothing, per G203.
        ///
        /// Every peer raises it for every other peer, at its own moment, rather than the
        /// reporter raising it once for the room. Two peers' connections to one newcomer
        /// open independently, so there is no single instant this could be raised from.
        ///
        /// A room of one never raises it, because no second player ever becomes reachable.
        /// </summary>
        protected void RaisePeerReachable(PeerId peer)
        {
            if (!_isConnected || !peer.IsValid) return;
            if (peer == _localPeer) return;

            PeerReachable?.Invoke(peer);
        }

        protected bool HasPeer(PeerId peer) => _peers.Contains(peer);

        protected int PeerCount => _peers.Count;

        protected bool TryGetBlock(PeerId peer, out PersonalBlock block)
            => _blocks.TryGetValue(peer, out block);

        private bool AssignBlock(PeerId peer, int blockIndex)
        {
            if (_blocks.ContainsKey(peer)) return true;

            if (blockIndex < 0 || blockIndex >= NetEntity.PersonalBlockCount)
            {
                Debug.LogWarning($"Block index {blockIndex} is outside 0 to {NetEntity.PersonalBlockCount - 1}. {peer} has no avatar ID and cannot join.");
                return false;
            }

            foreach (var kv in _blocks)
            {
                if (kv.Value.Min == NetEntity.PersonalBlockBase(blockIndex))
                {
                    Debug.LogWarning($"Block {blockIndex} is already held by {kv.Key}. {peer} cannot take it.");
                    return false;
                }
            }

            ushort min = NetEntity.PersonalBlockBase(blockIndex);
            _blocks[peer] = new PersonalBlock
            {
                Min = min,
                Max = (ushort)(min + NetEntity.PersonalBlockSize - 1),
                Cursor = (ushort)(min + 1)
            };
            return true;
        }

        // ===== Entities =====

        private void ScanSceneForAuthored()
        {
            NetEntity[] found = FindObjectsByType<NetEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < found.Length; i++)
                RegisterAuthored(found[i]);
        }

        public void RegisterAuthored(NetEntity entity)
        {
            if (entity == null || !_isConnected) return;
            if (!NetEntity.IsAuthoredId(entity.Id)) return;

            if (_entities.TryGetValue(entity.Id, out NetEntity existing))
            {
                if (existing == entity) return;
                Debug.LogError($"Two objects share authored ID {entity.Id}: '{existing.name}' and '{entity.name}'. Second one refused.", entity);
                return;
            }

            _entities[entity.Id] = entity;
            entity.InitializeAuthored();
        }

        public NetEntity SpawnRoomProp(NetPrefab prefab, Vector3 position, Quaternion rotation, ushort origin)
        {
            if (!IsReporter)
            {
                Debug.LogWarning("Room props are spawned by the reporter. Send a spawn request instead.");
                return null;
            }

            if (!TryResolvePrefabIndex(prefab, out ushort prefabIndex)) return null;

            ushort id = NextRoomPropId();
            if (id == 0)
            {
                Debug.LogWarning("Room prop range is full.");
                return null;
            }

            NetEntity entity = CreateEntity(prefab, id, PeerId.None, position, rotation, origin, prefabIndex);
            if (entity != null) PublishSpawn(entity, prefabIndex);
            return entity;
        }

        public NetEntity SpawnPersonalItem(NetPrefab prefab, Vector3 position, Quaternion rotation)
        {
            if (!TryResolvePrefabIndex(prefab, out ushort prefabIndex)) return null;

            ushort id = NextPersonalId(_localPeer);
            if (id == 0)
            {
                Debug.LogWarning("No personal ID available. This peer holds no block, or its block is full.");
                return null;
            }

            NetEntity entity = CreateEntity(prefab, id, _localPeer, position, rotation, 0, prefabIndex);
            if (entity != null) PublishSpawn(entity, prefabIndex);
            return entity;
        }

        public NetEntity SpawnPlayer(NetPrefab prefab, Vector3 position, Quaternion rotation, PeerId owner)
        {
            if (!_blocks.TryGetValue(owner, out PersonalBlock block))
            {
                Debug.LogWarning($"{owner} holds no personal block, so it has no avatar ID. Player not spawned.");
                return null;
            }

            ushort id = block.Min;
            if (_entities.ContainsKey(id))
            {
                Debug.LogWarning($"Avatar ID {id} for {owner} is already live. Player not spawned.");
                return null;
            }

            // Prefab index 0: a player prefab is never in the table per G99, and no message
            // ever names an avatar, so there is no index to carry.
            return CreateEntity(prefab, id, owner, position, rotation, 0, 0, true);
        } 

        protected NetEntity CreateEntity(NetPrefab prefab, ushort id, PeerId owner, Vector3 position, Quaternion rotation, ushort origin, ushort prefabIndex, bool isPlayerAvatar = false)
        {
            if (!prefab.IsValid) return null;

            GameObject go = Instantiate(prefab.Prefab, position, rotation);
            NetEntity entity = go.GetComponent<NetEntity>();
            if (entity == null)
            {
                Debug.LogError($"NetPrefab '{prefab.Prefab.name}' has no NetEntity component.");
                Destroy(go);
                return null;
            }

            _entities[id] = entity;

            // The peer-to-entity mapping is written before InitializeSpawn, so a component
            // on the player prefab that resolves its own entity in a spawn callback finds
            // it. Avatars only: personal items share the local peer as owner and would
            // otherwise replace the local player's entry. Per G98 and G115.
            if (isPlayerAvatar) _playerEntities[owner] = entity;

            entity.InitializeSpawn(id, owner, position, rotation, origin, prefabIndex);
            return entity;
        }

        /// <summary>
        /// Turns the prefab a caller handed us into the number a Spawn message carries.
        /// Refuses rather than sending a number that means nothing on the far side.
        ///
        /// The message names no fix, because the two callers reach this from different
        /// creator surfaces and only one of them has a button. Registration belongs to the
        /// publish walk per F41, so an unregistered prefab in an unpublished build is the
        /// ordinary state rather than an authoring mistake.
        ///
        /// Runs on both backends so the refusal is visible in the editor, where there is a
        /// console to read it in, rather than only in a deployed build.
        /// </summary>
        protected bool TryResolvePrefabIndex(NetPrefab prefab, out ushort index)
        {
            index = 0;

            if (!prefab.IsValid) return false;

            U3DObjectIdRegistry registry = U3DObjectIdRegistry.Runtime;
            if (registry == null)
            {
                Debug.LogError("The multiplayer object registry is missing, so nothing can be spawned for other players. It belongs at Assets/U3D_ProjectData/Resources/U3DObjectIdRegistry.asset.");
                return false;
            }

            if (!registry.TryGetPrefabIndex(prefab.Prefab, out index))
            {
                Debug.LogWarning($"'{prefab.Prefab.name}' has no multiplayer number yet, so other players cannot be told to build it and it will not appear. Prefabs are numbered when you publish.", prefab.Prefab);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Turns a number arriving in a Spawn message back into a prefab. An index that
        /// does not resolve is not a race: indices are baked into the build, so it means
        /// the two builds differ and it will not correct itself. Logged once per unknown
        /// index rather than once per message, because a spawner set to respawn would
        /// otherwise repeat forever. Per G65.
        /// </summary>
        protected bool TryResolvePrefab(ushort index, out NetPrefab prefab)
        {
            prefab = default;

            U3DObjectIdRegistry registry = U3DObjectIdRegistry.Runtime;
            if (registry == null)
            {
                if (_unknownPrefabIndices.Add(index))
                    Debug.LogError("The multiplayer object registry is missing, so objects other players create cannot be built.");
                return false;
            }

            if (!registry.TryGetPrefab(index, out GameObject asset))
            {
                if (_unknownPrefabIndices.Add(index))
                    Debug.LogError($"Another player created an object this build does not have, at prefab index {index}. The two builds are different versions of this experience.");
                return false;
            }

            prefab = NetPrefab.FromAsset(asset);
            return true;
        }

        public void Despawn(NetEntity entity)
        {
            if (entity == null) return;

            ushort id = entity.Id;

            entity.InvokeDespawn();
            _entities.Remove(id);
            if (_playerEntities.TryGetValue(entity.Owner, out NetEntity pe) && pe == entity)
                _playerEntities.Remove(entity.Owner);
            Destroy(entity.gameObject);

            PublishDespawn(id);
        }

        /// <summary>
        /// Builds an object another player created. The room-prop cursor advances once here
        /// for every room prop received, and the reporter's advances once for every number it
        /// tries, so the two agree until the reporter has to skip a number that is still
        /// live — which cannot happen before its cursor has gone round the whole room-prop
        /// range once. After that a receiver's cursor sits behind the reporter's, which costs
        /// a skipped number if this peer later becomes reporter, because allocation skips
        /// anything live in this peer's own table. Per G325.
        ///
        /// Arrival state is the one caller that does not advance it. The counter value a
        /// newcomer is given already accounts for every object in that same message, so
        /// advancing per object would put the newcomer that many numbers ahead of everyone
        /// else — and per spec section 14 the result would be quiet rather than loud.
        /// </summary>
        protected NetEntity ApplyRemoteSpawn(NetPrefab prefab, ushort id, PeerId owner, Vector3 position, Quaternion rotation, ushort origin, ushort prefabIndex, bool advanceCursor = true)
        {
            if (id == 0) return null;

            if (_entities.ContainsKey(id))
            {
                // Ordinary if a spawn is seen twice: the object is already here. Also reached,
                // silently, when a new reporter re-issues a number this peer still holds
                // because the reporter never received that object. B104.
                return null;
            }

            if (advanceCursor && NetEntity.IsRoomPropId(id)) AdvanceRoomPropCursor();

            return CreateEntity(prefab, id, owner, position, rotation, origin, prefabIndex);
        }

        /// <summary>
        /// Removes an object another player removed. An ID that does not resolve is
        /// discarded silently: the object was already gone here, which is an ordinary race.
        /// </summary>
        protected void ApplyRemoteDespawn(ushort id)
        {
            if (!_entities.TryGetValue(id, out NetEntity entity) || entity == null)
            {
                _entities.Remove(id);
                return;
            }

            entity.InvokeDespawn();
            _entities.Remove(id);
            if (_playerEntities.TryGetValue(entity.Owner, out NetEntity pe) && pe == entity)
                _playerEntities.Remove(entity.Owner);
            Destroy(entity.gameObject);
        }

        public bool TryGetPlayerEntity(PeerId peer, out NetEntity entity)
            => _playerEntities.TryGetValue(peer, out entity);

        public bool TryGetEntity(ushort id, out NetEntity entity)
            => _entities.TryGetValue(id, out entity);

        private ushort NextRoomPropId()
        {
            int span = NetEntity.RoomPropIdMax - NetEntity.RoomPropIdMin + 1;
            for (int i = 0; i < span; i++)
            {
                ushort candidate = _roomPropCursor;
                _roomPropCursor = _roomPropCursor >= NetEntity.RoomPropIdMax
                    ? NetEntity.RoomPropIdMin
                    : (ushort)(_roomPropCursor + 1);

                if (!_entities.ContainsKey(candidate)) return candidate;
            }
            return 0;
        }

        private ushort NextPersonalId(PeerId peer)
        {
            if (!_blocks.TryGetValue(peer, out PersonalBlock block)) return 0;

            ushort first = (ushort)(block.Min + 1);
            for (int i = 0; i < NetEntity.PersonalBlockSize - 1; i++)
            {
                ushort candidate = block.Cursor;
                block.Cursor = block.Cursor >= block.Max
                    ? first
                    : (ushort)(block.Cursor + 1);

                if (!_entities.ContainsKey(candidate)) return candidate;
            }
            return 0;
        }

        /// <summary>
        /// Advances the room-prop cursor without allocating, so a peer receiving a spawn
        /// stays in step with the reporter per G26, up to the point G325 describes.
        /// </summary>
        protected void AdvanceRoomPropCursor()
        {
            _roomPropCursor = _roomPropCursor >= NetEntity.RoomPropIdMax
                ? NetEntity.RoomPropIdMin
                : (ushort)(_roomPropCursor + 1);
        }

        /// <summary>
        /// This peer's next room-prop number. Read by the reporter when it tells a newcomer
        /// where the counter has reached.
        /// </summary>
        protected ushort RoomPropCursor => _roomPropCursor;

        /// <summary>
        /// Sets this peer's counter to the value a newcomer was given. Every peer runs an
        /// identical counter per G26, and a newcomer has no way to reach the right value by
        /// counting, because it was not present for the spawns that advanced it.
        /// </summary>
        protected void SeedRoomPropCursor(ushort value)
        {
            if (value < NetEntity.RoomPropIdMin || value > NetEntity.RoomPropIdMax) return;
            _roomPropCursor = value;
        }

        /// <summary>
        /// Every object that was built during play and that a newcomer therefore has to be
        /// told to build. Walks the session's own table rather than keeping a second record
        /// of what was spawned, since the ID ranges already separate the four kinds.
        ///
        /// Authored objects are excluded because they arrive with the scene. Avatars are
        /// excluded because every peer derives them from the roster with no message at all,
        /// per spec section 19. Position and rotation are read live rather than from the
        /// spawn pose, so a newcomer sees things where they are now.
        /// </summary>
        protected void CollectSpawnedObjects(List<NetSpawnedObject> into)
        {
            if (into == null) return;
            into.Clear();

            _spawnedScratch.Clear();
            foreach (var kv in _entities)
            {
                if (kv.Value == null || !kv.Value.IsLive) continue;
                if (NetEntity.IsAuthoredId(kv.Key)) continue;
                if (NetEntity.IsAvatarId(kv.Key)) continue;

                // Index 0 is reserved in the prefab table, so an object holding it was not
                // built from the table and cannot be named to anyone else.
                if (kv.Value.PrefabIndex == 0) continue;

                _spawnedScratch.Add(kv.Value);
            }

            for (int i = 0; i < _spawnedScratch.Count; i++)
            {
                NetEntity entity = _spawnedScratch[i];
                into.Add(new NetSpawnedObject(
                    entity.Id,
                    entity.PrefabIndex,
                    entity.Origin,
                    entity.transform.position,
                    entity.transform.rotation,
                    entity.Owner));
            }
            _spawnedScratch.Clear();
        }

        /// <summary>
        /// Every authored object sitting somewhere other than where the scene put it, and
        /// which a newcomer therefore has to be moved. Walks the session's own table for
        /// the same reason the spawned list does, and the two cannot overlap: this one is
        /// authored IDs only and that one excludes them, so no object is ever described
        /// twice.
        ///
        /// An authored object with no NetRigidbody is skipped, because nothing records
        /// where it was authored and nothing could place it if it did. That is the same
        /// reach the placement has, so the list can never name something the receiver
        /// cannot act on.
        ///
        /// Poses are read live rather than from any recorded value, so a newcomer sees
        /// things where they are now.
        /// </summary>
        protected void CollectDisplacedObjects(List<NetDisplacedObject> into)
        {
            if (into == null) return;
            into.Clear();

            foreach (var kv in _entities)
            {
                NetEntity entity = kv.Value;
                if (entity == null || !entity.IsLive) continue;
                if (!NetEntity.IsAuthoredId(kv.Key)) continue;

                if (!TryGetBody(entity, out NetRigidbody body)) continue;
                if (!body.IsAwayFromAuthoredPose) continue;

                into.Add(new NetDisplacedObject(
                    entity.Id,
                    entity.transform.position,
                    entity.transform.rotation,
                    entity.Owner));
            }
        }

        /// <summary>
        /// The object's motion component, if it has one. Read off the entity's own cached
        /// component list rather than by searching the object, so this costs nothing per
        /// call and there is no second reference to keep in step with that list.
        /// </summary>
        protected static bool TryGetBody(NetEntity entity, out NetRigidbody body)
        {
            body = null;
            if (entity == null) return false;

            IReadOnlyList<NetComponent> components = entity.Components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is NetRigidbody found)
                {
                    body = found;
                    return true;
                }
            }

            return false;
        }

        // One correction leaves the reporter at this spacing, whatever the fixed step is.
        // Seven bytes at this rate is a fixed cost that does not grow with the scene; what
        // grows is how long a full pass takes, and therefore how long a peer that has
        // fallen out of step stays that way.
        private const float AuthorityCorrectionInterval = 0.05f;

        /// <summary>
        /// Re-states one object's owner and handoff counter, working through every object
        /// the room has handled and then starting again.
        ///
        /// The reporter is the right voice for this and not merely the available one: it
        /// holds the earliest join time, so it is never the peer that arrived after
        /// something happened. Every other peer's account can be short; its own can only be
        /// wrong if somebody else's was wrong first.
        ///
        /// One object per pass rather than the whole room at once, so the cost is flat
        /// rather than a burst that scales with the scene.
        ///
        /// A room of one has nobody to tell.
        /// </summary>
        private void TickAuthorityCorrection()
        {
            if (!IsReporter || _peers.Count < 2) return;

            _correctionTimer += Time.fixedDeltaTime;
            if (_correctionTimer < AuthorityCorrectionInterval) return;
            _correctionTimer = 0f;

            if (_correctionIndex >= _correctionCycle.Count) RebuildCorrectionCycle();
            if (_correctionCycle.Count == 0) return;

            ushort id = _correctionCycle[_correctionIndex];
            _correctionIndex++;

            if (!_entities.TryGetValue(id, out NetEntity entity)) return;
            if (entity == null || !entity.IsLive) return;

            PublishAuthority(entity);
        }

        /// <summary>
        /// The objects worth re-stating: everything the room has handled at least once.
        ///
        /// An object nobody has touched sits at counter zero and unowned, which is exactly
        /// what a peer joining now would build it as, so there is nothing to correct and a
        /// scene full of untouched objects costs an empty cycle.
        ///
        /// Avatars are excluded. Every peer derives them from the roster and nothing ever
        /// claims one, so a correction about an avatar could only spend a slot in the cycle.
        ///
        /// Rebuilt from the live table each pass rather than maintained, so an object that
        /// has gone since the last pass is simply absent from the next one. That is also
        /// why the cycle needs no clearing when a session ends: every entry is looked up
        /// again before it is used, and a list left over from a previous session names
        /// nothing and empties itself within one pass.
        /// </summary>
        private void RebuildCorrectionCycle()
        {
            _correctionCycle.Clear();
            _correctionIndex = 0;

            foreach (var kv in _entities)
            {
                NetEntity entity = kv.Value;
                if (entity == null || !entity.IsLive) continue;
                if (NetEntity.IsAvatarId(kv.Key)) continue;
                if (entity.Handoff == 0 && !entity.IsOwned) continue;

                _correctionCycle.Add(kv.Key);
            }
        }

        /// <summary>
        /// Puts one object's owner and handoff counter on the wire. Loopback has nobody to
        /// tell and does nothing, exactly as it does for a spawn.
        /// </summary>
        protected virtual void PublishAuthority(NetEntity entity) { }

        /// <summary>
        /// Tells every other player that an object now exists. Called by the peer entitled
        /// to have created it — the reporter for a room prop, the block holder for a
        /// personal item — and never for a player avatar, which every peer derives from the
        /// roster with no message.
        ///
        /// The prefab is already resolved to its table index, so a backend never reaches
        /// the registry and an unregistered prefab has been refused before this runs.
        ///
        /// Loopback has nobody to tell and does nothing.
        /// </summary>
        protected virtual void PublishSpawn(NetEntity entity, ushort prefabIndex) { }

        /// <summary>
        /// Tells every other player that an object is gone.
        /// </summary>
        protected virtual void PublishDespawn(ushort id) { }

        /// <summary>
        /// Sends the local player's body to everyone else. Called by the composer on the
        /// local player prefab at G16's rate, not once per frame and not once per tick.
        ///
        /// The sequence number is allocated here rather than by the caller. It identifies
        /// one packet in one sender's stream, which is the session's fact rather than the
        /// body's, and a composer that could be handed a sequence number is one that can be
        /// handed the wrong one. It wraps, which is what the receiver's comparison expects.
        /// </summary>
        public void PublishAvatarState(in NetAvatarState state)
        {
            if (!_isConnected) return;

            unchecked { _avatarSequence++; }
            PublishAvatar(_avatarSequence, state);
        }

        /// <summary>
        /// Puts one avatar packet on the wire. Loopback has nobody to tell and does
        /// nothing, exactly as it does for a spawn.
        /// </summary>
        protected virtual void PublishAvatar(ushort sequence, in NetAvatarState state) { }

        /// <summary>
        /// Sends the local player's display name. PeerId.None broadcasts to everyone;
        /// a valid peer ID unicasts to one newcomer. Empty or null names are sent as a
        /// zero-length payload, which the receiver interprets as "no profile name" and
        /// keeps the generated name it already shows.
        /// </summary>
        public void PublishDisplayName(string name, PeerId target)
        {
            if (!_isConnected) return;
            PublishDisplayNameWire(name, target);
        }

        /// <summary>
        /// Puts one display name on the wire. Loopback has nobody to tell and does
        /// nothing, exactly as it does for a spawn.
        /// </summary>
        protected virtual void PublishDisplayNameWire(string name, PeerId target) { }

        /// <summary>
        /// The playback buffer on a player's avatar, if it has one. Read off the entity's
        /// own cached component list rather than by searching the object, so a packet
        /// arriving twenty times a second per player costs no lookup — the same reason
        /// TryGetBody is written this way.
        ///
        /// Found by the interface rather than by the component's own type, because the
        /// component is on the game side and this assembly cannot name it.
        /// </summary>
        protected static bool TryGetPlayback(NetEntity entity, out INetAvatarPlayback playback)
        {
            playback = null;
            if (entity == null) return false;

            IReadOnlyList<NetComponent> components = entity.Components;
            for (int i = 0; i < components.Count; i++)
            {
                if (components[i] is INetAvatarPlayback found)
                {
                    playback = found;
                    return true;
                }
            }

            return false;
        }

        // ===== Loop =====

        /// <summary>
        /// Runs at the top of FixedUpdate, before the Tick pass. Loopback delivers its
        /// pending messages here.
        /// </summary>
        protected virtual void OnBeforeTick() { }

        private void FixedUpdate()
        {
            if (!_isConnected) return;

            OnBeforeTick();

            _tickList.Clear();
            foreach (var kv in _entities)
            {
                if (kv.Value != null) _tickList.Add(kv.Value);
            }
            for (int i = 0; i < _tickList.Count; i++)
                _tickList[i].Tick();

            TickAuthorityCorrection();
        }

        private void LateUpdate()
        {
            if (!_isConnected) return;

            _tickList.Clear();
            foreach (var kv in _entities)
            {
                if (kv.Value != null) _tickList.Add(kv.Value);
            }
            for (int i = 0; i < _tickList.Count; i++)
                _tickList[i].Render();
        }
    }
}