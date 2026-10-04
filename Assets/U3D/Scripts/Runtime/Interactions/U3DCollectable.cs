using System.ComponentModel;
using System.Xml.Linq;
using U3D.Net;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UIElements;

namespace U3D
{
    /// <summary>
    /// Add to any object that should be collectable into a player's Inventory.
    /// Pairs with U3DInventory in the scene.
    ///
    /// Collection methods:
    ///   OnEnter — picks up automatically when the player walks into the trigger.
    ///   OnInteract — picks up when the player presses the Interact key in range.
    ///
    /// Every collect is decided by one player in the room, so two people touching
    /// the same item cannot both receive it. Alone, that decision is instant.
    ///
    /// The world GameObject is never destroyed by this component. Wire OnCollected
    /// to SetActive(false) or Destroy() to make it disappear — that event fires on
    /// everyone's machine, so it disappears for everyone. OnCollectedByMe fires only
    /// for the player who got it, and is where score, sound and UI belong.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    [RequireComponent(typeof(NetEntity))]
    public class U3DCollectable : NetComponent, IU3DInteractable
    {
        public enum CollectionMethod { OnEnter, OnInteract }

        [Header("What Gets Collected")]
        [Tooltip("Prefab added to the collector's Inventory. Must be a prefab asset from your Project, and must carry a Net Entity so it can be created on every machine.")]
        [SerializeField] private NetPrefab prefabToCollect;

        [Tooltip("How many copies are added per successful collection.")]
        [Min(1)]
        [SerializeField] private int quantity = 1;

        [HideInInspector]
        [SerializeField] private CollectionMethod collectionMethod = CollectionMethod.OnEnter;

        [Header("Collection Trigger")]
        [Tooltip("Maximum distance for OnInteract collection (ignored for OnEnter). The U3DInteractionManager's overall interaction range still applies as a coarse outer filter.")]
        [SerializeField] private float interactDistance = 3f;

        [Header("Trigger Configuration")]
        [Tooltip("If enabled, this collectable can only be picked up once and then becomes inert.")]
        [SerializeField] private bool triggerOnce = false;

        [Tooltip("Seconds the collectable is inert after a successful pickup. 0 = no cooldown.")]
        [SerializeField] private float cooldownTime = 0f;

        [Tooltip("Only collect when the colliding object has a specific tag.")]
        [SerializeField] private bool requireTag = false;

        [Tooltip("Tag required to collect. Only checked when Require Tag is enabled.")]
        [SerializeField] private string requiredTag = "Player";

        [Header("Optional Label")]
        [Tooltip("Assign a U3DWorldspaceUI in your scene to show a label near this object. Edit the text on that object directly.")]
        public U3DWorldspaceUI labelUI;

        [Header("Events")]
        [Tooltip("Fired on EVERY player's machine when this is collected by anyone. Wire disappearance here — SetActive(false) or Destroy() — so the object goes away for everyone.")]
        public UnityEvent OnCollected;

        [Tooltip("Fired only for the player who collected it. The argument is that player's GameObject. Wire score, sound and UI here.")]
        public UnityEvent<GameObject> OnCollectedByMe;

        [Tooltip("Fired on every machine when a cooldown ends and this becomes collectable again. Wire the counterpart to whatever OnCollected did — SetActive(true), for instance.")]
        public UnityEvent OnAvailableAgain;

        [Tooltip("Fired when a collection attempt fails (cooldown, already collected, tag mismatch, inventory full, or losing a race to another player).")]
        public UnityEvent OnCollectFailed;

        [Tooltip("Fired when the local player enters interact range. OnInteract mode only.")]
        public UnityEvent OnPlayerEnterRangeEvent;

        [Tooltip("Fired when the local player exits interact range. OnInteract mode only.")]
        public UnityEvent OnPlayerExitRangeEvent;

        private bool _exhausted;
        private float _cooldownUntil;
        private bool _requestPending;
        private GameObject _pendingCollector;

        private NetMessage _collectRequest;
        private NetMessage _collectResult;
        private NetMessage _resetRequest;

        private Collider triggerCollider;
        private Transform playerTransform;
        private bool isInRange = false;
        private U3DInventory cachedInventory;

        private void Reset()
        {
            // Intentionally does NOT change the collider's isTrigger state. Forcing a trigger
            // silently removes a creator's intended blocking collider. The dashboard's two
            // "Make ... Collectable" tools set up the correct collider for each intent.
        }

        private void Awake()
        {
            triggerCollider = GetComponent<Collider>();
        }

        protected override void OnNetSpawn()
        {
            _collectRequest = RegisterMessage(NetKeys.CollectableRequest, HandleCollectRequest);
            _collectResult = RegisterMessage(NetKeys.CollectableResult, HandleCollectResult);
            _resetRequest = RegisterMessage(NetKeys.CollectableReset, HandleResetRequest);

            if (Session != null)
                Session.ReporterChanged += HandleReporterChanged;
        }

        protected override void OnNetDespawn()
        {
            if (Session != null)
                Session.ReporterChanged -= HandleReporterChanged;
        }

        /// <summary>
        /// The peer deciding collects has changed. A request we sent to the old one may never
        /// be answered, so stop waiting rather than blocking every future attempt.
        /// </summary>
        private void HandleReporterChanged(PeerId reporter)
        {
            if (!_requestPending) return;

            _requestPending = false;
            _pendingCollector = null;
            OnCollectFailed?.Invoke();
        }

        private void Update()
        {
            if (_cooldownUntil > 0f && Time.time >= _cooldownUntil)
            {
                _cooldownUntil = 0f;
                if (!_exhausted) OnAvailableAgain?.Invoke();
            }

            // Range tracking only matters for OnInteract mode.
            if (collectionMethod == CollectionMethod.OnInteract)
                UpdatePlayerProximity();
        }

        private void UpdatePlayerProximity()
        {
            if (playerTransform == null)
            {
                FindLocalPlayer();
                if (playerTransform == null) return;
            }

            float distance = Vector3.Distance(transform.position, playerTransform.position);
            bool wasInRange = isInRange;
            isInRange = distance <= interactDistance;

            if (isInRange && !wasInRange)
                OnPlayerEnterRangeEvent?.Invoke();
            else if (!isInRange && wasInRange)
                OnPlayerExitRangeEvent?.Invoke();
        }

        private void FindLocalPlayer()
        {
            U3DPlayerController controller = U3DPlayerController.FindLocalPlayer();
            if (controller != null)
                playerTransform = controller.transform;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (collectionMethod != CollectionMethod.OnEnter) return;

            if (!PassesTagCheck(other.transform)) return;

            // Only the local player triggers collection — remote-player overlaps must not
            // dispense items into the local inventory.
            U3DPlayerController controller = other.GetComponentInParent<U3DPlayerController>();
            if (controller == null || !controller.IsLocalPlayer) return;

            AttemptCollect(controller.gameObject);
        }

        // IU3DInteractable implementation
        public void OnInteract()
        {
            if (collectionMethod != CollectionMethod.OnInteract)
            {
                OnCollectFailed?.Invoke();
                return;
            }

            U3DPlayerController controller = U3DPlayerController.FindLocalPlayer();
            if (controller == null)
            {
                OnCollectFailed?.Invoke();
                return;
            }

            if (!PassesTagCheck(controller.transform))
            {
                OnCollectFailed?.Invoke();
                return;
            }

            AttemptCollect(controller.gameObject);
        }

        public void OnPlayerEnterRange() { }
        public void OnPlayerExitRange() { }

        public bool CanInteract()
        {
            if (collectionMethod != CollectionMethod.OnInteract) return false;
            if (!IsAvailable) return false;
            return isInRange;
        }

        public string GetInteractionPrompt()
        {
            return prefabToCollect.IsValid ? $"Collect {prefabToCollect.Prefab.name} (R)" : "Collect (R)";
        }

        /// <summary>
        /// Public entry point for a collect that isn't a player touch or an interact press —
        /// a button, a cutscene, a timer. The collector GameObject is passed to OnCollectedByMe.
        /// </summary>
        public void Collect(GameObject collector)
        {
            if (!PassesTagCheck(collector != null ? collector.transform : null))
            {
                OnCollectFailed?.Invoke();
                return;
            }

            AttemptCollect(collector);
        }

        private bool PassesTagCheck(Transform candidate)
        {
            if (!requireTag) return true;
            if (candidate == null) return false;
            return candidate.CompareTag(requiredTag);
        }

        /// <summary>
        /// Runs every check this machine can answer alone, then asks the deciding peer.
        /// Nothing enters the inventory here; that happens when the answer arrives.
        /// </summary>
        private void AttemptCollect(GameObject collectingPlayer)
        {
            if (!IsLive)
            {
                OnCollectFailed?.Invoke();
                return;
            }

            if (_requestPending) return;

            if (!IsAvailable)
            {
                OnCollectFailed?.Invoke();
                return;
            }

            if (!prefabToCollect.IsValid)
            {
                Debug.LogWarning($"U3DCollectable on '{name}': Prefab To Collect is not assigned — nothing to add to inventory.", this);
                OnCollectFailed?.Invoke();
                return;
            }

            U3DInventory inventory = FindInventory();
            if (inventory == null)
            {
                Debug.LogWarning($"U3DCollectable on '{name}': No U3DInventory found in scene. Add one via Creator Dashboard → Game Systems → Add Inventory.", this);
                OnCollectFailed?.Invoke();
                return;
            }

            if (!inventory.CanAccept(prefabToCollect))
            {
                OnCollectFailed?.Invoke();
                return;
            }

            // Set before sending: when this peer is the decider the answer arrives
            // synchronously inside the send call, before the next line would run.
            _requestPending = true;
            _pendingCollector = collectingPlayer;
            _collectRequest.SendToReporter();
        }

        /// <summary>
        /// Runs only on the deciding peer. Ignores anything it cannot honour, per G27.
        /// State is not written here — it is written by the result handler below, which
        /// this peer also receives, so there is one write path on every machine.
        /// </summary>
        private void HandleCollectRequest(PeerId sender, object[] args)
        {
            if (Session == null || !Session.IsReporter) return;
            if (!sender.IsValid) return;
            if (!IsAvailable) return;

            _collectResult.SendToAll(sender);
        }

        /// <summary>
        /// Runs on every peer. A valid winner means a collect happened; PeerId.None means
        /// the collectable was reset and is available again.
        /// </summary>
        private void HandleCollectResult(PeerId sender, object[] args)
        {
            if (Session == null) return;
            if (sender != Session.Reporter) return;
            if (args == null || args.Length < 1) return;
            if (!(args[0] is PeerId winner)) return;

            if (!winner.IsValid)
            {
                ApplyReset();
                return;
            }

            MarkCollected();
            OnCollected?.Invoke();

            bool iWon = winner == Session.LocalPeer;

            if (_requestPending)
            {
                _requestPending = false;
                if (!iWon)
                {
                    _pendingCollector = null;
                    OnCollectFailed?.Invoke();
                }
            }

            if (!iWon) return;

            U3DInventory inventory = FindInventory();
            if (inventory != null)
                inventory.AddItem(prefabToCollect, quantity);

            OnCollectedByMe?.Invoke(_pendingCollector);
            _pendingCollector = null;
        }

        /// <summary>
        /// Runs only on the deciding peer, for a creator-wired reset from any machine.
        /// </summary>
        private void HandleResetRequest(PeerId sender, object[] args)
        {
            if (Session == null || !Session.IsReporter) return;
            _collectResult.SendToAll(PeerId.None);
        }

        /// <summary>
        /// Marks this collectable spent. Every peer runs this from the same event, and each
        /// times its own cooldown from the moment the news arrived. No time value is ever
        /// transmitted or compared between machines.
        /// </summary>
        private void MarkCollected()
        {
            if (triggerOnce) _exhausted = true;
            if (cooldownTime > 0f) _cooldownUntil = Time.time + cooldownTime;
        }

        private void ApplyReset()
        {
            bool wasUnavailable = !IsAvailable;

            _exhausted = false;
            _cooldownUntil = 0f;

            if (wasUnavailable) OnAvailableAgain?.Invoke();
        }

        private U3DInventory FindInventory()
        {
            if (cachedInventory != null) return cachedInventory;
            cachedInventory = UnityEngine.Object.FindAnyObjectByType<U3DInventory>();
            return cachedInventory;
        }

        private void OnValidate()
        {
            if (cooldownTime < 0f) cooldownTime = 0f;
            if (interactDistance < 0f) interactDistance = 0f;
            if (quantity < 1) quantity = 1;
        }

        // Public API

        /// <summary>
        /// Makes this collectable available again on every machine. Safe to call from any
        /// peer; the request is routed to whoever is deciding collects.
        /// </summary>
        public void ResetCollectable()
        {
            if (!IsLive) return;
            _resetRequest.SendToReporter();
        }

        public void SetCollectionMethod(CollectionMethod method)
        {
            collectionMethod = method;
        }

        public bool IsAvailable => !_exhausted && Time.time >= _cooldownUntil;
        public bool IsInRange => isInRange;
        public NetPrefab PrefabToCollect { get => prefabToCollect; set => prefabToCollect = value; }
        public int Quantity { get => quantity; set => quantity = Mathf.Max(1, value); }
    }
}