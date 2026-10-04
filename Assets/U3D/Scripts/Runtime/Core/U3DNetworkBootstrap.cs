using System;
using UnityEngine;
using U3D.Net;
using U3DNet = U3D.Net.Net;

namespace U3D.Networking
{
    /// <summary>
    /// Starts the network session and spawns the local player. Lives on the U3D CORE
    /// prefab root. Replaces legacy network manager, which nothing ever switched on.
    /// </summary>
    public class U3DNetworkBootstrap : MonoBehaviour
    {
        [Header("Player Prefabs")]
        [Tooltip("Spawned for the person playing on this machine. Carries the controller, camera and input.")]
        [SerializeField] private NetPrefab localPlayerPrefab;

        [Tooltip("Spawned to represent everyone else. Carries the avatar, animator and nametag, and no camera or input.")]
        [SerializeField] private NetPrefab remoteAvatarPrefab;

        /// <summary>The local player's entity, or null if the spawn did not happen.</summary>
        public NetEntity LocalPlayer { get; private set; }

        /// <summary>The room this session joined, or null when the experience is running alone.</summary>
        public string RoomPath { get; private set; }

        public NetPrefab RemoteAvatarPrefab => remoteAvatarPrefab;

        private async void Start()
        {
            if (U3DNet.Session == null)
            {
                Debug.LogError("No network session in the scene. Restore the U3D_NetSession object on the U3D CORE prefab.", this);
                return;
            }

            if (!localPlayerPrefab.IsValid)
            {
                Debug.LogError("Local Player Prefab is not assigned on the network bootstrap. No player will spawn.", this);
                return;
            }

            if (U3DRoomIdentity.TryResolveLocalRoomPath(out string resolved))
            {
                RoomPath = resolved;
            }

            // Subscribed before Connect rather than after. The roster is read as part of
            // joining, so every player already in the room is announced inside the Connect
            // call — a subscription taken out afterwards would hear about nobody who was
            // there first, and their bodies would never appear.
            U3DNet.Session.PeerJoined += HandlePeerJoined;
            U3DNet.Session.PeerLeft += HandlePeerLeft;

            JoinResult result;
            try
            {
                result = await U3DNet.Session.Connect(RoomPath ?? string.Empty);
            }
            catch (Exception e)
            {
                Debug.LogError($"The network session failed to start: {e.Message}", this);
                return;
            }

            // PORT: a refused join is logged where a deployed build cannot show it. G159
            // owes the player a brief retry before anything is said and a message they can
            // read; both belong to the roster's player-facing half. G167
            if (result == JoinResult.Full)
                Debug.LogWarning("This room is full, so the experience is running single-player.", this);
            else if (result == JoinResult.Unavailable)
                Debug.LogWarning("Multiplayer could not be reached, so the experience is running single-player.", this);

            if (!U3DNet.Session.IsConnected)
            {
                Debug.LogError("The network session did not start, so no player can spawn.", this);
                return;
            }

            SpawnLocalPlayer();
        }

        private void OnDestroy()
        {
            if (U3DNet.Session == null) return;

            U3DNet.Session.PeerJoined -= HandlePeerJoined;
            U3DNet.Session.PeerLeft -= HandlePeerLeft;
        }

        private void SpawnLocalPlayer()
        {
            U3DPlayerSpawner spawner = U3DPlayerSpawner.Instance;
            if (spawner == null)
            {
                Debug.LogError("No U3D Player Spawner in the scene, so there is no spawn position. Restore it on the U3D CORE prefab.", this);
                return;
            }

            if (spawner.GetSpawnPointCount() == 0)
            {
                Debug.LogError("No player spawn points in the scene. Tag a GameObject 'PlayerSpawnPoint' to set where players appear.", this);
                return;
            }

            (Vector3 position, Quaternion rotation) = spawner.GetSpawnData();
            LocalPlayer = U3DNet.Session.SpawnPlayer(localPlayerPrefab, position, rotation, U3DNet.Session.LocalPeer);
        }

        /// <summary>
        /// Gives another player a body on this machine.
        ///
        /// The body is created the moment the roster names them, which is well before their
        /// connection opens, so it exists to receive packets rather than because there is
        /// anything to show yet. What stops it standing at an invented position in the
        /// meantime is the avatar keeping itself out of sight until its first packet
        /// arrives — being here and being visible are two different moments, and only the
        /// second one needs data.
        ///
        /// The local player arrives through this event too, and is skipped: SpawnLocalPlayer
        /// gives them their own body, at a spawn point, with a camera and input on it.
        ///
        /// The name is worked out here rather than sent. Every machine derives the same peer
        /// ID from the roster, and the generator turns that into a name by fixed arithmetic,
        /// so everyone in the room calls this player the same thing with nothing on the
        /// wire. A real profile name, when there is one, replaces it later through the
        /// nametag's own setter.
        /// </summary>
        private void HandlePeerJoined(PeerId peer)
        {
            if (peer == U3DNet.Session.LocalPeer) return;

            if (!remoteAvatarPrefab.IsValid)
            {
                Debug.LogError("Remote Avatar Prefab is not assigned on the network bootstrap. Other players will be invisible.", this);
                return;
            }

            // Spawned at the origin rather than at a spawn point. Nothing here knows where
            // this player is, and their first packet is what says — placing them somewhere
            // plausible instead would be inventing a position that is about to be replaced.
            NetEntity avatar = U3DNet.Session.SpawnPlayer(remoteAvatarPrefab, Vector3.zero, Quaternion.identity, peer);
            if (avatar == null) return;

            var nametag = avatar.GetComponentInChildren<U3DPlayerNametag>(true);
            if (nametag != null)
                nametag.Initialize(avatar.transform, U3DSafeNameGenerator.Generate(peer.Raw));
        }

        /// <summary>
        /// A player left. Their body is destroyed by the session along with everything else
        /// in their block, because an avatar sits at the first slot of it — so there is
        /// nothing to do here beyond noting that this is where it would go if there were.
        /// </summary>
        private void HandlePeerLeft(PeerId peer)
        {
        }
    }
}
