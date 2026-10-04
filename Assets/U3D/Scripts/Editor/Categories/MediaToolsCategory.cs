using System.Collections.Generic;
using TMPro;
using U3D;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.Video;

namespace U3D.Editor
{
    public class MediaToolsCategory : IToolCategory
    {
        public string CategoryName => "Media & Content";
        public System.Action<int> OnRequestTabSwitch { get; set; }
        private List<CreatorTool> tools;

        private const string MIXER_PATH = "Assets/U3D/Prefabs/U3D_AudioMixer.mixer";
        private const string SETTINGS_UI_PREFAB_PATH = "Assets/U3D/Prefabs/Settings UI Canvas.prefab";
        private const string LASER_POINTER_PREFAB_PATH = "Assets/U3D/Prefabs/U3D_LaserPointer.prefab";

        public MediaToolsCategory()
        {
            tools = new List<CreatorTool>
            {
                new CreatorTool("🟢 Add Audio Playlist", "Play audio clips through your AudioSource. Add clips, then start playback from a trigger (like U3D Enter Trigger).", ApplyAudioList),
                new CreatorTool("🟢 Make Audio Playlist", "Adds an Audio Playlist component to the selected object. If the object doesn't have an AudioSource, one is added and routed to the Effects mixer (3D spatial).", MakeAudioPlaylist, true),
                new CreatorTool("🟢 Add Ambient Audio Source", "Adds an AudioSource routed to the Ambient channel. 2D playback, same volume everywhere. Good for background music and ambient sound.", CreateAmbientSource),
                new CreatorTool("🟢 Add Local Audio Source", "Adds an AudioSource routed to the Effects channel. 3D spatial, sound fades with distance. Good for sound effects on objects.", CreateLocalSource),
                new CreatorTool("🟢 Add Worldspace UI", "World space canvas with proximity fade and billboard behavior options", CreateWorldspaceUI),
                new CreatorTool("🟢 Make URL Link", "Click to open a URL in a new browser tab. Adds an Interact Trigger wired to open the link.", ApplyURLLink, true),
                new CreatorTool("🟢 Add Video Player", "Stream a video from a URL onto a screen in your world. After placing, select the Video Screen child object and paste a direct .mp4 or .webm link into the Video URL field.", CreateVideoPlayer),
                new CreatorTool("🟢 Add Mirror", "Reflective surface for vanity mirrors, avatar viewing, or scene composition. Each mirror creates its own render texture asset in Assets/U3D/U3D_Assets/Mirrors/.", CreateMirror),
                new CreatorTool("🟢 Add Instructions", "Worldspace UI showing default movement and control patterns with all current input bindings. Updates automatically if you remap controls.", CreateMovementInstructions),
                new CreatorTool("🟢 Add Touch Controls", "Move and Look circles in the bottom corners of the screen, shown only on phones and tablets so visitors know where to put their thumbs. Touches pass straight through to the touch controls. Swap in your own circle art on the Move Hint and Look Hint images.", CreateTouchControlHints),
                new CreatorTool("🟢 Add Settings UI", "Adds the U3D Settings UI prefab. Players use this to adjust audio, graphics, and controls at runtime.", AddSettingsUI),
                new CreatorTool("🟢 Add Screenspace UI", "Screen overlay canvas with title and body text. Good for HUDs, menus, or info overlays. Add your own buttons and content.", CreateScreenspaceUI),
                new CreatorTool("🟢 Add Laser Pointer", "Adds a laser pointer that fires a visible beam onto the first surface it hits, with a dot where it lands. It starts off — wire a button or an Interact Trigger to its Activate, Toggle, or Pulse to control it.", ApplyAddLaserPointer),
                new CreatorTool("🚧 Add Slide Presentation", "Display and cycle through image collections in a sequence, in one UI element", () => { }),
                new CreatorTool("🚧 Add Guestbook", "Visitors can leave a note that appears in your world", () => { }),
            };
        }

        public List<CreatorTool> GetTools() => tools;

        public void DrawCategory()
        {
            EditorGUILayout.LabelField("Media & Content Tools", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Add multimedia elements to enrich your experiences.", MessageType.Info);
            EditorGUILayout.Space(10);

            foreach (var tool in tools)
            {
                ProjectToolsTab.DrawCategoryTool(tool);
            }
        }

        // -----------------------------──────────────────
        // URL Link
        // -----------------------------──────────────────

        private static void ApplyURLLink()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Undo.RecordObject(selected, "Make URL Link");

            Collider collider = selected.GetComponent<Collider>();
            if (collider == null)
                selected.AddComponent<BoxCollider>();

            InteractionToolsCategory.EnsureNetEntity(selected);

            U3DInteractTrigger interactTrigger = selected.GetComponent<U3DInteractTrigger>();
            if (interactTrigger == null)
                interactTrigger = selected.AddComponent<U3DInteractTrigger>();

            U3DOpenURL openURL = selected.GetComponent<U3DOpenURL>();
            if (openURL == null)
                openURL = selected.AddComponent<U3DOpenURL>();

            if (interactTrigger.OnInteractTriggered == null)
                interactTrigger.OnInteractTriggered = new UnityEngine.Events.UnityEvent();

            if (!IsAlreadyWired(interactTrigger.OnInteractTriggered, openURL, "Open"))
            {
                UnityEditor.Events.UnityEventTools.AddVoidPersistentListener(
                    interactTrigger.OnInteractTriggered,
                    new UnityEngine.Events.UnityAction(openURL.Open)
                );
            }

            EditorUtility.SetDirty(selected);
        }

        private static bool IsAlreadyWired(UnityEngine.Events.UnityEvent unityEvent, Object target, string methodName)
        {
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); i++)
            {
                if (unityEvent.GetPersistentTarget(i) == target &&
                    unityEvent.GetPersistentMethodName(i) == methodName)
                    return true;
            }
            return false;
        }

        // -----------------------------──────────────────
        // Audio Playlist
        // -----------------------------──────────────────

        private static void ApplyAudioList()
        {
            GameObject obj = new GameObject("Audio Playlist");

            // RequireComponent on U3DAudioPlaylist pulls in an AudioSource automatically.
            // The playlist's Reset() configures that AudioSource with U3D's standard 3D
            // spatial defaults (Effects mixer, log rolloff, 1-500m range). So this single
            // AddComponent call produces a fully wired, ready-to-play playlist — same end
            // state as Make Audio Playlist on a pre-existing object.
            obj.AddComponent<U3DAudioPlaylist>();

            PositionInScene(obj);
            Selection.activeGameObject = obj;
            EditorGUIUtility.PingObject(obj);
            EditorUtility.SetDirty(obj);
        }

        private static void MakeAudioPlaylist()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogWarning("Please select an object first");
                return;
            }

            Undo.RegisterCompleteObjectUndo(selected, "Make Audio Playlist");

            // Idempotent: don't double-add if the creator runs the tool twice on the same object.
            U3DAudioPlaylist existingPlaylist = selected.GetComponent<U3DAudioPlaylist>();
            if (existingPlaylist != null)
            {
                EditorUtility.DisplayDialog("Audio Playlist",
                    "This object already has an Audio Playlist component.",
                    "OK");
                EditorGUIUtility.PingObject(selected);
                return;
            }

            // RequireComponent on U3DAudioPlaylist pulls in an AudioSource if the selected
            // object doesn't already have one. The playlist's Reset() configures that
            // AudioSource with U3D's standard 3D spatial defaults. If the selected object
            // already had an AudioSource that the creator configured manually, Reset()
            // detects that (playOnAwake check) and leaves their settings alone.
            Undo.AddComponent<U3DAudioPlaylist>(selected);

            EditorGUIUtility.PingObject(selected);
            EditorUtility.SetDirty(selected);
        }

        // -----------------------------──────────────────
        // Ambient Source
        // -----------------------------──────────────────

        private static void CreateAmbientSource()
        {
            AudioMixerGroup ambientGroup = FindMixerGroup("Ambient");
            if (ambientGroup == null) return;

            GameObject obj = new GameObject("Ambient Audio Source");

            AudioSource source = obj.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = ambientGroup;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.loop = false;

            PositionInScene(obj);
            Selection.activeGameObject = obj;
            EditorGUIUtility.PingObject(obj);
            EditorUtility.SetDirty(obj);
        }

        // -----------------------------──────────────────
        // Local Source
        // -----------------------------──────────────────

        private static void CreateLocalSource()
        {
            AudioMixerGroup effectsGroup = FindMixerGroup("Effects");
            if (effectsGroup == null) return;

            GameObject obj = new GameObject("Local Audio Source");

            AudioSource source = obj.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = effectsGroup;
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.minDistance = 1f;
            source.maxDistance = 500f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.loop = false;

            PositionInScene(obj);
            Selection.activeGameObject = obj;
            EditorGUIUtility.PingObject(obj);
            EditorUtility.SetDirty(obj);
        }

        // -----------------------------──────────────────
        // Video Player
        // -----------------------------──────────────────

        private static void CreateVideoPlayer()
        {
            GameObject root = new GameObject("Video Player");
            PositionInScene(root);

            GameObject screenObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            screenObj.name = "Video Screen";
            screenObj.transform.SetParent(root.transform, false);
            screenObj.transform.localScale = new Vector3(1.92f, 1.08f, 1f);

            MeshCollider meshCollider = screenObj.GetComponent<MeshCollider>();
            if (meshCollider != null)
                Object.DestroyImmediate(meshCollider);

            // Assign the shared U3D video screen material so the screen shows unlit at edit time.
            // Without this, the URP default Lit material that GameObject.CreatePrimitive applied
            // would produce lighting glare in the Scene view. At runtime, U3DVideoPlayer instantiates
            // a per-instance copy of this material to hold its own RenderTexture (different Video
            // Players therefore play different videos despite sharing the edit-time material).
            MeshRenderer screenRenderer = screenObj.GetComponent<MeshRenderer>();
            if (screenRenderer != null)
            {
                Material screenMat = GetOrCreateVideoScreenMaterial();
                if (screenMat != null)
                    screenRenderer.sharedMaterial = screenMat;
                // If GetOrCreateVideoScreenMaterial returned null, it already logged a warning
                // and the runtime path in U3DVideoPlayer.CreateRenderTexture will attempt the
                // swap when the scene plays.
            }

            AudioSource audioSource = screenObj.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 1f;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 30f;
            audioSource.rolloffMode = AudioRolloffMode.Logarithmic;

            AudioMixerGroup effectsGroup = FindMixerGroup("Effects");
            if (effectsGroup != null)
                audioSource.outputAudioMixerGroup = effectsGroup;

            VideoPlayer videoPlayer = screenObj.AddComponent<VideoPlayer>();
            videoPlayer.source = VideoSource.Url;
            videoPlayer.playOnAwake = false;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.AudioSource;
            videoPlayer.SetTargetAudioSource(0, audioSource);
            videoPlayer.hideFlags = HideFlags.HideInInspector;
            audioSource.hideFlags = HideFlags.HideInInspector;

            U3DVideoPlayer u3dVideo = screenObj.AddComponent<U3DVideoPlayer>();

            var uiResources = new DefaultControls.Resources();
            var tmpResources = new TMP_DefaultControls.Resources();

            GameObject canvasObj = new GameObject("Video Controls Canvas");
            canvasObj.transform.SetParent(root.transform, false);
            canvasObj.transform.localPosition = new Vector3(0f, -0.7f, 0f);

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            canvasObj.AddComponent<CanvasGroup>();
            canvasObj.AddComponent<GraphicRaycaster>();

            U3DWorldspaceUI worldspaceUI = canvasObj.AddComponent<U3DWorldspaceUI>();
            worldspaceUI.faceCamera = false;
            worldspaceUI.hideDistance = 8f;
            worldspaceUI.showDistance = 1f;

            RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(300, 40);
            canvasRect.localScale = Vector3.one * 0.006f;

            GameObject panelObj = DefaultControls.CreatePanel(uiResources);
            panelObj.name = "Controls Panel";
            panelObj.transform.SetParent(canvasObj.transform, false);
            panelObj.layer = LayerMask.NameToLayer("UI");

            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyPanelStyle(panelObj);

            Image panelImage = panelObj.GetComponent<Image>();
            if (panelImage != null)
                panelImage.color = new Color(1f, 1f, 1f, 0.95f);

            GameObject buttonObj = TMP_DefaultControls.CreateButton(tmpResources);
            buttonObj.name = "PlayPause Button";
            buttonObj.transform.SetParent(panelObj.transform, false);
            buttonObj.layer = LayerMask.NameToLayer("UI");

            RectTransform buttonRect = buttonObj.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(0.02f, 0.1f);
            buttonRect.anchorMax = new Vector2(0.18f, 0.9f);
            buttonRect.offsetMin = Vector2.zero;
            buttonRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyButtonStyle(buttonObj, "Play");
            TextMeshProUGUI buttonTMP = buttonObj.GetComponentInChildren<TextMeshProUGUI>();
            // Button label raycastTarget left at Unity's default (true) so the Button hit test
            // works through the label.

            GameObject sliderObj = DefaultControls.CreateSlider(uiResources);
            sliderObj.name = "Progress Slider";
            sliderObj.transform.SetParent(panelObj.transform, false);
            sliderObj.layer = LayerMask.NameToLayer("UI");

            RectTransform sliderRect = sliderObj.GetComponent<RectTransform>();
            sliderRect.anchorMin = new Vector2(0.20f, 0.1f);
            sliderRect.anchorMax = new Vector2(0.75f, 0.9f);
            sliderRect.offsetMin = Vector2.zero;
            sliderRect.offsetMax = Vector2.zero;

            Slider slider = sliderObj.GetComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.value = 0f;

            // Apply U3D flat slider style with blue progress fill (Video Player exception
            // documented in spec — blue communicates "progress" in a way a neutral fill does not).
            U3DUIStyle.ApplySliderStyle(sliderObj, new Color(0.4f, 0.6f, 1f, 1f));
            // Restore the Handle Slide Area offsets — sized for the Video Player's compact controls strip.
            // ApplySliderStyle does not touch these because they're authored values that control the
            // knob's bounds and travel range, owned by the caller.
            RectTransform handleSlideArea = sliderObj.transform.Find("Handle Slide Area")?.GetComponent<RectTransform>();
            if (handleSlideArea != null)
            {
                handleSlideArea.offsetMin = new Vector2(20f, 6f);
                handleSlideArea.offsetMax = new Vector2(-20f, -6f);
            }

            GameObject timeObj = TMP_DefaultControls.CreateText(tmpResources);
            timeObj.name = "Time Display";
            timeObj.transform.SetParent(panelObj.transform, false);
            timeObj.layer = LayerMask.NameToLayer("UI");

            RectTransform timeRect = timeObj.GetComponent<RectTransform>();
            timeRect.anchorMin = new Vector2(0.77f, 0.1f);
            timeRect.anchorMax = new Vector2(0.98f, 0.9f);
            timeRect.offsetMin = Vector2.zero;
            timeRect.offsetMax = Vector2.zero;

            TextMeshProUGUI timeTMP = timeObj.GetComponent<TextMeshProUGUI>();
            if (timeTMP != null)
            {
                timeTMP.text = "0:00 / 0:00";
                U3DUIStyle.ApplyStatusStyle(timeTMP);
                timeTMP.raycastTarget = false;
            }

            u3dVideo.playPauseButton = buttonObj.GetComponent<Button>();
            u3dVideo.progressSlider = slider;
            u3dVideo.timeDisplay = timeTMP;
            u3dVideo.playPauseButtonText = buttonTMP;

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            EditorUtility.SetDirty(root);
        }

        // -----------------------------──────────────────
        // Mirror
        // -----------------------------──────────────────

        private const string MIRROR_RT_FOLDER = "Assets/U3D/U3D_Assets/Mirrors";

        private static void CreateMirror()
        {
            // Ensure the render texture folder exists.
            if (!AssetDatabase.IsValidFolder(MIRROR_RT_FOLDER))
            {
                if (!AssetDatabase.IsValidFolder("Assets/U3D/U3D_Assets"))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/U3D"))
                        AssetDatabase.CreateFolder("Assets", "U3D");
                    AssetDatabase.CreateFolder("Assets/U3D", "U3D_Assets");
                }
                AssetDatabase.CreateFolder("Assets/U3D/U3D_Assets", "Mirrors");
            }

            // Create the render texture asset with a unique name so multiple mirrors don't collide.
            string rtPath = AssetDatabase.GenerateUniqueAssetPath(MIRROR_RT_FOLDER + "/MirrorRenderTexture.renderTexture");
            RenderTexture rt = new RenderTexture(1024, 1536, 24, RenderTextureFormat.Default);
            rt.name = System.IO.Path.GetFileNameWithoutExtension(rtPath);
            AssetDatabase.CreateAsset(rt, rtPath);
            AssetDatabase.SaveAssets();

            // Create the material that displays the render texture on the mirror surface.
            string matPath = AssetDatabase.GenerateUniqueAssetPath(MIRROR_RT_FOLDER + "/MirrorMaterial.mat");
            Material mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            mat.SetTexture("_BaseMap", rt);
            // Render Face = Both. Required because the negative X scale on the mirror quad
            // (used to flip the image left-to-right) inverts the quad's normals.
            mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Back);
            AssetDatabase.CreateAsset(mat, matPath);
            AssetDatabase.SaveAssets();

            // Root.
            GameObject root = new GameObject("Mirror");
            PositionInScene(root);

            // Mirror surface quad. Faces along the root's local +Z by default.
            GameObject surfaceObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
            surfaceObj.name = "Mirror Surface";
            surfaceObj.transform.SetParent(root.transform, false);
            // Negative X scale flips the quad's UVs so the camera feed reads as a mirror image
            // (raise your right hand, the reflection raises its left). Standard Unity mirror trick.
            surfaceObj.transform.localScale = new Vector3(-2f, 3f, 1f);

            MeshRenderer surfaceRenderer = surfaceObj.GetComponent<MeshRenderer>();
            if (surfaceRenderer != null)
                surfaceRenderer.sharedMaterial = mat;

            // Reflection camera. Faces opposite the mirror surface so it looks out at the room
            // the player is standing in, not through the back of the mirror.
            GameObject camObj = new GameObject("Reflection Camera");
            camObj.transform.SetParent(root.transform, false);
            camObj.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            Camera reflectionCam = camObj.AddComponent<Camera>();
            reflectionCam.targetTexture = rt;
            // Don't tag this MainCamera and don't let it become the audio listener — the player controller owns both.
            reflectionCam.clearFlags = CameraClearFlags.Skybox;
            // Wide FOV reduces the size mismatch where objects close to the mirror appear larger
            // in the reflection than in the scene. Creators can tune this on the Reflection Camera
            // child in the Inspector if they want a different look.
            reflectionCam.fieldOfView = 120f;


            AudioListener stowawayListener = camObj.GetComponent<AudioListener>();
            if (stowawayListener != null)
                Object.DestroyImmediate(stowawayListener);

            // Mirror driver.
            U3DMirror mirror = root.AddComponent<U3DMirror>();
            mirror.reflectionCamera = reflectionCam;
            mirror.mirrorSurface = surfaceObj.transform;

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            EditorUtility.SetDirty(root);
        }

        // -----------------------------──────────────────
        // Movement Instructions
        // -----------------------------──────────────────

        private static void CreateMovementInstructions()
        {
            // Editor-side lookups. The asset reference is serialized onto the runtime component
            // below; the flags source is only for the preview bake — at runtime the component
            // reads the spawned player instance instead.
            InputActionAsset inputAsset = FindInputActionAsset();
            string instructionText = U3DControlsInstructions.BuildText(inputAsset, FindPlayerControllerForFlags());

            GameObject canvasObj = new GameObject("Instructions");

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            canvasObj.AddComponent<CanvasGroup>();
            canvasObj.AddComponent<GraphicRaycaster>();

            U3DWorldspaceUI worldspaceUI = canvasObj.AddComponent<U3DWorldspaceUI>();
            worldspaceUI.faceCamera = true;

            RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(500, 600);
            canvasRect.localScale = Vector3.one * 0.005f;

            var uiResources = new DefaultControls.Resources();
            var tmpResources = new TMP_DefaultControls.Resources();

            // Background panel
            GameObject panelObj = DefaultControls.CreatePanel(uiResources);
            panelObj.name = "Panel";
            panelObj.transform.SetParent(canvasObj.transform, false);
            panelObj.layer = LayerMask.NameToLayer("UI");

            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyPanelStyle(panelObj);

            // Title
            GameObject titleObj = TMP_DefaultControls.CreateText(tmpResources);
            titleObj.name = "Title";
            titleObj.transform.SetParent(panelObj.transform, false);
            titleObj.layer = LayerMask.NameToLayer("UI");

            RectTransform titleRect = titleObj.GetComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0.05f, 0.9f);
            titleRect.anchorMax = new Vector2(0.95f, 0.98f);
            titleRect.offsetMin = Vector2.zero;
            titleRect.offsetMax = Vector2.zero;

            TextMeshProUGUI titleTMP = titleObj.GetComponent<TextMeshProUGUI>();
            if (titleTMP != null)
            {
                titleTMP.text = "CONTROLS";
                U3DUIStyle.ApplyTitleStyle(titleTMP);
                titleTMP.raycastTarget = false;
            }

            // Scrollable content area
            GameObject scrollObj = DefaultControls.CreateScrollView(uiResources);
            scrollObj.name = "Scroll View";
            scrollObj.transform.SetParent(panelObj.transform, false);
            scrollObj.layer = LayerMask.NameToLayer("UI");

            RectTransform scrollRect = scrollObj.GetComponent<RectTransform>();
            scrollRect.anchorMin = new Vector2(0.03f, 0.03f);
            scrollRect.anchorMax = new Vector2(0.97f, 0.88f);
            scrollRect.offsetMin = Vector2.zero;
            scrollRect.offsetMax = Vector2.zero;

            // ApplyScrollViewStyle: vertical-only, transparent background, flat scrollbar sprites,
            // narrow scrollbar width.
            U3DUIStyle.ApplyScrollViewStyle(scrollObj);

            Transform contentArea = scrollObj.transform.Find("Viewport/Content");

            // Instruction text
            GameObject textObj = TMP_DefaultControls.CreateText(tmpResources);
            textObj.name = "Instructions Text";
            textObj.transform.SetParent(contentArea, false);
            textObj.layer = LayerMask.NameToLayer("UI");

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 1f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.pivot = new Vector2(0.5f, 1f);
            textRect.sizeDelta = new Vector2(0f, 0f);
            textRect.anchoredPosition = Vector2.zero;

            TextMeshProUGUI instructionTMP = textObj.GetComponent<TextMeshProUGUI>();
            if (instructionTMP != null)
            {
                instructionTMP.text = instructionText;
                U3DUIStyle.ApplyBodyStyle(instructionTMP);
                instructionTMP.alignment = TextAlignmentOptions.TopLeft;
                instructionTMP.textWrappingMode = TextWrappingModes.Normal;
                // Must be false — this text fills the entire scroll view content area.
                // With raycastTarget = true, the text swallows drag events and creators
                // can't scroll by clicking on the text itself.
                instructionTMP.raycastTarget = false;
            }

            // Runtime text owner: rebuilds the controls list at scene start from the live input
            // asset and the spawned player's flags, overwriting the preview bake above. This is
            // what makes existing canvases pick up future binding changes without regeneration.
            U3DControlsInstructions runtimeInstructions = canvasObj.AddComponent<U3DControlsInstructions>();
            runtimeInstructions.Configure(inputAsset, instructionTMP);

            // Auto-size content to fit text
            ContentSizeFitter fitter = textObj.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            if (contentArea != null)
            {
                VerticalLayoutGroup layout = contentArea.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 10, 10);
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;

                ContentSizeFitter contentFitter = contentArea.gameObject.AddComponent<ContentSizeFitter>();
                contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            }

            PositionInScene(canvasObj);
            Selection.activeGameObject = canvasObj;
            EditorGUIUtility.PingObject(canvasObj);
            EditorUtility.SetDirty(canvasObj);
        }

        private static U3DPlayerController FindPlayerControllerForFlags()
        {
            // Prefer a Player Controller that's actually in the open scene — reflects
            // any scene-level overrides the creator has made.
            U3DPlayerController inScene = Object.FindAnyObjectByType<U3DPlayerController>(
                FindObjectsInactive.Include);
            if (inScene != null) return inScene;

            // Fall back to the prefab — covers the common case where the creator
            // hasn't dropped the player into the scene yet.
            string[] guids = AssetDatabase.FindAssets("U3D_PlayerController t:Prefab");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;
                var controller = prefab.GetComponent<U3DPlayerController>();
                if (controller != null) return controller;
            }

            return null;
        }

        private static InputActionAsset FindInputActionAsset()
        {
            string[] guids = AssetDatabase.FindAssets("U3DInputActions t:InputActionAsset");
            if (guids.Length == 0)
                guids = AssetDatabase.FindAssets("t:InputActionAsset", new[] { "Assets/U3D" });

            if (guids.Length == 0)
                return null;

            string path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
        }

        // -----------------------------──────────────────
        // Settings UI
        // -----------------------------──────────────────

        private static void AddSettingsUI()
        {
            var existing = Object.FindAnyObjectByType<Canvas>();
            if (existing != null && existing.gameObject.name.Contains("Settings UI"))
            {
                EditorUtility.DisplayDialog("Settings UI",
                    "A Settings UI Canvas already exists in the scene.\n\nFound: " + existing.gameObject.name,
                    "OK");
                Selection.activeGameObject = existing.gameObject;
                return;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SETTINGS_UI_PREFAB_PATH);
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Settings UI Not Found",
                    "Could not find the Settings UI prefab at:\n" + SETTINGS_UI_PREFAB_PATH +
                    "\n\nMake sure the U3D template prefab has not been moved or renamed.",
                    "OK");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = "U3D Settings UI";
            Undo.RegisterCreatedObjectUndo(instance, "Add Settings UI");

            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        // -----------------------------──────────────────
        // Screenspace UI
        // -----------------------------──────────────────

        private static void CreateScreenspaceUI()
        {
            // Check if a screenspace canvas with the same name already exists
            GameObject existing = GameObject.Find("Screenspace UI Canvas");
            if (existing != null)
            {
                EditorUtility.DisplayDialog("Screenspace UI",
                    "A Screenspace UI Canvas already exists in the scene.\n\nFound: " + existing.name +
                    "\n\nYou can have multiple, but you'll want to rename them to tell them apart.",
                    "OK");
                Selection.activeGameObject = existing;
                EditorGUIUtility.PingObject(existing);
                return;
            }

            GameObject canvasObj = new GameObject("Screenspace UI Canvas");

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObj.AddComponent<GraphicRaycaster>();

            var uiResources = new DefaultControls.Resources();
            var tmpResources = new TMP_DefaultControls.Resources();

            // Default panel: centered, 400x300. Creators resize or reposition as needed.
            GameObject panelObj = DefaultControls.CreatePanel(uiResources);
            panelObj.name = "Panel";
            panelObj.transform.SetParent(canvasObj.transform, false);
            panelObj.layer = LayerMask.NameToLayer("UI");

            U3DUIStyle.ApplyPanelStyle(panelObj);

            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(400, 300);
            panelRect.anchoredPosition = Vector2.zero;

            U3DUIStyle.CreateHeader(panelObj, "Screenspace UI");

            // Body text: fills the lower 80% of the panel with padding, ready for creator content.
            GameObject textObj = TMP_DefaultControls.CreateText(tmpResources);
            textObj.name = "Body Text";
            textObj.transform.SetParent(panelObj.transform, false);
            textObj.layer = LayerMask.NameToLayer("UI");

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0f, 0f);
            textRect.anchorMax = new Vector2(1f, 0.8f);
            textRect.offsetMin = new Vector2(15f, 15f);
            textRect.offsetMax = new Vector2(-15f, -10f);

            TextMeshProUGUI bodyTMP = textObj.GetComponent<TextMeshProUGUI>();
            if (bodyTMP != null)
            {
                bodyTMP.text = "Replace this with your content. Add buttons, images, or whatever else you need.";
                U3DUIStyle.ApplyBodyStyle(bodyTMP);
                bodyTMP.textWrappingMode = TextWrappingModes.Normal;
                bodyTMP.raycastTarget = false;
            }

            Selection.activeGameObject = canvasObj;
            EditorGUIUtility.PingObject(canvasObj);
            EditorUtility.SetDirty(canvasObj);
        }

        // -----------------------------──────────────────
        // Touch Controls
        // -----------------------------──────────────────

        // Approved exception to U3DUIStyle (see the UI Creation Methods Reference). The hints only
        // appear on touch devices, where the standard 18pt body text renders at about 7 CSS pixels,
        // and they sit over the scene, so the circle is translucent white instead of an opaque fill.
        private const float TOUCH_HINT_FONT_SIZE = 40f;
        private static readonly Color TOUCH_HINT_CIRCLE_COLOR = new Color(1f, 1f, 1f, 0.35f);

        private static void CreateTouchControlHints()
        {
            var existing = Object.FindAnyObjectByType<U3D.Input.U3DTouchControlHints>(FindObjectsInactive.Include);
            if (existing != null)
            {
                EditorUtility.DisplayDialog("Touch Controls",
                    "This scene already has touch control hints.\n\nFound: " + existing.gameObject.name,
                    "OK");
                Selection.activeGameObject = existing.gameObject;
                EditorGUIUtility.PingObject(existing.gameObject);
                return;
            }

            GameObject canvasObj = new GameObject("Touch Controls Canvas");
            canvasObj.layer = LayerMask.NameToLayer("UI");

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Below Screenspace UI (10) and the menus, so every other overlay draws on top.
            canvas.sortingOrder = -10;

            CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // No GraphicRaycaster: nothing on this canvas is interactive, and U3DSimpleTouchZones
            // ignores any touch that begins over a raycast target. U3DTouchControlHints also forces
            // its CanvasGroup to pass touches through, in case a raycaster is added later.
            canvasObj.AddComponent<U3D.Input.U3DTouchControlHints>();

            Sprite circleSprite = U3DUIStyle.GetFlatCircleSprite();

            // Anchored as a fraction of screen width and height, not as a pixel offset from the
            // bottom edge. On mobile browsers the page canvas is sized to 100vh, which extends
            // behind the browser's bottom toolbar, so a fixed offset from the bottom lands off
            // screen. 33% up keeps both circles above the toolbar and the Full Screen button,
            // and 20% / 80% across keeps them outside the Interact and Jump bands (35-65% of
            // screen width), in portrait and landscape.
            CreateTouchHintCircle(canvasObj.transform, "Move Hint", "Move", circleSprite,
                new Vector2(0.20f, 0.33f), Vector2.zero);
            CreateTouchHintCircle(canvasObj.transform, "Look Hint", "Look", circleSprite,
                new Vector2(0.80f, 0.33f), Vector2.zero);

            Undo.RegisterCreatedObjectUndo(canvasObj, "Add Touch Controls");
            Selection.activeGameObject = canvasObj;
            EditorGUIUtility.PingObject(canvasObj);
            EditorUtility.SetDirty(canvasObj);
        }

        private static void CreateTouchHintCircle(Transform parent, string objectName, string label, Sprite sprite, Vector2 anchor, Vector2 anchoredPosition)
        {
            GameObject circleObj = new GameObject(objectName, typeof(RectTransform));
            circleObj.transform.SetParent(parent, false);
            circleObj.layer = LayerMask.NameToLayer("UI");

            RectTransform circleRect = circleObj.GetComponent<RectTransform>();
            circleRect.anchorMin = anchor;
            circleRect.anchorMax = anchor;
            circleRect.pivot = new Vector2(0.5f, 0.5f);
            circleRect.sizeDelta = new Vector2(220f, 220f);
            circleRect.anchoredPosition = anchoredPosition;

            Image circleImage = circleObj.AddComponent<Image>();
            circleImage.sprite = sprite;
            circleImage.color = TOUCH_HINT_CIRCLE_COLOR;
            circleImage.raycastTarget = false;

            var tmpResources = new TMP_DefaultControls.Resources();
            GameObject labelObj = TMP_DefaultControls.CreateText(tmpResources);
            labelObj.name = "Label";
            labelObj.transform.SetParent(circleObj.transform, false);
            labelObj.layer = LayerMask.NameToLayer("UI");

            RectTransform labelRect = labelObj.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            TextMeshProUGUI labelTMP = labelObj.GetComponent<TextMeshProUGUI>();
            if (labelTMP != null)
            {
                labelTMP.text = label;
                U3DUIStyle.ApplyBodyStyle(labelTMP);
                labelTMP.fontSize = TOUCH_HINT_FONT_SIZE;
                labelTMP.raycastTarget = false;
            }
        }

        // -----------------------------──────────────────
        // Laser Pointer
        // -----------------------------──────────────────

        private static void ApplyAddLaserPointer()
        {
            GameObject prefab = LoadLaserPointerPrefab();
            if (prefab == null)
            {
                EditorUtility.DisplayDialog("Laser Pointer Not Found",
                    "Could not find the Laser Pointer prefab at:\n" + LASER_POINTER_PREFAB_PATH +
                    "\n\nIf it isn't in your project yet, the Laser Pointer hasn't been added. " +
                    "If you moved it, putting it back under Assets/U3D/Prefabs will let this tool find it.",
                    "OK");
                return;
            }

            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
            {
                Debug.LogWarning("Add Laser Pointer: could not instantiate the Laser Pointer prefab.");
                return;
            }

            Undo.RegisterCreatedObjectUndo(instance, "Add Laser Pointer");

            // Detach from the source prefab so the entity added below is a plain component,
            // not a prefab-instance override a future template update could collide with.
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            instance.name = "Laser Pointer";
            PositionInScene(instance);

            // The entity goes on the pointer itself. The beam is drawn locally on every peer
            // from the pointer's own state; what the pointer needs is a number every peer can
            // resolve when that state is shared.
            InteractionToolsCategory.EnsureNetEntity(instance);

            EditorUtility.SetDirty(instance);
            Selection.activeGameObject = instance;
            EditorGUIUtility.PingObject(instance);
        }

        private static GameObject LoadLaserPointerPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LASER_POINTER_PREFAB_PATH);
            if (prefab != null && prefab.GetComponent<U3DLaserPointer>() != null)
                return prefab;

            // Fallback: locate by name anywhere in the project, in case it was moved.
            foreach (string guid in AssetDatabase.FindAssets("U3D_LaserPointer t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var candidate = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (candidate != null && candidate.GetComponent<U3DLaserPointer>() != null)
                    return candidate;
            }

            return null;
        }

        // -----------------------------──────────────────
        // Video Screen Material Lookup
        // -----------------------------──────────────────

        private const string VIDEO_PLAYER_FOLDER = "Assets/U3D/U3D_Assets/Video Player";
        private const string VIDEO_SCREEN_MATERIAL_PATH = "Assets/U3D/U3D_Assets/Video Player/U3D_VideoScreenMaterial.mat";

        /// <summary>
        /// Returns the shared unlit material used by all Video Player screens at edit time.
        /// Creates it on disk if it doesn't exist yet. Runtime instantiates per-Video-Player copies
        /// of this material to hold individual RenderTextures.
        /// </summary>
        private static Material GetOrCreateVideoScreenMaterial()
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(VIDEO_SCREEN_MATERIAL_PATH);
            if (existing != null)
                return existing;

            // Ensure the folder hierarchy exists. Same pattern as CreateMirror's MIRROR_RT_FOLDER setup.
            if (!AssetDatabase.IsValidFolder(VIDEO_PLAYER_FOLDER))
            {
                if (!AssetDatabase.IsValidFolder("Assets/U3D/U3D_Assets"))
                {
                    if (!AssetDatabase.IsValidFolder("Assets/U3D"))
                        AssetDatabase.CreateFolder("Assets", "U3D");
                    AssetDatabase.CreateFolder("Assets/U3D", "U3D_Assets");
                }
                AssetDatabase.CreateFolder("Assets/U3D/U3D_Assets", "Video Player");
            }

            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlitShader == null)
            {
                Debug.LogWarning("Add Video Player: Universal Render Pipeline/Unlit shader not found. Could not create the shared video screen material. The screen will keep its default Lit material and may show lighting glare at edit time. Runtime will attempt to swap to unlit when the scene plays.");
                return null;
            }

            Material mat = new Material(unlitShader);
            mat.name = System.IO.Path.GetFileNameWithoutExtension(VIDEO_SCREEN_MATERIAL_PATH);
            AssetDatabase.CreateAsset(mat, VIDEO_SCREEN_MATERIAL_PATH);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // -----------------------------──────────────────
        // Mixer Lookup
        // -----------------------------──────────────────

        private static AudioMixerGroup FindMixerGroup(string groupName)
        {
            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(MIXER_PATH);
            if (mixer == null)
            {
                EditorUtility.DisplayDialog("Audio Mixer Not Found",
                    "Could not find U3D_AudioMixer at:\n" + MIXER_PATH +
                    "\n\nMake sure the U3D template audio mixer has not been moved or renamed.",
                    "OK");
                return null;
            }

            AudioMixerGroup[] groups = mixer.FindMatchingGroups(groupName);
            if (groups == null || groups.Length == 0)
            {
                EditorUtility.DisplayDialog("Mixer Group Not Found",
                    "Could not find the '" + groupName + "' group in U3D_AudioMixer." +
                    "\n\nExpected groups: Master, Ambient, Effects, Music, Voice.",
                    "OK");
                return null;
            }

            return groups[0];
        }

        // -----------------------------──────────────────
        // Scene Positioning
        // -----------------------------──────────────────

        private static void PositionInScene(GameObject obj)
        {
            if (SceneView.lastActiveSceneView != null)
                obj.transform.position = SceneView.lastActiveSceneView.pivot;
        }

        // -----------------------------──────────────────
        // Worldspace UI
        // -----------------------------──────────────────

        private static void CreateWorldspaceUI()
        {
            GameObject canvasObj = new GameObject("Worldspace UI Canvas");

            Canvas canvas = canvasObj.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            canvasObj.AddComponent<CanvasGroup>();
            canvasObj.AddComponent<GraphicRaycaster>();
            canvasObj.AddComponent<U3DWorldspaceUI>();

            RectTransform canvasRect = canvasObj.GetComponent<RectTransform>();
            canvasRect.sizeDelta = U3DUIStyle.WorldspaceSingleElementSize;
            canvasRect.localScale = Vector3.one * U3DUIStyle.WorldspaceCanvasScale;

            var uiResources = new DefaultControls.Resources();
            GameObject panelObj = DefaultControls.CreatePanel(uiResources);
            panelObj.name = "Panel";
            panelObj.transform.SetParent(canvasObj.transform, false);
            panelObj.layer = LayerMask.NameToLayer("UI");

            RectTransform panelRect = panelObj.GetComponent<RectTransform>();
            panelRect.anchorMin = Vector2.zero;
            panelRect.anchorMax = Vector2.one;
            panelRect.offsetMin = Vector2.zero;
            panelRect.offsetMax = Vector2.zero;

            U3DUIStyle.ApplyPanelStyle(panelObj);

            var tmpResources = new TMP_DefaultControls.Resources();
            GameObject textObj = TMP_DefaultControls.CreateText(tmpResources);
            textObj.name = "Text (TMP)";
            textObj.transform.SetParent(panelObj.transform, false);
            textObj.layer = LayerMask.NameToLayer("UI");

            RectTransform textRect = textObj.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10f, 10f);
            textRect.offsetMax = new Vector2(-10f, -10f);

            TextMeshProUGUI tmpText = textObj.GetComponent<TextMeshProUGUI>();
            if (tmpText != null)
            {
                tmpText.text = "Worldspace UI Text";
                U3DUIStyle.ApplyBodyStyle(tmpText);
                tmpText.raycastTarget = false;
            }

            if (SceneView.lastActiveSceneView != null)
                canvasObj.transform.position = SceneView.lastActiveSceneView.pivot;

            Selection.activeGameObject = canvasObj;
            EditorGUIUtility.PingObject(canvasObj);
            EditorUtility.SetDirty(canvasObj);
        }
    }
}