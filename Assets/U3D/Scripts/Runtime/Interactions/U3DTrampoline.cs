using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    [AddComponentMenu("U3D/U3D Trampoline")]
    public class U3DTrampoline : MonoBehaviour
    {
        [Header("Launch Settings")]
        [Tooltip("How high the launch sends the player, in meters. Same tuning scale as the player's Jump Height. Ignored when Randomize Height is on.")]
        [SerializeField] private float launchHeight = 6f;

        [Tooltip("How long the player rests on the surface between touchdown and launch, in seconds. This brief pause is what lets the landing animation play — the same foot-plant you see after a normal jump. Higher values feel like a saggy trampoline, lower like a stiff one. Very small values are raised to a minimum internally so the landing still registers.")]
        [SerializeField] private float contactTime = 0.15f;

        [Header("Randomize")]
        [Tooltip("When on, each bounce picks a random height between Min and Max instead of using Launch Height.")]
        [SerializeField] private bool randomizeHeight = false;

        [Tooltip("Lowest possible bounce height, in meters. Only used when Randomize Height is on.")]
        [SerializeField] private float minLaunchHeight = 3f;

        [Tooltip("Highest possible bounce height, in meters. Only used when Randomize Height is on.")]
        [SerializeField] private float maxLaunchHeight = 9f;

        [Header("Events")]
        [Tooltip("Fires at the moment a player is launched. Hook up sounds, particles, or a squash animation here.")]
        public UnityEvent OnLaunched;

        public void HandleLanding(U3DPlayerController player)
        {
            if (player == null) return;

            float height;
            if (randomizeHeight)
            {
                float low = Mathf.Min(minLaunchHeight, maxLaunchHeight);
                float high = Mathf.Max(minLaunchHeight, maxLaunchHeight);
                height = Random.Range(low, high);
            }
            else
            {
                height = launchHeight;
            }

            player.QueueTrampolineLaunch(this, height, contactTime);
        }

        public void NotifyLaunched()
        {
            OnLaunched?.Invoke();
        }
    }
}