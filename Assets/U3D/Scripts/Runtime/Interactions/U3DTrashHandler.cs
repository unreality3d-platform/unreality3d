using System.Collections.Generic;
using U3D.Net;
using U3D.Networking;
using UnityEngine;
using UnityEngine.Events;

namespace U3D
{
    /// <summary>
    /// Trigger zone that destroys or respawns objects that enter it.
    /// Place on any trigger collider — typically an invisible zone under the world
    /// to catch fallen objects, or anywhere a creator needs a kill/reset plane.
    ///
    /// Target scope:
    ///   - Empty reference list: acts on any Rigidbody that enters (including creator custom objects)
    ///   - Populated reference list: acts only on the listed GameObjects
    ///
    /// Mode:
    ///   - Destroy: calls RequestDestroy() on U3DDestroyable if present, which fires that
    ///     object's own OnDestroyed event. Otherwise the object is removed with Destroy().
    ///   - Respawn: returns any networked object to the place the scene put it, on every
    ///     machine; teleports the player to the scene spawn point; teleports plain
    ///     Rigidbody objects to a cached original transform.
    ///
    /// Note: For plain Rigidbody objects — ones carrying no U3D interactable, and so no
    /// motion component — the original transform is cached the first time the object
    /// enters this zone, not at scene start. If the object fell under gravity before
    /// arriving here, Respawn returns it to where it was on that first entry, not to its
    /// authored editor position.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class U3DTrashHandler : MonoBehaviour
    {
        public enum TrashMode
        {
            Destroy,
            Respawn
        }

        [Header("Trash Handler Configuration")]
        [Tooltip("Destroy: removes objects permanently. Respawn: returns them to their original position.")]
        [SerializeField] private TrashMode mode = TrashMode.Destroy;

        [Tooltip("Leave empty to act on any Rigidbody that enters this zone. Add specific GameObjects to restrict to those objects only.")]
        [SerializeField] private List<GameObject> targetObjects = new List<GameObject>();

        [Header("Filtering (Optional)")]
        [Tooltip("When enabled, this zone only acts on objects that carry the tag below. Set the tag to Player to make a player-only respawn zone that ignores physics objects.")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("The tag an object must have for this zone to act on it. Only used when Require Tag is enabled.")]
        [SerializeField] private string requiredTag = "Player";

        [Header("Events")]
        [Tooltip("Called when an object is destroyed by this zone. Not called for player respawns.")]
        public UnityEvent OnObjectDestroyed;

        [Tooltip("Called when an object or the player is respawned by this zone.")]
        public UnityEvent OnObjectRespawned;

        // Cached original transforms for plain Rigidbody objects with no motion component.
        // Populated lazily on first trigger enter for each object.
        private readonly Dictionary<GameObject, (Vector3 position, Quaternion rotation)> cachedOriginalTransforms
            = new Dictionary<GameObject, (Vector3, Quaternion)>();

        private void Awake()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            Rigidbody rb = other.attachedRigidbody;

            // The player moves via a CharacterController and has no Rigidbody. Anything
            // carrying a Rigidbody is a physics object — even a grabbed object parented
            // to the player — so the player check only runs when there is no Rigidbody.
            U3DPlayerController player = (rb == null) ? other.GetComponentInParent<U3DPlayerController>() : null;

            // Nothing actionable entered (no Rigidbody and not the player).
            if (rb == null && player == null) return;

            GameObject obj = player != null ? player.gameObject : rb.gameObject;

            // Optional tag filter: when enabled, act only on objects carrying the required tag.
            if (requireTag && !obj.CompareTag(requiredTag)) return;

            // Reference list filter: if populated, only act on listed objects.
            if (targetObjects.Count > 0 && !targetObjects.Contains(obj)) return;

            // Player: respawn only. A trash zone never destroys the player.
            if (player != null)
            {
                if (mode == TrashMode.Respawn)
                {
                    RespawnPlayer(player);
                }
                return;
            }

            if (mode == TrashMode.Destroy)
            {
                HandleDestroy(obj);
            }
            else
            {
                HandleRespawn(obj, rb);
            }
        }

        /// <summary>
        /// A networked object is removed for everyone through its own U3DDestroyable, which
        /// asks the one peer entitled to retire it. An object with no U3DDestroyable and no
        /// NetEntity is the creator's own plain Rigidbody and is removed locally, which is
        /// all it ever was.
        ///
        /// A networked object with no U3DDestroyable is left alone rather than destroyed
        /// locally. Removing it here would take it off this screen and leave it on everyone
        /// else's, which reads as working for a creator testing alone. Silent, because a
        /// zone acting on whatever falls into it meets objects it is not meant to remove as
        /// an ordinary condition, and this fires per object per entry.
        /// </summary>
        private void HandleDestroy(GameObject obj)
        {
            U3DDestroyable destroyable = obj.GetComponent<U3DDestroyable>();
            if (destroyable != null)
            {
                destroyable.RequestDestroy();
                OnObjectDestroyed?.Invoke();
                return;
            }

            if (obj.GetComponent<NetEntity>() != null) return;

            Destroy(obj);
            OnObjectDestroyed?.Invoke();
        }

        /// <summary>
        /// A networked object goes home through its motion component, which every U3D
        /// interactable carries and which publishes the move so every machine performs it.
        /// The zone does not ask which interactable the object is: what decides the answer
        /// is whether the object can be moved across the wire, and that is one question
        /// with one component that answers it.
        ///
        /// Anything else is a plain Rigidbody the creator built themselves, moved locally
        /// against a cached pose.
        /// </summary>
        private void HandleRespawn(GameObject obj, Rigidbody rb)
        {
            NetRigidbody motion = obj.GetComponent<NetRigidbody>();

            if (motion != null)
            {
                // A held object is in somebody's hand, not lost. Sending it home while it
                // is still parented to a hand bone would place a pose measured against a
                // moving target. Leave it — the player drops or throws it, and it comes
                // back through here as a loose object.
                if (motion.State == NetRigidbody.MotionState.Carried) return;

                if (!motion.ResetToAuthored()) return;

                OnObjectRespawned?.Invoke();
                return;
            }

            // Plain Rigidbody — teleport to cached original transform.
            if (!cachedOriginalTransforms.ContainsKey(obj))
            {
                // First time we've seen this object — cache its current position now.
                // This will be where it was when it first entered this zone, which for
                // most static objects equals their authored editor position.
                cachedOriginalTransforms[obj] = (obj.transform.position, obj.transform.rotation);
                Debug.LogWarning($"U3DTrashHandler: '{obj.name}' has no U3D interactable component. Original position was not captured at scene start — caching current position as fallback.");
            }

            var original = cachedOriginalTransforms[obj];
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            obj.transform.position = original.position;
            obj.transform.rotation = original.rotation;
            rb.isKinematic = false;

            OnObjectRespawned?.Invoke();
        }

        private void RespawnPlayer(U3DPlayerController player)
        {
            if (U3DPlayerSpawner.Instance == null)
            {
                Debug.LogWarning("U3DTrashHandler: Cannot respawn player — U3DPlayerSpawner instance not found.");
                return;
            }

            (Vector3 spawnPosition, Quaternion spawnRotation) = U3DPlayerSpawner.Instance.GetSpawnData();

            // Route through the controller's own API rather than writing the transform
            // directly. The controller owns the CharacterController, which overrides direct
            // transform writes, and it owns the camera heading.
            player.SetPosition(spawnPosition);
            player.SetRotation(spawnRotation.eulerAngles.y);

            OnObjectRespawned?.Invoke();
        }

        /// <summary>
        /// Pre-register an object's original transform so Respawn mode has an
        /// accurate starting position even for objects that drop under gravity
        /// before they could enter this zone. Call this from scene setup code
        /// if needed for plain Rigidbody objects with startActive-style behaviour.
        /// </summary>
        public void RegisterOriginalTransform(GameObject obj)
        {
            if (obj == null) return;
            if (!cachedOriginalTransforms.ContainsKey(obj))
            {
                cachedOriginalTransforms[obj] = (obj.transform.position, obj.transform.rotation);
            }
        }

        public TrashMode Mode => mode;
        public int TargetCount => targetObjects.Count;
    }
}