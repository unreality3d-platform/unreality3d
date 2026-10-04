using UnityEngine;

namespace U3D
{
    [RequireComponent(typeof(Collider))]
    public class U3DRideableTrigger : MonoBehaviour
    {
        private U3DRideableController _controller;

        private void Awake()
        {
            _controller = GetComponentInParent<U3DRideableController>();
        }

        private void OnTriggerStay(Collider other)
        {
            if (_controller == null) return;
            var player = other.GetComponentInParent<U3DPlayerController>();
            if (player == null) return;
            if (!player.IsGrounded) return;
            if (player.IsRiding(_controller)) return;
            player.MountRideable(_controller);
        }

        // OnTriggerExit is intentionally not used for dismount.
        // Dismount is driven by the player controller detecting loss of grounding,
        // which avoids unreliable trigger exit events caused by hierarchy parenting.
    }
}