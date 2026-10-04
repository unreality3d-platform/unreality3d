using System;
using UnityEngine;

namespace U3D.Networking
{
    /// <summary>
    /// Everything a build carries to compose its room name: the creator name, the product
    /// name, and the asset GUID of each scene in the build. Written whole by the publish walk
    /// on every publish, so the names and the scenes always come from the same publish and
    /// cannot go stale relative to the build they ship in. Read by
    /// U3DRoomIdentity.TryResolveLocalRoomPath, which U3DNetworkBootstrap.Start calls once;
    /// nothing caches the read and nothing at run time writes it.
    ///
    /// The names travel in the build rather than being parsed from the page address, so the
    /// unreality3d.com URL and the GitHub Pages URL of one experience compose one room.
    /// </summary>
    public class U3DRoomIdentityData : ScriptableObject
    {
        public const string ResourceName = "U3DRoomIdentityData";

        [Serializable]
        public struct Entry
        {
            public string ScenePath;
            public string Guid;
        }

        [SerializeField] private string creator = string.Empty;
        [SerializeField] private string product = string.Empty;
        [SerializeField] private Entry[] entries = new Entry[0];

        public static U3DRoomIdentityData Load()
        {
            return Resources.Load<U3DRoomIdentityData>(ResourceName);
        }

        public bool TryGetNames(out string creatorName, out string productName)
        {
            creatorName = null;
            productName = null;

            if (string.IsNullOrWhiteSpace(creator)) return false;
            if (string.IsNullOrWhiteSpace(product)) return false;

            creatorName = creator;
            productName = product;
            return true;
        }

        public bool TryGetGuid(string scenePath, out string guid)
        {
            guid = null;

            if (string.IsNullOrEmpty(scenePath)) return false;
            if (entries == null) return false;

            for (int i = 0; i < entries.Length; i++)
            {
                if (!string.Equals(entries[i].ScenePath, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entries[i].Guid)) return false;

                guid = entries[i].Guid;
                return true;
            }

            return false;
        }

#if UNITY_EDITOR
        public void SetContents(string creatorName, string productName, Entry[] value)
        {
            creator = creatorName ?? string.Empty;
            product = productName ?? string.Empty;
            entries = value ?? new Entry[0];
        }
#endif
    }
}
