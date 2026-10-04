using UnityEngine;
using System.Collections.Generic;

namespace U3D.Networking
{
    public class U3DPlayerSpawner : MonoBehaviour
    {
        [Header("Spawn Behavior")]
        [Tooltip("Use random spawn points instead of cycling through them")]
        [SerializeField] private bool useRandomSpawning = false;

        private List<U3DPlayerSpawnPoint> enhancedSpawnPoints = new List<U3DPlayerSpawnPoint>();
        private List<Transform> simpleSpawnPoints = new List<Transform>();
        private int lastUsedIndex = -1;

        public static U3DPlayerSpawner Instance { get; private set; }

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            FindSpawnPoints();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void FindSpawnPoints()
        {
            enhancedSpawnPoints.Clear();
            simpleSpawnPoints.Clear();

            var taggedSpawnPoints = GameObject.FindGameObjectsWithTag("PlayerSpawnPoint");

            foreach (var spawnPoint in taggedSpawnPoints)
            {
                var enhancedComponent = spawnPoint.GetComponent<U3DPlayerSpawnPoint>();
                if (enhancedComponent != null)
                    enhancedSpawnPoints.Add(enhancedComponent);
                else
                    simpleSpawnPoints.Add(spawnPoint.transform);
            }
        }

        public (Vector3 position, Quaternion rotation) GetSpawnData()
        {
            int totalSpawnPoints = enhancedSpawnPoints.Count + simpleSpawnPoints.Count;

            if (totalSpawnPoints == 0)
                return (transform.position, Quaternion.Euler(0, transform.eulerAngles.y, 0));

            int spawnIndex;
            if (useRandomSpawning)
            {
                spawnIndex = Random.Range(0, totalSpawnPoints);
            }
            else
            {
                lastUsedIndex = (lastUsedIndex + 1) % totalSpawnPoints;
                spawnIndex = lastUsedIndex;
            }

            if (spawnIndex < enhancedSpawnPoints.Count)
                return enhancedSpawnPoints[spawnIndex].GetSpawnData();

            int simpleIndex = spawnIndex - enhancedSpawnPoints.Count;
            Transform simplePoint = simpleSpawnPoints[simpleIndex];
            return (simplePoint.position, Quaternion.Euler(0, simplePoint.eulerAngles.y, 0));
        }

        public void RefreshSpawnPoints()
        {
            FindSpawnPoints();
        }

        public int GetSpawnPointCount()
        {
            return enhancedSpawnPoints.Count + simpleSpawnPoints.Count;
        }
    }
}