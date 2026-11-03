using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using U3D.Input;

namespace U3D.UI
{
    /// <summary>
    /// The player menu bar: the local player's inventory slots, each showing the item's name,
    /// stack count and hotkey. Pressing a slot uses it exactly as its hotkey does. On desktop the
    /// bar sits at bottom centre; on touch devices it sits at top centre below the Settings chip.
    /// It shows one row of slots when one row fits at a usable size and two rows otherwise.
    /// Dragging the title toward the screen edge, or tapping it, collapses the bar to a chip
    /// showing only the title, styled like the Settings chip; dragging or tapping the chip opens
    /// it again. Hidden when the scene has no U3DInventory.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DPlayerMenu : MonoBehaviour
    {
        private const string OpenPrefKey = "U3D_PlayerMenuOpen";

        [Header("Parts")]
        [SerializeField] private RectTransform bar;
        [Tooltip("Shown instead of the bar while it is collapsed.")]
        [SerializeField] private RectTransform collapsedChip;
        [Tooltip("Text on the collapsed chip. Follows the bar's title.")]
        [SerializeField] private TextMeshProUGUI collapsedLabel;
        [SerializeField] private GridLayoutGroup slotGrid;
        [SerializeField] private U3DPlayerMenuSlot[] slots = new U3DPlayerMenuSlot[U3DInventory.SLOT_COUNT];
        [SerializeField] private TextMeshProUGUI titleLabel;
        [Tooltip("Arrows that switch between Inventory and Emotes. Hidden until Emotes exist.")]
        [SerializeField] private GameObject modeArrows;

        [Header("Layout (screen points)")]
        [Tooltip("Slot width and height on desktop.")]
        [SerializeField] private float desktopSlotSize = 64f;
        [Tooltip("Slot width and height on touch devices. Keep at 44 or more so a fingertip can hit it.")]
        [SerializeField] private float touchSlotSize = 56f;
        [Tooltip("Gap between the bar and the screen edge it sits against, and the margin kept at each side.")]
        [SerializeField] private float edgeMargin = 12f;
        [Tooltip("Distance from the top of the screen to the bar on touch devices, below any notch or camera cutout. Leaves room for the Settings chip above it.")]
        [SerializeField] private float touchTopOffset = 56f;

        [Header("Collapse")]
        [SerializeField] private bool startOpen = true;
        [Tooltip("How far, in screen points, a drag on the title or the collapsed chip must travel to collapse or open the bar.")]
        [SerializeField] private float dragThreshold = 24f;

        private const float InventorySearchInterval = 1f;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private U3DInventory _inventory;
        private float _nextInventorySearch;
        private bool _open;
        private bool _layoutApplied;
        private bool _touchLayout;
        private int _columns;
        private float _topInset;
        private float _dragAccumulated;

        void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas != null) _canvasRect = _canvas.transform as RectTransform;

            _open = PlayerPrefs.GetInt(OpenPrefKey, startOpen ? 1 : 0) == 1;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null || slots[i].Button == null) continue;
                int index = i;
                slots[i].Button.onClick.AddListener(() => OnSlotPressed(index));
            }

            if (modeArrows != null) modeArrows.SetActive(false);
            if (collapsedLabel != null && titleLabel != null) collapsedLabel.text = titleLabel.text;

            ApplyVisibility();
        }

        void OnEnable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged += ForceLayout;
            ForceLayout();
        }

        void OnDisable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged -= ForceLayout;
        }

        void OnDestroy()
        {
            if (_inventory != null)
                _inventory.OnSlotChanged.RemoveListener(OnSlotChanged);
        }

        void Update()
        {
            if (_inventory == null && Time.unscaledTime >= _nextInventorySearch)
            {
                _nextInventorySearch = Time.unscaledTime + InventorySearchInterval;
                U3DInventory found = FindAnyObjectByType<U3DInventory>();
                if (found != null) BindInventory(found);
            }

            UpdateLayout(false);
        }

        private void BindInventory(U3DInventory inventory)
        {
            _inventory = inventory;
            _inventory.OnSlotChanged.AddListener(OnSlotChanged);
            RefreshAllSlots();
            ApplyVisibility();
        }

        // ---------- Slots ----------

        private void OnSlotPressed(int index)
        {
            if (_inventory != null)
                _inventory.UseSlot(index);
        }

        private void OnSlotChanged(int index, GameObject prefab, int count)
        {
            RefreshSlot(index);
        }

        private void RefreshAllSlots()
        {
            for (int i = 0; i < slots.Length; i++)
                RefreshSlot(i);
        }

        private void RefreshSlot(int index)
        {
            if (index < 0 || index >= slots.Length || slots[index] == null) return;

            string key = _inventory != null ? KeyLabel(_inventory.GetSlotHotkey(index)) : string.Empty;
            GameObject prefab = _inventory != null ? _inventory.GetSlotPrefab(index) : null;

            if (prefab == null)
                slots[index].Show(string.Empty, 0, key);
            else
                slots[index].Show(prefab.name, _inventory.GetSlotCount(index), key);
        }

        private static string KeyLabel(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((int)(key - KeyCode.Alpha0)).ToString();
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return ((int)(key - KeyCode.Keypad0)).ToString();
            if (key == KeyCode.None)
                return string.Empty;
            return key.ToString();
        }

        // ---------- Open and collapse ----------

        /// <summary>Opens the bar or collapses it to the chip. Remembered for the next visit.</summary>
        public void SetOpen(bool open)
        {
            _open = open;
            PlayerPrefs.SetInt(OpenPrefKey, open ? 1 : 0);
            PlayerPrefs.Save();
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            bool hasContent = _inventory != null;
            if (bar != null) bar.gameObject.SetActive(hasContent && _open);
            if (collapsedChip != null) collapsedChip.gameObject.SetActive(hasContent && !_open);
        }

        /// <summary>EventTrigger target on the title and the collapsed chip.</summary>
        public void OnHandleBeginDrag(BaseEventData data)
        {
            _dragAccumulated = 0f;
        }

        /// <summary>EventTrigger target on the title and the collapsed chip.</summary>
        public void OnHandleDrag(BaseEventData data)
        {
            if (data is PointerEventData pointer)
            {
                float scale = _canvas != null && _canvas.scaleFactor > 0f ? _canvas.scaleFactor : 1f;
                _dragAccumulated += pointer.delta.y / scale;
            }
        }

        /// <summary>
        /// EventTrigger target on the title and the collapsed chip. Dragging toward the edge the bar sits
        /// against collapses it; dragging away from that edge opens it.
        /// </summary>
        public void OnHandleEndDrag(BaseEventData data)
        {
            float towardEdge = _touchLayout ? _dragAccumulated : -_dragAccumulated;

            if (_open && towardEdge >= dragThreshold)
                SetOpen(false);
            else if (!_open && -towardEdge >= dragThreshold)
                SetOpen(true);

            _dragAccumulated = 0f;
        }

        /// <summary>EventTrigger target on the title and the collapsed chip. A tap or click toggles.</summary>
        public void OnHandleClick(BaseEventData data)
        {
            SetOpen(!_open);
        }

        // ---------- Layout ----------

        private void ForceLayout()
        {
            UpdateLayout(true);
        }

        private void UpdateLayout(bool force)
        {
            if (slotGrid == null || _canvasRect == null) return;

            bool touch = U3DSimpleTouchZones.ShouldShowTouchHints;
            float size = touch ? touchSlotSize : desktopSlotSize;

            float available = _canvasRect.rect.width - 2f * edgeMargin - BarHorizontalPadding();
            float oneRowWidth = U3DInventory.SLOT_COUNT * size
                                + (U3DInventory.SLOT_COUNT - 1) * slotGrid.spacing.x
                                + slotGrid.padding.left + slotGrid.padding.right;
            int columns = oneRowWidth <= available ? U3DInventory.SLOT_COUNT : U3DInventory.SLOT_COUNT / 2;
            float topInset = touch ? U3DCanvasCssScaler.SafeAreaTopCss : 0f;

            if (!force && _layoutApplied && touch == _touchLayout && columns == _columns
                && Mathf.Approximately(slotGrid.cellSize.x, size) && Mathf.Approximately(topInset, _topInset))
                return;

            _layoutApplied = true;
            _touchLayout = touch;
            _columns = columns;
            _topInset = topInset;

            slotGrid.cellSize = new Vector2(size, size);
            slotGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            slotGrid.constraintCount = columns;

            if (touch)
            {
                PlaceAtEdge(bar, 1f, -(touchTopOffset + topInset));
                PlaceAtEdge(collapsedChip, 1f, -(touchTopOffset + topInset));
            }
            else
            {
                PlaceAtEdge(bar, 0f, edgeMargin);
                PlaceAtEdge(collapsedChip, 0f, edgeMargin);
            }
        }

        private float BarHorizontalPadding()
        {
            if (bar == null) return 0f;
            var group = bar.GetComponent<LayoutGroup>();
            return group != null ? group.padding.left + group.padding.right : 0f;
        }

        private static void PlaceAtEdge(RectTransform rect, float edgeY, float offsetY)
        {
            if (rect == null) return;
            rect.anchorMin = new Vector2(0.5f, edgeY);
            rect.anchorMax = new Vector2(0.5f, edgeY);
            rect.pivot = new Vector2(0.5f, edgeY);
            rect.anchoredPosition = new Vector2(0f, offsetY);
        }
    }
}
