using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace U3D.Networking
{
    /// <summary>
    /// The only path to a room name. Builds rooms/v1/{creator}/{product}-{sceneGuid} so that
    /// two links to the same experience always produce the identical string. Nothing else
    /// assembles a room name from parts.
    ///
    /// All three parts come from U3DRoomIdentityData, which the publish walk writes into the
    /// build, so the name does not depend on which address served the page. Two scenes of one
    /// product are two rooms. The active scene is read here rather than cached anywhere: a
    /// static survives a scene load in a build, so a cached value would give a scene loaded at
    /// run time the previous scene's identifier.
    ///
    /// Only a WebGL build resolves a room. The editor never runs multiplayer, so Play mode
    /// always runs alone.
    /// </summary>
    public static class U3DRoomIdentity
    {
        public const string RootSegment = "rooms";
        public const string ProtocolVersion = "v1";

        private const int MaxSegmentBytes = 768;
        private const int GuidLength = 32;
        private const int GuidSuffixBytes = GuidLength + 1;

        public static bool TryResolveLocalRoomPath(out string roomPath)
        {
            roomPath = null;

#if UNITY_WEBGL && !UNITY_EDITOR
            U3DRoomIdentityData data = U3DRoomIdentityData.Load();

            if (data == null)
            {
                Debug.LogError("This build carries no room identity, so multiplayer is running " +
                               "single-player. Publish the project again from the Creator Dashboard.");
                return false;
            }

            if (!data.TryGetNames(out string creator, out string product))
            {
                Debug.LogError("This build carries no creator or product name, so multiplayer is running " +
                               "single-player. Publish the project again from the Creator Dashboard.");
                return false;
            }

            string scenePath = SceneManager.GetActiveScene().path;

            if (!data.TryGetGuid(scenePath, out string sceneGuid))
            {
                Debug.LogError("This build has no scene identifier for \"" + scenePath + "\", so multiplayer " +
                               "is running single-player. Publish the project again from the Creator Dashboard.");
                return false;
            }

            if (!TryBuildRoomPath(creator, product, sceneGuid, out roomPath))
            {
                Debug.LogError("This build's creator and product names cannot form a room name, so multiplayer " +
                               "is running single-player. Publish the project again from the Creator Dashboard.");
                return false;
            }

            return true;
#else
            return false;
#endif
        }

        public static bool TryBuildRoomPath(string creatorName, string productName, string sceneGuid, out string roomPath)
        {
            roomPath = null;

            if (!TryNormalizeGuid(sceneGuid, out string guid)) return false;
            if (!TryNormalizeSegment(creatorName, MaxSegmentBytes, out string creator)) return false;
            if (!TryNormalizeSegment(productName, MaxSegmentBytes - GuidSuffixBytes, out string product)) return false;

            roomPath = string.Concat(RootSegment, "/", ProtocolVersion, "/", creator, "/", product, "-", guid);
            return true;
        }

        private static bool TryNormalizeGuid(string raw, out string guid)
        {
            guid = null;
            if (string.IsNullOrEmpty(raw)) return false;
            if (raw.Length != GuidLength) return false;

            string lowered = raw.ToLowerInvariant();

            for (int i = 0; i < lowered.Length; i++)
            {
                char c = lowered[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex) return false;
            }

            guid = lowered;
            return true;
        }

        private static bool TryNormalizeSegment(string raw, int maxBytes, out string segment)
        {
            segment = null;
            if (string.IsNullOrEmpty(raw)) return false;

            string trimmed = raw.Trim();
            if (trimmed.Length == 0) return false;

            byte[] utf8 = Encoding.UTF8.GetBytes(trimmed.ToLowerInvariant());
            var builder = new StringBuilder(utf8.Length);

            for (int i = 0; i < utf8.Length; i++)
            {
                byte b = utf8[i];
                if (IsAllowed(b))
                {
                    builder.Append((char)b);
                }
                else
                {
                    builder.Append('~');
                    builder.Append(HexDigit(b >> 4));
                    builder.Append(HexDigit(b & 0x0F));
                }
            }

            if (builder.Length == 0 || builder.Length > maxBytes) return false;

            segment = builder.ToString();
            return true;
        }

        private static bool IsAllowed(byte b)
            => (b >= (byte)'a' && b <= (byte)'z')
            || (b >= (byte)'0' && b <= (byte)'9')
            || b == (byte)'-'
            || b == (byte)'_';

        private static char HexDigit(int value)
            => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
    }
}
