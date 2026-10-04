using UnityEngine;
using U3D.Net;

namespace U3D
{
    /// <summary>
    /// Rides on every object a U3DObjectSpawner creates. Tells the spawner when the
    /// object arrives and when it goes, on every peer, so each machine keeps its own
    /// count without anything being transmitted.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetEntity))]
    public class U3DSpawnTracker : NetComponent
    {
        private U3DObjectSpawner _spawner;
        private ushort _id;
        private bool _despawned;

        private static bool _quitting;
        private static bool _warnedAboutLocalDestroy;

        protected override void OnNetSpawn()
        {
            _id = Entity.Id;
            _despawned = false;

            ushort origin = Entity.Origin;
            if (origin == 0) return;
            if (Session == null) return;
            if (!Session.TryGetEntity(origin, out NetEntity originEntity) || originEntity == null) return;

            Attach(originEntity.GetComponent<U3DObjectSpawner>());
        }

        protected override void OnNetDespawn()
        {
            _despawned = true;
        }

        internal void Attach(U3DObjectSpawner spawner)
        {
            if (spawner == null) return;
            if (_spawner == spawner) return;

            _spawner = spawner;
            _spawner.RegisterInstance(this);
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
        }

        private void OnDestroy()
        {
            if (_quitting) return;

            bool wasLocalOnly = _id != 0 && !_despawned;

            if (wasLocalOnly && !_warnedAboutLocalDestroy)
            {
                _warnedAboutLocalDestroy = true;
                Debug.LogWarning($"'{name}' was destroyed with Destroy(), so it disappeared on this screen only and will not be replaced. Add a U3D Destroyable component and call Request Destroy on it instead to remove it for everyone.");
            }

            if (_spawner != null)
                _spawner.OnInstanceDestroyed(this, wasLocalOnly);
        }
    }
}