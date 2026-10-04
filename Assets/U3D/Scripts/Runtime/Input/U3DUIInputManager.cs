using UnityEngine;
using UnityEngine.UI;
using TMPro;
using U3D.Input;

/// <summary>
/// Universal UI input manager for any interactive UI component.
/// Automatically detects and manages input focus for all UI types.
/// </summary>
public class U3DUIInputManager : MonoBehaviour, IUIInputHandler
{
    [Header("UI Component Detection")]
    public string componentName = "UI Component";
    [SerializeField] private bool autoDetectInputFields = true;
    [SerializeField] private bool autoDetectButtons = true;
    [SerializeField] private bool autoDetectScrollViews = true;
    [SerializeField] private bool autoDetectDropdowns = true;

    [Header("Input Behavior")]
    public int inputPriority = 0;
    public bool blockAllInput = false;
    [SerializeField] private bool onlyWhenVisible = true;

    [Header("Manual UI References")]
    [SerializeField] private Selectable[] manualSelectables;
    [SerializeField] private TMP_InputField[] manualInputFields;
    [SerializeField] private ScrollRect[] manualScrollRects;

    // Auto-detected components
    private Selectable[] _autoSelectables;
    private TMP_InputField[] _autoInputFields;
    private ScrollRect[] _autoScrollRects;
    private TMP_Dropdown[] _autoDropdowns;

    // Cached visibility hierarchy. GetComponentsInParent includes this object, so these
    // cover both self and ancestors in one array each.
    private Canvas[] _cachedCanvases;
    private CanvasGroup[] _cachedCanvasGroups;
    private bool _hierarchyCached = false;

    private U3DPlayerInput _playerInput;
    private bool _registeredWithInput = false;
    private bool _wasVisible = false;

    void Start()
    {
        AutoDetectUIComponents();
        CacheVisibilityHierarchy();
        RegisterWithPlayerInput();
    }

    void Update()
    {
        // Handle visibility changes for dynamic UI
        bool isCurrentlyVisible = IsUIVisible();
        if (isCurrentlyVisible != _wasVisible)
        {
            _wasVisible = isCurrentlyVisible;

            if (isCurrentlyVisible)
            {
                RegisterWithPlayerInput();
            }
            else
            {
                UnregisterFromPlayerInput();
            }
        }
    }

    void AutoDetectUIComponents()
    {
        if (autoDetectInputFields)
        {
            _autoInputFields = GetComponentsInChildren<TMP_InputField>(true);
        }

        if (autoDetectButtons)
        {
            _autoSelectables = GetComponentsInChildren<Selectable>(true);
        }

        if (autoDetectScrollViews)
        {
            _autoScrollRects = GetComponentsInChildren<ScrollRect>(true);
        }

        if (autoDetectDropdowns)
        {
            _autoDropdowns = GetComponentsInChildren<TMP_Dropdown>(true);
        }
    }

    /// <summary>
    /// Captures every Canvas and CanvasGroup from this object up through its ancestors.
    /// Cached because IsUIVisible runs twice per frame per panel (once from Update, once
    /// through IsUIFocused during the input focus sweep), and the whole chain is captured
    /// rather than only the nearest one so a panel nested inside a faded-out CanvasGroup
    /// correctly reports itself hidden instead of continuing to block player input.
    /// </summary>
    private void CacheVisibilityHierarchy()
    {
        _cachedCanvases = GetComponentsInParent<Canvas>(true);
        _cachedCanvasGroups = GetComponentsInParent<CanvasGroup>(true);
        _hierarchyCached = true;
    }

    private void RegisterWithPlayerInput()
    {
        if (_registeredWithInput) return;

        _playerInput = U3DPlayerInput.Instance;
        if (_playerInput != null)
        {
            _playerInput.RegisterUIInputHandler(this);
            _registeredWithInput = true;
        }
    }

    private void UnregisterFromPlayerInput()
    {
        if (!_registeredWithInput) return;

        if (_playerInput != null)
        {
            _playerInput.UnregisterUIInputHandler(this);
            _registeredWithInput = false;
        }
    }

    private bool IsUIVisible()
    {
        if (!onlyWhenVisible) return true;

        if (!gameObject.activeInHierarchy) return false;

        if (!_hierarchyCached) CacheVisibilityHierarchy();

        if (_cachedCanvases != null)
        {
            foreach (var canvas in _cachedCanvases)
            {
                if (canvas != null && !canvas.enabled) return false;
            }
        }

        if (_cachedCanvasGroups != null)
        {
            foreach (var group in _cachedCanvasGroups)
            {
                if (group != null && group.alpha <= 0f) return false;
            }
        }

        return true;
    }

    // IUIInputHandler implementation
    public bool IsUIFocused()
    {
        if (onlyWhenVisible && !IsUIVisible()) return false;

        // Check manual references first
        if (IsAnyComponentFocused(manualInputFields)) return true;
        if (IsAnyComponentFocused(manualSelectables)) return true;
        if (IsAnyComponentFocused(manualScrollRects)) return true;

        // Check auto-detected components
        if (IsAnyComponentFocused(_autoInputFields)) return true;
        if (IsAnyComponentFocused(_autoSelectables)) return true;
        if (IsAnyComponentFocused(_autoScrollRects)) return true;
        if (IsAnyComponentFocused(_autoDropdowns)) return true;

        return false;
    }

    private bool IsAnyComponentFocused<T>(T[] components) where T : Component
    {
        if (components == null) return false;

        foreach (var component in components)
        {
            if (component == null || !component.gameObject.activeInHierarchy) continue;

            switch (component)
            {
                case TMP_InputField inputField:
                    if (inputField.isFocused) return true;
                    break;

                case TMP_Dropdown dropdown: // CHECK DROPDOWN FIRST (before Selectable)
                    if (dropdown.IsExpanded) return true;
                    break;

                case Selectable selectable: // Check other selectables after dropdown
                    if (UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject == selectable.gameObject)
                        return true;
                    break;

                case ScrollRect scrollRect:
                    if (scrollRect.velocity.magnitude > 0.1f) return true;
                    break;
            }
        }

        return false;
    }

    public string GetHandlerName() => componentName;
    public int GetInputPriority() => inputPriority;
    public bool ShouldBlockAllInput() => blockAllInput;

    void OnDestroy()
    {
        UnregisterFromPlayerInput();
    }

    // Editor utilities
    [ContextMenu("Refresh Auto-Detection")]
    public void RefreshAutoDetection()
    {
        AutoDetectUIComponents();
        CacheVisibilityHierarchy();
    }

    [ContextMenu("Test Focus Detection")]
    public void TestFocusDetection()
    {
        bool isFocused = IsUIFocused();
        Debug.Log($"{componentName} focus test: {(isFocused ? "FOCUSED" : "not focused")}");
    }
}