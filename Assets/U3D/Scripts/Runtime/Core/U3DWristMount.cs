using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using U3D.XR;

namespace U3D.UI
{
    /// <summary>
    /// Holds screen-overlay canvases on the local player's left hand while VR is active, laid out as
    /// the compact phone layout: the Upper canvas's header (the Settings chip, or its open panel) on
    /// top and the Lower canvas (the player menu) below it, the column centred over the wrist joint
    /// and raised by the player controller's VR Wrist Menu Height. The top of the column stays put;
    /// when the Upper header grows, the Lower canvas moves down. A held canvas becomes a worldspace
    /// panel; released, it returns exactly as it was. Placed once per VR entry through the grab chain
    /// (ResolveHandBone, TryGetHandGrabFrame, ComputeHoldPose's math) and parented to the hand bone,
    /// so it turns with the wrist. Held canvases move to an unnamed layer the player camera renders,
    /// and that layer is masked out of every other camera while it renders, so mirrors do not show
    /// the menu. Created by the first canvas held and destroyed after the last release. Canvases
    /// stay hidden until the hand resolves.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DWristMount : MonoBehaviour
    {
        public enum Slot { Upper, Lower }

        [Tooltip("Signed distance from the wrist joint along the forearm line, in metres: positive toward the fingers, negative up the forearm. The column is centred here. The height above the hand is VR Wrist Menu Height on the U3D Player Controller.")]
        [SerializeField] private float anchorDistance = 0f;
        [Tooltip("Turn of the panel about its face normal, in degrees. At 0 the text runs along the forearm from elbow to fingers and the column's top points away from the body when the forearm is across the chest.")]
        [SerializeField] private float faceRoll = 0f;
        [Tooltip("Metres per screen point. 0.0005 makes 300 points 15 cm.")]
        [SerializeField] private float metresPerPoint = 0.0005f;
        [Tooltip("Gap between the Upper header and the Lower canvas, in metres.")]
        [SerializeField] private float gap = 0.006f;
        [Tooltip("How far the Upper canvas sits toward the viewer, in metres.")]
        [SerializeField] private float upperDepth = 0.002f;
        [Tooltip("Used only when the avatar has no left hand bone: position relative to the player root.")]
        [SerializeField] private Vector3 fallbackLocalPosition = new Vector3(-0.2f, 1.1f, 0.35f);
        [SerializeField] private Vector3 fallbackLocalEuler = new Vector3(30f, 0f, 0f);

        private const int FirstUserLayer = 8;
        private const int LastLayer = 31;

        private class Held
        {
            public Canvas Canvas;
            public Slot Slot;
            public Vector2 SizePoints;
            public float ClosedHeaderPoints;
            public float HeaderPoints;
            public Transform Parent;
            public int SiblingIndex;
            public RenderMode RenderMode;
            public Camera WorldCamera;
            public bool CanvasEnabled;
            public Vector2 Pivot;
            public Vector2 SizeDelta;
            public Vector3 LocalScale;
            public bool LayerApplied;
            public readonly List<Behaviour> Scalers = new List<Behaviour>();
            public readonly List<KeyValuePair<Transform, int>> OriginalLayers = new List<KeyValuePair<Transform, int>>();
        }

        private static U3DWristMount _instance;
        private readonly List<Held> _held = new List<Held>();
        private readonly List<Camera> _maskedCameras = new List<Camera>();
        private readonly List<int> _maskedOriginalMasks = new List<int>();
        private bool _placed;
        private int _localLayer = -1;

        /// <summary>
        /// Holds a canvas on the wrist. sizePoints is the canvas size in screen points; headerPoints is
        /// how much of an Upper canvas's top shows while it is closed, which fixes the column's layout.
        /// </summary>
        public static void Hold(Canvas canvas, Slot slot, Vector2 sizePoints, float headerPoints = 0f)
        {
            if (canvas == null) return;
            if (_instance == null)
                _instance = new GameObject("U3D Wrist Mount").AddComponent<U3DWristMount>();
            _instance.Add(canvas, slot, sizePoints, headerPoints);
        }

        /// <summary>
        /// Sets how much of a held Upper canvas currently shows, so the Lower canvas sits below it.
        /// </summary>
        public static void SetHeader(Canvas canvas, float headerPoints)
        {
            if (_instance == null || canvas == null) return;
            Held held = _instance.Find(canvas);
            if (held == null) return;
            held.HeaderPoints = headerPoints;
            _instance.Layout();
        }

        public static void Release(Canvas canvas)
        {
            if (_instance == null || canvas == null) return;
            _instance.Remove(canvas);
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            for (int i = _maskedCameras.Count - 1; i >= 0; i--)
                if (_maskedCameras[i] != null) _maskedCameras[i].cullingMask = _maskedOriginalMasks[i];
            _maskedCameras.Clear();
            _maskedOriginalMasks.Clear();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (!_placed) TryPlace();
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_localLayer < 0 || camera.cameraType != CameraType.Game) return;
            Camera player = U3DGazePointer.Instance != null ? U3DGazePointer.Instance.PlayerCamera : null;
            if (camera == player) return;

            int bit = 1 << _localLayer;
            if ((camera.cullingMask & bit) == 0) return;

            _maskedCameras.Add(camera);
            _maskedOriginalMasks.Add(camera.cullingMask);
            camera.cullingMask &= ~bit;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            int index = _maskedCameras.IndexOf(camera);
            if (index < 0) return;
            camera.cullingMask = _maskedOriginalMasks[index];
            _maskedCameras.RemoveAt(index);
            _maskedOriginalMasks.RemoveAt(index);
        }

        private static int FindLocalOnlyLayer(Camera player)
        {
            if (player == null) return -1;
            for (int i = LastLayer; i >= FirstUserLayer; i--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && (player.cullingMask & (1 << i)) != 0)
                    return i;
            return -1;
        }

        private void ApplyLocalLayer(Held held)
        {
            if (_localLayer < 0 || held.LayerApplied) return;
            held.OriginalLayers.Clear();
            foreach (Transform t in held.Canvas.GetComponentsInChildren<Transform>(true))
            {
                held.OriginalLayers.Add(new KeyValuePair<Transform, int>(t, t.gameObject.layer));
                t.gameObject.layer = _localLayer;
            }
            held.LayerApplied = true;
        }

        private static void RestoreLayers(Held held)
        {
            if (!held.LayerApplied) return;
            foreach (var entry in held.OriginalLayers)
                if (entry.Key != null) entry.Key.gameObject.layer = entry.Value;
            held.OriginalLayers.Clear();
            held.LayerApplied = false;
        }

        private void Add(Canvas canvas, Slot slot, Vector2 sizePoints, float headerPoints)
        {
            Held held = Find(canvas);
            RectTransform rect = (RectTransform)canvas.transform;

            if (held == null)
            {
                held = new Held
                {
                    Canvas = canvas,
                    Parent = rect.parent,
                    SiblingIndex = rect.GetSiblingIndex(),
                    RenderMode = canvas.renderMode,
                    WorldCamera = canvas.worldCamera,
                    CanvasEnabled = canvas.enabled,
                    Pivot = rect.pivot,
                    SizeDelta = rect.sizeDelta,
                    LocalScale = rect.localScale
                };
                foreach (var scaler in canvas.GetComponents<CanvasScaler>())
                    if (scaler.enabled) { held.Scalers.Add(scaler); scaler.enabled = false; }
                foreach (var scaler in canvas.GetComponents<U3DCanvasCssScaler>())
                    if (scaler.enabled) { held.Scalers.Add(scaler); scaler.enabled = false; }
                _held.Add(held);
            }

            held.Slot = slot;
            held.SizePoints = sizePoints;
            held.ClosedHeaderPoints = headerPoints;
            held.HeaderPoints = headerPoints;

            canvas.renderMode = RenderMode.WorldSpace;
            rect.SetParent(transform, false);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = sizePoints;
            rect.localScale = Vector3.one * metresPerPoint;
            rect.localRotation = Quaternion.identity;

            Layout();

            if (_placed) ShowOnHand(held);
            else canvas.enabled = false;
        }

        private void Remove(Canvas canvas)
        {
            Held held = Find(canvas);
            if (held == null) return;
            _held.Remove(held);

            RestoreLayers(held);

            RectTransform rect = (RectTransform)canvas.transform;
            rect.SetParent(held.Parent, false);
            if (held.Parent != null) rect.SetSiblingIndex(held.SiblingIndex);
            rect.localRotation = Quaternion.identity;
            rect.localScale = held.LocalScale;
            rect.pivot = held.Pivot;
            rect.sizeDelta = held.SizeDelta;
            canvas.renderMode = held.RenderMode;
            canvas.worldCamera = held.WorldCamera;
            canvas.enabled = held.CanvasEnabled;
            foreach (var scaler in held.Scalers)
                if (scaler != null) scaler.enabled = true;

            if (_held.Count == 0) Destroy(gameObject);
            else Layout();
        }

        private Held Find(Canvas canvas)
        {
            foreach (var held in _held)
                if (held.Canvas == canvas) return held;
            return null;
        }

        private Held FindSlot(Slot slot)
        {
            foreach (var held in _held)
                if (held.Slot == slot) return held;
            return null;
        }

        private void Layout()
        {
            Held upper = FindSlot(Slot.Upper);
            Held lower = FindSlot(Slot.Lower);

            float gapPoints = upper != null && lower != null ? gap / metresPerPoint : 0f;
            float closedHeader = upper != null ? upper.ClosedHeaderPoints : 0f;
            float header = upper != null ? upper.HeaderPoints : 0f;
            float top = (closedHeader + gapPoints + (lower != null ? lower.SizePoints.y : 0f)) * 0.5f;

            if (upper != null) PlaceCanvas(upper, top, upperDepth);
            if (lower != null) PlaceCanvas(lower, top - header - gapPoints, 0f);
        }

        private void PlaceCanvas(Held held, float topPoints, float towardViewer)
        {
            RectTransform rect = (RectTransform)held.Canvas.transform;
            rect.localPosition = new Vector3(0f, topPoints * metresPerPoint, -towardViewer);
        }

        private void TryPlace()
        {
            U3DWebXRManager xr = U3DWebXRManager.Instance;
            U3DPlayerController player = xr != null ? xr.LocalPlayer : null;
            if (player == null) return;

            U3DAvatarManager avatar = player.GetComponent<U3DAvatarManager>();
            if (avatar == null || !avatar.IsAvatarInitialized) return;

            if (U3DGazePointer.Instance == null || U3DGazePointer.Instance.PlayerCamera == null) return;

            Transform hand = avatar.ResolveHandBone(U3DHandChoice.LeftHand, false);

            if (hand != null)
            {
                Quaternion neutral = player.transform.rotation;
                Vector3 anchor = hand.position;
                Vector3 towardFingers = neutral * Vector3.left;
                if (avatar.TryGetHandGrabFrame(hand, out Quaternion neutralRelative, out Vector3 anchorDirection))
                {
                    neutral = hand.rotation * Quaternion.Inverse(neutralRelative);
                    towardFingers = hand.TransformDirection(anchorDirection).normalized;
                    anchor = hand.position + towardFingers * anchorDistance;
                }

                Vector3 backOfHand = neutral * Vector3.up;
                Vector3 forward = -backOfHand;
                Vector3 up = Vector3.Cross(forward, towardFingers);
                Quaternion face = Quaternion.LookRotation(forward, up) * Quaternion.Euler(0f, 0f, faceRoll);

                transform.SetParent(hand, false);
                float boneScale = hand.lossyScale.x;
                transform.localScale = Vector3.one / (Mathf.Abs(boneScale) > 1e-5f ? boneScale : 1f);
                transform.SetPositionAndRotation(anchor + backOfHand * player.VRWristMenuHeight, face);
            }
            else
            {
                transform.SetParent(player.transform, false);
                transform.localScale = Vector3.one;
                transform.SetLocalPositionAndRotation(fallbackLocalPosition, Quaternion.Euler(fallbackLocalEuler));
            }

            _localLayer = FindLocalOnlyLayer(U3DGazePointer.Instance.PlayerCamera);
            _placed = true;
            foreach (var held in _held)
                ShowOnHand(held);
        }

        private void ShowOnHand(Held held)
        {
            ApplyLocalLayer(held);
            held.Canvas.worldCamera = U3DGazePointer.Instance != null ? U3DGazePointer.Instance.PlayerCamera : null;
            held.Canvas.enabled = true;
            U3DGazePointer.RefreshWorldCanvases();
        }
    }
}