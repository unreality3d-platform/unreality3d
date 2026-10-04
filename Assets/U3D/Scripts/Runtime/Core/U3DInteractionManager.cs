using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Handles interaction detection and dispatch for the U3D interaction system.
    /// Uses an OverlapSphere centered on the player to find nearby interactables.
    /// The closest one that CanInteract() returns true wins. No priority scoring,
    /// no directionality — proximity alone determines selection.
    ///
    /// If the player is currently holding a grabbable or occupying a seat, R is
    /// routed directly to that object. The OverlapSphere is skipped in those cases.
    /// A steerable is not a direct route — Interact reaches it through the normal
    /// OverlapSphere path like anything else, and exiting one is the Remove key,
    /// not Interact.
    /// </summary>
    public class U3DInteractionManager : MonoBehaviour
    {
        // Direct-route static references — checked before OverlapSphere, mirrors CurrentlyGrabbed pattern.

        [Header("Interaction Detection")]
        [Tooltip("Radius of the OverlapSphere used to find interactables near the player.")]
        [SerializeField] private float interactionRange = 10f;

        [Tooltip("Layers checked by the interaction OverlapSphere.")]
        [SerializeField] private LayerMask interactionLayerMask = -1;

        [Tooltip("Show the interaction range sphere in the Scene view when this object is selected.")]
        [SerializeField] private bool debugMode = false;

        private static U3DInteractionManager instance;
        private U3DPlayerController localPlayerController;

        public static U3DInteractionManager Instance
        {
            get
            {
                if (instance == null)
                    instance = FindAnyObjectByType<U3DInteractionManager>();
                return instance;
            }
        }

        private void Awake()
        {
            if (instance == null)
            {
                instance = this;
            }
            else if (instance != this)
            {
                Debug.LogWarning("Multiple InteractionManagers found. Destroying duplicate on: " + gameObject.name);
                Destroy(this);
                return;
            }
        }

        private void Start()
        {
            FindLocalPlayer();
        }

        private void Update()
        {
            if (localPlayerController == null)
                FindLocalPlayer();
        }

        private void FindLocalPlayer()
        {
            localPlayerController = U3DPlayerController.FindLocalPlayer();
        }

        /// <summary>
        /// Called by PlayerController when the interact button is pressed.
        /// If the player is holding a grabbable, route the press to that object for release.
        /// Otherwise find the closest interactable within range.
        ///
        /// Worldspace UI interaction is NOT handled here — U3DGazePointer reads the Interact
        /// action directly and runs UI event dispatch in its own Update loop, off the network tick.
        /// </summary>
        public void OnPlayerInteract()
        {
            if (localPlayerController == null) return;

            if (U3DGrabbable.CurrentlyGrabbed != null)
            {
                U3DGrabbable.CurrentlyGrabbed.OnInteract();
                return;
            }

            if (U3DSeat.CurrentlyOccupied != null)
            {
                U3DSeat.CurrentlyOccupied.Stand();
                return;
            }

            IU3DInteractable target = GetBestInteractable();
            if (target != null)
                target.OnInteract();
        }

        /// <summary>
        /// OverlapSphere centered on the player. Returns the closest IU3DInteractable
        /// whose CanInteract() returns true. Closest wins, no priority, no directionality.
        /// </summary>
        private IU3DInteractable GetBestInteractable()
        {
            if (localPlayerController == null) return null;

            Vector3 playerPosition = localPlayerController.transform.position;

            Collider[] colliders = Physics.OverlapSphere(playerPosition, interactionRange,
                interactionLayerMask, QueryTriggerInteraction.Collide);

            IU3DInteractable best = null;
            float closestDistance = Mathf.Infinity;

            foreach (Collider col in colliders)
            {
                IU3DInteractable interactable = col.GetComponentInParent<IU3DInteractable>();
                if (interactable == null || !interactable.CanInteract()) continue;

                float distance = Vector3.Distance(playerPosition,
                    ((MonoBehaviour)interactable).transform.position);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    best = interactable;
                }
            }

            return best;
        }

        private void OnDrawGizmosSelected()
        {
            if (!debugMode) return;
            if (localPlayerController == null) return;

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(localPlayerController.transform.position, interactionRange);
        }
    }

    /// <summary>
    /// Interface that all interactable objects must implement.
    /// </summary>
    public interface IU3DInteractable
    {
        void OnInteract();
        void OnPlayerEnterRange();
        void OnPlayerExitRange();
        bool CanInteract();
        string GetInteractionPrompt();
    }

    /// <summary>
    /// Optional interface for interactable components that need a non-proximity-gated
    /// activation path when used from U3DInventory.
    ///
    /// The standard IU3DInteractable.OnInteract() path is gated by proximity (e.g.
    /// Grabbable's CanAttemptGrab, Kickable's isInKickRange) because in the world
    /// context, the player has to walk up to a thing to interact with it. When the
    /// player summons an item from their own inventory, that gate is the wrong one:
    /// the player has already intentionally chosen the item, and the item spawns
    /// directly at the player's hand. Implementing this interface lets a component
    /// expose a parallel "activate without world-context gates" path that Inventory
    /// can call instead of OnInteract().
    ///
    /// Implementations should still honor non-proximity gates: tag checks,
    /// triggerOnce flags, cooldowns. Only the spatial proximity gate is bypassed.
    /// </summary>
    public interface IU3DInventoryActivatable
    {
        void OnInventoryActivate();
    }
}