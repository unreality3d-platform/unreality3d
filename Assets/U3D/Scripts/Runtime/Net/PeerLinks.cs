using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// One report from the browser about a single peer connection.
    /// </summary>
    [Serializable]
    public class LinkUpdateJson
    {
        public int block;
        public string state;
        public string message;
    }

    /// <summary>
    /// The peer connections, one per other player in the room. The connections themselves
    /// live in the browser, because they are browser objects with no counterpart here; this
    /// holds the record of which ones exist, which side opened them, and which are still
    /// waiting for an answer.
    ///
    /// Told about arrivals and departures by the session, and tells the session when a
    /// connection becomes usable, which is the first moment anything sent to that player
    /// can arrive.
    /// </summary>
    public class PeerLinks
    {
        public enum LinkState
        {
            /// <summary>An offer has been written and no answer has come back yet.</summary>
            Offering,

            /// <summary>An offer arrived and the browser answered it.</summary>
            Answering,

            /// <summary>Both labelled channels report open on this side.</summary>
            Open,

            /// <summary>Gave up, or the browser reported the connection failed.</summary>
            Failed
        }

        private class Link
        {
            public int Block;
            public string Uid;
            public LinkState State;
            public float ResendAt;
            public int Attempts;
            public bool Reported;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void U3DNetWatchSignals(string gameObjectName, string roomPath, int maxMessage);

        [DllImport("__Internal")]
        private static extern void U3DNetOffer(string gameObjectName, string roomPath, int remoteBlock, string remoteUid);

        [DllImport("__Internal")]
        private static extern void U3DNetSend(int remoteBlock, int channel, byte[] bytes, int length);

        [DllImport("__Internal")]
        private static extern void U3DNetClosePeer(int remoteBlock);

        [DllImport("__Internal")]
        private static extern void U3DNetCloseAll();
#endif

        private readonly string _callbackTarget;
        private readonly float _resendInterval;
        private readonly int _maxAttempts;
        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly List<Link> _pumpScratch = new List<Link>();

        private string _roomPath;
        private bool _watching;

        public PeerLinks(string callbackTarget, float resendInterval, int maxAttempts)
        {
            _callbackTarget = callbackTarget;
            _resendInterval = Mathf.Max(0.5f, resendInterval);
            _maxAttempts = Mathf.Max(1, maxAttempts);
        }

        /// <summary>
        /// Both channels to one player have come up and messages can now reach them. The
        /// block index is the argument.
        ///
        /// This is the moment a send to that player stops being a no-op, and it is the only
        /// honest one: presence in the roster is not reachability, so a message sent when a
        /// roster delivery first names somebody goes nowhere.
        ///
        /// Worth knowing which side hears a truthful version of this. On the side that made
        /// the offer, open means the channels were announced and proves nothing about the
        /// far page having recognised them. On the side that answered, the channels exist
        /// only because the announcement arrived and the label matched. The later player
        /// always offers per G190 and a newcomer is always later than the reporter, so the
        /// reporter is always the answering side toward a newcomer and its open is the half
        /// backed by something the other machine actually did.
        /// </summary>
        public event Action<int> LinkOpened;

        /// <summary>Whether anything is waiting on a resend, so the session can idle its timer.</summary>
        public bool HasPendingOffers
        {
            get
            {
                foreach (var kv in _links)
                {
                    if (kv.Value.State == LinkState.Offering) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Starts watching this player's own mailbox. Called once, as part of joining, before
        /// any peer is added, so an offer arriving from a player who joined in the same
        /// instant is not missed.
        /// </summary>
        public void Open(string roomPath)
        {
            _roomPath = roomPath;

            if (_watching) return;
            _watching = true;

#if UNITY_WEBGL && !UNITY_EDITOR
            // The browser refuses anything longer than this, and it is told the number from
            // here rather than holding its own copy, so the two cannot drift apart.
            U3DNetWatchSignals(_callbackTarget, _roomPath, NetWire.MaxMessageSize);
#endif
        }

        /// <summary>
        /// A player is in the room. When this side is the one that opens the connection, the
        /// offer goes out now; otherwise nothing happens here and their offer arrives on its
        /// own. Which side offers is decided by the caller, per G190.
        /// </summary>
        public void Add(int block, string uid, bool offer)
        {
            if (_links.ContainsKey(block)) return;

            var link = new Link
            {
                Block = block,
                Uid = uid,
                State = offer ? LinkState.Offering : LinkState.Answering,
                Attempts = 0,
                ResendAt = 0f
            };

            _links[block] = link;

            if (offer) SendOffer(link);
        }

        /// <summary>A player left the room, so the connection to them closes.</summary>
        public void Remove(int block)
        {
            if (!_links.Remove(block)) return;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetClosePeer(block);
#endif
        }

        /// <summary>This player is leaving, so every connection closes and the mailbox watcher stops.</summary>
        public void CloseAll()
        {
            _links.Clear();
            _watching = false;
            _roomPath = null;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetCloseAll();
#endif
        }

        /// <summary>
        /// Whether both labelled channels report open to this player. Presence in the room
        /// and reachability are different facts: a player is added to the session the
        /// moment a roster delivery names their block, which is before any connection has
        /// been attempted. A send to a player who is present but not reachable does
        /// nothing, and this is what lets the caller know that rather than guess.
        /// </summary>
        public bool IsOpen(int block)
            => _links.TryGetValue(block, out Link link) && link.State == LinkState.Open;

        /// <summary>
        /// Writes one message to one player. Does nothing when that connection is not open,
        /// which is an ordinary condition rather than a fault — a player who has just
        /// arrived is in the room for some moments before they can be reached.
        /// </summary>
        public void Send(int block, NetChannel channel, byte[] bytes, int length)
        {
            if (bytes == null || length <= 0) return;
            if (!IsOpen(block)) return;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetSend(block, (int)channel, bytes, length);
#endif
        }

        /// <summary>
        /// Writes one message to every player whose connection is open. There is no
        /// broadcast underneath this: a mesh is one connection per player, so a broadcast
        /// is this loop and nothing else.
        /// </summary>
        public void SendToAll(NetChannel channel, byte[] bytes, int length)
        {
            if (bytes == null || length <= 0) return;

            foreach (var kv in _links)
            {
                if (kv.Value.State != LinkState.Open) continue;

#if UNITY_WEBGL && !UNITY_EDITOR
                U3DNetSend(kv.Key, (int)channel, bytes, length);
#endif
            }
        }

        /// <summary>
        /// Re-sends any offer still unanswered. Driven by the session on an interval rather
        /// than per frame. The clock is this machine's own and is never compared against
        /// another player's, so G48 is untouched.
        /// </summary>
        public void Pump()
        {
            if (!_watching) return;

            float now = Time.realtimeSinceStartup;

            _pumpScratch.Clear();
            foreach (var kv in _links)
            {
                if (kv.Value.State == LinkState.Offering && now >= kv.Value.ResendAt)
                    _pumpScratch.Add(kv.Value);
            }

            for (int i = 0; i < _pumpScratch.Count; i++)
            {
                Link link = _pumpScratch[i];

                if (link.Attempts >= _maxAttempts)
                {
                    Fail(link, $"no answer after {link.Attempts} attempts");
                    continue;
                }

                // The item is written at a path naming both blocks, so a re-send replaces
                // the previous one rather than adding a second. Per G190.
                SendOffer(link);
            }

            _pumpScratch.Clear();
        }

        /// <summary>
        /// Called by the session with a report from the browser. Not called from anywhere
        /// else.
        /// </summary>
        public void ApplyUpdate(string json)
        {
            LinkUpdateJson update;
            try
            {
                update = JsonUtility.FromJson<LinkUpdateJson>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"A peer connection report could not be read: {e.Message}");
                return;
            }

            if (update == null) return;

            if (!_links.TryGetValue(update.block, out Link link))
            {
                // An offer from a player whose roster record has not arrived here yet, or
                // one who left between the report leaving the browser and it landing. The
                // browser holds the connection either way; the next roster delivery settles
                // whether it should.
                if (update.state == "answering")
                {
                    _links[update.block] = new Link
                    {
                        Block = update.block,
                        Uid = string.Empty,
                        State = LinkState.Answering
                    };
                }
                return;
            }

            switch (update.state)
            {
                case "answering":
                    link.State = LinkState.Answering;
                    break;

                case "open":
                    bool wasOpen = link.State == LinkState.Open;
                    link.State = LinkState.Open;
                    link.Attempts = 0;
                    if (!wasOpen) LinkOpened?.Invoke(link.Block);
                    break;

                case "failed":
                    Fail(link, string.IsNullOrEmpty(update.message) ? "the connection failed" : update.message);
                    break;
            }
        }

        private void SendOffer(Link link)
        {
            link.Attempts++;
            link.ResendAt = Time.realtimeSinceStartup + _resendInterval;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetOffer(_callbackTarget, _roomPath, link.Block, link.Uid);
#endif
        }

        private void Fail(Link link, string reason)
        {
            link.State = LinkState.Failed;

            if (!link.Reported)
            {
                link.Reported = true;

                // One line per attempted connection. A pair that never connects leaves a
                // room where each player's own view is correct and the two disagree, and
                // G159 already owes the surface that would tell a player about it, so
                // nothing is invented here.
                Debug.LogWarning($"No connection to the player holding block {link.Block}: {reason}. They will be missing from this player's room.");
            }

            // The record goes with the failure. Blocks are re-issued, so a failed link left
            // in place makes that block number unreachable from this page for the rest of
            // the session — Add returns early on a key that is already present, so a real
            // person later drawing that number is never offered to and never answered.
            _links.Remove(link.Block);

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DNetClosePeer(link.Block);
#endif
        }
    }
}