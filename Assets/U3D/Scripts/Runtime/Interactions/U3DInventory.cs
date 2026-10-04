using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using U3DNet = U3D.Net.Net;
using U3D.Net;

namespace U3D
{
    /// <summary>
    /// Scene-placed, local-only 10-slot hotkey inventory for the local player.
    /// Items are added by U3DCollectable. Pressing a slot's hotkey spawns that
    /// slot's prefab at the local player's hand bone and activates it.
    ///
    /// Activation is routed through IU3DInventoryActivatable when the spawned
    /// component implements it (Grabbable, Kickable — components whose
    /// IU3DInteractable.OnInteract() path has a proximity gate that doesn't apply
    /// to inventory-summoned items). Otherwise activation falls back to the
    /// standard IU3DInteractable.OnInteract() path. Inventory itself does not
    /// know about specific component types.
    ///
    /// Rapid-fire behavior: When a slot's hotkey is pressed while the previously
    /// spawned item from that slot is still being held (Grabbable's currentlyGrabbed
    /// auto-release fires when a new grab happens), the next item replaces it
    /// seamlessly. When the slot becomes empty but the last spawned item is still
    /// held, pressing the hotkey one more time releases it — closing the "stuck
    /// last item" gap so the same key fires throughout the entire stack.
    ///
    /// Live spawn cap: maxLiveSpawnedInstances limits how many inventory-spawned
    /// objects can exist in the world at once across all slots. 0 = unlimited.
    /// When the cap is reached, hotkey presses that would spawn a new object are
    /// blocked (the banked stack is not consumed) and OnSpawnLimitReached fires.
    /// This guards against a player banking a large stack and spam-spawning enough
    /// world meshes to crash the browser.
    ///
    /// State is in-memory and per-session. There is no Firestore or PlayerPrefs
    /// persistence in v1.
    /// </summary>
    public class U3DInventory : MonoBehaviour
    {
        public const int SLOT_COUNT = 10;

        [Header("Hotkeys (one per slot)")]
        [Tooltip("Keys that activate each inventory slot, in slot order. Default: 1, 2, 3, 4, 5, 6, 7, 8, 9, 0. Remap freely per scene.")]
        [SerializeField]
        private KeyCode[] slotHotkeys = new KeyCode[SLOT_COUNT]
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
            KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0
        };

        [Header("Hand Attachment")]
        [Tooltip("Which hand summoned items appear in. Works on any humanoid avatar without naming bones. Matches U3DGrabbable's default.")]
        [SerializeField] private U3DHandChoice hand = U3DHandChoice.RightHand;

        [Header("Spawn Limit")]
        [Tooltip("Maximum number of inventory-spawned objects that can exist in the world at once, across all slots combined. When reached, pressing a slot hotkey will not spawn a new object (the banked stack is left untouched) and OnSpawnLimitReached fires. Set to 0 for unlimited.")]
        [Min(0)]
        [SerializeField] private int maxLiveSpawnedInstances = 0;

        [Header("Events")]
        [Tooltip("Fired when an item is added to inventory. Args: prefab added, new stack count in its slot.")]
        public UnityEvent<GameObject, int> OnItemAdded;

        [Tooltip("Fired when an item is removed from inventory. Args: prefab removed, new stack count in its slot (0 if slot cleared).")]
        public UnityEvent<GameObject, int> OnItemRemoved;

        [Tooltip("Fired when any slot changes (add, remove, or use). Args: slot index, prefab in slot (null if empty), new stack count.")]
        public UnityEvent<int, GameObject, int> OnSlotChanged;

        [Tooltip("Fired when a slot's hotkey is pressed and an item is successfully spawned. Args: slot index, prefab that was spawned.")]
        public UnityEvent<int, GameObject> OnSlotUsed;

        [Tooltip("Fired when AddItem fails because all 10 slots are occupied with different prefabs. Wire to UI feedback like a 'pack full' message.")]
        public UnityEvent<GameObject> OnInventoryFull;

        [Tooltip("Fired when a slot hotkey press is blocked because the live spawn limit is reached. The banked stack is not consumed. Wire to UI feedback like a 'too many out — pick some up' message. Arg: the prefab that would have spawned.")]
        public UnityEvent<GameObject> OnSpawnLimitReached;

        private NetPrefab[] slotPrefabs = new NetPrefab[SLOT_COUNT];
        private int[] slotCounts = new int[SLOT_COUNT];

        // Tracks the most recently spawned instance per slot. Used so that pressing
        // an empty slot's hotkey while still holding the last-summoned item from
        // that slot will release it — closing the "stuck last item" gap in
        // the rapid-fire flow.
        private GameObject[] lastSpawnedFromSlot = new GameObject[SLOT_COUNT];

        // Inventory-wide count of live spawned-from-inventory objects. Only tracked
        // when maxLiveSpawnedInstances > 0; stays at 0 and is never read when the
        // cap is disabled, so uncapped behavior carries no tracking overhead.
        private int _liveSpawnedCount = 0;

        private U3DPlayerController playerController;
        private Transform playerTransform;
        private Transform handTransform;
        private bool keyConflictsChecked = false;

        private void Awake()
        {
            // Validate hotkey array size in case the creator clears it in the Inspector.
            if (slotHotkeys == null || slotHotkeys.Length != SLOT_COUNT)
            {
                Debug.LogWarning($"U3DInventory on '{name}': slotHotkeys must have exactly {SLOT_COUNT} entries. Resetting to defaults.", this);
                slotHotkeys = new KeyCode[SLOT_COUNT]
                {
                    KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
                    KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.Alpha0
                };
            }
        }

        private void Update()
        {
            // First-time conflict check needs the player controller to exist so U3DKeyManager
            // can scan its InputActions. Defer until we find it.
            if (!keyConflictsChecked && EnsurePlayerFound())
            {
                CheckHotkeyConflicts();
                keyConflictsChecked = true;
            }

            if (Keyboard.current == null) return;

            for (int i = 0; i < SLOT_COUNT; i++)
            {
                // Read input for slots that either have items OR have a still-held
                // last-spawned instance, so the empty-slot release path is reachable.
                bool slotHasItem = slotPrefabs[i].IsValid;
                bool slotHasHeldLast = lastSpawnedFromSlot[i] != null;
                if (!slotHasItem && !slotHasHeldLast) continue;

                KeyControl keyControl = ResolveKeyControl(slotHotkeys[i]);
                if (keyControl != null && keyControl.wasPressedThisFrame)
                    UseSlot(i);
            }
        }

        private GameObject SpawnInstance(NetPrefab prefab, Vector3 position, Quaternion rotation)
        {
            if (U3DNet.Session == null) return null;

            NetEntity entity = U3DNet.Session.SpawnPersonalItem(prefab, position, rotation);
            if (entity == null) return null;

            // The item is created at the player's own hand, which is inside their capsule.
            // Nothing else knows that, so the suspension is started from here and handed
            // over to the carry state when the grab lands a frame later.
            NetRigidbody motion = entity.GetComponent<NetRigidbody>();
            if (motion != null) motion.SuspendUntilPlayerClear();

            return entity.gameObject;
        }

        private bool EnsurePlayerFound()
        {
            if (playerController != null) return true;

            playerController = U3DPlayerController.FindLocalPlayer();
            if (playerController == null) return false;

            playerTransform = playerController.transform;
            return true;
        }

        private void FindHandBone()
        {
            handTransform = null;

            if (playerTransform == null) return;

            U3DAvatarManager avatarManager = playerController != null
                ? playerController.GetComponent<U3DAvatarManager>()
                : null;

            if (avatarManager != null)
                handTransform = avatarManager.ResolveHandBone(hand, true);

            if (handTransform == null)
                handTransform = playerTransform;
        }

        private void CheckHotkeyConflicts()
        {
            U3DKeyManager.InitializeFromPlayerController(playerController);

            for (int i = 0; i < slotHotkeys.Length; i++)
            {
                if (!U3DKeyManager.IsKeyAvailable(slotHotkeys[i]))
                {
                    string conflict = U3DKeyManager.GetKeyConflictInfo(slotHotkeys[i]);
                    Debug.LogWarning($"U3DInventory slot {i} hotkey '{slotHotkeys[i]}' conflicts with Player Controller. {conflict}", this);
                }
            }
        }

        // ---------- Public API ----------

        /// <summary>
        /// Whether AddItem would succeed for this prefab, without changing anything.
        /// A collectable asks this before requesting a collect, because the request has to
        /// be refused before it is sent rather than after the item has been awarded.
        /// </summary>
        public bool CanAccept(NetPrefab prefab)
        {
            if (!prefab.IsValid) return false;

            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (!slotPrefabs[i].IsValid) return true;
                if (slotPrefabs[i].Prefab == prefab.Prefab) return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a quantity of the given prefab to inventory. If a slot already holds this
        /// prefab, its stack count increments. Otherwise the next empty slot is used.
        /// Returns false if all slots are occupied with different prefabs.
        /// </summary>
        public bool AddItem(NetPrefab prefab, int quantity = 1)
        {
            if (!prefab.IsValid) return false;
            if (quantity < 1) return false;

            // First pass: existing matching slot.
            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (!slotPrefabs[i].IsValid) continue;
                if (slotPrefabs[i].Prefab != prefab.Prefab) continue;

                slotCounts[i] += quantity;
                OnItemAdded?.Invoke(prefab.Prefab, slotCounts[i]);
                OnSlotChanged?.Invoke(i, prefab.Prefab, slotCounts[i]);
                return true;
            }

            // Second pass: first empty slot.
            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (slotPrefabs[i].IsValid) continue;

                slotPrefabs[i] = prefab;
                slotCounts[i] = quantity;
                OnItemAdded?.Invoke(prefab.Prefab, slotCounts[i]);
                OnSlotChanged?.Invoke(i, prefab.Prefab, slotCounts[i]);
                return true;
            }

            // All slots occupied with different prefabs.
            OnInventoryFull?.Invoke(prefab.Prefab);
            return false;
        }

        /// <summary>
        /// Removes a quantity of the given prefab from inventory. Returns false if the
        /// prefab is not present or quantity exceeds the stack.
        /// </summary>
        public bool RemoveItem(NetPrefab prefab, int quantity = 1)
        {
            if (!prefab.IsValid) return false;
            if (quantity < 1) return false;

            for (int i = 0; i < SLOT_COUNT; i++)
            {
                if (!slotPrefabs[i].IsValid) continue;
                if (slotPrefabs[i].Prefab != prefab.Prefab) continue;
                if (slotCounts[i] < quantity) return false;

                slotCounts[i] -= quantity;
                if (slotCounts[i] <= 0)
                {
                    slotCounts[i] = 0;
                    slotPrefabs[i] = default;
                    OnItemRemoved?.Invoke(prefab.Prefab, 0);
                    OnSlotChanged?.Invoke(i, null, 0);
                }
                else
                {
                    OnItemRemoved?.Invoke(prefab.Prefab, slotCounts[i]);
                    OnSlotChanged?.Invoke(i, prefab.Prefab, slotCounts[i]);
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Press-the-hotkey entry point. Three paths:
        ///   1. Slot has items → spawn the next one and activate it. (Grabbable's
        ///      currentlyGrabbed auto-release handles "fire the previous one"
        ///      mid-stack via Throwable's release-as-throw listener.)
        ///   2. Slot empty but the last spawned item is still held → release it
        ///      so the same hotkey closes the stack with the last item firing.
        ///   3. Slot empty and nothing relevant held → no-op.
        ///
        /// When maxLiveSpawnedInstances > 0 and the live spawn count is at the cap,
        /// the spawn in path 1 is blocked: the banked stack is left untouched and
        /// OnSpawnLimitReached fires. The release path (path 2) is never blocked —
        /// releasing a held item lowers world-object pressure rather than raising it.
        /// </summary>
        public void UseSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SLOT_COUNT) return;

            NetPrefab prefab = slotPrefabs[slotIndex];

            // Path 2: empty slot but last item from this slot is still in hand.
            // Release it so the player's hotkey "fires" the last item rather than
            // forcing them to switch keys to R.
            if (!prefab.IsValid)
            {
                ReleaseHeldFromSlot(slotIndex);
                return;
            }

            // Live spawn cap (inventory-wide). Checked before spawning and before
            // the stack is decremented, so a blocked press does not consume a
            // banked item. 0 = unlimited (cap disabled).
            if (maxLiveSpawnedInstances > 0 && _liveSpawnedCount >= maxLiveSpawnedInstances)
            {
                OnSpawnLimitReached?.Invoke(prefab.Prefab);
                return;
            }

            if (!EnsurePlayerFound())
            {
                Debug.LogWarning("U3DInventory: Cannot use slot, local player not found.", this);
                return;
            }

            // Resolve the hand fresh at use time (not cached at player-found time) so a
            // humanoid avatar that finished loading after the player was first located
            // still resolves to the real hand bone rather than a stale fallback.
            FindHandBone();

            // Spawn at the hand bone position. Inventory has no opinion on item
            // rotation — we use identity so the prefab's authored orientation isn't
            // overridden by the hand bone's animation-rig rotation. Throwable derives
            // throw direction from the camera at release time, so spawn rotation
            // doesn't affect throw behavior.
            Vector3 spawnPos = handTransform != null ? handTransform.position : playerTransform.position;
            Quaternion spawnRot = Quaternion.identity;

            GameObject spawned = SpawnInstance(prefab, spawnPos, spawnRot);
            if (spawned == null) return;

            lastSpawnedFromSlot[slotIndex] = spawned;

            // Track the live instance for the cap. Only attach the tracker and count
            // when the cap is enabled, so uncapped scenes carry no extra component
            // or bookkeeping. The tracker decrements the count on destruction.
            if (maxLiveSpawnedInstances > 0)
            {
                _liveSpawnedCount++;
                var tracker = spawned.AddComponent<U3DInventorySpawnTracker>();
                tracker.Initialize(this);
            }

            // Decrement the stack regardless of whether the spawned instance has an
            // activatable component — the spawn itself counts as "using" the item.
            slotCounts[slotIndex]--;
            GameObject remainingPrefab = prefab.Prefab;
            if (slotCounts[slotIndex] <= 0)
            {
                slotCounts[slotIndex] = 0;
                slotPrefabs[slotIndex] = default;
                remainingPrefab = null;
            }

            OnSlotUsed?.Invoke(slotIndex, prefab.Prefab);
            OnSlotChanged?.Invoke(slotIndex, remainingPrefab, slotCounts[slotIndex]);

            // One-frame yield so the spawned instance's Awake/Start have time to run
            // before activation. Then route through the inventory-aware activation path:
            // components implementing IU3DInventoryActivatable get their
            // non-proximity-gated path called; everything else falls through to
            // the standard IU3DInteractable.OnInteract() path.
            StartCoroutine(ActivateAfterFrame(spawned));
        }

        /// <summary>
        /// Called by U3DInventorySpawnTracker when a tracked spawned instance is
        /// destroyed. Lowers the live spawn count so the cap reflects current world
        /// state. Guarded against going negative.
        /// </summary>
        public void OnLiveSpawnedInstanceDestroyed()
        {
            _liveSpawnedCount = Mathf.Max(0, _liveSpawnedCount - 1);
        }

        private IEnumerator ActivateAfterFrame(GameObject spawned)
        {
            yield return null;
            if (spawned == null) yield break;

            // Inventory-aware activation: components opt in by implementing
            // IU3DInventoryActivatable when their OnInteract() path has a
            // proximity gate that doesn't apply to inventory-summoned items.
            IU3DInventoryActivatable inventoryActivatable =
                spawned.GetComponent<IU3DInventoryActivatable>();
            if (inventoryActivatable == null)
                inventoryActivatable = spawned.GetComponentInChildren<IU3DInventoryActivatable>();

            if (inventoryActivatable != null)
            {
                inventoryActivatable.OnInventoryActivate();
                yield break;
            }

            // Universal fallback: components without an inventory-specific path
            // use the standard interaction call. This covers InteractTrigger,
            // EnterTrigger-only items, and any creator-added IU3DInteractable
            // that has no proximity gate to worry about.
            IU3DInteractable interactable = spawned.GetComponent<IU3DInteractable>();
            if (interactable == null)
                interactable = spawned.GetComponentInChildren<IU3DInteractable>();

            if (interactable != null)
                interactable.OnInteract();

            // Spawned objects with no IU3DInteractable / IU3DInventoryActivatable
            // are not an error — the creator may have wired the prefab to do
            // its work entirely through component Awake/Start (e.g. particle
            // effect prefabs, scripted self-destructing items).
        }

        /// <summary>
        /// If the player is still holding the last item we spawned for this slot,
        /// release it. Used when the slot is empty but the held item logically
        /// "belongs" to this slot, so the same hotkey can fire it.
        ///
        /// Whatever happens after release (throw via Throwable, drop under gravity,
        /// float in place if kinematic) is determined by the prefab's components
        /// and authored physics state — Inventory doesn't impose any release behavior.
        /// </summary>
        private void ReleaseHeldFromSlot(int slotIndex)
        {
            GameObject tracked = lastSpawnedFromSlot[slotIndex];
            if (tracked == null) return;

            U3DGrabbable grabbable = tracked.GetComponent<U3DGrabbable>();
            if (grabbable == null || !grabbable.IsGrabbed)
            {
                // Tracked reference is stale (item already released / destroyed /
                // never had a Grabbable in the first place — e.g. Kickable). Clear
                // it so subsequent presses don't re-poll a dead reference.
                lastSpawnedFromSlot[slotIndex] = null;
                return;
            }

            grabbable.Release();
            lastSpawnedFromSlot[slotIndex] = null;
        }

        /// <summary>
        /// Maps a legacy KeyCode to the new Input System's KeyControl on the current Keyboard.
        /// Covers the keys creators would realistically pick for inventory hotkeys:
        /// digits, letters, and common function/modifier keys.
        /// </summary>
        private static KeyControl ResolveKeyControl(KeyCode key)
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return null;

            switch (key)
            {
                case KeyCode.Alpha0: return kb.digit0Key;
                case KeyCode.Alpha1: return kb.digit1Key;
                case KeyCode.Alpha2: return kb.digit2Key;
                case KeyCode.Alpha3: return kb.digit3Key;
                case KeyCode.Alpha4: return kb.digit4Key;
                case KeyCode.Alpha5: return kb.digit5Key;
                case KeyCode.Alpha6: return kb.digit6Key;
                case KeyCode.Alpha7: return kb.digit7Key;
                case KeyCode.Alpha8: return kb.digit8Key;
                case KeyCode.Alpha9: return kb.digit9Key;

                case KeyCode.A: return kb.aKey;
                case KeyCode.B: return kb.bKey;
                case KeyCode.C: return kb.cKey;
                case KeyCode.D: return kb.dKey;
                case KeyCode.E: return kb.eKey;
                case KeyCode.F: return kb.fKey;
                case KeyCode.G: return kb.gKey;
                case KeyCode.H: return kb.hKey;
                case KeyCode.I: return kb.iKey;
                case KeyCode.J: return kb.jKey;
                case KeyCode.K: return kb.kKey;
                case KeyCode.L: return kb.lKey;
                case KeyCode.M: return kb.mKey;
                case KeyCode.N: return kb.nKey;
                case KeyCode.O: return kb.oKey;
                case KeyCode.P: return kb.pKey;
                case KeyCode.Q: return kb.qKey;
                case KeyCode.R: return kb.rKey;
                case KeyCode.S: return kb.sKey;
                case KeyCode.T: return kb.tKey;
                case KeyCode.U: return kb.uKey;
                case KeyCode.V: return kb.vKey;
                case KeyCode.W: return kb.wKey;
                case KeyCode.X: return kb.xKey;
                case KeyCode.Y: return kb.yKey;
                case KeyCode.Z: return kb.zKey;

                case KeyCode.F1: return kb.f1Key;
                case KeyCode.F2: return kb.f2Key;
                case KeyCode.F3: return kb.f3Key;
                case KeyCode.F4: return kb.f4Key;
                case KeyCode.F5: return kb.f5Key;
                case KeyCode.F6: return kb.f6Key;
                case KeyCode.F7: return kb.f7Key;
                case KeyCode.F8: return kb.f8Key;
                case KeyCode.F9: return kb.f9Key;
                case KeyCode.F10: return kb.f10Key;
                case KeyCode.F11: return kb.f11Key;
                case KeyCode.F12: return kb.f12Key;

                case KeyCode.Tab: return kb.tabKey;
                case KeyCode.LeftShift: return kb.leftShiftKey;
                case KeyCode.RightShift: return kb.rightShiftKey;
                case KeyCode.LeftControl: return kb.leftCtrlKey;
                case KeyCode.RightControl: return kb.rightCtrlKey;
                case KeyCode.LeftAlt: return kb.leftAltKey;
                case KeyCode.RightAlt: return kb.rightAltKey;
                case KeyCode.Space: return kb.spaceKey;
                case KeyCode.Return: return kb.enterKey;
                case KeyCode.Backspace: return kb.backspaceKey;
                case KeyCode.Escape: return kb.escapeKey;

                case KeyCode.Comma: return kb.commaKey;
                case KeyCode.Period: return kb.periodKey;
                case KeyCode.Slash: return kb.slashKey;
                case KeyCode.Semicolon: return kb.semicolonKey;
                case KeyCode.Quote: return kb.quoteKey;
                case KeyCode.LeftBracket: return kb.leftBracketKey;
                case KeyCode.RightBracket: return kb.rightBracketKey;
                case KeyCode.Backslash: return kb.backslashKey;
                case KeyCode.Minus: return kb.minusKey;
                case KeyCode.Equals: return kb.equalsKey;
                case KeyCode.BackQuote: return kb.backquoteKey;

                default: return null;
            }
        }

        // ---------- Read-only API for UI building ----------

        public int GetSlotCount(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SLOT_COUNT) return 0;
            return slotCounts[slotIndex];
        }

        public GameObject GetSlotPrefab(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SLOT_COUNT) return null;
            return slotPrefabs[slotIndex].Prefab;
        }

        public bool IsSlotOccupied(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SLOT_COUNT) return false;
            return slotPrefabs[slotIndex].IsValid;
        }
        public KeyCode GetSlotHotkey(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= SLOT_COUNT) return KeyCode.None;
            return slotHotkeys[slotIndex];
        }

        public int SlotCount => SLOT_COUNT;

        /// <summary>
        /// Current number of live inventory-spawned objects in the world.
        /// Always 0 when the cap is disabled (maxLiveSpawnedInstances == 0).
        /// </summary>
        public int LiveSpawnedCount => _liveSpawnedCount;

        /// <summary>
        /// The configured live spawn cap. 0 means unlimited.
        /// </summary>
        public int MaxLiveSpawnedInstances => maxLiveSpawnedInstances;
    }

    /// <summary>
    /// Internal helper attached to inventory-spawned instances to notify the
    /// inventory when one is destroyed, so the live spawn count stays accurate.
    /// Only attached when U3DInventory's live spawn cap is enabled.
    /// Not intended for direct use by creators.
    /// </summary>
    public class U3DInventorySpawnTracker : MonoBehaviour
    {
        private U3DInventory _inventory;

        public void Initialize(U3DInventory inventory)
        {
            _inventory = inventory;
        }

        void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnLiveSpawnedInstanceDestroyed();
        }
    }
}