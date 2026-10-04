using System.Collections.Generic;
using UnityEngine;

namespace U3D.Net
{
    /// <summary>
    /// Project-local counter for authored object IDs and the build-time prefab table.
    /// Must live outside every entry in CORE_UPDATE_PATHS: a template update that
    /// overwrote this would reset the counter and reissue numbers already held by
    /// published objects, and would repoint every spawn index at the wrong prefab.
    /// Must live inside a Resources folder so the table survives into a build.
    /// </summary>
    public class U3DObjectIdRegistry : ScriptableObject
    {
        public const string ResourceName = "U3DObjectIdRegistry";

        [SerializeField] private ushort _nextAuthoredId = NetEntity.AuthoredIdMin;
        [SerializeField] private List<GameObject> _prefabTable = new List<GameObject> { null };

        [System.NonSerialized] private Dictionary<GameObject, ushort> _indexByPrefab;

        private static U3DObjectIdRegistry _runtime;

        public static U3DObjectIdRegistry Runtime
        {
            get
            {
                if (_runtime == null)
                {
                    _runtime = Resources.Load<U3DObjectIdRegistry>(ResourceName);
                }

                return _runtime;
            }
        }

        public ushort NextAuthoredId => _nextAuthoredId;

        public int PrefabTableCount => _prefabTable != null ? _prefabTable.Count : 0;

        private void OnEnable()
        {
            _indexByPrefab = null;
        }

        public bool TryGetPrefab(ushort index, out GameObject prefab)
        {
            prefab = null;

            if (index == 0) return false;
            if (_prefabTable == null) return false;
            if (index >= _prefabTable.Count) return false;

            prefab = _prefabTable[index];
            return prefab != null;
        }

        public bool TryGetPrefabIndex(GameObject prefab, out ushort index)
        {
            index = 0;

            if (prefab == null) return false;

            EnsureLookup();
            return _indexByPrefab.TryGetValue(prefab, out index);
        }

        private void EnsureLookup()
        {
            if (_indexByPrefab != null) return;

            _indexByPrefab = new Dictionary<GameObject, ushort>();

            if (_prefabTable == null) return;

            for (int i = 1; i < _prefabTable.Count; i++)
            {
                GameObject entry = _prefabTable[i];
                if (entry == null) continue;
                if (_indexByPrefab.ContainsKey(entry)) continue;

                _indexByPrefab.Add(entry, (ushort)i);
            }
        }

#if UNITY_EDITOR
        internal bool TryTakeNextAuthoredId(out ushort id)
        {
            if (_nextAuthoredId < NetEntity.AuthoredIdMin || _nextAuthoredId > NetEntity.AuthoredIdMax)
            {
                id = 0;
                return false;
            }

            id = _nextAuthoredId;
            _nextAuthoredId = (ushort)(_nextAuthoredId + 1);
            UnityEditor.EditorUtility.SetDirty(this);
            return true;
        }

        public void RaiseFloorAbove(ushort highestSeen)
        {
            if (highestSeen == 0) return;
            if (highestSeen > NetEntity.AuthoredIdMax) return;

            ushort floor = (ushort)(highestSeen + 1);
            if (floor <= _nextAuthoredId) return;

            _nextAuthoredId = floor;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        public bool TryRegisterPrefab(GameObject prefabAsset, out ushort index)
        {
            index = 0;

            if (prefabAsset == null) return false;
            if (!UnityEditor.AssetDatabase.Contains(prefabAsset)) return false;

            if (_prefabTable == null)
            {
                _prefabTable = new List<GameObject> { null };
            }

            if (_prefabTable.Count == 0)
            {
                _prefabTable.Add(null);
            }

            for (int i = 1; i < _prefabTable.Count; i++)
            {
                if (_prefabTable[i] != prefabAsset) continue;

                index = (ushort)i;
                return true;
            }

            if (_prefabTable.Count > ushort.MaxValue) return false;

            _prefabTable.Add(prefabAsset);
            index = (ushort)(_prefabTable.Count - 1);
            _indexByPrefab = null;
            UnityEditor.EditorUtility.SetDirty(this);
            return true;
        }

        public bool IsPrefabRegistered(GameObject prefabAsset)
        {
            return TryGetPrefabIndex(prefabAsset, out _);
        }
#endif
    }
}