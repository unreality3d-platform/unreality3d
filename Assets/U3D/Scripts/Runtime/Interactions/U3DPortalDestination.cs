using UnityEngine;

namespace U3D
{
    public class U3DPortalDestination : MonoBehaviour
    {
        [Header("Destination Settings")]
        [Tooltip("When enabled, the player lands facing this marker's forward direction. When disabled, the player keeps their current facing.")]
        public bool useRotation = true;

        private void OnDrawGizmos()
        {
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;

            Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.06f);

            if (useRotation)
            {
                Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.8f);
                Gizmos.DrawLine(origin, origin + forward * 0.5f);

                Vector3 tip = origin + forward * 0.5f;
                Vector3 right = transform.right;
                Gizmos.DrawLine(tip, tip - forward * 0.15f + right * 0.1f);
                Gizmos.DrawLine(tip, tip - forward * 0.15f - right * 0.1f);

                Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.5f);
                Gizmos.DrawSphere(tip, 0.03f);
            }
        }
    }
}