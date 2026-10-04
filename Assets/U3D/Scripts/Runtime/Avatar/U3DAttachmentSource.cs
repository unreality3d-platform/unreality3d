using UnityEngine;
using UnityEngine.Events;
using U3D.Net;

namespace U3D
{
    /// <summary>
    /// A scene station that hands a cosmetic accessory — or a whole costume — to whoever interacts
    /// with it. Place it on a persistent scene object (a dummy visual or an empty) and assign ONE
    /// accessory prefab. The pieces of that prefab are defined by its attachment-point markers: each
    /// U3DAttachmentPoint marks one piece — the piece is the object the marker is parented to. A
    /// single accessory has one marker on the prefab root (worn as one piece); a costume has a marker
    /// inside each child (each child rides its own bone). Children with no marker are ignored. The
    /// whole prefab is worn and removed as one unit.
    ///
    /// Interacting toggles the costume on the local player: once to wear it, again to take it off,
    /// like a wardrobe stand. The accessory itself is never spawned as a shared object —
    /// U3DPlayerAttachments instantiates it and parents each piece to its bone, so it rides the
    /// player's own movement and animation.
    ///
    /// On Wear and On Remove fire on the player who performs the action, once per action. They are
    /// for that player's own feedback (a sound, a message, a score) — they do not fire when
    /// re-wearing something already on (a no-op) or when an add is refused at capacity.
    ///
    /// Apply via the Creator Dashboard "Make Attachment" tool.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class U3DAttachmentSource : MonoBehaviour, IU3DInteractable
    {
        // This object carries a NetEntity, added by the Creator Dashboard's Make Attachment tool.
        // Its authored ID is what travels when somebody wears this item: the number goes on the
        // wire, every machine looks up its own copy of this station by it, and builds the accessory
        // from that — so nothing about the accessory itself is ever sent. The tool calls
        // EnsureNetEntity rather than this file declaring [RequireComponent], which is the either/or
        // that convention states: a component does one or the other, never both. G264.

        [Tooltip("The cosmetic prefab handed to the player. Each piece is marked by a U3DAttachmentPoint (added with Add Attachment Point): a single accessory has one point on the prefab itself; a costume has a point inside each child piece, each riding its own bone. Assign a Project prefab asset, not a scene object. The pieces are local cosmetics and must not be set up as multiplayer objects. The whole prefab is worn and removed as one unit.")]
        [SerializeField] private GameObject accessoryPrefab;

        [Tooltip("Fires on the player who wears this, the moment they put it on. Once per action — not when re-wearing something already on. Use for that player's own feedback: an equip sound, a message, a score change.")]
        [SerializeField] private UnityEvent onWear;

        [Tooltip("Fires on the player who removes this, the moment they take it off. Once per action. Use for that player's own feedback: an unequip sound, a message, a score change.")]
        [SerializeField] private UnityEvent onRemove;

        public GameObject AccessoryPrefab => accessoryPrefab;

        [Tooltip("Optional. Stations sharing the same slot name replace each other: wearing this removes anything worn from another station with the same slot (e.g. two different heads both marked \"Head\"). Leave empty to stack freely with everything, like a hat over glasses. Case and surrounding spaces are ignored.")]
        [SerializeField] private string slot = "";

        public string Slot => slot;

        private U3DPlayerController _localPlayer;

        public bool CanInteract() => HasPrefab();

        private bool HasPrefab()
        {
            return accessoryPrefab != null;
        }

        public void OnInteract()
        {
            if (!HasPrefab()) return;

            if (_localPlayer == null)
                _localPlayer = U3DPlayerController.FindLocalPlayer();
            if (_localPlayer == null) return;

            // PORT: a player prefab without U3DPlayerAttachments makes this a silent no-op. The
            // component ships on U3D_PlayerController.prefab, so this only happens if it was
            // removed; the check belongs in publish validation, where a creator can act on it,
            // rather than in a runtime warning a deployed build cannot display. B39
            U3DPlayerAttachments attachments = _localPlayer.GetComponent<U3DPlayerAttachments>();
            if (attachments == null) return;

            attachments.Wear(this);
        }

        /// <summary>
        /// Fires the On Wear event. Called by U3DPlayerAttachments right after this source is
        /// actually added to the worn list — never on a re-wear no-op or a capacity refusal.
        /// </summary>
        public void InvokeOnWear()
        {
            onWear?.Invoke();
        }

        /// <summary>
        /// Fires the On Remove event. Called by U3DPlayerAttachments right after this source is
        /// actually removed from the worn list.
        /// </summary>
        public void InvokeOnRemove()
        {
            onRemove?.Invoke();
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }
        public string GetInteractionPrompt() => "Wear / Remove";

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.85f);
            Gizmos.DrawWireCube(transform.position, Vector3.one * 0.3f);
        }

        private void OnValidate()
        {
            if (accessoryPrefab == null) return;

            if (accessoryPrefab.scene.IsValid())
                Debug.LogWarning($"{name}: accessory prefab '{accessoryPrefab.name}' is a scene object. Assign a Project prefab asset instead.", this);

            if (accessoryPrefab.GetComponentInChildren<NetEntity>(true) != null)
                Debug.LogWarning($"{name}: accessory prefab '{accessoryPrefab.name}' is set up as a multiplayer object. The accessory is built locally as a cosmetic and must not be one — remove the Net Entity component from the prefab and its children.", this);

            if (accessoryPrefab.GetComponentInChildren<U3DAttachmentPoint>(true) == null)
                Debug.LogWarning($"{name}: accessory prefab '{accessoryPrefab.name}' has no attachment point. Use Add Attachment Point to mark where it sits on the avatar — without one, it won't attach.", this);
        }
    }
}