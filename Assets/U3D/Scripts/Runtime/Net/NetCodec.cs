using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// Turns a message into bytes and back. Sits underneath Send and above the channel, so
    /// no component changes: the argument array a component already passes is what gets
    /// encoded, and the far side rebuilds an identical array before the handler runs.
    ///
    /// The decoded types are fixed by what the receiving handlers test for. A handler that
    /// tests `args[0] is Vector3` returns silently when handed anything else, so a wrong
    /// output type here is a message that arrives, routes, and does nothing, with nothing
    /// naming the cause.
    ///
    /// Every read is bounds-checked against the buffer it was handed. A short or malformed
    /// message is discarded exactly as an unrecognised tag is: it is a message this build
    /// cannot make sense of, and both causes deserve the same silence.
    /// </summary>
    public static class NetCodec
    {
        // Smallest-three quaternion packing. The largest of the four components is dropped
        // and rebuilt on the far side from the unit-length constraint; the other three are
        // bounded by 1/sqrt(2) for any unit quaternion, which is the whole range they can
        // ever occupy, so this needs no constant chosen against the world. Ten bits each
        // gives a step of about 0.0014, which is roughly a sixth of a degree.
        private const float RotationComponentRange = 0.70710678f;
        private const int RotationComponentBits = 10;
        private const int RotationComponentMax = (1 << RotationComponentBits) - 1;

        /// <summary>
        /// Writes tag, object ID where the message carries one, and the declared payload.
        /// Returns the number of bytes written, or 0 when the message cannot be encoded —
        /// a reserved tag, a buffer too small, or an argument whose type does not match the
        /// declared field.
        /// </summary>
        public static int Encode(byte tag, ushort objectId, object[] args, byte[] buffer)
        {
            if (buffer == null) return 0;
            if (!NetWire.TryGetDescriptor(tag, out NetWire.Descriptor descriptor)) return 0;

            NetField[] fields = descriptor.Fields;
            int argCount = args != null ? args.Length : 0;
            if (argCount < fields.Length)
            {
                Debug.LogError($"NetCodec: tag {tag} declares {fields.Length} fields and was given {argCount} arguments. Not sent.");
                return 0;
            }

            int required = 1 + (descriptor.CarriesId ? 2 : 0);
            for (int i = 0; i < fields.Length; i++)
                required += NetWire.SizeOf(fields[i]);

            if (buffer.Length < required) return 0;

            int offset = 0;
            buffer[offset++] = tag;

            if (descriptor.CarriesId)
                WriteUShort(buffer, ref offset, objectId);

            for (int i = 0; i < fields.Length; i++)
            {
                if (!WriteField(buffer, ref offset, fields[i], args[i]))
                {
                    Debug.LogError($"NetCodec: tag {tag} field {i} expects {fields[i]} and was given {(args[i] == null ? "null" : args[i].GetType().Name)}. Not sent.");
                    return 0;
                }
            }

            return offset;
        }

        /// <summary>
        /// Reads a message. Returns false for anything this build cannot make sense of,
        /// which is discarded silently by the caller.
        ///
        /// The returned argument array is freshly allocated rather than pooled, because the
        /// handlers it reaches may hold onto a value past the call — a pending hold pose
        /// keeps its position and rotation across frames.
        /// </summary>
        public static bool Decode(byte[] buffer, int length, out byte tag, out ushort objectId, out object[] args)
        {
            tag = 0;
            objectId = 0;
            args = null;

            if (buffer == null || length < 1) return false;

            tag = buffer[0];
            if (!NetWire.TryGetDescriptor(tag, out NetWire.Descriptor descriptor)) return false;

            int offset = 1;

            if (descriptor.CarriesId)
            {
                if (!ReadUShort(buffer, length, ref offset, out objectId)) return false;
            }

            NetField[] fields = descriptor.Fields;
            var values = new object[fields.Length];

            for (int i = 0; i < fields.Length; i++)
            {
                if (!ReadField(buffer, length, ref offset, fields[i], out values[i])) return false;
            }

            args = values;
            return true;
        }

        /// <summary>
        /// Bytes a message needs, so a caller can size a buffer without encoding twice.
        /// Zero for a reserved tag.
        /// </summary>
        public static int SizeOf(byte tag)
        {
            if (!NetWire.TryGetDescriptor(tag, out NetWire.Descriptor descriptor)) return 0;

            int size = 1 + (descriptor.CarriesId ? 2 : 0);
            for (int i = 0; i < descriptor.Fields.Length; i++)
                size += NetWire.SizeOf(descriptor.Fields[i]);

            return size;
        }

        // Fields

        private static bool WriteField(byte[] buffer, ref int offset, NetField field, object value)
        {
            switch (field)
            {
                case NetField.Vector3:
                    if (!(value is Vector3 v)) return false;
                    WriteFloat(buffer, ref offset, v.x);
                    WriteFloat(buffer, ref offset, v.y);
                    WriteFloat(buffer, ref offset, v.z);
                    return true;

                case NetField.Quaternion:
                    if (!(value is Quaternion q)) return false;
                    WriteFloat(buffer, ref offset, q.x);
                    WriteFloat(buffer, ref offset, q.y);
                    WriteFloat(buffer, ref offset, q.z);
                    WriteFloat(buffer, ref offset, q.w);
                    return true;

                case NetField.Rotation:
                    if (!(value is Quaternion rot)) return false;
                    WriteUInt(buffer, ref offset, PackRotation(rot));
                    return true;

                case NetField.PeerId:
                    if (!(value is PeerId peer)) return false;
                    WriteUShort(buffer, ref offset, peer.Raw);
                    return true;

                case NetField.UShort:
                    if (!(value is ushort raw)) return false;
                    WriteUShort(buffer, ref offset, raw);
                    return true;

                default:
                    return false;
            }
        }

        private static bool ReadField(byte[] buffer, int length, ref int offset, NetField field, out object value)
        {
            value = null;

            switch (field)
            {
                case NetField.Vector3:
                    {
                        if (!ReadFloat(buffer, length, ref offset, out float x)) return false;
                        if (!ReadFloat(buffer, length, ref offset, out float y)) return false;
                        if (!ReadFloat(buffer, length, ref offset, out float z)) return false;
                        value = new Vector3(x, y, z);
                        return true;
                    }

                case NetField.Quaternion:
                    {
                        if (!ReadFloat(buffer, length, ref offset, out float x)) return false;
                        if (!ReadFloat(buffer, length, ref offset, out float y)) return false;
                        if (!ReadFloat(buffer, length, ref offset, out float z)) return false;
                        if (!ReadFloat(buffer, length, ref offset, out float w)) return false;
                        value = new Quaternion(x, y, z, w);
                        return true;
                    }

                case NetField.Rotation:
                    {
                        if (!ReadUInt(buffer, length, ref offset, out uint packed)) return false;
                        value = UnpackRotation(packed);
                        return true;
                    }

                case NetField.PeerId:
                    {
                        if (!ReadUShort(buffer, length, ref offset, out ushort raw)) return false;
                        value = new PeerId(raw);
                        return true;
                    }

                case NetField.UShort:
                    {
                        if (!ReadUShort(buffer, length, ref offset, out ushort raw)) return false;
                        value = raw;
                        return true;
                    }

                default:
                    return false;
            }
        }

        // Rotation packing. Two bits name which component was dropped, then ten bits each
        // for the three that travel, most significant first.

        private static uint PackRotation(Quaternion rotation)
        {
            float x = rotation.x;
            float y = rotation.y;
            float z = rotation.z;
            float w = rotation.w;

            float magnitude = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (magnitude < 0.000001f)
            {
                x = 0f; y = 0f; z = 0f; w = 1f;
            }
            else
            {
                float inverse = 1f / magnitude;
                x *= inverse; y *= inverse; z *= inverse; w *= inverse;
            }

            int largest = 0;
            float largestMagnitude = Mathf.Abs(x);
            if (Mathf.Abs(y) > largestMagnitude) { largest = 1; largestMagnitude = Mathf.Abs(y); }
            if (Mathf.Abs(z) > largestMagnitude) { largest = 2; largestMagnitude = Mathf.Abs(z); }
            if (Mathf.Abs(w) > largestMagnitude) { largest = 3; }

            // A quaternion and its negation are the same rotation. Negating so the dropped
            // component is positive is what lets the far side rebuild it as a square root,
            // which has no sign of its own.
            float sign;
            switch (largest)
            {
                case 0: sign = x < 0f ? -1f : 1f; break;
                case 1: sign = y < 0f ? -1f : 1f; break;
                case 2: sign = z < 0f ? -1f : 1f; break;
                default: sign = w < 0f ? -1f : 1f; break;
            }

            float a, b, c;
            switch (largest)
            {
                case 0: a = y * sign; b = z * sign; c = w * sign; break;
                case 1: a = x * sign; b = z * sign; c = w * sign; break;
                case 2: a = x * sign; b = y * sign; c = w * sign; break;
                default: a = x * sign; b = y * sign; c = z * sign; break;
            }

            uint packed = (uint)largest << 30;
            packed |= (uint)QuantizeRotationComponent(a) << 20;
            packed |= (uint)QuantizeRotationComponent(b) << 10;
            packed |= (uint)QuantizeRotationComponent(c);
            return packed;
        }

        private static Quaternion UnpackRotation(uint packed)
        {
            int largest = (int)(packed >> 30);

            float a = DequantizeRotationComponent((packed >> 20) & 0x3FF);
            float b = DequantizeRotationComponent((packed >> 10) & 0x3FF);
            float c = DequantizeRotationComponent(packed & 0x3FF);

            float remainder = 1f - (a * a + b * b + c * c);
            float d = remainder <= 0f ? 0f : Mathf.Sqrt(remainder);

            switch (largest)
            {
                case 0: return new Quaternion(d, a, b, c);
                case 1: return new Quaternion(a, d, b, c);
                case 2: return new Quaternion(a, b, d, c);
                default: return new Quaternion(a, b, c, d);
            }
        }

        private static int QuantizeRotationComponent(float value)
        {
            float clamped = Mathf.Clamp(value, -RotationComponentRange, RotationComponentRange);
            float normalized = (clamped / RotationComponentRange + 1f) * 0.5f;

            int quantized = Mathf.RoundToInt(normalized * RotationComponentMax);
            if (quantized < 0) return 0;
            if (quantized > RotationComponentMax) return RotationComponentMax;
            return quantized;
        }

        private static float DequantizeRotationComponent(uint raw)
        {
            return ((raw / (float)RotationComponentMax) * 2f - 1f) * RotationComponentRange;
        }

        // Primitives. Little-endian explicitly rather than by BitConverter, which reports
        // the machine's order and would silently produce two incompatible formats if a
        // build ever ran on a big-endian machine.

        public static void WriteUShort(byte[] buffer, ref int offset, ushort value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
        }

        public static bool ReadUShort(byte[] buffer, int length, ref int offset, out ushort value)
        {
            value = 0;
            if (offset + 2 > length) return false;

            value = (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
            offset += 2;
            return true;
        }

        public static void WriteUInt(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
        }

        public static bool ReadUInt(byte[] buffer, int length, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > length) return false;

            value = (uint)(buffer[offset]
                         | (buffer[offset + 1] << 8)
                         | (buffer[offset + 2] << 16)
                         | (buffer[offset + 3] << 24));
            offset += 4;
            return true;
        }

        public static void WriteFloat(byte[] buffer, ref int offset, float value)
        {
            uint bits = unchecked((uint)System.BitConverter.SingleToInt32Bits(value));

            buffer[offset++] = (byte)(bits & 0xFF);
            buffer[offset++] = (byte)((bits >> 8) & 0xFF);
            buffer[offset++] = (byte)((bits >> 16) & 0xFF);
            buffer[offset++] = (byte)((bits >> 24) & 0xFF);
        }

        public static bool ReadFloat(byte[] buffer, int length, ref int offset, out float value)
        {
            value = 0f;
            if (offset + 4 > length) return false;

            uint bits = (uint)(buffer[offset]
                             | (buffer[offset + 1] << 8)
                             | (buffer[offset + 2] << 16)
                             | (buffer[offset + 3] << 24));
            offset += 4;

            value = System.BitConverter.Int32BitsToSingle(unchecked((int)bits));
            return true;
        }
    }
}