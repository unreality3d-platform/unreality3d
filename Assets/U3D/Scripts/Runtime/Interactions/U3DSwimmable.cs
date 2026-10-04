using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Self-contained swimmable water volume. Add via Creator Dashboard "Make Swimmable" button.
    /// Place a trigger collider of any shape (Box, Sphere, Mesh — including concave meshes)
    /// sized to where you want swimming to engage. The local player swims while inside the
    /// trigger and stops swimming when leaving.
    ///
    /// Swimming reuses the player's flying locomotion model: full 3D camera-aligned movement,
    /// gravity disabled, Space/Crouch for vertical. The IsSwimming animator flag drives swim
    /// animations instead of fly animations.
    ///
    /// On Enter Water and On Exit Water fire on the machine of the player who entered or left,
    /// so a splash sound plays for the swimmer. They do not fire for anyone else watching —
    /// see the PORT note below.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class U3DSwimmable : MonoBehaviour
    {
        [Header("Trigger Configuration")]
        [Tooltip("Only fire events when the player carries a specific tag")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag the player must carry for this volume's events to fire")]
        [SerializeField] private string requiredTag = "Player";

        [Header("Events")]
        [Tooltip("Fires for the player who enters the water. Use for their own splash, audio, or score.")]
        public UnityEvent OnEnterWater;

        [Tooltip("Fires for the player who leaves the water. Use for their own splash, audio, or score.")]
        public UnityEvent OnExitWater;

        // PORT: entering water is observed independently on each machine, so no message is needed
        // for a swimmer's own splash and none is needed for the swimming itself — a remote swimmer
        // reaches everyone through the ordinary avatar path, including the swim animation.
        // What Phase 5 adds is the everyone-versus-me split G127 established for kick and throw:
        // a splash that everybody hears needs a broadcast carrying no payload, with the sender
        // taken from the connection per G20. Until then these are the swimmer's own feedback. G147
        private U3DPlayerController engagedLocalPlayer;

        private void Awake()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            U3DPlayerController player = other.GetComponentInParent<U3DPlayerController>();
            if (player == null) return;
            if (engagedLocalPlayer == player) return;

            engagedLocalPlayer = player;
            player.SetSwimmingState(true);

            if (requireTag && !player.CompareTag(requiredTag)) return;

            OnEnterWater?.Invoke();
        }

        private void OnTriggerExit(Collider other)
        {
            U3DPlayerController player = other.GetComponentInParent<U3DPlayerController>();
            if (player == null) return;
            if (engagedLocalPlayer != player) return;

            engagedLocalPlayer = null;
            player.SetSwimmingState(false);

            if (requireTag && !player.CompareTag(requiredTag)) return;

            OnExitWater?.Invoke();
        }

        private void OnDisable()
        {
            // A disabled water volume must not leave the player stuck swimming. Matches
            // U3DClimbable's detach-on-disable.
            if (engagedLocalPlayer != null)
                engagedLocalPlayer.SetSwimmingState(false);

            engagedLocalPlayer = null;
        }
    }
}