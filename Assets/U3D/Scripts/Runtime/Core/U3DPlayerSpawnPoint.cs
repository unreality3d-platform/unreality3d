using UnityEngine;

namespace U3D.Networking
{
    public class U3DPlayerSpawnPoint : MonoBehaviour
    {
        [Header("Spawn Settings")]
        [Tooltip("Use this spawn point's Y rotation for player facing direction")]
        public bool useRotation = true;

        [Header("Editor Visualization")]
        [Tooltip("Show direction arrow and spawn area in Scene view")]
        public bool showGizmos = true;

        [Tooltip("Color of the spawn point gizmos")]
        public Color gizmoColor = Color.green;

        public Vector3 GetSpawnPosition()
        {
            return transform.position;
        }

        public Quaternion GetSpawnRotation()
        {
            if (useRotation)
                return Quaternion.Euler(0, transform.eulerAngles.y, 0);

            return Quaternion.identity;
        }

        public (Vector3 position, Quaternion rotation) GetSpawnData()
        {
            return (GetSpawnPosition(), GetSpawnRotation());
        }

        void OnDrawGizmos()
        {
            if (!showGizmos) return;

            Gizmos.color = gizmoColor;
            Gizmos.DrawWireSphere(transform.position, 0.5f);

            if (useRotation)
            {
                Gizmos.color = Color.blue;
                Vector3 forward = transform.forward * 2f;
                Vector3 arrowStart = transform.position + Vector3.up * 0.1f;

                Gizmos.DrawRay(arrowStart, forward);

                Vector3 arrowTip = arrowStart + forward;
                Vector3 arrowLeft = Quaternion.Euler(0, -25, 0) * forward.normalized * 0.5f;
                Vector3 arrowRight = Quaternion.Euler(0, 25, 0) * forward.normalized * 0.5f;

                Gizmos.DrawLine(arrowTip, arrowTip - arrowLeft);
                Gizmos.DrawLine(arrowTip, arrowTip - arrowRight);

                Gizmos.color = Color.yellow;
                Gizmos.DrawWireCube(transform.position + Vector3.up * 0.5f, Vector3.one * 0.2f);
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 0.7f);

            if (useRotation)
            {
                Gizmos.color = Color.cyan;
                Vector3 forward = transform.forward * 3f;
                Vector3 arrowStart = transform.position + Vector3.up * 0.1f;

                Gizmos.DrawRay(arrowStart, forward);
                Gizmos.DrawRay(arrowStart + Vector3.right * 0.05f, forward);
                Gizmos.DrawRay(arrowStart - Vector3.right * 0.05f, forward);
            }
        }
    }
}