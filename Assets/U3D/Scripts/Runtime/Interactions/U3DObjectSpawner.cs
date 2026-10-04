using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using U3D.Net;

namespace U3D
{
    [System.Serializable]
    public struct SpawnEntry
    {
        [Tooltip("The prefab to spawn.")]
        public NetPrefab prefab;

        [Tooltip("Relative spawn weight. Higher values mean this prefab is chosen more often. A weight of 0 removes it from the pool.")]
        [Min(0f)]
        public float weight;
    }

    /// <summary>
    /// Spawns a prefab at this object's position and rotation, for everyone in the room.
    /// Place this component on any GameObject to define where and what spawns.
    ///
    /// Supports a single prefab or a weighted list of prefabs with random selection.
    /// When a prefab list is populated, a prefab is chosen based on relative weights.
    /// When the list is empty, the single Prefab To Spawn field is used instead.
    /// </summary>
    [RequireComponent(typeof(NetEntity))]
    public class U3DObjectSpawner : NetComponent
    {
        [Header("What to Spawn")]
        [Tooltip("The prefab to spawn at this location. If a Prefab List is populated below, that list is used instead.")]
        public NetPrefab prefabToSpawn;

        [Tooltip("Optional weighted list of prefabs. When populated, a prefab is chosen based on relative weights each time Spawn is called.")]
        public SpawnEntry[] prefabList;

        [Tooltip("When enabled, everyone in the room sees the spawned object. Use the 'Register Prefab(s) for Multiplayer' button at the bottom of this Inspector to set your prefabs up. When disabled, the object is built on each player's own machine and is never shared, which is what you want for local effects like particle bursts.")]
        public bool networkedSpawn = true;

        [Header("Spawn Behavior")]
        [Tooltip("Spawn automatically when the scene starts.")]
        public bool spawnOnStart = true;

        [Tooltip("Respawn automatically when the spawned object is destroyed.")]
        public bool respawnWhenDestroyed = false;

        [Tooltip("Maximum number of spawned objects that can exist at once. New spawns are blocked when this limit is reached. Set to 0 for unlimited.")]
        [Min(0)]
        public int maxInstances = 1;

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this spawner. Edit the text on that object directly.")]
        public U3DWorldspaceUI labelUI;

        [Header("Events")]
        public UnityEvent<GameObject> onSpawned;
        public UnityEvent onSpawnFailed;

        private readonly List<U3DSpawnTracker> _instances = new List<U3DSpawnTracker>();
        private NetMessage _spawnRequest;

        public int ActiveCount => _instances.Count;

        protected override void OnNetSpawn()
        {
            _spawnRequest = RegisterMessage(NetKeys.SpawnerRequest, OnSpawnRequest);
            UpdateLabel();

            if (!spawnOnStart) return;

            if (!networkedSpawn)
                SpawnLocal();
            else if (Session != null && Session.IsReporter)
                PerformSpawn();
        }

        /// <summary>
        /// Creates one object. Safe to call from any player, from a button, a trigger,
        /// or any other UnityEvent.
        /// </summary>
        public void Spawn()
        {
            if (!networkedSpawn)
            {
                SpawnLocal();
                return;
            }

            if (!IsLive || Session == null || _spawnRequest == null)
            {
                onSpawnFailed?.Invoke();
                return;
            }

            if (!CanSpawnMore())
            {
                onSpawnFailed?.Invoke();
                return;
            }

            if (!HasAnyPrefab())
            {
                Debug.LogWarning($"U3DObjectSpawner on '{name}': No prefab assigned.");
                onSpawnFailed?.Invoke();
                return;
            }

            _spawnRequest.SendToReporter();
        }

        private void OnSpawnRequest(PeerId sender, object[] args)
        {
            if (Session == null || !Session.IsReporter) return;
            PerformSpawn();
        }

        private void PerformSpawn()
        {
            if (!CanSpawnMore()) return;

            NetPrefab prefab = ResolvePrefab();
            if (!prefab.IsValid) return;

            NetEntity spawned = Session.SpawnRoomProp(prefab, transform.position, transform.rotation, Entity.Id);

            // An unregistered prefab is an authoring mistake rather than a network failure,
            // so the object is built here instead of not at all. It takes part in nothing —
            // its entity stays dormant on a zero ID — so nobody else sees it and no counter
            // moves. The session has already named the button that fixes it.
            if (spawned == null)
                SpawnLocal();
        }

        private void SpawnLocal()
        {
            if (!CanSpawnMore())
            {
                onSpawnFailed?.Invoke();
                return;
            }

            NetPrefab prefab = ResolvePrefab();
            if (!prefab.IsValid)
            {
                onSpawnFailed?.Invoke();
                return;
            }

            GameObject instance = Instantiate(prefab.Prefab, transform.position, transform.rotation);

            var tracker = instance.GetComponent<U3DSpawnTracker>();
            if (tracker == null) tracker = instance.AddComponent<U3DSpawnTracker>();

            tracker.Attach(this);
        }

        internal void RegisterInstance(U3DSpawnTracker tracker)
        {
            if (tracker == null) return;
            if (_instances.Contains(tracker)) return;

            _instances.Add(tracker);
            UpdateLabel();
            onSpawned?.Invoke(tracker.gameObject);
        }

        internal void OnInstanceDestroyed(U3DSpawnTracker tracker, bool wasLocalOnly)
        {
            if (!_instances.Remove(tracker)) return;

            UpdateLabel();

            if (!respawnWhenDestroyed) return;
            if (wasLocalOnly) return;

            if (!networkedSpawn)
            {
                SpawnLocal();
                return;
            }

            if (Session != null && Session.IsReporter)
                PerformSpawn();
        }

        private bool CanSpawnMore()
        {
            return maxInstances <= 0 || _instances.Count < maxInstances;
        }

        private void UpdateLabel()
        {
            if (labelUI == null) return;
            labelUI.gameObject.SetActive(CanSpawnMore() || respawnWhenDestroyed);
        }

        private bool HasAnyPrefab()
        {
            if (prefabToSpawn.IsValid) return true;

            if (prefabList != null)
            {
                for (int i = 0; i < prefabList.Length; i++)
                {
                    if (prefabList[i].prefab.IsValid && prefabList[i].weight > 0f)
                        return true;
                }
            }

            return false;
        }

        private NetPrefab ResolvePrefab()
        {
            if (prefabList != null && prefabList.Length > 0)
            {
                float totalWeight = 0f;
                for (int i = 0; i < prefabList.Length; i++)
                {
                    if (prefabList[i].prefab.IsValid && prefabList[i].weight > 0f)
                        totalWeight += prefabList[i].weight;
                }

                if (totalWeight > 0f)
                {
                    float roll = Random.Range(0f, totalWeight);
                    float cumulative = 0f;
                    for (int i = 0; i < prefabList.Length; i++)
                    {
                        if (!prefabList[i].prefab.IsValid || prefabList[i].weight <= 0f)
                            continue;
                        cumulative += prefabList[i].weight;
                        if (roll < cumulative)
                            return prefabList[i].prefab;
                    }
                }
            }

            return prefabToSpawn;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0f, 0.8f, 1f, 0.6f);
            DrawDiamond(transform.position, 0.4f);

            Gizmos.color = new Color(0f, 0.8f, 1f, 0.9f);
            Vector3 arrowStart = transform.position + Vector3.up * 0.1f;
            Gizmos.DrawRay(arrowStart, transform.forward * 1.5f);

            Vector3 tip = arrowStart + transform.forward * 1.5f;
            Vector3 arrowLeft = Quaternion.Euler(0, -25, 0) * transform.forward.normalized * 0.4f;
            Vector3 arrowRight = Quaternion.Euler(0, 25, 0) * transform.forward.normalized * 0.4f;
            Gizmos.DrawLine(tip, tip - arrowLeft);
            Gizmos.DrawLine(tip, tip - arrowRight);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            DrawDiamond(transform.position, 0.55f);

            Gizmos.color = new Color(0f, 1f, 1f, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.2f);
        }

        private void DrawDiamond(Vector3 center, float size)
        {
            Vector3 top = center + Vector3.up * size;
            Vector3 bottom = center - Vector3.up * size;
            Vector3 front = center + Vector3.forward * size;
            Vector3 back = center - Vector3.forward * size;
            Vector3 right = center + Vector3.right * size;
            Vector3 left = center - Vector3.right * size;

            Gizmos.DrawLine(top, front); Gizmos.DrawLine(top, back);
            Gizmos.DrawLine(top, right); Gizmos.DrawLine(top, left);
            Gizmos.DrawLine(bottom, front); Gizmos.DrawLine(bottom, back);
            Gizmos.DrawLine(bottom, right); Gizmos.DrawLine(bottom, left);
            Gizmos.DrawLine(front, right); Gizmos.DrawLine(right, back);
            Gizmos.DrawLine(back, left); Gizmos.DrawLine(left, front);
        }
    }
}