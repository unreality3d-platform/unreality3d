using UnityEngine;
using System.Collections;

namespace U3D
{
    /// <summary>
    /// Disables the trigger collider on this GameObject for a short window after
    /// scene load, then re-enables it. Use when an object starts the scene already
    /// inside this trigger volume and you want OnTriggerEnter to fire only on a
    /// real entry transition, not on the scene-load overlap.
    ///
    /// Example: a hot air balloon animating up through a trigger volume that fires
    /// audio and effects each time it passes through. At scene load the balloon
    /// collider is overlapping the trigger, which would normally fire OnTriggerEnter
    /// once at frame 0 with no transition. With this component, the trigger collider
    /// waits the configured delay before activating, by which time the balloon
    /// animation has carried the collider out of the volume. The first real entry
    /// fires OnTriggerEnter as intended.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class U3DDelayedTriggerActivation : MonoBehaviour
    {
        [Tooltip("Seconds to wait after scene load before enabling the trigger collider. Should be longer than the time it takes for any object that starts inside this trigger to animate out of it.")]
        [SerializeField] private float activationDelay = 0.5f;

        private void Start()
        {
            GetComponent<Collider>().enabled = false;
            StartCoroutine(EnableAfterDelay());
        }

        private IEnumerator EnableAfterDelay()
        {
            yield return new WaitForSeconds(activationDelay);
            GetComponent<Collider>().enabled = true;
        }
    }
}