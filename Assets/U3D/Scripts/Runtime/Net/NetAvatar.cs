using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// One player's body, as it travels. The fields are G15's, fixed before anything was
    /// built and not reopened here.
    ///
    /// The sequence number is not in this struct. It belongs to the sending session rather
    /// than to any one packet's contents, and a sender that could be handed a sequence
    /// number is a sender that can be handed the wrong one.
    /// </summary>
    public readonly struct NetAvatarState
    {
        public readonly Vector3 Position;

        /// <summary>Which way the body faces, in degrees. Any value; wrapped on the wire.</summary>
        public readonly float BodyYaw;

        /// <summary>Where the head is aimed, in degrees, negative down. Clamped to ±80 on the wire.</summary>
        public readonly float HeadPitch;

        public readonly ushort Flags;

        /// <summary>The rideable this player is standing on, or 0. No consumer yet.</summary>
        public readonly ushort Rideable;

        /// <summary>The steerable this player is driving, or 0. No consumer yet.</summary>
        public readonly ushort Steerable;

        public NetAvatarState(Vector3 position, float bodyYaw, float headPitch, ushort flags, ushort rideable, ushort steerable)
        {
            Position = position;
            BodyYaw = bodyYaw;
            HeadPitch = headPitch;
            Flags = flags;
            Rideable = rideable;
            Steerable = steerable;
        }
    }

    /// <summary>
    /// One arrival, held in a playback buffer until its moment comes round.
    /// </summary>
    public readonly struct NetAvatarSample
    {
        public readonly ushort Sequence;
        public readonly NetAvatarState State;

        /// <summary>This machine's own clock at the moment it arrived. Never compared with
        /// another machine's, so G48 is untouched.</summary>
        public readonly float ReceivedAt;

        public NetAvatarSample(ushort sequence, NetAvatarState state, float receivedAt)
        {
            Sequence = sequence;
            State = state;
            ReceivedAt = receivedAt;
        }
    }

    /// <summary>
    /// The avatar packet's format. Twenty-four bytes on the unreliable channel, twenty
    /// times a second per player: one tag, then G15's twenty-three.
    ///
    /// Carries its own encoder and decoder rather than going through NetCodec, for two
    /// reasons that are both about the codec rather than about this message. The codec's
    /// field set is closed and holds no signed byte, so head pitch cannot be described in
    /// it, and widening that set is a format decision spent on one field of one message.
    /// And the codec hands a decoded message back as a boxed object array, which is a cost
    /// worth paying on messages that arrive when something happens and not on the one
    /// message that arrives continuously — C12 puts the ceiling on per-connection
    /// processing rather than on bandwidth.
    ///
    /// The tag stays reserved in NetWire so nothing tries to read this as a fixed layout,
    /// and the receiver tests the tag before the codec is consulted. Same arrangement as
    /// arrival state at G225.
    ///
    /// Bytes are composed by hand, least significant first, rather than through
    /// BitConverter.GetBytes, which reports the machine's byte order and would quietly
    /// produce two incompatible formats. Both ends of every packet run this file.
    /// </summary>
    public static class NetAvatar
    {
        /// <summary>Tag plus payload. G15's twenty-three bytes are the payload.</summary>
        public const int PacketSize = 24;

        // G15's flag bits. Bit 10 is grounded per G93. Bits 11 and 12 are pushing and
        // pulling per F23, which are sustained states and repair themselves on the next
        // packet if one is lost. Bits 13 to 15 are reserved, sent as zero and ignored on
        // receipt, which is what lets a flag be added without a version change.
        // Suppress-locomotion is deliberately absent: it is derived from the steerable on
        // the receiver per G108, so no bit is spent on it.
        public const ushort FlagMoving = 1 << 0;
        public const ushort FlagSprinting = 1 << 1;
        public const ushort FlagCrouching = 1 << 2;
        public const ushort FlagFlying = 1 << 3;
        public const ushort FlagJumping = 1 << 4;
        public const ushort FlagSwimming = 1 << 5;
        public const ushort FlagClimbing = 1 << 6;
        public const ushort FlagSeated = 1 << 7;
        public const ushort FlagInVR = 1 << 8;
        public const ushort FlagFirstPerson = 1 << 9;
        public const ushort FlagGrounded = 1 << 10;
        public const ushort FlagPushing = 1 << 11;
        public const ushort FlagPulling = 1 << 12;

        /// <summary>Head pitch's range, which is also what one signed byte can carry.</summary>
        public const float HeadPitchLimit = 80f;

        /// <summary>
        /// Writes one packet, tag included. Returns the length, or 0 if the buffer is too
        /// small — which cannot happen with the session's own buffer and is checked rather
        /// than assumed.
        /// </summary>
        public static int Encode(ushort sequence, in NetAvatarState state, byte[] buffer)
        {
            if (buffer == null || buffer.Length < PacketSize) return 0;

            int at = 0;

            buffer[at++] = NetWire.TagAvatar;

            WriteUShort(buffer, ref at, sequence);

            WriteFloat(buffer, ref at, state.Position.x);
            WriteFloat(buffer, ref at, state.Position.y);
            WriteFloat(buffer, ref at, state.Position.z);

            WriteUShort(buffer, ref at, EncodeYaw(state.BodyYaw));

            buffer[at++] = (byte)EncodePitch(state.HeadPitch);

            WriteUShort(buffer, ref at, state.Flags);
            WriteUShort(buffer, ref at, state.Rideable);
            WriteUShort(buffer, ref at, state.Steerable);

            return at;
        }

        /// <summary>
        /// Reads one packet. Anything of the wrong length or the wrong tag is refused
        /// silently: a build ahead of this one, or a corrupted delivery, and neither is
        /// helped by saying so on every arrival.
        /// </summary>
        public static bool TryDecode(byte[] bytes, int length, out ushort sequence, out NetAvatarState state)
        {
            sequence = 0;
            state = default;

            if (bytes == null || length != PacketSize) return false;
            if (bytes[0] != NetWire.TagAvatar) return false;

            int at = 1;

            sequence = ReadUShort(bytes, ref at);

            float x = ReadFloat(bytes, ref at);
            float y = ReadFloat(bytes, ref at);
            float z = ReadFloat(bytes, ref at);

            float yaw = DecodeYaw(ReadUShort(bytes, ref at));
            float pitch = (sbyte)bytes[at++];

            ushort flags = ReadUShort(bytes, ref at);
            ushort rideable = ReadUShort(bytes, ref at);
            ushort steerable = ReadUShort(bytes, ref at);

            state = new NetAvatarState(new Vector3(x, y, z), yaw, pitch, flags, rideable, steerable);
            return true;
        }

        /// <summary>
        /// Whether an arriving packet is later than the one already held. The counter wraps
        /// at 65535, so this is a comparison of the gap rather than of the two values: a
        /// difference in the lower half of the range is forward, anything else is a packet
        /// that took a longer road and is discarded per G18.
        ///
        /// Equal is not newer, so a duplicate delivery changes nothing.
        /// </summary>
        public static bool IsNewer(ushort candidate, ushort current)
        {
            return (ushort)(candidate - current) is > 0 and < 32768;
        }

        // ===== Fields =====

        private static ushort EncodeYaw(float degrees)
        {
            float wrapped = Mathf.Repeat(degrees, 360f);
            int units = Mathf.RoundToInt(wrapped * (65536f / 360f));
            return (ushort)(units & 0xFFFF);
        }

        private static float DecodeYaw(ushort units)
        {
            return units * (360f / 65536f);
        }

        private static sbyte EncodePitch(float degrees)
        {
            float clamped = Mathf.Clamp(degrees, -HeadPitchLimit, HeadPitchLimit);
            return (sbyte)Mathf.RoundToInt(clamped);
        }

        private static void WriteUShort(byte[] buffer, ref int at, ushort value)
        {
            buffer[at++] = (byte)(value & 0xFF);
            buffer[at++] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUShort(byte[] bytes, ref int at)
        {
            ushort value = (ushort)(bytes[at] | (bytes[at + 1] << 8));
            at += 2;
            return value;
        }

        private static void WriteFloat(byte[] buffer, ref int at, float value)
        {
            int bits = System.BitConverter.SingleToInt32Bits(value);
            buffer[at++] = (byte)(bits & 0xFF);
            buffer[at++] = (byte)((bits >> 8) & 0xFF);
            buffer[at++] = (byte)((bits >> 16) & 0xFF);
            buffer[at++] = (byte)((bits >> 24) & 0xFF);
        }

        private static float ReadFloat(byte[] bytes, ref int at)
        {
            int bits = bytes[at]
                | (bytes[at + 1] << 8)
                | (bytes[at + 2] << 16)
                | (bytes[at + 3] << 24);
            at += 4;
            return System.BitConverter.Int32BitsToSingle(bits);
        }
    }
}