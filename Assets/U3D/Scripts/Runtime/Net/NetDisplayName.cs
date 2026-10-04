using System.Text;

namespace U3D.Net
{
    /// <summary>
    /// One player's display name on the wire.
    ///
    /// Variable-length and outside the codec's closed field set, so it carries its own
    /// encoder rather than going through the codec, exactly as NetArrival does for the
    /// same reason. The format is one tag byte, one length byte, then that many bytes of
    /// UTF-8 text. A length of zero is a valid message: it means the sender has no
    /// profile name yet, and the receiver keeps whatever generated name it already shows.
    ///
    /// The length byte caps the payload at 255 bytes. A display name is truncated to 40
    /// characters before it reaches here, and 40 characters of UTF-8 is at most 160 bytes
    /// in the worst case (4 bytes per character), well within that ceiling.
    ///
    /// Bytes are composed by hand, least significant first, rather than through
    /// BitConverter, which reports the machine's own byte order. The same convention
    /// NetArrival and NetAvatar follow, and for the same reason.
    /// </summary>
    public static class NetDisplayName
    {
        /// <summary>Tag byte, length byte.</summary>
        public const int HeaderSize = 2;

        /// <summary>
        /// Writes a display name into the buffer. Returns the total byte count written,
        /// or -1 when the name does not fit.
        /// </summary>
        public static int Encode(string name, byte[] buffer)
        {
            if (buffer == null) return -1;

            int byteCount = 0;
            if (!string.IsNullOrEmpty(name))
                byteCount = Encoding.UTF8.GetByteCount(name);

            if (byteCount > 255) return -1;
            if (HeaderSize + byteCount > buffer.Length) return -1;

            int offset = 0;
            buffer[offset++] = NetWire.TagDisplayName;
            buffer[offset++] = (byte)byteCount;

            if (byteCount > 0)
                Encoding.UTF8.GetBytes(name, 0, name.Length, buffer, offset);

            return HeaderSize + byteCount;
        }

        /// <summary>
        /// Reads a display name from the buffer. Returns false for anything malformed,
        /// with no log — the same policy NetArrival follows per G183.
        /// </summary>
        public static bool TryDecode(byte[] bytes, int length, out string name)
        {
            name = null;
            if (bytes == null || length < HeaderSize) return false;
            if (bytes[0] != NetWire.TagDisplayName) return false;

            int byteCount = bytes[1];
            if (HeaderSize + byteCount > length) return false;

            if (byteCount == 0)
            {
                name = string.Empty;
                return true;
            }

            name = Encoding.UTF8.GetString(bytes, HeaderSize, byteCount);
            return true;
        }
    }
}
