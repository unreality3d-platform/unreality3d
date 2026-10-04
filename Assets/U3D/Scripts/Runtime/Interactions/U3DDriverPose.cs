using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Position marker that lives inside a steerable's visual (costume) prefab as a child.
    /// It marks WHERE the driver is placed relative to the costume. The pose itself
    /// (seated vs standing vs hidden) and visibility are set by the steerable's Avatar
    /// Mode, not here — this component only carries position and entry/exit events.
    ///
    /// U3DSteerable finds it in the instantiated costume on entry and anchors the driver
    /// avatar to this point each frame. Which point lands on the marker depends on the
    /// steerable's Avatar Mode: when Seated, the hips rest on the marker (like a chair);
    /// when Standing, the avatar's base (the feet/floor plane) rests on the marker, so you
    /// position the marker at the surface the driver stands on.
    ///
    /// This marker carries no identity of its own and needs none. The costume is built at
    /// runtime from a prefab every player already has, so each player rebuilds the same
    /// marker in the same place. What travels between players is who is driving and how
    /// they are posed, which belongs to the driver rather than to this marker.
    ///
    /// Position this object inside the costume prefab to set where the driver goes. The green
    /// arrow gizmo shows the authored forward; at runtime the driver faces the steering
    /// direction, so author the costume's forward to match.
    ///
    /// Added automatically by the Creator Dashboard "Add Seat" tool when the selected object
    /// is a Steerable.
    /// </summary>
    public class U3DDriverPose : MonoBehaviour
    {
        [Header("Events")]
        [Tooltip("Fired on the local client when the driver enters the steerable.")]
        public UnityEvent OnDriverEnter;

        [Tooltip("Fired on the local client when the driver leaves the steerable.")]
        public UnityEvent OnDriverExit;

        private void OnDrawGizmos()
        {
            // Marks the driver's anchor point (hips when Seated, feet/base when Standing) in
            // green, so a driver seat reads differently from a standalone U3DSeat in the scene.
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;

            Gizmos.color = new Color(0.4f, 0.9f, 0.4f, 0.9f);
            Gizmos.DrawSphere(origin, 0.06f);

            Gizmos.color = new Color(0.4f, 0.9f, 0.4f, 0.8f);
            Gizmos.DrawLine(origin, origin + forward * 0.5f);

            Vector3 tip = origin + forward * 0.5f;
            Vector3 right = transform.right;
            Gizmos.DrawLine(tip, tip - forward * 0.15f + right * 0.1f);
            Gizmos.DrawLine(tip, tip - forward * 0.15f - right * 0.1f);

            Gizmos.color = new Color(0.4f, 0.9f, 0.4f, 0.5f);
            Gizmos.DrawSphere(tip, 0.03f);
        }
    }
}