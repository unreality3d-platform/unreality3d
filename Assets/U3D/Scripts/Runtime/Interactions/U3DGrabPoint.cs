using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Optional marker that lives inside a grabbable object as a child, marking WHERE the
    /// player's hand grips the object and HOW the object faces while held. Position it against
    /// the object you can see — for a mug, on the handle; for a sword, on the hilt. At grab
    /// time U3DGrabbable slides and turns the whole object so this marker lands on the hold
    /// point — a short distance out from the wrist along the forearm line — with the marker's
    /// forward pointing away from the player and its up pointing up, judged against the
    /// avatar's neutral stance so the result is identical no matter what pose the player is in
    /// at the moment of pickup. From that moment the object rides the hand bone, so hand
    /// animation carries it naturally.
    ///
    /// The green arrow gizmo shows the authored forward — the direction that points away from
    /// the player when held. The short light line shows up.
    ///
    /// Without this marker, U3DGrabbable falls back to its Grab Offset field and keeps the
    /// object's rotation from the moment of pickup (legacy behavior).
    ///
    /// Added by the Creator Dashboard "Add Grab Point" tool.
    /// </summary>
    public class U3DGrabPoint : MonoBehaviour
    {
        [Tooltip("How far out from the wrist joint the grip point sits, in meters, along the forearm line. Default is about 3 inches — roughly the palm. Increase for objects held away from the hand, or set to 0 to hold at the wrist joint itself.")]
        [SerializeField] private float anchorDistance = 0.0762f;

        public float AnchorDistance => anchorDistance;

        private void OnDrawGizmos()
        {
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;

            Gizmos.color = new Color(0.35f, 0.9f, 0.45f, 0.9f);
            Gizmos.DrawSphere(origin, 0.04f);

            Gizmos.color = new Color(0.35f, 0.9f, 0.45f, 0.8f);
            Vector3 tip = origin + forward * 0.4f;
            Gizmos.DrawLine(origin, tip);

            Vector3 right = transform.right;
            Gizmos.DrawLine(tip, tip - forward * 0.12f + right * 0.08f);
            Gizmos.DrawLine(tip, tip - forward * 0.12f - right * 0.08f);

            Gizmos.color = new Color(0.7f, 1f, 0.75f, 0.7f);
            Gizmos.DrawLine(origin, origin + transform.up * 0.15f);
        }
    }
}