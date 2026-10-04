using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace U3D.Net
{
    public static class Net
    {
        public static INetSession Session { get; internal set; }
    }

    // Per G159. Unavailable is zero so an unassigned value reports a transient
    // failure rather than a silent success.
    public enum JoinResult
    {
        Unavailable = 0,
        Joined = 1,
        Full = 2
    }

    public interface INetSession
    {
        PeerId LocalPeer { get; }
        PeerId Reporter { get; }
        bool IsReporter { get; }
        IReadOnlyList<PeerId> Peers { get; }
        bool IsConnected { get; }

        event Action<PeerId> PeerJoined;
        event Action<PeerId> PeerLeft;
        event Action<PeerId> ReporterChanged;
        event Action<bool> ConnectionChanged;

        /// <summary>
        /// A player this peer can now actually send to. Distinct from PeerJoined, which
        /// fires the instant a roster delivery names somebody — always before their
        /// connection opens, so anything sent from it is discarded silently at the
        /// transport per G203. This is the earliest moment a send to that player arrives.
        ///
        /// Raised on every peer, not only the reporter, and once per player per
        /// connection. A component holding state nobody else can derive — what its player
        /// is wearing, the pose of something in their hand — sends it here.
        /// </summary>
        event Action<PeerId> PeerReachable;

        Task<JoinResult> Connect(string sessionName);
        Task Disconnect();

        void RegisterAuthored(NetEntity entity);

        // origin names the authored object that caused this spawn, or 0 for none.
        // It travels in the Spawn message and in arrival state, so every peer can
        // attribute a room prop to the spawner that produced it.
        NetEntity SpawnRoomProp(NetPrefab prefab, Vector3 position, Quaternion rotation, ushort origin);
        NetEntity SpawnPersonalItem(NetPrefab prefab, Vector3 position, Quaternion rotation);
        NetEntity SpawnPlayer(NetPrefab prefab, Vector3 position, Quaternion rotation, PeerId owner);
        void Despawn(NetEntity entity);

        bool TryGetPlayerEntity(PeerId peer, out NetEntity entity);
        bool TryGetEntity(ushort id, out NetEntity entity);

        // Transport plumbing. Called by NetEntity and NetMessage, not by game code.
        // A message addressed to the local peer, or broadcast, is delivered locally.
        // The local player's body, twenty times a second. Broadcast on the unreliable
        // channel, so a lost packet is replaced by the next one rather than resent.
        void PublishAvatarState(in NetAvatarState state);
        void SubmitClaim(NetEntity entity, AuthorityClaim claim);
        void Send(NetEntity entity, string key, PeerId target, object[] args);

        /// <summary>
        /// Sends the local player's display name to one peer or to everyone. PeerId.None
        /// means broadcast. Called by the player controller when the profile name
        /// resolves, and on PeerReachable to tell a newcomer what this player is called.
        /// </summary>
        void PublishDisplayName(string name, PeerId target);
    }
}