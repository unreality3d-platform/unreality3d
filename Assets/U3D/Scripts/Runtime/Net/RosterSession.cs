using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// The signaling backend. Runs only in a WebGL build per G161, so the editor never
    /// reaches any of it and Play mode stays on LoopbackSession.
    /// </summary>
    [DefaultExecutionOrder(110)]
    public class RosterSession : NetSessionBase
    {
        [Header("Firebase")]
        [Tooltip("Web API key for the multiplayer sign-in. This is a public value; the database rules are what protect the data.")]
        [SerializeField] private string apiKey;

        [Tooltip("Auth domain, normally yourproject.firebaseapp.com")]
        [SerializeField] private string authDomain;

        [SerializeField] private string projectId;

        [Tooltip("Realtime Database URL, from the top of the Data tab in the Firebase console. Distinct from anything Firestore uses.")]
        [SerializeField] private string databaseURL = "https://unreality3d-default-rtdb.firebaseio.com";

        [Tooltip("Firebase JavaScript SDK version loaded from Google's CDN. Matches the portal.")]
        [SerializeField] private string sdkVersion = "10.12.2";

        [Header("Relay")]
        [Tooltip("Address discovery. Free and unlimited, and the only thing most pairs of players need to reach each other.")]
        [SerializeField] private string discoveryAddress = "stun:stun.cloudflare.com:3478";

        [Tooltip("Address of the relay credential function. Leave empty to connect players on discovery alone, which works for most pairs but not all.")]
        [SerializeField] private string relayEndpoint = "https://us-central1-unreality3d.cloudfunctions.net/createRelayCredential";

        [Header("Connections")]
        [Tooltip("Seconds between re-sends of an unanswered connection offer. Keep this above 4, the time a connection is given to gather its addresses, or a re-send can replace a connection the other player is already answering.")]
        [SerializeField] private float offerResendInterval = 6f;

        [Tooltip("How many times an unanswered offer is re-sent before the connection to that player is given up on.")]
        [SerializeField] private int offerAttempts = 5;

        [Header("Timing")]
        [Tooltip("Seconds to wait for the browser to sign in before giving up and running single-player. A placeholder until a real round trip is observed.")]
        [SerializeField] private float signInTimeout = 15f;

        [Tooltip("Seconds to wait for the block claim before giving up and running single-player. A placeholder until a real round trip is observed.")]
        [SerializeField] private float claimTimeout = 15f;

        [Tooltip("Seconds to wait for the first roster read before giving up and running single-player. A placeholder until a real round trip is observed.")]
        [SerializeField] private float rosterTimeout = 15f;

        [Serializable]
        private class FirebaseConfig
        {
            public string apiKey;
            public string authDomain;
            public string projectId;
            public string databaseURL;
        }

        [Serializable]
        private class SignInRequest
        {
            public string sdkVersion;
            public FirebaseConfig firebase;
        }

        [Serializable]
        private class SignInReply
        {
            public bool ok;
            public string uid;
            public string message;
        }

        [Serializable]
        private class ClaimReply
        {
            public bool ok;
            public int blockIndex;
            public bool full;
            public string message;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void U3DNetSignIn(string gameObjectName, string configJson);

        [DllImport("__Internal")]
        private static extern void U3DNetClaimBlock(string gameObjectName, string roomPath, int blockCount);

        [DllImport("__Internal")]
        private static extern void U3DNetLeave();

        [DllImport("__Internal")]
        private static extern void U3DNetReloadWhenConnected();

        [DllImport("__Internal")]
        private static extern void U3DNetWatchRoster(string gameObjectName, string roomPath);

        [DllImport("__Internal")]
        private static extern void U3DNetConfigureIce(string discoveryUrl, string relayEndpointUrl);

        [DllImport("__Internal")]
        private static extern int U3DNetReceive(byte[] buffer, int maxLength, int[] sender);
#endif

        private TaskCompletionSource<SignInReply> _signIn;
        private TaskCompletionSource<ClaimReply> _claim;
        private Coroutine _signInTimer;
        private Coroutine _claimTimer;

        private TaskCompletionSource<RosterReply> _rosterOpening;
        private Coroutine _rosterTimer;
        private bool _rosterOpen;
        private RosterReply _latestRoster;
        private bool _hasLatestRoster;
        private readonly Dictionary<int, RosterRecord> _records = new Dictionary<int, RosterRecord>();
        private readonly List<int> _diffScratch = new List<int>();

        /// <summary>
        /// A display name that arrived before its sender's avatar existed here, held until the
        /// roster delivery that spawns the avatar. Kept for a few seconds only, because blocks
        /// are reused: a name held for a player who left before the roster ever named them
        /// must not land on the next player given that block.
        /// </summary>
        private readonly struct PendingDisplayName
        {
            public readonly string Name;
            public readonly float ReceivedAt;

            public PendingDisplayName(string name, float receivedAt)
            {
                Name = name;
                ReceivedAt = receivedAt;
            }
        }

        private const float PendingDisplayNameLifetime = 10f;

        private readonly Dictionary<PeerId, PendingDisplayName> _pendingDisplayNames = new Dictionary<PeerId, PendingDisplayName>();
        private readonly byte[] _sendBuffer = new byte[NetWire.MaxMessageSize];
        private readonly byte[] _receiveBuffer = new byte[NetWire.MaxMessageSize];
        private readonly List<NetSpawnedObject> _arrivalScratch = new List<NetSpawnedObject>();
        private readonly List<NetDisplacedObject> _displacedScratch = new List<NetDisplacedObject>();
        private readonly int[] _receiveSender = new int[1];
        private PeerLinks _links;
        private Coroutine _linkPump;

        /// <summary>The anonymous user ID this tab signed in as, or null before sign-in.</summary>
        public string Uid { get; private set; }

        /// <summary>The block this peer holds in the room, or -1 when running alone.</summary>
        public int BlockIndex { get; private set; } = -1;

        private void Awake()
        {
            // LoopbackSession takes the locator everywhere else. The two never both claim
            // it, because this test is the only place the choice is made.
            if (!IsWebGLBuild)
            {
                enabled = false;
                return;
            }

            ClaimLocator();
        }

        public override async Task<JoinResult> Connect(string sessionName)
        {
            // No room identity means the experience runs alone, on the same code path, per
            // A1 and G31. Nothing to sign in for and nothing to claim.
            if (string.IsNullOrWhiteSpace(sessionName))
            {
                BeginSoloSession();
                return JoinResult.Joined;
            }

            SignInReply signIn = await SignIn();

            if (signIn == null || !signIn.ok)
            {
                Debug.LogWarning($"Multiplayer sign-in did not complete: {signIn?.message ?? "no answer from the browser."}");
                BeginSoloSession();
                return JoinResult.Unavailable;
            }

            Uid = signIn.uid;

            ConfigureIce();

            ClaimReply claim = await ClaimBlock(sessionName);

            if (claim == null)
            {
                Debug.LogWarning("The block claim got no answer from the browser.");
                BeginSoloSession();
                return JoinResult.Unavailable;
            }

            if (claim.full)
            {
                BeginSoloSession();
                return JoinResult.Full;
            }

            if (!claim.ok)
            {
                Debug.LogWarning($"The block claim did not complete: {claim.message}");
                BeginSoloSession();
                return JoinResult.Unavailable;
            }

            BlockIndex = claim.blockIndex;

            // The roster read is part of joining rather than something that starts after
            // it, per G180. BeginSession sets the reporter and then registers authored
            // objects, and that path reaches spawn callbacks that test IsReporter.
            RosterReply first = await WatchRoster(sessionName);

            if (first == null || !first.ok)
            {
                Debug.LogWarning($"The multiplayer roster could not be read: {first?.message ?? "no answer from the browser."}");
                BeginSoloSession();
                return JoinResult.Unavailable;
            }

            OpenSession(sessionName, first);
            return JoinResult.Joined;
        }

        private void BeginSoloSession()
        {
            // Releases anything the claim won late, which happens when the transaction
            // commits after the timeout has already answered. Without this the room holds
            // a slot for a peer that never speaks and that can hold the reporter role.
            LeaveSlot();
            TearDownLinks();

            _rosterOpen = false;
            _records.Clear();
            _latestRoster = null;
            _hasLatestRoster = false;
            BlockIndex = 0;
            BeginSession(PeerId.FromBlockIndex(0), 0);
        }

        public override Task Disconnect()
        {
            LeaveSlot();
            TearDownLinks();
            _rosterOpen = false;
            _records.Clear();
            _pendingDisplayNames.Clear();
            _latestRoster = null;
            _hasLatestRoster = false;
            BlockIndex = -1;
            EndSession();
            return Task.CompletedTask;
        }

        private void LeaveSlot()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetLeave();
#endif
        }

        // ===== Sign-in =====

        private Task<SignInReply> SignIn()
        {
            if (_signIn != null) return _signIn.Task;

            _signIn = new TaskCompletionSource<SignInReply>();

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(databaseURL))
            {
                CompleteSignIn(new SignInReply
                {
                    ok = false,
                    message = "Multiplayer configuration is incomplete on the session component."
                });
                return _signIn.Task;
            }

            var request = new SignInRequest
            {
                sdkVersion = sdkVersion,
                firebase = new FirebaseConfig
                {
                    apiKey = apiKey,
                    authDomain = authDomain,
                    projectId = projectId,
                    databaseURL = databaseURL
                }
            };

#if UNITY_WEBGL && !UNITY_EDITOR
            // The callback target is this object's own name, sent at call time, so renaming
            // the object cannot break the reply silently. Per G114 and G170.
            _signInTimer = StartCoroutine(TimeoutSignIn());
            U3DNetSignIn(gameObject.name, JsonUtility.ToJson(request));
#else
            CompleteSignIn(new SignInReply { ok = false, message = "Not a WebGL build." });
#endif

            return _signIn.Task;
        }

        private IEnumerator TimeoutSignIn()
        {
            yield return new WaitForSecondsRealtime(signInTimeout);
            _signInTimer = null;
            CompleteSignIn(new SignInReply
            {
                ok = false,
                message = $"The browser did not answer within {signInTimeout} seconds."
            });
        }

        /// <summary>
        /// Called by the browser through SendMessage. Not called from C#.
        /// </summary>
        public void OnNetSignInComplete(string json)
        {
            SignInReply reply;
            try
            {
                reply = JsonUtility.FromJson<SignInReply>(json);
            }
            catch (Exception e)
            {
                reply = new SignInReply { ok = false, message = e.Message };
            }

            CompleteSignIn(reply);
        }

        private void CompleteSignIn(SignInReply reply)
        {
            if (_signInTimer != null)
            {
                StopCoroutine(_signInTimer);
                _signInTimer = null;
            }

            if (_signIn == null || _signIn.Task.IsCompleted) return;
            _signIn.SetResult(reply);
        }

        // ===== Block claim =====

        private Task<ClaimReply> ClaimBlock(string roomPath)
        {
            if (_claim != null) return _claim.Task;

            _claim = new TaskCompletionSource<ClaimReply>();

#if UNITY_WEBGL && !UNITY_EDITOR
            _claimTimer = StartCoroutine(TimeoutClaim());
            U3DNetClaimBlock(gameObject.name, roomPath, NetEntity.PersonalBlockCount);
#else
            CompleteClaim(new ClaimReply { ok = false, message = "Not a WebGL build." });
#endif

            return _claim.Task;
        }

        private IEnumerator TimeoutClaim()
        {
            yield return new WaitForSecondsRealtime(claimTimeout);
            _claimTimer = null;
            CompleteClaim(new ClaimReply
            {
                ok = false,
                message = $"The browser did not answer within {claimTimeout} seconds."
            });
        }

        /// <summary>
        /// Called by the browser through SendMessage. Not called from C#.
        /// </summary>
        public void OnNetClaimComplete(string json)
        {
            ClaimReply reply;
            try
            {
                reply = JsonUtility.FromJson<ClaimReply>(json);
            }
            catch (Exception e)
            {
                reply = new ClaimReply { ok = false, message = e.Message };
            }

            CompleteClaim(reply);
        }

        private void CompleteClaim(ClaimReply reply)
        {
            if (_claimTimer != null)
            {
                StopCoroutine(_claimTimer);
                _claimTimer = null;
            }

            if (_claim == null || _claim.Task.IsCompleted) return;
            _claim.SetResult(reply);
        }

        // ===== Roster =====

        private Task<RosterReply> WatchRoster(string roomPath)
        {
            if (_rosterOpening != null) return _rosterOpening.Task;

            _rosterOpening = new TaskCompletionSource<RosterReply>();

#if UNITY_WEBGL && !UNITY_EDITOR
            _rosterTimer = StartCoroutine(TimeoutRoster());
            U3DNetWatchRoster(gameObject.name, roomPath);
#else
            CompleteRosterOpening(new RosterReply { ok = false, message = "Not a WebGL build." });
#endif

            return _rosterOpening.Task;
        }

        private IEnumerator TimeoutRoster()
        {
            yield return new WaitForSecondsRealtime(rosterTimeout);
            _rosterTimer = null;
            CompleteRosterOpening(new RosterReply
            {
                ok = false,
                message = $"The browser did not answer within {rosterTimeout} seconds."
            });
        }

        /// <summary>
        /// Called by the browser through SendMessage, once when the watch is attached and
        /// again on every change. Not called from C#. Each delivery is the whole node, so
        /// a missed one cannot leave this peer permanently wrong.
        ///
        /// Deliveries arriving before the session opens are kept rather than discarded.
        /// The first one answers the join, but the continuation that opens the session
        /// runs a frame later at the earliest, and a change landing in that gap used to be
        /// read and thrown away — leaving the session to open on a picture of the room
        /// that was already out of date, permanently, since only a further change to the
        /// blocks node could ever correct it.
        /// </summary>
        public void OnNetRosterUpdate(string json)
        {
            RosterReply reply = RosterView.Parse(json);

            if (reply.ok)
            {
                _latestRoster = reply;
                _hasLatestRoster = true;
            }

            if (!_rosterOpen)
            {
                CompleteRosterOpening(reply);
                return;
            }

            if (!reply.ok)
            {
                Debug.LogWarning($"A multiplayer roster update could not be read: {reply.message}");
                return;
            }

            ApplyUpdate(reply);
        }

        private void CompleteRosterOpening(RosterReply reply)
        {
            if (_rosterTimer != null)
            {
                StopCoroutine(_rosterTimer);
                _rosterTimer = null;
            }

            if (_rosterOpening == null || _rosterOpening.Task.IsCompleted) return;
            _rosterOpening.SetResult(reply);
        }

        /// <summary>
        /// The exclusion's watchpoint. G179 accepted the contention case unobserved because
        /// a duplicate block would warn; after G174 the block is the node key, so two peers
        /// can never both appear holding one. A failed exclusion looks like this instead:
        /// our own slot holding somebody else's user. Per G182.
        ///
        /// Returns false when our own slot is missing altogether, which after the session
        /// opens means the database connection dropped and the server ran the disconnect
        /// write registered at claim time.
        /// </summary>
        private bool VerifyOwnSlot()
        {
            if (!_records.TryGetValue(BlockIndex, out RosterRecord mine))
            {
                Debug.LogError($"Block {BlockIndex} is no longer in the multiplayer roster, but this player still holds it. Its avatar and items may collide with another player's.");
                return false;
            }

            if (!string.IsNullOrEmpty(Uid) && mine.Uid != Uid)
            {
                Debug.LogError($"Block {BlockIndex} is held by another player in the multiplayer roster. Two players hold one block, so they share an avatar ID and a player ID.");
            }

            return true;
        }

        /// <summary>
        /// The reporter is the earliest join time, per G22. Equal times are broken by the
        /// lowest block index, following G4's lowest-wins posture — the server clock
        /// guarantees one origin for every record, not that two differ. Per G181.
        /// </summary>
        private PeerId DeriveReporter()
        {
            int block = RosterView.DeriveReporterBlock(_records);
            return block < 0 ? LocalPeer : PeerId.FromBlockIndex(block);
        }

        protected override PeerId ChooseSuccessorReporter()
        {
            PeerId derived = DeriveReporter();
            return HasPeer(derived) ? derived : base.ChooseSuccessorReporter();
        }

        private void OpenSession(string roomPath, RosterReply first)
        {
            // The latest delivery rather than the one that answered the join. Between the
            // two, the session's opening set decides who this peer believes was present,
            // which decides the reporter — and BeginSession registers authored objects
            // under that answer once, with no second pass. A peer opening as its own
            // reporter and correcting afterwards would first drop and settle every unowned
            // Start Active object in the scene.
            //
            // Nothing can arrive between this read and _rosterOpen below: the browser
            // reaches this object through SendMessage, which cannot interleave with C#
            // already running.
            RosterReply opening = _hasLatestRoster ? _latestRoster : first;

            RosterView.ReadRecords(opening, _records);
            VerifyOwnSlot();

            var others = new List<RosterSlot>();
            foreach (var kv in _records)
            {
                if (kv.Key == BlockIndex) continue;
                others.Add(new RosterSlot(PeerId.FromBlockIndex(kv.Key), kv.Key));
            }

            _rosterOpen = true;
            BeginSession(PeerId.FromBlockIndex(BlockIndex), BlockIndex, others, DeriveReporter());

            // The mailbox is watched before any offer goes out, so an offer written by a
            // player who joined in the same instant is already sitting there when the watch
            // attaches. Every delivery is the whole mailbox, so nothing that arrived early
            // is missed.
            EnsureLinks();
            _links.Open(roomPath);
            ConnectToPresentPeers();
        }

        private void ApplyUpdate(RosterReply reply)
        {
            var incoming = new Dictionary<int, RosterRecord>();
            RosterView.ReadRecords(reply, incoming);

            // Arrivals before departures. An arrival can never move the reporter, because
            // every arrival is later than everyone already present, so doing them first
            // means a departing reporter's successor is chosen from the full set and
            // ReporterChanged is raised once.
            _diffScratch.Clear();
            foreach (var kv in incoming)
            {
                if (!_records.ContainsKey(kv.Key)) _diffScratch.Add(kv.Key);
            }

            var departed = new List<int>();
            foreach (var kv in _records)
            {
                if (!incoming.ContainsKey(kv.Key)) departed.Add(kv.Key);
            }

            _records.Clear();
            foreach (var kv in incoming) _records[kv.Key] = kv.Value;

            _records.TryGetValue(BlockIndex, out RosterRecord mine);

            for (int i = 0; i < _diffScratch.Count; i++)
            {
                int index = _diffScratch[i];
                if (index == BlockIndex) continue;

                PeerId arrived = PeerId.FromBlockIndex(index);
                if (AddPeer(arrived, index)) ApplyPendingDisplayName(arrived);

                // Which side opens the connection is decided here and nowhere else. A
                // player receiving an offer answers it; deriving the same answer on both
                // sides would give one rule two homes. Per G190.
                //
                // No own record means the order cannot be worked out at all, which
                // VerifyOwnSlot reports below as the serious thing it is.
                if (_links != null && !string.IsNullOrEmpty(mine.Uid))
                {
                    RosterRecord theirs = _records[index];
                    _links.Add(index, theirs.Uid, RosterView.ShouldOffer(BlockIndex, mine, index, theirs));
                }
            }
            _diffScratch.Clear();

            for (int i = 0; i < departed.Count; i++)
            {
                int index = departed[i];

                // Our own record leaving is not a departure to act on: routed through
                // RemovePeer it would despawn this player's own avatar, which sits at the
                // first slot of this very block. A deliberate leave never reaches here,
                // because U3DNetLeave detaches the roster watcher before removing the slot,
                // so our own record leaving always means the block was lost, and
                // VerifyOwnSlot below is what acts on it.
                if (index == BlockIndex) continue;

                RemovePeer(PeerId.FromBlockIndex(index));
                _pendingDisplayNames.Remove(PeerId.FromBlockIndex(index));
                if (_links != null) _links.Remove(index);
            }

            // The room has already dropped this player, despawned their avatar and items,
            // and may re-issue the block, so nothing here can be repaired in place. The
            // page joins again from the start once it can reach the database.
            if (!VerifyOwnSlot())
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                U3DNetReloadWhenConnected();
#endif
            }

            SetReporter(DeriveReporter());
        }

        // ===== Peer connections =====

        private void ConfigureIce()
        {
            // Discovery is set whether or not a relay is asked for, so a relay that never
            // answers leaves players connecting the way most of them would anyway.
#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetConfigureIce(discoveryAddress, relayEndpoint);
#endif
        }

        private void EnsureLinks()
        {
            if (_links != null) return;

            _links = new PeerLinks(gameObject.name, offerResendInterval, offerAttempts);
            _links.LinkOpened += OnLinkOpened;
            _linkPump = StartCoroutine(PumpLinks());
        }

        private void TearDownLinks()
        {
            if (_linkPump != null)
            {
                StopCoroutine(_linkPump);
                _linkPump = null;
            }

            if (_links == null) return;

            _links.LinkOpened -= OnLinkOpened;
            _links.CloseAll();
            _links = null;
        }

        private IEnumerator PumpLinks()
        {
            var wait = new WaitForSecondsRealtime(0.5f);

            while (true)
            {
                yield return wait;
                if (_links != null) _links.Pump();
            }
        }

        private void ConnectToPresentPeers()
        {
            if (!_records.TryGetValue(BlockIndex, out RosterRecord mine)) return;

            foreach (var kv in _records)
            {
                if (kv.Key == BlockIndex) continue;
                _links.Add(kv.Key, kv.Value.Uid, RosterView.ShouldOffer(BlockIndex, mine, kv.Key, kv.Value));
            }
        }

        /// <summary>
        /// Called by the browser through SendMessage as a connection changes state. Not
        /// called from C#.
        /// </summary>
        public void OnNetLinkUpdate(string json)
        {
            if (_links != null) _links.ApplyUpdate(json);
        }

        // ===== Arrival state =====

        /// <summary>
        /// A player has become reachable. This is the earliest moment anything sent to them
        /// can actually arrive, so it is where anyone holding state that player cannot
        /// derive gets to say so.
        ///
        /// The announcement is first and unconditional. Every peer's connection to a
        /// newcomer opens at its own moment, so there is no one instant the room could
        /// announce this from, and a peer that stayed quiet here would leave the newcomer
        /// holding a wrong picture of it until its next action.
        ///
        /// The reporter then does its own additional job on top: answering the newcomer
        /// with the room's shared state, which is what G22 makes its role.
        /// </summary>
        private void OnLinkOpened(int block)
        {
            RaisePeerReachable(PeerId.FromBlockIndex(block));

            if (!IsReporter) return;
            PublishArrivalState(block);
        }

        /// <summary>
        /// Tells one newly reachable player what is already in the room: where the shared
        /// object counter has reached, and every object built during play with the prefab
        /// each was built from.
        ///
        /// Sent to one player rather than broadcast. Everyone else was told when they
        /// arrived, and telling them again would rebuild nothing and cost the room a
        /// message per player per arrival.
        /// </summary>
        private void PublishArrivalState(int block)
        {
            if (_links == null) return;

            CollectDisplacedObjects(_displacedScratch);
            CollectSpawnedObjects(_arrivalScratch);

            int length = NetArrival.Encode(RoomPropCursor, _displacedScratch, _arrivalScratch, _sendBuffer);

            if (length < 0)
            {
                Debug.LogError($"This experience has {_displacedScratch.Count} scene objects that have moved and {_arrivalScratch.Count} objects created during play, which together are more than can be described to a player joining later — the limit is around {NetArrival.Capacity} objects. Players joining now will see the room exactly as it was built. Lower the Max Instances on the object spawners in this scene.");
                _displacedScratch.Clear();
                _arrivalScratch.Clear();
                return;
            }

            _displacedScratch.Clear();
            _arrivalScratch.Clear();

            _links.Send(block, NetChannel.Reliable, _sendBuffer, length);
        }

        /// <summary>
        /// Builds everything that was already in the room when this player arrived.
        ///
        /// Accepted from the reporter only. Unlike an ordinary spawn, the owner cannot be
        /// checked against the sender — the reporter is describing other players' objects
        /// as well as its own — so each object is checked against its own ID instead, which
        /// is the same range arithmetic G211 uses and reaches the same answer.
        /// </summary>
        private void ReceiveArrivalState(PeerId sender, byte[] bytes, int length)
        {
            if (sender != Reporter) return;

            if (!NetArrival.TryDecode(bytes, length, out ushort cursor, _displacedScratch, _arrivalScratch)) return;

            SeedRoomPropCursor(cursor);

            // Objects the scene already contains, moved to where they actually are. Only a
            // scene object can be displaced: anything built during play belongs to the list
            // below and arrives with the prefab it was built from.
            for (int i = 0; i < _displacedScratch.Count; i++)
            {
                NetDisplacedObject o = _displacedScratch[i];

                if (!NetEntity.IsAuthoredId(o.Id)) continue;

                // An ID this build does not have is discarded silently, per spec section 14.
                if (!TryGetEntity(o.Id, out NetEntity existing) || existing == null) continue;
                if (!TryGetBody(existing, out NetRigidbody body)) continue;

                body.PlaceAt(o.Position, o.Rotation);
            }

            for (int i = 0; i < _arrivalScratch.Count; i++)
            {
                NetSpawnedObject o = _arrivalScratch[i];

                if (!IsArrivalObjectConsistent(o)) continue;
                if (!TryResolvePrefab(o.PrefabIndex, out NetPrefab prefab)) continue;

                ApplyRemoteSpawn(prefab, o.Id, o.Owner, o.Position, o.Rotation, o.Origin, o.PrefabIndex, false);
            }

            _displacedScratch.Clear();
            _arrivalScratch.Clear();
        }

        /// <summary>
        /// Whether one object in arrival state is one this build could really contain. The
        /// ID is the whole test: it establishes who was entitled to create the object, and
        /// the three ranges it can fall outside are all build differences rather than
        /// anything this room can act on.
        ///
        /// The owner is deliberately not checked against the ID, and checking it was the
        /// defect at Remaining Work 1.8a. Entitlement to create and ownership now are two
        /// different facts, and only a live spawn message can treat them as one, because
        /// there the object is one instant old. An object described in arrival state has a
        /// history: per G286 a settle from its owner leaves it unowned on every machine, so
        /// a personal item lying on the ground carries no owner at all, and per G21 an
        /// object can be taken from a hand, so it may carry a peer other than its block
        /// holder. Both are ordinary, and both were discarded here with no log.
        ///
        /// A newcomer builds these at handoff zero while the room may have moved past it,
        /// which is F43 and is closed by the reporter's authority correction at G291 rather
        /// than by anything owed here.
        /// </summary>
        private bool IsArrivalObjectConsistent(NetSpawnedObject o)
        {
            if (o.Id == 0) return false;

            // Authored objects arrive with the scene and are never created by a message.
            if (NetEntity.IsAuthoredId(o.Id)) return false;

            // Avatars are derived from the roster by every player with no message at all.
            if (NetEntity.IsAvatarId(o.Id)) return false;

            if (NetEntity.IsRoomPropId(o.Id)) return true;

            return NetEntity.PersonalBlockOf(o.Id) >= 0;
        }

        // ===== Publishing =====

        /// <summary>
        /// Tells every other player to build an object this peer just created. The object
        /// ID travels in the frame rather than the payload, because Send already takes the
        /// entity, and the owner travels not at all — it follows from the ID's range.
        /// </summary>
        protected override void PublishSpawn(NetEntity entity, ushort prefabIndex)
        {
            if (entity == null || _links == null) return;

            int length = NetCodec.Encode(
                NetWire.TagSpawn,
                entity.Id,
                new object[]
                {
                    prefabIndex,
                    entity.Origin,
                    entity.transform.position,
                    entity.transform.rotation
                },
                _sendBuffer);

            if (length <= 0) return;

            _links.SendToAll(NetChannel.Reliable, _sendBuffer, length);
        }

        /// <summary>
        /// Tells every other player an object is gone. Sent after the object has been
        /// removed here, so the ID is passed in rather than read off an entity that no
        /// longer exists.
        /// </summary>
        protected override void PublishDespawn(ushort id)
        {
            if (id == 0 || _links == null) return;

            int length = NetCodec.Encode(NetWire.TagDespawn, id, null, _sendBuffer);
            if (length <= 0) return;

            _links.SendToAll(NetChannel.Reliable, _sendBuffer, length);
        }

        /// <summary>
        /// Broadcasts who holds one object and how far its handoff counter has reached.
        ///
        /// Broadcast rather than aimed at a newcomer, because being out of step is not a
        /// newcomer's condition alone — a tab that stalled, or a peer whose claim was lost
        /// against a settle, arrives at the same place by another route and nothing marks
        /// either of them out to be told.
        /// </summary>
        protected override void PublishAuthority(NetEntity entity)
        {
            if (entity == null || _links == null) return;

            int length = NetCodec.Encode(
                NetWire.TagAuthority,
                entity.Id,
                new object[] { entity.Owner, entity.Handoff },
                _sendBuffer);

            if (length <= 0) return;

            _links.SendToAll(NetChannel.Unreliable, _sendBuffer, length);
        }

        /// <summary>
        /// Puts the local player's body on the unreliable channel, to everyone whose
        /// connection is open.
        ///
        /// Unreliable is the right channel rather than a cheaper one: a lost packet is
        /// followed fifty milliseconds later by one describing the same body more
        /// recently, so resending the old one would deliver something already stale. The
        /// flags travel in every packet for the same reason — a dropped packet heals
        /// itself and a player arriving needs no separate catch-up.
        /// </summary>
        protected override void PublishAvatar(ushort sequence, in NetAvatarState state)
        {
            if (_links == null) return;

            int length = NetAvatar.Encode(sequence, state, _sendBuffer);
            if (length <= 0) return;

            _links.SendToAll(NetChannel.Unreliable, _sendBuffer, length);
        }

        protected override void PublishDisplayNameWire(string name, PeerId target)
        {
            if (_links == null) return;

            int length = NetDisplayName.Encode(name, _sendBuffer);
            if (length <= 0) return;

            if (target == PeerId.None)
            {
                _links.SendToAll(NetChannel.Reliable, _sendBuffer, length);
                return;
            }

            int block = target.BlockIndex;
            if (block < 0) return;

            _links.Send(block, NetChannel.Reliable, _sendBuffer, length);
        }

        /// <summary>
        /// Hands one player's body to their avatar's playback buffer.
        ///
        /// A packet naming a player whose avatar does not exist here yet is discarded. That
        /// is an ordinary condition rather than a fault: a connection can open before the
        /// roster delivery that names its block has landed, and the next packet fifty
        /// milliseconds later finds the avatar there.
        /// </summary>
        private void ReceiveAvatarState(PeerId sender, byte[] bytes, int length)
        {
            if (!NetAvatar.TryDecode(bytes, length, out ushort sequence, out NetAvatarState state)) return;

            if (!TryGetPlayerEntity(sender, out NetEntity entity) || entity == null) return;
            if (!TryGetPlayback(entity, out INetAvatarPlayback playback)) return;

            playback.Receive(sequence, state);
        }

        /// <summary>
        /// Applies one player's display name to their nametag.
        ///
        /// A name can arrive before the sender's avatar exists here. A player answers an
        /// offer the moment it arrives, so a newcomer's connection can open, and the
        /// newcomer's PeerReachable send can land, before this machine's roster delivery
        /// has named them. The name is sent once per connection, unlike an avatar packet,
        /// so it is held rather than discarded and applied when the roster delivery spawns
        /// the avatar.
        ///
        /// An empty name is ignored rather than applied, so the generated name stays
        /// visible until a real one arrives.
        /// </summary>
        private void ReceiveDisplayName(PeerId sender, byte[] bytes, int length)
        {
            if (!NetDisplayName.TryDecode(bytes, length, out string name)) return;
            if (string.IsNullOrEmpty(name)) return;

            if (!TryGetPlayerEntity(sender, out NetEntity entity) || entity == null)
            {
                _pendingDisplayNames[sender] = new PendingDisplayName(name, Time.unscaledTime);
                return;
            }

            ApplyDisplayName(entity, name);
        }

        /// <summary>
        /// Runs straight after a roster arrival has raised PeerJoined, by which point the
        /// bootstrap has spawned the avatar and given its nametag the generated name, so a
        /// held name applied here replaces that rather than being replaced by it.
        /// </summary>
        private void ApplyPendingDisplayName(PeerId peer)
        {
            if (!_pendingDisplayNames.TryGetValue(peer, out PendingDisplayName pending)) return;
            _pendingDisplayNames.Remove(peer);

            if (Time.unscaledTime - pending.ReceivedAt > PendingDisplayNameLifetime) return;
            if (!TryGetPlayerEntity(peer, out NetEntity entity) || entity == null) return;

            ApplyDisplayName(entity, pending.Name);
        }

        private static void ApplyDisplayName(NetEntity entity, string name)
        {
            var target = entity.GetComponentInChildren<INetDisplayNameTarget>(true);
            if (target == null) return;

            target.SetDisplayName(name);
        }

        // ===== Transport =====

        public override void SubmitClaim(NetEntity entity, AuthorityClaim claim)
        {
            if (entity == null || claim.IsEmpty) return;

            // A claim is always a broadcast, with no target test, unlike Send. The claimant
            // travels in the message rather than being taken from the connection, because a
            // correction compares two claimants for one handoff value and the sender is not
            // always the claimant.
            int length = NetCodec.Encode(
                NetWire.TagClaim,
                entity.Id,
                new object[] { claim.Peer, claim.Handoff },
                _sendBuffer);

            if (length <= 0) return;

            _links?.SendToAll(NetChannel.Reliable, _sendBuffer, length);
        }

        public override void Send(NetEntity entity, string key, PeerId target, object[] args)
        {
            if (entity == null) return;

            // Local delivery is not part of the transport and cannot wait for it. G55 makes
            // a self-addressed or broadcast message land locally, synchronously, which is
            // what lets a component have one code path instead of a branch on whether it is
            // the reporter. In a room of one every message is self-addressed, so without
            // this every arbitrated interaction is silently dead in a deployed build.
            if (target == PeerId.None || target == LocalPeer)
                entity.RouteMessage(key, LocalPeer, args);

            if (_links == null) return;
            if (target == LocalPeer) return;

            if (!NetWire.TryGetTag(key, out byte tag))
            {
                Debug.LogError($"Message key '{key}' has no tag, so it cannot leave this machine. Add it to NetWire.");
                return;
            }

            if (!NetWire.TryGetDescriptor(tag, out NetWire.Descriptor descriptor)) return;

            int length = NetCodec.Encode(tag, entity.Id, args, _sendBuffer);
            if (length <= 0) return;

            if (target == PeerId.None)
            {
                _links.SendToAll(descriptor.Channel, _sendBuffer, length);
                return;
            }

            // A player present in the room but not yet reachable is a silent no-op here,
            // and that is correct rather than a gap being tolerated. What is wrong is
            // sending at the roster moment, which is always too early: a roster delivery
            // names a peer before any connection to them exists. Sending at the moment
            // their connection opens is the same cue arrival state uses, and PeerReachable
            // is where a component gets it.
            int block = target.BlockIndex;
            if (block < 0) return;

            _links.Send(block, descriptor.Channel, _sendBuffer, length);
        }

        /// <summary>
        /// Takes every message the browser has been holding since the last fixed step and
        /// delivers it, reliable channel first. Messages are drained here rather than
        /// pushed as they arrive, because a push would run a component's handler at an
        /// arbitrary point in the frame — partway through a physics step, or between two
        /// components' own Update calls — which nothing on this side expects.
        /// </summary>
        protected override void OnBeforeTick()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_links == null) return;

            while (true)
            {
                int length = U3DNetReceive(_receiveBuffer, _receiveBuffer.Length, _receiveSender);

                // Nothing left this step.
                if (length == 0) return;

                // One message was too long for the buffer, so it was skipped rather than
                // read. Keep draining: reporting it the same way as an empty queue would
                // stall everything behind it for a whole step, one step per such message,
                // with nothing naming the cause.
                if (length < 0) continue;

                PeerId sender = PeerId.FromBlockIndex(_receiveSender[0]);
                if (!sender.IsValid) continue;

                Deliver(sender, _receiveBuffer, length);
            }
#endif
        }

        private void Deliver(PeerId sender, byte[] bytes, int length)
        {
            if (length < 1) return;

            // Arrival state is read before the codec is asked anything, because it is the
            // one message with no fixed field layout for the codec to work from.
            if (bytes[0] == NetWire.TagArrivalState)
            {
                ReceiveArrivalState(sender, bytes, length);
                return;
            }

            // The avatar packet is read here for a different reason: it has a fixed layout
            // and the codec's closed field set cannot describe it, since head pitch is one
            // signed byte. It also names no object — the sender's block is what identifies
            // whose body this is — so there is no ID for the frame to carry.
            if (bytes[0] == NetWire.TagAvatar)
            {
                ReceiveAvatarState(sender, bytes, length);
                return;
            }

            // A display name is variable-length (UTF-8 string) and outside the codec's
            // closed field set, so it is read here before the codec is asked anything,
            // exactly as arrival state is and for the same reason. The sender's peer
            // identifies whose name this is, so there is no object ID.
            if (bytes[0] == NetWire.TagDisplayName)
            {
                ReceiveDisplayName(sender, bytes, length);
                return;
            }

            if (!NetCodec.Decode(bytes, length, out byte tag, out ushort objectId, out object[] args))
                return;

            if (!NetWire.TryGetDescriptor(tag, out NetWire.Descriptor descriptor)) return;

            // The tag is tested before the object ID is resolved. Spawn names an object the
            // receiving player does not have yet — that is what a spawn is — so resolving
            // first would discard it as an unknown ID, which is its ordinary condition
            // rather than a fault.
            if (descriptor.SessionOwned)
            {
                DeliverToSession(tag, sender, objectId, args);
                return;
            }

            if (tag == NetWire.TagClaim)
            {
                if (!TryGetEntity(objectId, out NetEntity claimed) || claimed == null) return;
                if (!(args[0] is PeerId claimant)) return;
                if (!(args[1] is ushort handoff)) return;

                claimed.ApplyRemoteClaim(new AuthorityClaim(claimant, handoff));
                return;
            }

            // Only the reporter speaks for an object's owner and counter. A correction from
            // anyone else is a peer describing a room it does not speak for, and taking it
            // would let a peer that is itself out of step pull everyone else out with it.
            if (tag == NetWire.TagAuthority)
            {
                if (sender != Reporter) return;
                if (!TryGetEntity(objectId, out NetEntity corrected) || corrected == null) return;
                if (!(args[0] is PeerId owner)) return;
                if (!(args[1] is ushort counter)) return;

                corrected.ApplyAuthorityCorrection(owner, counter);
                return;
            }

            // An ID that does not resolve is discarded silently. The object was destroyed a
            // moment before the message arrived, the sender will find out, and nothing is
            // wrong.
            if (!TryGetEntity(objectId, out NetEntity entity) || entity == null) return;
            if (descriptor.Key == null) return;

            entity.RouteMessage(descriptor.Key, sender, args);
        }

        private void DeliverToSession(byte tag, PeerId sender, ushort objectId, object[] args)
        {
            switch (tag)
            {
                case NetWire.TagSpawn:
                    ReceiveSpawn(sender, objectId, args);
                    return;

                case NetWire.TagDespawn:
                    ReceiveDespawn(sender, objectId);
                    return;
            }
        }

        /// <summary>
        /// Builds an object another player created.
        ///
        /// The owner is derived rather than carried. A room prop is unowned; a personal
        /// item belongs to whoever holds its block, which is the same comparison that
        /// establishes the sender was entitled to create it, so the two can never disagree.
        /// </summary>
        private void ReceiveSpawn(PeerId sender, ushort objectId, object[] args)
        {
            if (!TryResolveSpawnOwner(sender, objectId, out PeerId owner)) return;

            if (!(args[0] is ushort prefabIndex)) return;
            if (!(args[1] is ushort origin)) return;
            if (!(args[2] is Vector3 position)) return;
            if (!(args[3] is Quaternion rotation)) return;

            if (!TryResolvePrefab(prefabIndex, out NetPrefab prefab)) return;

            ApplyRemoteSpawn(prefab, objectId, owner, position, rotation, origin, prefabIndex);
        }

        /// <summary>
        /// Removes an object another player removed. Entitlement is the same rule as a
        /// spawn: only the reporter retires a room prop, only a block's holder retires
        /// something from it.
        /// </summary>
        private void ReceiveDespawn(PeerId sender, ushort objectId)
        {
            if (!TryResolveSpawnOwner(sender, objectId, out _)) return;

            ApplyRemoteDespawn(objectId);
        }

        /// <summary>
        /// Whether this sender was entitled to create or retire this ID, and who owns the
        /// result. Checked on receipt from the ID's range alone, per G24, which is what
        /// lets a Spawn carry no owner field.
        ///
        /// Refused without a log: a peer running the same build cannot produce any of
        /// these, so they mean a build difference or a message that should never have been
        /// sent, and neither corrects itself by being announced on every arrival.
        /// </summary>
        private bool TryResolveSpawnOwner(PeerId sender, ushort objectId, out PeerId owner)
        {
            owner = PeerId.None;

            if (objectId == 0) return false;

            // Authored objects live in the scene and are never created by a message. One
            // naming an authored ID would displace a scene object in the session's table.
            if (NetEntity.IsAuthoredId(objectId)) return false;

            if (NetEntity.IsRoomPropId(objectId))
                return sender == Reporter;

            // Avatars are derived from the roster by every peer with no message at all, so
            // no message may name one.
            if (NetEntity.IsAvatarId(objectId)) return false;

            int block = NetEntity.PersonalBlockOf(objectId);
            if (block < 0) return false;

            PeerId holder = PeerId.FromBlockIndex(block);
            if (!holder.IsValid || holder != sender) return false;

            owner = holder;
            return true;
        }
    }
}