using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    [RequireComponent(typeof(Collider))]
    public class U3DPortal : MonoBehaviour
    {
        [Header("Portal Configuration")]
        [Tooltip("The destination marker the player is sent to. Each portal has its own destination, so multiple portal pairs in one scene never interfere with each other.")]
        [SerializeField] private U3DPortalDestination destination;

        [Header("Events")]
        [Tooltip("Called when the local player is teleported by this portal. Fires only on the teleporting player's own client.")]
        public UnityEvent OnTeleported;

        // Shared across all portals: after any teleport, no portal fires again for a
        // short window. This lets a destination sit inside another portal's trigger
        // (two-way portals) without instantly bouncing the player back.
        private static float _lastTeleportTime = -999f;
        private const float TeleportGracePeriod = 0.5f;

        private void Awake()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (destination == null) return;

            // Physics objects (including grabbed objects parented to the player) carry a
            // Rigidbody; the player capsule does not. Ignore them so a held object poking
            // into the portal doesn't teleport the player early.
            if (other.attachedRigidbody != null) return;

            U3DPlayerController player = other.GetComponentInParent<U3DPlayerController>();
            if (player == null) return;

            if (Time.time - _lastTeleportTime < TeleportGracePeriod) return;
            _lastTeleportTime = Time.time;

            player.SetPosition(destination.transform.position);
            if (destination.useRotation)
                player.SetRotation(destination.transform.eulerAngles.y);

            OnTeleported?.Invoke();
        }

        public void SetDestination(U3DPortalDestination newDestination)
        {
            destination = newDestination;
        }

        public U3DPortalDestination Destination => destination;

        private void OnDrawGizmos()
        {
            Vector3 origin = transform.position;

            Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.06f);

            Collider col = GetComponent<Collider>();
            if (col is BoxCollider box)
            {
                Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.4f);
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(box.center, box.size);
                Gizmos.matrix = Matrix4x4.identity;
            }

            if (destination != null)
            {
                Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.6f);
                Gizmos.DrawLine(origin, destination.transform.position);
            }
        }
    }
}