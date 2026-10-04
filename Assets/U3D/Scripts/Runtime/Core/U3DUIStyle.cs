using TMPro;
using UnityEngine;
using UnityEngine.UI;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace U3D
{
    /// <summary>
    /// Central style helper for all U3D-created UI.
    /// Every method either sets a small, explicit subset of visual values or strips a sprite.
    /// Anything not explicitly set here is left at Unity's default — including raycastTarget.
    ///
    /// Raycast target behavior is the calling tool's responsibility. These style methods
    /// do not touch raycastTarget. The calling tool is the only code that knows whether
    /// a given text element is decorative (clicks pass through), part of a button
    /// (clicks land on the Button parent), or interactive in its own right.
    ///
    /// The goal of this helper is to keep U3D tool-created UI visually consistent
    /// by routing every tool through the same style methods, rather than hardcoding
    /// font sizes and colors in each tool. See the UI Creation Methods Reference
    /// (U3D-Reference-Router) for the rules this helper encodes and when to deviate.
    /// </summary>
    public static class U3DUIStyle
    {
        // -----------------------------──────────────────
        // Text Colors
        // -----------------------------──────────────────

        /// <summary>Dark gray used for all title, body, status, and button text. Reads well against Unity's default panel.</summary>
        public static readonly Color32 TextColor = new Color32(50, 50, 50, 255);

        /// <summary>Muted gray used for input field placeholder text.</summary>
        public static readonly Color32 PlaceholderColor = new Color32(150, 150, 150, 128);

        // -----------------------------──────────────────
        // Surface Color Hierarchy
        //
        // Three tiers of grayscale, each darker than the surface it sits on:
        //   - Panel (Unity default white, 255) — the main surface
        //   - InsetSurface (220) — recessed areas: input fields, slider tracks, scrollbar tracks
        //   - ButtonNormal (240) — interactive controls: buttons, slider knobs, scrollbar thumbs
        //
        // ButtonNormal sits between Panel and InsetSurface so interactive controls read against
        // either context. The contrast is intentionally subtle — controls are defined by their
        // edges rather than by saturated color, matching U3D's flat aesthetic.
        // -----------------------------──────────────────

        /// <summary>Slightly off-white tint for buttons, slider knobs, and scrollbar thumbs — the "draggable/clickable" surface tier. Matches across all interactive controls for consistent visual language.</summary>
        public static readonly Color32 ButtonNormalColor = new Color32(240, 240, 240, 255);

        /// <summary>Slightly darker tint for input field backgrounds, slider tracks, and scrollbar tracks — the "inset surface" tier. These are recessed areas that contain or are traveled by interactive controls.</summary>
        public static readonly Color32 InsetSurfaceColor = new Color32(220, 220, 220, 255);

        // -----------------------------──────────────────
        // Font Sizes
        // -----------------------------──────────────────

        public const float TitleFontSize = 16f;
        public const float BodyFontSize = 18f;
        public const float StatusFontSize = 10f;
        public const float ButtonFontSize = 14f;

        // -----------------------------──────────────────
        // Worldspace Canvas Defaults
        // -----------------------------──────────────────

        /// <summary>Default scale for worldspace UI canvases created via U3D tools.</summary>
        public const float WorldspaceCanvasScale = 0.01f;

        /// <summary>Default sizeDelta for a single-purpose worldspace UI (like the Worldspace UI sign tool).</summary>
        public static readonly Vector2 WorldspaceSingleElementSize = new Vector2(180, 100);

        // -----------------------------──────────────────
        // Flat Shape Sprite Paths (Editor-Only Loading)
        //
        // U3D's flat-shape sprites used for scrollbars, sliders, input fields, and
        // any other interactive control that needs a visible affordance without
        // Unity's skeuomorphic default sprites. Loaded via AssetDatabase at
        // editor-tool time; runtime callers will get null and a logged warning.
        // -----------------------------──────────────────

        private const string FlatSquareSpritePath = "Assets/U3D/U3D_Assets/UI/U3D_FlatSquare.png";
        private const string FlatCircleSpritePath = "Assets/U3D/U3D_Assets/UI/U3D_FlatCircle.png";

        /// <summary>
        /// Load U3D's flat square sprite, used for scrollbar thumbs, slider tracks/fills, and input field backgrounds.
        /// Editor-only: runtime callers get null and a warning. The sprite must exist at the documented path.
        /// </summary>
        public static Sprite GetFlatSquareSprite()
        {
#if UNITY_EDITOR
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(FlatSquareSpritePath);
            if (sprite == null)
                Debug.LogWarning($"U3DUIStyle: Flat square sprite not found at {FlatSquareSpritePath}. Make sure the U3D template has not been moved or renamed.");
            return sprite;
#else
            Debug.LogWarning("U3DUIStyle.GetFlatSquareSprite() called at runtime. Flat shape sprites are loaded via AssetDatabase and only available in the editor.");
            return null;
#endif
        }

        /// <summary>
        /// Load U3D's flat circle sprite, used for slider knobs.
        /// Editor-only: runtime callers get null and a warning. The sprite must exist at the documented path.
        /// </summary>
        public static Sprite GetFlatCircleSprite()
        {
#if UNITY_EDITOR
            Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(FlatCircleSpritePath);
            if (sprite == null)
                Debug.LogWarning($"U3DUIStyle: Flat circle sprite not found at {FlatCircleSpritePath}. Make sure the U3D template has not been moved or renamed.");
            return sprite;
#else
            Debug.LogWarning("U3DUIStyle.GetFlatCircleSprite() called at runtime. Flat shape sprites are loaded via AssetDatabase and only available in the editor.");
            return null;
#endif
        }

        // -----------------------------──────────────────
        // Panel Styling
        // -----------------------------──────────────────

        /// <summary>
        /// Strip the sprite off a panel or button Image so it renders as a flat rectangle.
        /// Unity's default DefaultControls.CreatePanel with an empty Resources struct already
        /// produces this result, but calling StripSprite makes the intent explicit and also
        /// handles cases where a sprite was assigned elsewhere.
        /// </summary>
        public static void StripSprite(GameObject target)
        {
            if (target == null) return;
            var image = target.GetComponent<Image>();
            if (image != null)
                image.sprite = null;
        }

        /// <summary>
        /// Apply the U3D panel style to an already-created panel GameObject.
        /// Currently this means: strip the sprite so corners are square, and leave the
        /// Image color at Unity's default. Does not touch RectTransform, layout, or children.
        /// </summary>
        public static void ApplyPanelStyle(GameObject panel)
        {
            StripSprite(panel);
        }

        /// <summary>
        /// Apply the U3D button style to an already-created button GameObject.
        /// Strips the button's background sprite, sets the Button component's normalColor to a
        /// slightly off-white tint so the button's edges are visible against pure-white panels,
        /// leaves Unity-default hover/pressed/disabled states alone (Unity multiplies these from
        /// the new normal base), and styles the child TextMeshPro label if one is present.
        /// </summary>
        public static void ApplyButtonStyle(GameObject button, string label = null)
        {
            if (button == null) return;

            StripSprite(button);

            var buttonComponent = button.GetComponent<Button>();
            if (buttonComponent != null)
            {
                var colors = buttonComponent.colors;
                colors.normalColor = ButtonNormalColor;
                buttonComponent.colors = colors;
            }

            var buttonText = button.GetComponentInChildren<TextMeshProUGUI>();
            if (buttonText != null)
            {
                if (label != null)
                    buttonText.text = label;
                ApplyButtonTextStyle(buttonText);
            }
        }

        // -----------------------------──────────────────
        // Text Styling
        //
        // These methods set visual properties only — font size, color, alignment.
        // They do NOT touch raycastTarget. The calling tool owns that decision because
        // only the tool knows whether the text is decorative, part of a button, or
        // interactive in its own right.
        // -----------------------------──────────────────

        /// <summary>Title text: 16pt, dark gray, center-aligned. raycastTarget is not touched.</summary>
        public static void ApplyTitleStyle(TextMeshProUGUI text)
        {
            if (text == null) return;
            text.fontSize = TitleFontSize;
            text.color = TextColor;
            text.alignment = TextAlignmentOptions.Center;
        }

        /// <summary>Body/price text: 18pt, dark gray, center-aligned. raycastTarget is not touched.</summary>
        public static void ApplyBodyStyle(TextMeshProUGUI text)
        {
            if (text == null) return;
            text.fontSize = BodyFontSize;
            text.color = TextColor;
            text.alignment = TextAlignmentOptions.Center;
        }

        /// <summary>Status text: 10pt, dark gray, center-aligned. raycastTarget is not touched.</summary>
        public static void ApplyStatusStyle(TextMeshProUGUI text)
        {
            if (text == null) return;
            text.fontSize = StatusFontSize;
            text.color = TextColor;
            text.alignment = TextAlignmentOptions.Center;
        }

        /// <summary>Button label text: 14pt, dark gray, center-aligned. raycastTarget is not touched.</summary>
        public static void ApplyButtonTextStyle(TextMeshProUGUI text)
        {
            if (text == null) return;
            text.fontSize = ButtonFontSize;
            text.color = TextColor;
            text.alignment = TextAlignmentOptions.Center;
        }

        /// <summary>Placeholder text for TMP_InputField: muted gray. Font size is left at whatever the input field picked. raycastTarget is not touched.</summary>
        public static void ApplyPlaceholderStyle(TextMeshProUGUI placeholder)
        {
            if (placeholder == null) return;
            placeholder.color = PlaceholderColor;
        }

        // -----------------------------──────────────────
        // Combined Panel + Title Builder
        // -----------------------------──────────────────

        /// <summary>
        /// Create a styled header (panel with title text) anchored to the top of a parent container.
        /// Returns the header GameObject so callers can anchor additional content against it if needed.
        /// Replaces the per-tool CreateCleanHeaderUI methods that were duplicated across tool categories.
        ///
        /// The title text is created as a decorative header — raycastTarget is set to false here because
        /// this builder fully owns the text it creates and the header is unambiguously non-interactive.
        /// If a tool needs a clickable header, build it manually rather than using this helper.
        /// </summary>
        public static GameObject CreateHeader(GameObject parent, string title)
        {
            if (parent == null) return null;

            var uiResources = new DefaultControls.Resources();
            GameObject header = DefaultControls.CreatePanel(uiResources);
            header.name = "Header";
            header.transform.SetParent(parent.transform, false);

            ApplyPanelStyle(header);

            var headerRect = header.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 0.8f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.offsetMin = Vector2.zero;
            headerRect.offsetMax = Vector2.zero;

            var tmpResources = new TMP_DefaultControls.Resources();
            GameObject titleTextObj = TMP_DefaultControls.CreateText(tmpResources);
            titleTextObj.name = "Title";
            titleTextObj.transform.SetParent(header.transform, false);

            var titleRect = titleTextObj.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(10f, 0f);
            titleRect.offsetMax = new Vector2(-10f, 0f);

            var titleTMP = titleTextObj.GetComponent<TextMeshProUGUI>();
            if (titleTMP != null)
            {
                titleTMP.text = title;
                ApplyTitleStyle(titleTMP);
                titleTMP.raycastTarget = false;
            }

            return header;
        }

        /// <summary>
        /// Create a styled status text anchored to the bottom of a parent container.
        /// Returns the TextMeshProUGUI so callers can assign it to controllers that update it at runtime.
        /// Replaces the per-tool CreateCleanStatusText methods that were duplicated across tool categories.
        ///
        /// The status text is created as a decorative label — raycastTarget is set to false here because
        /// this builder fully owns the text it creates and status labels are unambiguously non-interactive.
        /// </summary>
        public static TextMeshProUGUI CreateStatusText(GameObject parent, string initialText = "")
        {
            if (parent == null) return null;

            var tmpResources = new TMP_DefaultControls.Resources();
            GameObject statusTextObj = TMP_DefaultControls.CreateText(tmpResources);
            statusTextObj.name = "StatusText";
            statusTextObj.transform.SetParent(parent.transform, false);

            var statusRect = statusTextObj.GetComponent<RectTransform>();
            statusRect.anchorMin = new Vector2(0f, 0f);
            statusRect.anchorMax = new Vector2(1f, 0.15f);
            statusRect.offsetMin = new Vector2(10f, 5f);
            statusRect.offsetMax = new Vector2(-10f, -5f);

            var statusTMP = statusTextObj.GetComponent<TextMeshProUGUI>();
            if (statusTMP != null)
            {
                statusTMP.text = initialText;
                ApplyStatusStyle(statusTMP);
                statusTMP.raycastTarget = false;
            }

            return statusTMP;
        }

        // -----------------------------──────────────────
        // Scroll View Configuration
        // -----------------------------──────────────────

        /// <summary>
        /// Configure a scroll view created by DefaultControls.CreateScrollView to scroll vertically only.
        /// Matches the settings verified to work in the Settings UI Canvas:
        ///   - ScrollRect.horizontal = false
        ///   - ScrollRect.horizontalScrollbarVisibility = Permanent
        ///   - Horizontal scrollbar GameObject and reference are preserved (required for the hide to work)
        /// Vertical scrollbar settings are left untouched for the caller to configure.
        ///
        /// Note on the counterintuitive enum value: in this configuration, Permanent hides the horizontal
        /// scrollbar in Play Mode while AutoHide variants show it. This is empirically verified behavior
        /// in the Settings UI Canvas. Do not change to AutoHide or AutoHideAndExpandViewport without
        /// retesting in Play Mode. In Edit Mode the scrollbar will be visible regardless — this is
        /// documented Unity behavior (Edit Mode always shows scrollbars so layout can be authored with
        /// them in mind).
        /// </summary>
        public static void ConfigureVerticalOnlyScrollView(GameObject scrollView)
        {
            if (scrollView == null) return;

            var scrollRect = scrollView.GetComponent<ScrollRect>();
            if (scrollRect == null) return;

            scrollRect.horizontal = false;
            scrollRect.horizontalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        /// <summary>
        /// Apply the full U3D scroll view style: vertical-only configuration, transparent background
        /// (so the parent panel shows through), and U3D's flat square sprite on the vertical
        /// scrollbar's track and thumb. The vertical scrollbar preserves Unity's 20px default.
        ///
        /// The scrollbar GameObject and Scrollbar component are preserved — only sprites and width
        /// change. ScrollRect functionality (drag, mouse wheel, touch) is unaffected.
        ///
        /// Editor-only because the flat sprites are loaded via AssetDatabase. Runtime callers get
        /// vertical-only configuration but no sprite swap, plus a logged warning.
        /// </summary>
        public static void ApplyScrollViewStyle(GameObject scrollView)
        {
            if (scrollView == null) return;

            ConfigureVerticalOnlyScrollView(scrollView);

            // Transparent scroll view background — the scroll view is structural,
            // not decorative, so let the parent panel show through.
            var scrollBg = scrollView.GetComponent<Image>();
            if (scrollBg != null)
                scrollBg.color = new Color(0f, 0f, 0f, 0f);

            Sprite flatSquare = GetFlatSquareSprite();
            if (flatSquare == null) return;

            // Find the vertical scrollbar and apply the flat square to its background and thumb
            Transform verticalScrollbar = scrollView.transform.Find("Scrollbar Vertical");
            if (verticalScrollbar != null)
            {
                // Keep the scrollbar Unity's 20px default
                var scrollbarRect = verticalScrollbar.GetComponent<RectTransform>();
                if (scrollbarRect != null)
                    scrollbarRect.sizeDelta = new Vector2(20f, scrollbarRect.sizeDelta.y);

                // Track background — inset surface tier
                var scrollbarImage = verticalScrollbar.GetComponent<Image>();
                if (scrollbarImage != null)
                {
                    scrollbarImage.sprite = flatSquare;
                    scrollbarImage.color = InsetSurfaceColor;
                }

                // Thumb (the draggable handle) — interactive control tier, matches buttons
                Transform handle = verticalScrollbar.Find("Sliding Area/Handle");
                if (handle != null)
                {
                    var handleImage = handle.GetComponent<Image>();
                    if (handleImage != null)
                    {
                        handleImage.sprite = flatSquare;
                        handleImage.color = ButtonNormalColor;
                    }
                }
            }

            // The viewport also has an Image (with a Mask component) that needs a sprite
            // to be valid — Unity's mask requires a sprite to clip against. Use the flat square.
            Transform viewport = scrollView.transform.Find("Viewport");
            if (viewport != null)
            {
                var viewportImage = viewport.GetComponent<Image>();
                if (viewportImage != null)
                    viewportImage.sprite = flatSquare;
            }
        }

        // -----------------------------──────────────────
        // Slider Style
        // -----------------------------──────────────────

        /// <summary>
        /// Apply U3D's flat slider style: square sprite for background and fill, circle sprite for the
        /// knob. Optional fillColor parameter — if provided, tints the fill (e.g., the Video Player's
        /// blue progress fill). Otherwise the fill stays at Unity's default color.
        ///
        /// Does NOT modify the Handle Slide Area RectTransform offsets — those are authored values
        /// that control the knob's bounds and travel range, set by the caller before this method
        /// is invoked.
        ///
        /// Editor-only because the flat sprites are loaded via AssetDatabase.
        /// </summary>
        public static void ApplySliderStyle(GameObject slider, Color? fillColor = null)
        {
            if (slider == null) return;

            Sprite flatSquare = GetFlatSquareSprite();
            Sprite flatCircle = GetFlatCircleSprite();

            // Background — inset surface tier
            Transform background = slider.transform.Find("Background");
            if (background != null)
            {
                var bgImage = background.GetComponent<Image>();
                if (bgImage != null && flatSquare != null)
                {
                    bgImage.sprite = flatSquare;
                    bgImage.color = InsetSurfaceColor;
                }
            }

            // Fill — flat square, optional caller-supplied tint (otherwise Unity default)
            Transform fill = slider.transform.Find("Fill Area/Fill");
            if (fill != null)
            {
                var fillImage = fill.GetComponent<Image>();
                if (fillImage != null && flatSquare != null)
                {
                    fillImage.sprite = flatSquare;
                    if (fillColor.HasValue)
                        fillImage.color = fillColor.Value;
                }
            }

            // Handle (knob) — interactive control tier, matches buttons
            Transform handle = slider.transform.Find("Handle Slide Area/Handle");
            if (handle != null)
            {
                var handleImage = handle.GetComponent<Image>();
                if (handleImage != null && flatCircle != null)
                {
                    handleImage.sprite = flatCircle;
                    handleImage.color = ButtonNormalColor;
                }
            }
        }

        // -----------------------------──────────────────
        // Input Field Style
        // -----------------------------──────────────────

        /// <summary>
        /// Apply U3D's flat input field style: flat square sprite for the background, inset surface
        /// color so the field reads as a recessed entry area against the panel. Matches slider tracks
        /// and scrollbar tracks for consistent "inset surface" visual language.
        ///
        /// Does NOT style placeholder text or typed text color — call ApplyPlaceholderStyle separately
        /// for the placeholder, and the typed text inherits TMP defaults which match the U3D text color.
        ///
        /// Editor-only because the flat sprite is loaded via AssetDatabase.
        /// </summary>
        public static void ApplyInputFieldStyle(GameObject inputField)
        {
            if (inputField == null) return;

            Sprite flatSquare = GetFlatSquareSprite();
            if (flatSquare == null) return;

            var image = inputField.GetComponent<Image>();
            if (image != null)
            {
                image.sprite = flatSquare;
                image.color = InsetSurfaceColor;
            }
        }
    }
}