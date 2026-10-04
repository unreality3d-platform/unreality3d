using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    public enum NetChannel
    {
        Reliable,
        Unreliable
    }

    /// <summary>
    /// One field in a message payload. The set is closed: a new kind is a format change
    /// and takes a new entry number rather than an edit.
    ///
    /// Rotation is a quaternion in four bytes rather than sixteen, by smallest-three:
    /// the largest of the four components is dropped and rebuilt from the unit-length
    /// constraint, and the other three are quantized to ten bits each over the range a
    /// unit quaternion bounds them to. It carries no range constant of its own, which is
    /// what separates it from quantizing a position — the bound is a property of the
    /// value rather than an assumption about the world.
    /// </summary>
    public enum NetField
    {
        Vector3,
        Quaternion,
        PeerId,
        UShort,
        Rotation
    }

    /// <summary>
    /// The wire's message table. Every message on either channel begins with one of these
    /// tags, followed by a two-byte object ID where the message names an object, followed
    /// by the declared payload.
    ///
    /// Numbers are written out rather than derived from position, because a derived number
    /// changes when this file is reordered and nothing would see it. They are never reused
    /// and never renumbered: a published build names a number, so two builds must not
    /// disagree about what a number means. This is the prefab table's rule for the same
    /// reason.
    ///
    /// A tag this build does not recognise is discarded silently, with no error, no log at
    /// default verbosity and no connection state change. A reserved tag below is a number
    /// held so two later pieces cannot pick it twice; it has no layout and nothing encodes
    /// it. Adding a layout to a reserved tag later is an addition, not a change.
    ///
    /// One tag space across both channels, so a tag means the same thing everywhere and a
    /// receiver never tests which channel a message arrived on.
    ///
    /// A message may only grow by appending a field at the end, never by insertion: an
    /// older build reads the fields it knows and ignores the rest, and an inserted field
    /// would make it read every later field as the wrong thing, confidently.
    /// </summary>
    public static class NetWire
    {
        public const byte TagReserved = 0;
        public const byte TagAvatar = 1;
        public const byte TagMotion = 2;
        public const byte TagClaim = 3;
        public const byte TagRelease = 4;
        public const byte TagSettle = 5;
        public const byte TagSpawn = 6;
        public const byte TagDespawn = 7;
        public const byte TagDespawnRequest = 8;
        public const byte TagSpawnRequest = 9;
        public const byte TagCollectRequest = 10;
        public const byte TagCollectResult = 11;
        public const byte TagCollectReset = 12;
        public const byte TagHold = 13;
        public const byte TagThrown = 14;
        public const byte TagKicked = 15;
        public const byte TagArrivalState = 16;
        public const byte TagChat = 17;
        public const byte TagEmote = 18;
        public const byte TagRideableCorrection = 19;
        public const byte TagDrive = 20;
        public const byte TagAttachments = 21;
        public const byte TagAuthority = 22;
        public const byte TagAvatarPose = 23;
        public const byte TagInteract = 24;
        public const byte TagEnterTrigger = 25;
        public const byte TagExitTrigger = 26;
        public const byte TagZoneTransition = 27;
        public const byte TagDisplayName = 28;

        public const int TagCount = 29;

        /// <summary>
        /// Largest any message with a declared field layout can be. Every field in the set
        /// is a fixed width, so this is arithmetic rather than a guess.
        /// </summary>
        public const int MaxFixedMessageSize = 64;

        /// <summary>
        /// The transport's ceiling, and the size of the buffers on both sides of the
        /// bridge. Arrival state is variable-length and is the only message this bound is
        /// for; everything else fits in MaxFixedMessageSize several times over.
        ///
        /// One number rather than two. The browser refuses anything longer than it is told
        /// to accept, and it is told from here, so there is no second copy to keep in step.
        ///
        /// A message this size is carried by every browser that supports the transport at
        /// all, and it holds roughly 1,800 spawned objects. A room past that gets a named
        /// error where the message is composed rather than a silent gap where it is read.
        /// </summary>
        public const int MaxMessageSize = 65536;

        public readonly struct Descriptor
        {
            /// <summary>
            /// The message key this tag stands for, or null when the message belongs to the
            /// session rather than to a component. The tag is what travels; the key is what
            /// the receiving entity routes on.
            /// </summary>
            public readonly string Key;

            /// <summary>
            /// Whether a two-byte object ID follows the tag.
            /// </summary>
            public readonly bool CarriesId;

            public readonly NetChannel Channel;

            /// <summary>
            /// The payload after the tag and the object ID. Null means reserved: the number
            /// is held and no layout is declared, so nothing encodes or decodes it.
            /// </summary>
            public readonly NetField[] Fields;

            /// <summary>
            /// Whether the message is delivered to the session rather than routed to a
            /// component on the named object. Spawn and arrival state name objects the
            /// receiving peer does not have yet, so resolving an ID first would discard
            /// them as unknown, which is their ordinary condition rather than a fault.
            /// </summary>
            public readonly bool SessionOwned;

            /// <summary>
            /// Whether multiple components on one entity are expected to register this
            /// same routing string. When true, the entity's duplicate-key validator
            /// allows the collision. Each handler distinguishes itself by an instance
            /// index packed into the payload, so all of them receive every message and
            /// only the matching one fires its event.
            /// </summary>
            public readonly bool InstanceKeyed;

            public bool IsReserved => Fields == null;

            public Descriptor(string key, bool carriesId, NetChannel channel, NetField[] fields,
                bool sessionOwned = false, bool instanceKeyed = false)
            {
                Key = key;
                CarriesId = carriesId;
                Channel = channel;
                Fields = fields;
                SessionOwned = sessionOwned;
                InstanceKeyed = instanceKeyed;
            }
        }

        private static readonly NetField[] Empty = new NetField[0];

        private static readonly Descriptor[] _table = BuildTable();

        private static readonly Dictionary<string, byte> _tagsByKey = BuildKeyIndex();

        private static Descriptor[] BuildTable()
        {
            var table = new Descriptor[TagCount];

            // Reserved. An all-zero buffer must not decode as a message.
            table[TagReserved] = new Descriptor(null, false, NetChannel.Reliable, null);

            // Reserved here, and read before any descriptor lookup, because the codec's
            // closed field set cannot describe it — head pitch is one signed byte. It is
            // keyed by peer rather than by object, so there is no ID for a frame to carry.
            // Its consumer is the playback buffer on the remote avatar, reached through
            // INetAvatarPlayback rather than through this table.
            table[TagAvatar] = new Descriptor(null, false, NetChannel.Unreliable, null);

            // A driven object: one a player is steering from outside it, which is a
            // continuous act rather than a single instant. Position and rotation because
            // the result is a physics outcome no other machine can reproduce, and a
            // sequence number because the unreliable channel delivers out of order and
            // there is no clock on the seam to compare against instead.
            //
            // No velocity. Playback interpolates between two samples it holds and never
            // extrapolates, exactly as the avatar packet does, so a velocity would be a
            // field nothing reads.
            table[TagMotion] = new Descriptor(NetKeys.MotionDriven, true, NetChannel.Unreliable,
                new[] { NetField.Vector3, NetField.Quaternion, NetField.UShort });

            // The claim carries the claimant, who is not always the sender, because a
            // correction compares the two claimants for one handoff value.
            table[TagClaim] = new Descriptor(null, true, NetChannel.Reliable,
                new[] { NetField.PeerId, NetField.UShort });

            table[TagRelease] = new Descriptor(NetKeys.MotionRelease, true, NetChannel.Reliable,
                new[] { NetField.Vector3, NetField.Quaternion, NetField.Vector3, NetField.Vector3 });

            table[TagSettle] = new Descriptor(NetKeys.MotionSettle, true, NetChannel.Reliable,
                new[] { NetField.Vector3, NetField.Quaternion });

            // No owner field. The object ID gives the block and the block gives the peer,
            // so carrying an owner would be a second copy of a fact already on the wire.
            table[TagSpawn] = new Descriptor(null, true, NetChannel.Reliable,
                new[] { NetField.UShort, NetField.UShort, NetField.Vector3, NetField.Quaternion },
                sessionOwned: true);

            table[TagDespawn] = new Descriptor(null, true, NetChannel.Reliable, Empty,
                sessionOwned: true);

            table[TagDespawnRequest] = new Descriptor(NetKeys.SpawnedDespawn, true, NetChannel.Reliable, Empty);
            table[TagSpawnRequest] = new Descriptor(NetKeys.SpawnerRequest, true, NetChannel.Reliable, Empty);

            table[TagCollectRequest] = new Descriptor(NetKeys.CollectableRequest, true, NetChannel.Reliable, Empty);

            table[TagCollectResult] = new Descriptor(NetKeys.CollectableResult, true, NetChannel.Reliable,
                new[] { NetField.PeerId });

            table[TagCollectReset] = new Descriptor(NetKeys.CollectableReset, true, NetChannel.Reliable, Empty);

            table[TagHold] = new Descriptor(NetKeys.GrabbableHold, true, NetChannel.Reliable,
                new[] { NetField.Vector3, NetField.Quaternion });

            table[TagThrown] = new Descriptor(NetKeys.ThrowableThrown, true, NetChannel.Reliable, Empty);
            table[TagKicked] = new Descriptor(NetKeys.KickableKicked, true, NetChannel.Reliable, Empty);

            // Variable-length and outside the codec's closed field set, so it carries its
            // own encoder at NetArrival and is routed before any descriptor lookup. Kept
            // reserved here so nothing tries to decode it as a fixed layout.
            table[TagArrivalState] = new Descriptor(null, false, NetChannel.Reliable, null,
                sessionOwned: true);

            // Reserved. Both have a format on record and no producer anywhere.
            table[TagChat] = new Descriptor(null, false, NetChannel.Reliable, null);
            table[TagEmote] = new Descriptor(null, false, NetChannel.Reliable, null);

            // A platform on an authored route. Position and rotation, then the route
            // bookkeeping a receiver needs to carry on from the same place: the waypoint
            // being travelled to, and the pause remainder packed with the ping-pong
            // direction. Unreliable because a lost correction is repaired by the next one
            // within two seconds, and a retransmit queue would deliver a stale pose.
            table[TagRideableCorrection] = new Descriptor(NetKeys.RideableCorrection, true, NetChannel.Unreliable,
                new[] { NetField.Vector3, NetField.Quaternion, NetField.UShort, NetField.UShort });

            // A drive beginning. Carries nothing: the first driven sample says where the
            // object is, and this says only that someone has taken hold of it, so a
            // creator's start event fires on every machine rather than only the driver's.
            // The end needs no counterpart — the release already reaches everyone and the
            // object leaving the driven state is what every peer derives it from.
            table[TagDrive] = new Descriptor(NetKeys.MotionDrive, true, NetChannel.Reliable, Empty);

            // What one player is wearing: eight attachment-station IDs in wear order,
            // newest last, zero for an empty slot. The object ID in the frame is the
            // wearer's own avatar.
            //
            // The whole set travels every time rather than a change to it. A set that
            // arrives late, twice, or out of order still leaves the receiver holding
            // exactly what the wearer holds, so there is no repair path to build and
            // nothing to keep in step.
            //
            // Eight is a wire constant, not a preference. U3DPlayerAttachments's cap must
            // match it, and wanting more of them later is a new tag rather than an edit to
            // this one.
            table[TagAttachments] = new Descriptor(NetKeys.AttachmentsWorn, true, NetChannel.Reliable,
                new[]
                {
                    NetField.UShort, NetField.UShort, NetField.UShort, NetField.UShort,
                    NetField.UShort, NetField.UShort, NetField.UShort, NetField.UShort
                });
            // Who holds an object and how many times it has changed hands. The reporter
            // re-states this on a rolling cycle rather than anyone keeping a tally in step,
            // which is the rideable correction's shape applied to a value instead of a
            // pose: nothing accumulates, so nothing can drift permanently.
            //
            // A running count repaired only by the messages that advance it can never
            // recover from a missed one. A peer that joins after an object has been handled
            // starts at zero, discards every later claim about it and has its own discarded
            // in turn, and nothing in the room closes the gap. That is what this exists for,
            // and a stalled tab or a claim lost against a settle produce the same state by
            // other routes.
            //
            // Unreliable, for the reason the rideable correction is: a lost one is replaced
            // by the next pass, and a retransmit would deliver a value already superseded.
            table[TagAuthority] = new Descriptor(null, true, NetChannel.Unreliable,
                new[] { NetField.PeerId, NetField.UShort });

            // A VR player's head and both hands. The object ID in the frame is the sender's
            // own avatar, as the worn-attachment set's is.
            //
            // Head rotation and both wrists on one message rather than two, because a hand
            // is stored as an offset from the head bone: a receiver holding hands and a
            // head from different instants reconstructs both arms against a head that was
            // never there. They are one pose and they travel together.
            //
            // Each wrist is an offset from the head bone, not a point in player-root space.
            // The head bone's own position is something every machine already has, so
            // sending a composed point would carry it twice, and the offset's magnitude is
            // an arm's reach rather than an arm's reach plus a body's height.
            //
            // The offsets stay at full precision deliberately. Quantizing them needs a
            // range constant, and under the no-growth rule a range constant chosen wrongly
            // cannot be widened later — it fails as a hand clipping silently at the limit.
            // The three rotations quantize with no such constant, which is why they do.
            //
            // A sequence number because the unreliable channel delivers out of order, and
            // an overtaken sample would throw a hand backwards. The channel is unreliable
            // for the reason the driven sample's is: the next one replaces a lost one, and
            // a retransmit would deliver a pose already superseded.
            table[TagAvatarPose] = new Descriptor(NetKeys.AvatarPose, true, NetChannel.Unreliable,
                new[]
                {
                    NetField.Rotation,
                    NetField.Vector3, NetField.Rotation,
                    NetField.Vector3, NetField.Rotation,
                    NetField.UShort
                });

            // An interact trigger fired. Empty payload — the object ID in the frame says
            // which trigger, and nothing else is needed. Reliable because a missed
            // interaction is a door that opened on one screen and not on another.
            table[TagInteract] = new Descriptor(NetKeys.InteractTriggered, true, NetChannel.Reliable, Empty);

            // An enter trigger fired. One UShort payload carrying the instance index, so
            // multiple enter triggers on the same entity each fire only their own event.
            // Reliable because a missed enter is a waterfall that started on one screen
            // and not on another.
            table[TagEnterTrigger] = new Descriptor(NetKeys.EnterTriggered, true, NetChannel.Reliable,
                new[] { NetField.UShort }, instanceKeyed: true);

            // An exit trigger fired. Same shape as the enter trigger: one UShort instance
            // index, reliable, instance-keyed.
            table[TagExitTrigger] = new Descriptor(NetKeys.ExitTriggered, true, NetChannel.Reliable,
                new[] { NetField.UShort }, instanceKeyed: true);

            // A zone transition. One UShort carrying the instance index in the low byte
            // and the event type in the high byte (0 = occupied, 1 = cleared). Reliable
            // because a missed transition is a door that opened but never closed, or a
            // light that turned on for one player and not another.
            table[TagZoneTransition] = new Descriptor(NetKeys.ZoneTransition, true, NetChannel.Reliable,
                new[] { NetField.UShort }, instanceKeyed: true);

            // Variable-length and outside the codec's closed field set (UTF-8 string),
            // so it carries its own encoder at NetDisplayName and is routed before any
            // descriptor lookup. No object ID — the sender's peer identifies whose name
            // this is, exactly as the avatar packet identifies whose body it is.
            table[TagDisplayName] = new Descriptor(null, false, NetChannel.Reliable, null);

            return table;
        }

        private static Dictionary<string, byte> BuildKeyIndex()
        {
            var index = new Dictionary<string, byte>(TagCount);
            for (byte tag = 0; tag < _table.Length; tag++)
            {
                string key = _table[tag].Key;
                if (key != null) index[key] = tag;
            }
            return index;
        }

        public static bool TryGetDescriptor(byte tag, out Descriptor descriptor)
        {
            if (tag >= _table.Length)
            {
                descriptor = default;
                return false;
            }
            descriptor = _table[tag];
            return !descriptor.IsReserved;
        }

        public static bool TryGetTag(string key, out byte tag)
            => _tagsByKey.TryGetValue(key, out tag);

        public static int SizeOf(NetField field)
        {
            switch (field)
            {
                case NetField.Vector3: return 12;
                case NetField.Quaternion: return 16;
                case NetField.PeerId: return 2;
                case NetField.UShort: return 2;
                case NetField.Rotation: return 4;
                default: return 0;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Every registered message key has exactly one tag, and no two tags share a key.
        /// The entity already refuses two components sharing one key; this is the same
        /// check on the other axis, and it runs where the answer is legible rather than in
        /// a chat's care.
        /// </summary>
        [UnityEditor.InitializeOnLoadMethod]
        private static void ValidateTable()
        {
            var seen = new Dictionary<string, byte>(TagCount);
            for (byte tag = 0; tag < _table.Length; tag++)
            {
                string key = _table[tag].Key;
                if (key == null) continue;

                if (seen.TryGetValue(key, out byte earlier))
                {
                    Debug.LogError($"NetWire: message key '{key}' is on both tag {earlier} and tag {tag}. One key, one tag.");
                    continue;
                }
                seen[key] = tag;
            }

            var fields = typeof(NetKeys).GetFields(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(string)) continue;
                string key = (string)fields[i].GetValue(null);
                if (seen.ContainsKey(key)) continue;

                Debug.LogError($"NetWire: message key '{key}' has no tag. A key with no tag cannot leave this machine, and the send would be discarded with nothing naming the cause.");
            }
        }
#endif
    }
}