using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
using U3D.Input;
using U3D.XR;

namespace U3D.UI
{
    /// <summary>
    /// The player menu bar. It offers up to two menus in the same ten slots: Inventory, where each
    /// slot shows an item's name, stack count and hotkey and pressing it uses the item exactly as
    /// its hotkey does, and Emotes, where each slot shows an emote and its key and pressing it sends
    /// that emote to everyone in the room. Menus decides which the bar offers. With both, the arrows
    /// beside the title switch between them, Emotes shows first, and the bar remembers the menu the
    /// player last chose. Inventory needs a U3DInventory in the scene and Emotes needs the local
    /// player's U3DEmote; the bar is hidden while the menu it would show has neither.
    ///
    /// The slot keys (the Inventory's hotkeys, or 1 to 0 without an Inventory) act on whichever menu
    /// the bar shows, including while it is collapsed. The bar reads them itself and the Inventory
    /// stops reading them while the bar is active.
    ///
    /// On desktop the bar sits at bottom centre. On touch devices it sits at top centre, below the
    /// Settings chip when the scene has a Settings UI. It shows one row of slots when one row fits at
    /// a usable size and two rows otherwise. Dragging the title toward the screen edge, or tapping
    /// it, collapses the bar to a chip showing only the title; dragging or tapping the chip opens it
    /// again. In VR the canvas is held on the left wrist by U3DWristMount, below the Settings chip,
    /// as two rows of five. On touch and in VR, while the Settings panel is open the bar shows only
    /// its chip, below the panel, and returns as it was when Settings closes.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DPlayerMenu : MonoBehaviour
    {
        public enum Menus
        {
            Inventory = 0,
            Emotes = 1,
            [InspectorName("Inventory and Emotes")] InventoryAndEmotes = 2
        }

        private enum Page
        {
            Inventory = 0,
            Emotes = 1
        }

        private const string OpenPrefKey = "U3D_PlayerMenuOpen";
        private const string PagePrefKey = "U3D_PlayerMenuPage";
        private const string InventoryTitle = "Inventory";
        private const string EmotesTitle = "Emotes";

        [Header("Menus")]
        [Tooltip("Which menus the bar offers. With both, players switch between them with the arrows beside the title. Inventory appears only when the scene has an Inventory.")]
        [SerializeField] private Menus menus = Menus.InventoryAndEmotes;

        [Header("Parts")]
        [SerializeField] private RectTransform bar;
        [Tooltip("Shown instead of the bar while it is collapsed.")]
        [SerializeField] private RectTransform collapsedChip;
        [Tooltip("Text on the collapsed chip. Follows the bar's title.")]
        [SerializeField] private TextMeshProUGUI collapsedLabel;
        [SerializeField] private GridLayoutGroup slotGrid;
        [SerializeField] private U3DPlayerMenuSlot[] slots = new U3DPlayerMenuSlot[U3DInventory.SLOT_COUNT];
        [SerializeField] private TextMeshProUGUI titleLabel;
        [Tooltip("Arrows that switch between Inventory and Emotes. Shown only when the bar offers both.")]
        [SerializeField] private GameObject modeArrows;

        [Header("Layout (screen points)")]
        [Tooltip("Slot width and height on desktop.")]
        [SerializeField] private float desktopSlotSize = 64f;
        [Tooltip("Slot width and height on touch devices and in VR. Keep at 44 or more so a fingertip can hit it.")]
        [SerializeField] private float touchSlotSize = 56f;
        [Tooltip("Gap between the bar and the screen edge it sits against, and the margin kept at each side.")]
        [SerializeField] private float edgeMargin = 12f;
        [Tooltip("Distance from the top of the screen to the bar on touch devices, below any notch or camera cutout, when the scene has a Settings UI. Leaves room for the Settings chip above it.")]
        [SerializeField] private float touchTopOffset = 56f;

        [Header("Collapse")]
        [SerializeField] private bool startOpen = true;
        [Tooltip("How far, in screen points, a drag on the title or the collapsed chip must travel to collapse or open the bar.")]
        [SerializeField] private float dragThreshold = 24f;

        private const float SearchInterval = 1f;
        private const float VRTitleAllowance = 64f;

        private Canvas _canvas;
        private RectTransform _canvasRect;
        private U3DInventory _inventory;
        private U3DEmote _emote;
        private bool _emoteBound;
        private bool _emoteMissing;
        private float _nextInventorySearch;
        private float _nextEmoteSearch;
        private bool _open;
        private Page _page;
        private bool _hasSettings;
        private bool _layoutApplied;
        private bool _touchLayout;
        private bool _vrLayout;
        private bool _inVR;
        private bool _settingsOpen;
        private float _settingsBottom;
        private int _columns;
        private float _topInset;
        private float _dragAccumulated;

        private Canvas RootCanvas => _canvas != null ? _canvas.rootCanvas : null;

        private bool CompactLayout => _inVR || U3DSimpleTouchZones.ShouldShowTouchHints;

        private bool InventoryAvailable => menus != Menus.Emotes && _inventory != null;

        private bool EmotesAvailable => menus != Menus.Inventory && _emote != null && _emote.EmoteSet != null;

        private bool EmotesPending => menus != Menus.Inventory && _emote == null && !_emoteMissing;

        private Page ShownPage
        {
            get
            {
                if (_page == Page.Emotes && (EmotesAvailable || EmotesPending)) return Page.Emotes;
                if (InventoryAvailable) return Page.Inventory;
                return Page.Emotes;
            }
        }

        /// <summary>Which menus the bar offers.</summary>
        public Menus ShownMenus => menus;

        void Awake()
        {
            _canvas = GetComponentInParent<Canvas>();
            if (_canvas != null) _canvasRect = _canvas.transform as RectTransform;

            _open = PlayerPrefs.GetInt(OpenPrefKey, startOpen ? 1 : 0) == 1;
            _page = PlayerPrefs.GetInt(PagePrefKey, (int)Page.Emotes) == (int)Page.Inventory ? Page.Inventory : Page.Emotes;
            _hasSettings = FindAnyObjectByType<U3DSettingsUI>(FindObjectsInactive.Include) != null;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null || slots[i].Button == null) continue;
                int index = i;
                slots[i].Button.onClick.AddListener(() => OnSlotPressed(index));
            }

            if (modeArrows != null)
            {
                foreach (Button arrow in modeArrows.GetComponentsInChildren<Button>(true))
                    arrow.onClick.AddListener(SwitchPage);
            }

            Refresh();
        }

        void OnEnable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged += OnTouchHintsChanged;
            U3DWebXRManager.OnVRModeChanged += OnVRModeChanged;
            U3DSettingsUI.PanelOpenChanged += OnSettingsPanelChanged;
            if (_inventory != null) _inventory.HotkeysClaimed = true;
            if (U3DWebXRManager.Instance != null && U3DWebXRManager.Instance.IsVRActive)
                OnVRModeChanged(true);
            ForceLayout();
        }

        void OnDisable()
        {
            U3DSimpleTouchZones.TouchHintsVisibilityChanged -= OnTouchHintsChanged;
            U3DWebXRManager.OnVRModeChanged -= OnVRModeChanged;
            U3DSettingsUI.PanelOpenChanged -= OnSettingsPanelChanged;
            if (_inventory != null) _inventory.HotkeysClaimed = false;
            if (_inVR)
            {
                _inVR = false;
                U3DWristMount.Release(RootCanvas);
            }
        }

        void OnDestroy()
        {
            if (_inventory != null)
            {
                _inventory.HotkeysClaimed = false;
                _inventory.OnSlotChanged.RemoveListener(OnSlotChanged);
            }
        }

        void Update()
        {
            if (_inventory == null && Time.unscaledTime >= _nextInventorySearch)
            {
                _nextInventorySearch = Time.unscaledTime + SearchInterval;
                U3DInventory found = FindAnyObjectByType<U3DInventory>();
                if (found != null) BindInventory(found);
            }

            if (_emoteBound && _emote == null)
            {
                _emoteBound = false;
                _emoteMissing = false;
                Refresh();
            }

            if (_emote == null && menus != Menus.Inventory && Time.unscaledTime >= _nextEmoteSearch)
            {
                _nextEmoteSearch = Time.unscaledTime + SearchInterval;
                U3DPlayerController player = U3DPlayerController.FindLocalPlayer();
                if (player != null)
                {
                    U3DEmote emote = player.GetComponent<U3DEmote>();
                    if (emote != null && emote.EmoteSet != null)
                    {
                        BindEmotes(emote);
                    }
                    else if (!_emoteMissing)
                    {
                        _emoteMissing = true;
                        Refresh();
                    }
                }
            }

            PollHotkeys();
            UpdateLayout(false);
        }

        private void BindInventory(U3DInventory inventory)
        {
            _inventory = inventory;
            _inventory.OnSlotChanged.AddListener(OnSlotChanged);
            _inventory.HotkeysClaimed = isActiveAndEnabled;
            Refresh();
        }

        private void BindEmotes(U3DEmote emote)
        {
            _emote = emote;
            _emoteBound = true;
            _emoteMissing = false;
            Refresh();
        }

        private void OnTouchHintsChanged()
        {
            ApplyVisibility();
            ForceLayout();
        }

        // ---------- Keys ----------

        private void PollHotkeys()
        {
            if (Keyboard.current == null) return;
            if (!InventoryAvailable && !EmotesAvailable) return;

            for (int i = 0; i < U3DInventory.SLOT_COUNT; i++)
            {
                KeyControl key = U3DInventory.ResolveKeyControl(SlotHotkey(i));
                if (key != null && key.wasPressedThisFrame)
                    OnSlotPressed(i);
            }
        }

        private KeyCode SlotHotkey(int index)
        {
            return _inventory != null ? _inventory.GetSlotHotkey(index) : U3DInventory.DefaultHotkey(index);
        }

        // ---------- VR ----------

        private void OnVRModeChanged(bool inVR)
        {
            if (inVR == _inVR) return;
            _inVR = inVR;

            if (inVR)
                U3DWristMount.Hold(RootCanvas, U3DWristMount.Slot.Lower, VRCanvasSize());
            else
                U3DWristMount.Release(RootCanvas);

            ApplyVisibility();
            ForceLayout();
        }

        private Vector2 VRCanvasSize()
        {
            int columns = U3DInventory.SLOT_COUNT / 2;
            float width = columns * touchSlotSize + (columns - 1) * slotGrid.spacing.x
                          + slotGrid.padding.horizontal + BarHorizontalPadding();
            float height = 2f * touchSlotSize + slotGrid.spacing.y + slotGrid.padding.vertical + VRTitleAllowance;

            if (collapsedChip != null)
                width = Mathf.Max(width, collapsedChip.rect.width);

            return new Vector2(width, height);
        }

        // ---------- Settings ----------

        private void OnSettingsPanelChanged(bool open, float panelBottom)
        {
            _settingsOpen = open;
            _settingsBottom = panelBottom;
            ApplyVisibility();
            ForceLayout();
        }

        // ---------- Menus ----------

        private void SwitchPage()
        {
            if (!InventoryAvailable || !EmotesAvailable) return;

            _page = ShownPage == Page.Inventory ? Page.Emotes : Page.Inventory;
            PlayerPrefs.SetInt(PagePrefKey, (int)_page);
            PlayerPrefs.Save();
            Refresh();
        }

        private void Refresh()
        {
            string title = ShownPage == Page.Emotes ? EmotesTitle : InventoryTitle;
            if (titleLabel != null) titleLabel.text = title;
            if (collapsedLabel != null) collapsedLabel.text = title;

            RefreshAllSlots();
            ApplyVisibility();
        }

        // ---------- Slots ----------

        private void OnSlotPressed(int index)
        {
            if (ShownPage == Page.Emotes)
            {
                if (EmotesAvailable) _emote.Send(index);
                return;
            }

            if (_inventory != null)
                _inventory.UseSlot(index);
        }

        private void OnSlotChanged(int index, GameObject prefab, int count)
        {
            if (ShownPage == Page.Inventory)
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

            if (ShownPage == Page.Emotes)
            {
                Sprite sprite = EmotesAvailable ? _emote.EmoteSet.GetSprite(index) : null;
                slots[index].Show(string.Empty, 0, sprite != null ? KeyLabel(SlotHotkey(index)) : string.Empty);
                slots[index].ShowIcon(sprite);
                return;
            }

            slots[index].ShowIcon(null);

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
            bool hasContent = ShownPage == Page.Emotes ? EmotesAvailable : InventoryAvailable;
            bool showBar = _open && !(_settingsOpen && CompactLayout);
            if (bar != null) bar.gameObject.SetActive(hasContent && showBar);
            if (collapsedChip != null) collapsedChip.gameObject.SetActive(hasContent && !showBar);
            if (modeArrows != null) modeArrows.SetActive(InventoryAvailable && EmotesAvailable);
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

        /// <summary>EventTrigger target on the title and the collapsed chip. A tap, click or VR press toggles.</summary>
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

            if (_inVR)
            {
                if (!force && _layoutApplied && _vrLayout) return;

                _layoutApplied = true;
                _vrLayout = true;
                _touchLayout = false;
                _columns = U3DInventory.SLOT_COUNT / 2;

                slotGrid.cellSize = new Vector2(touchSlotSize, touchSlotSize);
                slotGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                slotGrid.constraintCount = _columns;

                PlaceAtEdge(bar, 1f, 0f);
                PlaceAtEdge(collapsedChip, 1f, 0f);
                return;
            }

            bool touch = U3DSimpleTouchZones.ShouldShowTouchHints;
            float size = touch ? touchSlotSize : desktopSlotSize;

            float available = _canvasRect.rect.width - 2f * edgeMargin - BarHorizontalPadding();
            float oneRowWidth = U3DInventory.SLOT_COUNT * size
                                + (U3DInventory.SLOT_COUNT - 1) * slotGrid.spacing.x
                                + slotGrid.padding.left + slotGrid.padding.right;
            int columns = oneRowWidth <= available ? U3DInventory.SLOT_COUNT : U3DInventory.SLOT_COUNT / 2;
            float topInset = touch ? U3DCanvasCssScaler.SafeAreaTopCss : 0f;

            if (!force && _layoutApplied && !_vrLayout && touch == _touchLayout && columns == _columns
                && Mathf.Approximately(slotGrid.cellSize.x, size) && Mathf.Approximately(topInset, _topInset))
                return;

            _layoutApplied = true;
            _vrLayout = false;
            _touchLayout = touch;
            _columns = columns;
            _topInset = topInset;

            slotGrid.cellSize = new Vector2(size, size);
            slotGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            slotGrid.constraintCount = columns;

            if (touch)
            {
                float top = (_hasSettings ? touchTopOffset : edgeMargin) + topInset;
                if (_settingsOpen)
                    top = Mathf.Max(top, _settingsBottom + edgeMargin);
                PlaceAtEdge(bar, 1f, -top);
                PlaceAtEdge(collapsedChip, 1f, -top);
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