using System;
using UnityEngine;

namespace U3D.Net
{
    [Serializable]
    public struct NetPrefab
    {
        [SerializeField] private GameObject prefab;

        public GameObject Prefab => prefab;
        public bool IsValid => prefab != null;

        /// <summary>
        /// Wraps a prefab the table handed back, so a received spawn can be built. Internal
        /// because the seam assembly references nothing per G38, which makes this
        /// unreachable from game code and from creator-facing code: a NetPrefab still
        /// cannot be built from a bare GameObject anywhere outside U3D.Net.
        ///
        /// No AssetDatabase check, and none is lost. Nothing enters the prefab table
        /// without passing U3DObjectIdRegistry.TryRegisterPrefab's identical check, so a
        /// GameObject arriving here has already been tested against the case EditorCreate's
        /// guard exists for.
        /// </summary>
        internal static NetPrefab FromAsset(GameObject prefabAsset)
        {
            var result = new NetPrefab();
            result.prefab = prefabAsset;
            return result;
        }

#if UNITY_EDITOR
        public static NetPrefab EditorCreate(GameObject prefabAsset)
        {
            var result = new NetPrefab();
            if (prefabAsset == null) return result;
            if (!UnityEditor.AssetDatabase.Contains(prefabAsset)) return result;
            result.prefab = prefabAsset;
            return result;
        }
#endif
    }
}