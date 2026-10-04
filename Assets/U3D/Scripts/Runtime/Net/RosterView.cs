using System;
using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// One slot of the signaling roster as the browser sends it.
    /// </summary>
    [Serializable]
    public class RosterSlotJson
    {
        public int blockIndex;
        public string uid;
        public long joinedAt;
    }

    /// <summary>
    /// One delivery from the roster watcher. Every delivery is the whole node, so a missed
    /// one cannot leave a peer permanently wrong.
    /// </summary>
    [Serializable]
    public class RosterReply
    {
        public bool ok;
        public string message;
        public RosterSlotJson[] slots;
    }

    public struct RosterRecord
    {
        public long JoinedAt;
        public string Uid;
    }

    /// <summary>
    /// Reading the roster, deriving the reporter from it, and deciding which side of a pair
    /// offers. Pure, with no session and no network, so it runs in the editor where the
    /// signaling layer never does per G161. Per G180.
    /// </summary>
    public static class RosterView
    {
        public static RosterReply Parse(string json)
        {
            try
            {
                RosterReply reply = JsonUtility.FromJson<RosterReply>(json);
                if (reply == null) return new RosterReply { ok = false, message = "The roster message was empty." };
                return reply;
            }
            catch (Exception e)
            {
                return new RosterReply { ok = false, message = e.Message };
            }
        }

        public static void ReadRecords(RosterReply reply, Dictionary<int, RosterRecord> into)
        {
            into.Clear();
            if (reply == null || reply.slots == null) return;

            for (int i = 0; i < reply.slots.Length; i++)
            {
                RosterSlotJson slot = reply.slots[i];
                if (slot == null) continue;
                if (slot.blockIndex < 0 || slot.blockIndex >= NetEntity.PersonalBlockCount) continue;

                into[slot.blockIndex] = new RosterRecord
                {
                    // A record with no server time cannot be ordered against one that has
                    // it, so it sorts last rather than winning the role by holding zero.
                    JoinedAt = slot.joinedAt > 0 ? slot.joinedAt : long.MaxValue,
                    Uid = slot.uid
                };
            }
        }

        /// <summary>
        /// The room's join order, stated once because two things read it. Negative when the
        /// first record is earlier, positive when it is later, zero only for one record
        /// compared with itself.
        ///
        /// Earliest join time per G22; equal times broken by the lowest block index,
        /// following G4's lowest-wins posture, because the server clock guarantees one
        /// origin for every record and not that two of them differ. Per G181.
        /// </summary>
        public static int CompareJoinOrder(int blockA, RosterRecord a, int blockB, RosterRecord b)
        {
            if (a.JoinedAt != b.JoinedAt) return a.JoinedAt < b.JoinedAt ? -1 : 1;
            if (blockA != blockB) return blockA < blockB ? -1 : 1;
            return 0;
        }

        /// <summary>
        /// Whether the local peer opens the connection to the named peer. The later of any
        /// pair offers and the earlier waits, so both reach the same answer with no message
        /// and exactly one side offers. Per G190.
        ///
        /// Evaluated per pair rather than once at join, which is what makes an arrival into
        /// an occupied room and two peers arriving in the same instant one case instead of
        /// two.
        /// </summary>
        public static bool ShouldOffer(int localBlock, RosterRecord local, int remoteBlock, RosterRecord remote)
        {
            return CompareJoinOrder(localBlock, local, remoteBlock, remote) > 0;
        }

        /// <summary>
        /// The reporter's block, or -1 when there are no records.
        /// </summary>
        public static int DeriveReporterBlock(IReadOnlyDictionary<int, RosterRecord> records)
        {
            if (records == null) return -1;

            int bestBlock = -1;
            RosterRecord best = default;

            foreach (var kv in records)
            {
                if (bestBlock < 0 || CompareJoinOrder(kv.Key, kv.Value, bestBlock, best) < 0)
                {
                    bestBlock = kv.Key;
                    best = kv.Value;
                }
            }

            return bestBlock;
        }
    }
}