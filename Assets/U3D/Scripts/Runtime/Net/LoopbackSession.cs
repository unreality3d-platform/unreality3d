using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace U3D.Net
{
    [DefaultExecutionOrder(110)]
    public class LoopbackSession : NetSessionBase
    {
        [Header("Local identity")]
        [Tooltip("Peer ID this session claims. Must be 1 to 64: Connect derives the local block from it and AssignBlock refuses anything outside 0 to 63. Sits at the top of the range so simulated rivals are always below it and the local peer never wins a contest by holding the lowest ID.")]
        [Range(1, 64)]
        [SerializeField] private ushort localPeerRaw = 64;

        [Header("Simulation")]
        [SerializeField] private float simulatedLatency = 0.08f;
        [SerializeField] private float latencyJitter = 0.02f;

        [Header("Test overrides")]
        [Tooltip("Outcome the next Connect returns. Joined runs the real path. Full and Unavailable still connect as a room of one, so a caller's failure handling can be exercised.")]
        [SerializeField] private JoinResult forcedJoinResult = JoinResult.Joined;

        private readonly List<Pending> _pending = new List<Pending>();
        private int _nextBlockIndex;
        private double _deliveryClock;

        /// <summary>
        /// Test-only. Fires when a message would have left this machine, meaning it was
        /// broadcast with other peers present, or addressed to a peer other than the local
        /// one. Loopback has nowhere to send it, so a test subscribes here to see the
        /// outgoing half of a round trip and replies with SimulatePeerMessage.
        /// </summary>
        public event Action<NetEntity, string, PeerId, object[]> RemoteSendObserved;

        /// <summary>
        /// Test-only. The claim equivalent of RemoteSendObserved. A claim is always a
        /// broadcast, so it fires whenever another peer is in the room. Without this a
        /// harness could inject an incoming claim but could not verify that the local
        /// peer broadcast its own.
        /// </summary>
        public event Action<NetEntity, AuthorityClaim> RemoteClaimObserved;

        private struct Pending
        {
            public double DeliverAt;
            public ushort EntityId;
            public bool IsClaim;
            public AuthorityClaim Claim;
            public string Key;
            public PeerId Sender;
            public object[] Args;
        }

        private void Awake()
        {
            // The signaling backend takes the locator in a WebGL build. Per G161 the editor
            // never runs the roster, so Play mode is permanently this session.
            if (IsWebGLBuild)
            {
                enabled = false;
                return;
            }

            ClaimLocator();
        }

        public override Task<JoinResult> Connect(string sessionName)
        {
            _pending.Clear();
            _deliveryClock = 0;

            // The block is derived from the raw value rather than being 0, because the two
            // are one fact everywhere else: a peer ID is its block index plus one per §8.9,
            // and a receiver deciding who may retire a personal item derives a peer from
            // the object's ID and compares it against the local peer. With block 0 and a
            // raw value that does not match it, that comparison can never pass, so the
            // whole despawn path was unreachable in Play mode while being correct in a
            // deployed build.
            //
            // The raw value must stay within 1 to 64: AssignBlock refuses a block outside
            // 0 to 63, which leaves the peer with no block, no avatar ID and a warning
            // naming it. 64 is the top of the range, which preserves G40's property that
            // the local peer never holds the lowest ID and so never wins a contest by
            // arithmetic — every simulated rival drawn from _nextBlockIndex sits below it.
            int localBlock = localPeerRaw - 1;
            BeginSession(new PeerId(localPeerRaw), localBlock);
            _nextBlockIndex = localBlock == 0 ? 1 : 0;

            // A forced refusal still connects as a room of one, because that is the state a
            // real refusal ends in per G159, A1 and G31 — the outcome says what happened to
            // the player, not whether a session exists.
            return Task.FromResult(forcedJoinResult);
        }

        public override Task Disconnect()
        {
            EndSession();
            _pending.Clear();
            return Task.CompletedTask;
        }

        public override void SubmitClaim(NetEntity entity, AuthorityClaim claim)
        {
            if (entity == null || !IsConnected) return;

            if (PeerCount > 1)
                RemoteClaimObserved?.Invoke(entity, claim);
        }

        public override void Send(NetEntity entity, string key, PeerId target, object[] args)
        {
            if (entity == null) return;

            if (target == PeerId.None || target == LocalPeer)
                entity.RouteMessage(key, LocalPeer, args);

            if (target != LocalPeer && PeerCount > 1)
                RemoteSendObserved?.Invoke(entity, key, target, args);
        }

        // ===== Test API =====

        /// <summary>
        /// Test-only. Sets the outcome the next Connect returns. Exists alongside the
        /// serialized field so a harness can let a retry succeed after failing: set Full
        /// or Unavailable, start the retry loop, then set Joined mid-flight.
        /// </summary>
        public void ForceJoinResult(JoinResult result)
        {
            forcedJoinResult = result;
        }

        public PeerId SimulatePeerJoin(ushort peerRaw)
        {
            if (peerRaw == 0 || peerRaw == LocalPeer.Raw)
            {
                Debug.LogWarning($"Peer ID {peerRaw} is unusable. Pick a value other than 0 or {LocalPeer.Raw}.");
                return PeerId.None;
            }

            var peer = new PeerId(peerRaw);
            if (HasPeer(peer)) return peer;

            if (!AddPeer(peer, _nextBlockIndex)) return PeerId.None;

            _nextBlockIndex++;
            return peer;
        }

        public void SimulatePeerLeave(PeerId peer)
        {
            RemovePeer(peer);
        }

        /// <summary>
        /// Test-only. Hands the reporter role to a peer already in the room, so the local
        /// peer can be made a non-reporter without leaving. The only way to exercise the
        /// asking half of a request-and-arbitrate pattern under loopback.
        /// </summary>
        public void SimulateReporterChange(PeerId peer)
        {
            if (!IsConnected) return;
            if (!HasPeer(peer))
            {
                Debug.LogWarning($"{peer} is not in the room, so it cannot become the reporter. Call SimulatePeerJoin first.");
                return;
            }

            SetReporter(peer);
        }

        public void SimulatePeerClaim(PeerId peer, NetEntity entity)
        {
            if (entity == null) return;
            SimulatePeerClaim(peer, entity, entity.Handoff);
        }

        public void SimulatePeerClaim(PeerId peer, NetEntity entity, ushort handoff)
        {
            if (entity == null) return;
            _pending.Add(new Pending
            {
                DeliverAt = _deliveryClock + Latency(),
                EntityId = entity.Id,
                IsClaim = true,
                Claim = new AuthorityClaim(peer, handoff)
            });
        }

        public void SimulatePeerMessage(PeerId peer, NetEntity entity, string key, params object[] args)
        {
            if (entity == null) return;
            _pending.Add(new Pending
            {
                DeliverAt = _deliveryClock + Latency(),
                EntityId = entity.Id,
                IsClaim = false,
                Key = key,
                Sender = peer,
                Args = args
            });
        }

        private float Latency()
            => Mathf.Max(0f, simulatedLatency + UnityEngine.Random.Range(-latencyJitter, latencyJitter));

        // ===== Loop =====

        protected override void OnBeforeTick()
        {
            _deliveryClock += Time.fixedDeltaTime;
            DeliverPending();
        }

        private void DeliverPending()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].DeliverAt > _deliveryClock) continue;

                Pending p = _pending[i];
                _pending.RemoveAt(i);

                if (!TryGetEntity(p.EntityId, out NetEntity entity) || entity == null) continue;

                if (p.IsClaim) entity.ApplyRemoteClaim(p.Claim);
                else entity.RouteMessage(p.Key, p.Sender, p.Args);
            }
        }
    }
}