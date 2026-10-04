using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Simple component that draws dialog wireframe when selected
    /// </summary>
    public class U3DDialogPositionGizmo : MonoBehaviour
    {
        private void OnDrawGizmosSelected()
        {
            // Draw the dialog wireframe when this object is selected
            Gizmos.color = Color.green;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, transform.localScale);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(400, 350, 1));
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
}