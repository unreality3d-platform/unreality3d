using System;
using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// One authored object sitting somewhere other than where the scene put it, as it
    /// appears in arrival state.
    /// </summary>
    public readonly struct NetDisplacedObject
    {
        public readonly ushort Id;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly PeerId Owner;

        public NetDisplacedObject(ushort id, Vector3 position, Quaternion rotation, PeerId owner)
        {
            Id = id;
            Position = position;
            Rotation = rotation;
            Owner = owner;
        }
    }

    /// <summary>
    /// One object built during play, as it appears in arrival state.
    /// </summary>
    public readonly struct NetSpawnedObject
    {
        public readonly ushort Id;
        public readonly ushort PrefabIndex;
        public readonly ushort Origin;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly PeerId Owner;

        public NetSpawnedObject(ushort id, ushort prefabIndex, ushort origin, Vector3 position, Quaternion rotation, PeerId owner)
        {
            Id = id;
            PrefabIndex = prefabIndex;
            Origin = origin;
            Position = position;
            Rotation = rotation;
            Owner = owner;
        }
    }

    /// <summary>
    /// What the reporter tells a newly arrived player, per G22 and G24.
    ///
    /// This is the only variable-length message, so it carries its own encoder rather than
    /// going through the codec: the codec's field set is closed and every member of it is a
    /// fixed width, which is what lets NetWire.SizeOf answer at all. A list has no size
    /// until it is walked.
    ///
    /// A header and three sections, in the order G24 fixes and spec section 41 makes
    /// permanent. Two of them have contents. The peer list is still an empty count,
    /// because no peer sends a display name and the peer ID half tells a newcomer nothing
    /// it cannot derive from the block index the roster already gave it. An empty count is
    /// honest and costs one byte; leaving the section out would put a later build's fields
    /// at offsets this build reads as something else.
    ///
    /// Five sections are candidates for appending and two remain owed: the spawner counts
    /// and the set of removed authored IDs. The score, the Trigger Once spent state and the
    /// per-peer worn-attachment list are settled as needing none — each is sent by its own
    /// peer when a newcomer becomes reachable. A candidate appends after everything below
    /// when its open question closes, never inserted, carrying its own count at its own
    /// start.
    ///
    /// TryDecode returns without comparing its final offset against length, so a newer
    /// build's extra bytes are ignored rather than refused. Whatever appends must honour
    /// the other direction too: a section the sender did not write is read as a count of
    /// zero and the decode succeeds, because copying the bounds checks below would refuse
    /// the whole message and a newcomer would get an empty room with nothing in any log.
    ///
    /// Bytes are composed by hand, least significant first, rather than through
    /// BitConverter.GetBytes, which reports the machine's own byte order and would produce
    /// two incompatible formats without saying so.
    /// </summary>
    public static class NetArrival
    {
        /// <summary>Tag, room-prop counter, peer count, displaced count, spawned count.</summary>
        public const int HeaderSize = 8;

        /// <summary>Object ID, position, rotation, owner.</summary>
        public const int DisplacedObjectSize = 32;

        /// <summary>Object ID, prefab index, origin, position, rotation, owner.</summary>
        public const int SpawnedObjectSize = 36;

        /// <summary>
        /// Roughly how many objects fit in one arrival state. The two lists share one
        /// budget, so this is an indication rather than a limit either list reaches on its
        /// own. Encode is what actually refuses.
        /// </summary>
        public static int Capacity => (NetWire.MaxMessageSize - HeaderSize) / SpawnedObjectSize;

        /// <summary>
        /// Writes arrival state into the buffer. Returns the length, or -1 when the room
        /// holds more objects than one message can carry — refused here rather than sent
        /// truncated, because a newcomer cannot tell a short list from a complete one.
        /// </summary>
        public static int Encode(ushort roomPropCursor, List<NetDisplacedObject> displaced, List<NetSpawnedObject> spawned, byte[] buffer)
        {
            if (buffer == null) return -1;

            int displacedCount = displaced?.Count ?? 0;
            int spawnedCount = spawned?.Count ?? 0;

            // Both lists share one budget. Sizing on one of them was correct only while the
            // other could never have contents.
            int needed = HeaderSize
                       + displacedCount * DisplacedObjectSize
                       + spawnedCount * SpawnedObjectSize;

            if (needed > buffer.Length) return -1;
            if (displacedCount > ushort.MaxValue || spawnedCount > ushort.MaxValue) return -1;

            int offset = 0;

            buffer[offset++] = NetWire.TagArrivalState;

            WriteUShort(buffer, ref offset, roomPropCursor);

            // Peer list. No producer: display names have been unwired since G89, and the
            // peer ID half tells a newcomer nothing, because every peer derives every other
            // peer's ID from the block index the roster already gave them.
            buffer[offset++] = 0;

            WriteUShort(buffer, ref offset, (ushort)displacedCount);

            for (int i = 0; i < displacedCount; i++)
            {
                NetDisplacedObject o = displaced[i];

                WriteUShort(buffer, ref offset, o.Id);
                WriteVector3(buffer, ref offset, o.Position);
                WriteQuaternion(buffer, ref offset, o.Rotation);

                // Zero means unowned, per G24.
                WriteUShort(buffer, ref offset, o.Owner.IsValid ? o.Owner.Raw : (ushort)0);
            }

            WriteUShort(buffer, ref offset, (ushort)spawnedCount);

            for (int i = 0; i < spawnedCount; i++)
            {
                NetSpawnedObject o = spawned[i];

                WriteUShort(buffer, ref offset, o.Id);
                WriteUShort(buffer, ref offset, o.PrefabIndex);
                WriteUShort(buffer, ref offset, o.Origin);
                WriteVector3(buffer, ref offset, o.Position);
                WriteQuaternion(buffer, ref offset, o.Rotation);

                // Zero means unowned. A valid peer ID is the block index plus one per G158,
                // so the reader rebuilds it from the block rather than from a raw value.
                WriteUShort(buffer, ref offset, o.Owner.IsValid ? o.Owner.Raw : (ushort)0);
            }

            return offset;
        }

        /// <summary>
        /// Reads arrival state into the caller's lists. Anything malformed is refused with
        /// no log, per G183's first rule: a message this build cannot parse means the two
        /// builds differ, and announcing it on arrival helps nobody.
        /// </summary>
        public static bool TryDecode(byte[] bytes, int length, out ushort roomPropCursor, List<NetDisplacedObject> displacedInto, List<NetSpawnedObject> spawnedInto)
        {
            roomPropCursor = 0;
            if (displacedInto == null || spawnedInto == null) return false;
            displacedInto.Clear();
            spawnedInto.Clear();

            if (bytes == null || length < HeaderSize) return false;
            if (bytes[0] != NetWire.TagArrivalState) return false;

            int offset = 1;

            roomPropCursor = ReadUShort(bytes, ref offset);

            int peerCount = bytes[offset++];
            for (int i = 0; i < peerCount; i++)
            {
                // Peer ID, then a length-prefixed display name. Skipped rather than read,
                // because nothing consumes a display name yet and the section has to be
                // stepped over to reach what follows.
                if (offset + 3 > length) return false;
                offset += 2;
                int nameLength = bytes[offset++];
                offset += nameLength;
            }

            if (offset + 2 > length) return false;
            int displacedCount = ReadUShort(bytes, ref offset);
            if (offset + displacedCount * DisplacedObjectSize > length) return false;

            for (int i = 0; i < displacedCount; i++)
            {
                ushort id = ReadUShort(bytes, ref offset);
                Vector3 position = ReadVector3(bytes, ref offset);
                Quaternion rotation = ReadQuaternion(bytes, ref offset);
                ushort ownerRaw = ReadUShort(bytes, ref offset);

                PeerId owner = ownerRaw == 0 ? PeerId.None : PeerId.FromBlockIndex(ownerRaw - 1);

                displacedInto.Add(new NetDisplacedObject(id, position, rotation, owner));
            }

            if (offset + 2 > length) return false;
            int spawnedCount = ReadUShort(bytes, ref offset);
            if (offset + spawnedCount * SpawnedObjectSize > length) return false;

            for (int i = 0; i < spawnedCount; i++)
            {
                ushort id = ReadUShort(bytes, ref offset);
                ushort prefabIndex = ReadUShort(bytes, ref offset);
                ushort origin = ReadUShort(bytes, ref offset);
                Vector3 position = ReadVector3(bytes, ref offset);
                Quaternion rotation = ReadQuaternion(bytes, ref offset);
                ushort ownerRaw = ReadUShort(bytes, ref offset);

                PeerId owner = ownerRaw == 0 ? PeerId.None : PeerId.FromBlockIndex(ownerRaw - 1);

                spawnedInto.Add(new NetSpawnedObject(id, prefabIndex, origin, position, rotation, owner));
            }

            return true;
        }

        private static void WriteUShort(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUShort(byte[] buffer, ref int offset)
        {
            ushort value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static void WriteFloat(byte[] buffer, ref int offset, float value)
        {
            int bits = BitConverter.SingleToInt32Bits(value);
            buffer[offset++] = (byte)(bits & 0xFF);
            buffer[offset++] = (byte)((bits >> 8) & 0xFF);
            buffer[offset++] = (byte)((bits >> 16) & 0xFF);
            buffer[offset++] = (byte)((bits >> 24) & 0xFF);
        }

        private static float ReadFloat(byte[] buffer, ref int offset)
        {
            int bits = buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
            offset += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static void WriteVector3(byte[] buffer, ref int offset, Vector3 value)
        {
            WriteFloat(buffer, ref offset, value.x);
            WriteFloat(buffer, ref offset, value.y);
            WriteFloat(buffer, ref offset, value.z);
        }

        private static Vector3 ReadVector3(byte[] buffer, ref int offset)
        {
            float x = ReadFloat(buffer, ref offset);
            float y = ReadFloat(buffer, ref offset);
            float z = ReadFloat(buffer, ref offset);
            return new Vector3(x, y, z);
        }

        private static void WriteQuaternion(byte[] buffer, ref int offset, Quaternion value)
        {
            WriteFloat(buffer, ref offset, value.x);
            WriteFloat(buffer, ref offset, value.y);
            WriteFloat(buffer, ref offset, value.z);
            WriteFloat(buffer, ref offset, value.w);
        }

        private static Quaternion ReadQuaternion(byte[] buffer, ref int offset)
        {
            float x = ReadFloat(buffer, ref offset);
            float y = ReadFloat(buffer, ref offset);
            float z = ReadFloat(buffer, ref offset);
            float w = ReadFloat(buffer, ref offset);
            return new Quaternion(x, y, z, w);
        }
    }
}