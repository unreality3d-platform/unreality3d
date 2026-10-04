using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if WEBXR_ENABLED
using WebXR;
#endif

namespace U3D
{
    [RequireComponent(typeof(Light))]
    public class WorldSpaceLensFlare : MonoBehaviour
    {
        public enum OcclusionMode { None, Raycast, Manual }

        [System.Serializable]
        public class FlareElement
        {
            public enum ShapeType { Image, Circle, Ring, Polygon }

            [Header("Shape")]
            public ShapeType shapeType = ShapeType.Circle;
            public string name = "Element";

            [Header("Image (ShapeType.Image only)")]
            public Texture2D imageTexture;

            [Header("Circle / Polygon / Ring")]
            public float gradient = 0.7f;
            public float falloff = 1f;
            public bool invert = false;

            [Header("Polygon")]
            public int sideCount = 6;
            [Range(0f, 1f)] public float roundness = 0f;

            [Header("Ring")]
            public float ringThickness = 0.25f;
            public float noiseAmplitude = 0f;
            public float noiseRepeat = 1f;
            public float noiseSpeed = 0f;

            [Header("Appearance")]
            public Color tint = Color.white;
            [Min(0f)] public float intensity = 1f;
            public bool modulateByLightColor = false;

            [Header("Transform")]
            public Vector2 scale = Vector2.one;
            [Min(0.01f)] public float uniformScale = 1f;
            public float startingPosition = 0f;
            public float angularOffset = 0f;
            public bool autoRotate = true;
            public float rotation = 0f;

            [Header("Multiple Elements")]
            public bool multipleEnabled = false;
            public int count = 1;
            public enum DistributionType { Curve, Random }
            public DistributionType distribution = DistributionType.Curve;
            public float lengthSpread = 1f;
            public int seed = 0;

            [Header("Multiple - Variation")]
            [Range(0f, 1f)] public float intensityVariation = 0f;
            [Range(0f, 1f)] public float positionVariationX = 0f;
            [Range(0f, 1f)] public float positionVariationY = 0f;
            [Range(0f, 1f)] public float rotationVariation = 0f;
            [Range(0f, 1f)] public float scaleVariation = 0f;

            [Header("Multiple - Curves (Curve distribution)")]
            public AnimationCurve colorCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
            public AnimationCurve positionCurveX = AnimationCurve.Linear(0f, 0f, 1f, 0f);
            public AnimationCurve positionCurveY = AnimationCurve.Linear(0f, 0f, 1f, 0f);
            public AnimationCurve rotationCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);
            public AnimationCurve scaleCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

            [Header("Radial Distortion")]
            public bool radialDistortion = false;
            public Vector2 edgeSize = new Vector2(1f, 1.5f);
            public bool relativeToCenter = true;

            [Header("Texture Resolution")]
            public int textureResolution = 256;
        }

        [Header("Global Settings")]
        [SerializeField] float sunDistance = -1f;
        [SerializeField] float globalIntensityMultiplier = 1f;
        [SerializeField] float globalScaleFactor = 0.05f;

#if UNITY_EDITOR
        [Header("Editor Preview")]
        [Tooltip("Force world-space flare on in Editor play mode, disabling SRP flare")]
        [SerializeField] bool editorPreview = false;
        bool _editorPreviewActive = false;
#endif

        [Header("Occlusion")]
        [SerializeField] OcclusionMode occlusionMode = OcclusionMode.None;
        [SerializeField] LayerMask occlusionLayerMask = ~0;
        [SerializeField] float occlusionFadeSpeed = 4f;
        [SerializeField] float occlusionRadius = 0.5f;

        [Header("Element Definitions")]
        [SerializeField] FlareElement[] elements = new FlareElement[0];

        [Header("References")]
        [SerializeField] Shader flareShader;

        Light _directionalLight;
        Camera _mainCam;
        LensFlareComponentSRP _srpFlare;
        Material _flareMaterial;
        Transform[][] _elementQuads;
        Material[][] _elementMaterials;
        float _occlusionFactor = 1f;
        float _manualVisibility = 1f;
        bool _worldFlareActive = false;
        bool _initialized = false;

        static readonly int ShaderColorID = Shader.PropertyToID("_Color");
        static readonly int ShaderIntensityID = Shader.PropertyToID("_Intensity");
        static readonly int ShaderMainTexID = Shader.PropertyToID("_MainTex");

        void OnEnable()
        {
            _directionalLight = GetComponent<Light>();
            _srpFlare = GetComponent<LensFlareComponentSRP>();
            _mainCam = Camera.main;

            if (flareShader == null)
                flareShader = Shader.Find("U3D/WorldSpaceLensFlare");

            if (flareShader == null)
            {
                Debug.LogError("[WorldSpaceLensFlare] Shader 'U3D/WorldSpaceLensFlare' not found");
                enabled = false;
                return;
            }

            _flareMaterial = new Material(flareShader);
            BuildElements();
            SetWorldFlareActive(false);

#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            WebXRManager.OnXRChange += OnXRChange;
#endif
        }

        void OnDisable()
        {
#if WEBXR_ENABLED && UNITY_WEBGL && !UNITY_EDITOR
            WebXRManager.OnXRChange -= OnXRChange;
#endif

#if UNITY_EDITOR
            if (_editorPreviewActive)
            {
                _editorPreviewActive = false;
                if (_srpFlare != null) _srpFlare.enabled = true;
                SetWorldFlareActive(false);
            }
#endif

            DestroyElements();
            if (_flareMaterial != null)
                Destroy(_flareMaterial);
        }

#if WEBXR_ENABLED
        void OnXRChange(WebXRState state, int viewsCount, Rect leftRect, Rect rightRect)
        {
            bool isVR = (state == WebXRState.VR && viewsCount > 1);

            if (_srpFlare != null)
                _srpFlare.enabled = !isVR;

            SetWorldFlareActive(isVR);
        }
#endif

        void BuildElements()
        {
            DestroyElements();

            if (elements == null || elements.Length == 0) return;

            _elementQuads = new Transform[elements.Length][];
            _elementMaterials = new Material[elements.Length][];

            for (int i = 0; i < elements.Length; i++)
            {
                var elem = elements[i];
                int instanceCount = elem.multipleEnabled ? Mathf.Max(elem.count, 1) : 1;

                _elementQuads[i] = new Transform[instanceCount];
                _elementMaterials[i] = new Material[instanceCount];

                Texture2D tex = GenerateTextureForElement(elem);

                for (int j = 0; j < instanceCount; j++)
                {
                    string quadName = instanceCount > 1
                        ? $"FlareElement_{i}_{elem.name}_{j}"
                        : $"FlareElement_{i}_{elem.name}";

                    var quad = CreateQuad(quadName);
                    quad.SetParent(transform, false);
                    quad.localPosition = Vector3.zero;

                    var mat = new Material(_flareMaterial);
                    mat.SetTexture(ShaderMainTexID, tex);
                    quad.GetComponent<MeshRenderer>().sharedMaterial = mat;

                    _elementQuads[i][j] = quad;
                    _elementMaterials[i][j] = mat;
                }
            }

            _initialized = true;
        }

        void DestroyElements()
        {
            _initialized = false;

            if (_elementQuads != null)
            {
                for (int i = 0; i < _elementQuads.Length; i++)
                {
                    if (_elementQuads[i] == null) continue;
                    for (int j = 0; j < _elementQuads[i].Length; j++)
                    {
                        if (_elementQuads[i][j] != null)
                            Destroy(_elementQuads[i][j].gameObject);
                    }
                }
            }

            if (_elementMaterials != null)
            {
                for (int i = 0; i < _elementMaterials.Length; i++)
                {
                    if (_elementMaterials[i] == null) continue;
                    for (int j = 0; j < _elementMaterials[i].Length; j++)
                    {
                        if (_elementMaterials[i][j] != null)
                            Destroy(_elementMaterials[i][j]);
                    }
                }
            }

            _elementQuads = null;
            _elementMaterials = null;
        }

        Texture2D GenerateTextureForElement(FlareElement elem)
        {
            switch (elem.shapeType)
            {
                case FlareElement.ShapeType.Image:
                    return elem.imageTexture;

                case FlareElement.ShapeType.Circle:
                    return FlareTextureGenerator.GenerateCircle(
                        elem.textureResolution, elem.gradient, elem.falloff, elem.invert);

                case FlareElement.ShapeType.Ring:
                    return FlareTextureGenerator.GenerateRing(
                        elem.textureResolution, elem.gradient, elem.falloff, elem.invert,
                        elem.ringThickness, elem.noiseAmplitude, elem.noiseRepeat, elem.noiseSpeed);

                case FlareElement.ShapeType.Polygon:
                    return FlareTextureGenerator.GeneratePolygon(
                        elem.textureResolution, elem.sideCount, elem.roundness,
                        elem.gradient, elem.falloff, elem.invert);

                default:
                    return Texture2D.whiteTexture;
            }
        }

        Transform CreateQuad(string quadName)
        {
            var go = new GameObject(quadName, typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = BuildQuadMesh();
            go.layer = gameObject.layer;
            return go.transform;
        }

        Mesh _sharedQuadMesh;

        Mesh BuildQuadMesh()
        {
            if (_sharedQuadMesh != null) return _sharedQuadMesh;

            _sharedQuadMesh = new Mesh { name = "FlareQuad" };

            _sharedQuadMesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f)
            };

            _sharedQuadMesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f)
            };

            _sharedQuadMesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            _sharedQuadMesh.RecalculateNormals();
            _sharedQuadMesh.RecalculateBounds();
            _sharedQuadMesh.UploadMeshData(true);

            return _sharedQuadMesh;
        }

        void SetWorldFlareActive(bool active)
        {
            _worldFlareActive = active;
            SetQuadVisibility(active);
        }

        void SetQuadVisibility(bool visible)
        {
            if (_elementQuads == null) return;

            for (int i = 0; i < _elementQuads.Length; i++)
            {
                if (_elementQuads[i] == null) continue;
                for (int j = 0; j < _elementQuads[i].Length; j++)
                {
                    if (_elementQuads[i][j] != null)
                        _elementQuads[i][j].gameObject.SetActive(visible);
                }
            }
        }

        void LateUpdate()
        {
#if UNITY_EDITOR
            if (Application.isPlaying)
            {
                if (editorPreview && !_editorPreviewActive)
                {
                    _editorPreviewActive = true;
                    if (_srpFlare != null) _srpFlare.enabled = false;
                    SetWorldFlareActive(true);
                }
                else if (!editorPreview && _editorPreviewActive)
                {
                    _editorPreviewActive = false;
                    if (_srpFlare != null) _srpFlare.enabled = true;
                    SetWorldFlareActive(false);
                }
            }
#endif

            if (!_worldFlareActive || !_initialized) return;
            if (_mainCam == null)
            {
                _mainCam = Camera.main;
                if (_mainCam == null) return;
            }

            float actualSunDistance = sunDistance > 0f ? sunDistance : _mainCam.farClipPlane * 0.9f;
            Vector3 camPos = _mainCam.transform.position;
            Vector3 sunDir = -transform.forward;
            Vector3 sunWorldPos = camPos + sunDir * actualSunDistance;

            // Hide flare when sun is behind camera
            float sunDot = Vector3.Dot(_mainCam.transform.forward, sunDir);
            if (sunDot < 0f)
            {
                SetQuadVisibility(false);
                return;
            }
            SetQuadVisibility(true);

            UpdateOcclusion(camPos, sunDir, actualSunDistance);

            float visibility = _occlusionFactor * _manualVisibility;

            // Flare axis in viewport space: sun → center(0.5,0.5) → opposite side
            Vector3 sunViewport = _mainCam.WorldToViewportPoint(sunWorldPos);
            Vector2 sunVP = new Vector2(sunViewport.x, sunViewport.y);
            Vector2 centerVP = new Vector2(0.5f, 0.5f);
            // Axis direction from sun toward opposite side through center
            Vector2 flareAxis = centerVP - sunVP;

            float sunAngle = Mathf.Atan2(sunVP.y - 0.5f, sunVP.x - 0.5f) * Mathf.Rad2Deg;

            // Place all quads at a consistent depth for uniform scale behavior
            float planeDistance = actualSunDistance;
            Vector3 camForward = _mainCam.transform.forward;
            Vector3 rightWS = _mainCam.transform.right;
            Vector3 upWS = _mainCam.transform.up;

            Color lightColor = _directionalLight.color * _directionalLight.intensity;

            for (int i = 0; i < elements.Length; i++)
            {
                var elem = elements[i];
                int instanceCount = _elementQuads[i].Length;

                for (int j = 0; j < instanceCount; j++)
                {
                    float t = 0f;
                    float instanceIntensityMul = 1f;
                    float instanceScaleMul = 1f;
                    float instanceRotationOffset = 0f;
                    Vector2 instancePositionOffset = Vector2.zero;
                    Color instanceColorMul = Color.white;

                    if (elem.multipleEnabled && instanceCount > 1)
                    {
                        float normalizedIndex = (float)j / (instanceCount - 1);

                        if (elem.distribution == FlareElement.DistributionType.Curve)
                        {
                            t = elem.startingPosition + normalizedIndex * elem.lengthSpread;
                            instanceColorMul = Color.Lerp(Color.white, elem.tint, elem.colorCurve.Evaluate(normalizedIndex));
                            instancePositionOffset.x = elem.positionCurveX.Evaluate(normalizedIndex) * 0.1f;
                            instancePositionOffset.y = elem.positionCurveY.Evaluate(normalizedIndex) * 0.1f;
                            instanceRotationOffset = elem.rotationCurve.Evaluate(normalizedIndex) * 360f;
                            instanceScaleMul = elem.scaleCurve.Evaluate(normalizedIndex);
                        }
                        else
                        {
                            var rng = new System.Random(elem.seed + j * 31);
                            float randT = (float)rng.NextDouble();
                            t = elem.startingPosition + randT * elem.lengthSpread;
                            instanceIntensityMul = 1f - elem.intensityVariation * (float)rng.NextDouble();
                            instancePositionOffset.x = (float)(rng.NextDouble() * 2.0 - 1.0) * elem.positionVariationX * 0.1f;
                            instancePositionOffset.y = (float)(rng.NextDouble() * 2.0 - 1.0) * elem.positionVariationY * 0.1f;
                            instanceRotationOffset = (float)(rng.NextDouble() * 2.0 - 1.0) * elem.rotationVariation * 360f;
                            instanceScaleMul = 1f - elem.scaleVariation * (float)rng.NextDouble();
                        }
                    }
                    else
                    {
                        t = elem.startingPosition;
                    }

                    // t=0: at sun, t=1: at viewport center, t>1: past center to opposite side
                    Vector2 elemVP = sunVP + flareAxis * t;
                    elemVP += instancePositionOffset;

                    // Convert viewport position to world position on a plane at planeDistance
                    Vector3 viewportPoint = new Vector3(elemVP.x, elemVP.y, planeDistance);
                    Vector3 elementWorldPos = _mainCam.ViewportToWorldPoint(viewportPoint);

                    float distToCam = Vector3.Distance(camPos, elementWorldPos);
                    float baseScaleX = elem.scale.x * elem.uniformScale * globalScaleFactor * distToCam;
                    float baseScaleY = elem.scale.y * elem.uniformScale * globalScaleFactor * distToCam;
                    baseScaleX *= instanceScaleMul;
                    baseScaleY *= instanceScaleMul;

                    if (elem.radialDistortion)
                    {
                        float angularDist = Vector2.Distance(elemVP, centerVP) / Vector2.Distance(sunVP, centerVP);
                        float stretchFactor = Mathf.Lerp(elem.edgeSize.x, elem.edgeSize.y, Mathf.Clamp01(angularDist));
                        baseScaleX *= stretchFactor;
                    }

                    var quadTransform = _elementQuads[i][j];
                    quadTransform.position = elementWorldPos;

                    quadTransform.LookAt(quadTransform.position + camForward, upWS);

                    float localZRot = elem.rotation;
                    if (elem.autoRotate)
                    {
                        float elemAngle = Mathf.Atan2(elemVP.y - 0.5f, elemVP.x - 0.5f) * Mathf.Rad2Deg;
                        localZRot = elemAngle - sunAngle + elem.angularOffset;
                    }
                    localZRot += instanceRotationOffset;
                    quadTransform.Rotate(Vector3.forward, localZRot, Space.Self);

                    quadTransform.localScale = new Vector3(baseScaleX, baseScaleY, 1f);

                    Color finalTint = elem.tint;
                    if (elem.distribution == FlareElement.DistributionType.Curve && elem.multipleEnabled && instanceCount > 1)
                        finalTint *= instanceColorMul;

                    if (elem.modulateByLightColor)
                        finalTint *= lightColor;

                    float finalIntensity = elem.intensity * globalIntensityMultiplier * visibility * instanceIntensityMul;

                    var mat = _elementMaterials[i][j];
                    mat.SetColor(ShaderColorID, finalTint);
                    mat.SetFloat(ShaderIntensityID, finalIntensity);
                }
            }
        }

        void UpdateOcclusion(Vector3 camPos, Vector3 sunDir, float sunDist)
        {
            float targetOcclusion = 1f;

            if (occlusionMode == OcclusionMode.Raycast)
            {
                int hitCount = 0;
                int testCount = 5;

                for (int i = 0; i < testCount; i++)
                {
                    Vector3 offset = Vector3.zero;
                    if (i > 0)
                    {
                        float angle = (i - 1) * Mathf.PI * 0.5f;
                        offset = (_mainCam.transform.right * Mathf.Cos(angle) + _mainCam.transform.up * Mathf.Sin(angle)) * occlusionRadius;
                    }

                    Vector3 rayDir = (sunDir * sunDist + offset).normalized;
                    if (Physics.Raycast(camPos, rayDir, sunDist, occlusionLayerMask, QueryTriggerInteraction.Ignore))
                        hitCount++;
                }

                targetOcclusion = 1f - ((float)hitCount / testCount);
            }

            _occlusionFactor = Mathf.MoveTowards(_occlusionFactor, targetOcclusion, occlusionFadeSpeed * Time.deltaTime);
        }

        public void SetManualVisibility(float value)
        {
            _manualVisibility = Mathf.Clamp01(value);
        }

        public float ManualVisibility => _manualVisibility;

        [ContextMenu("Populate Default Sun Flare (8 Elements)")]
        void PopulateDefaultSunFlare()
        {
            elements = CreateDefaultSunFlareElements();
            Debug.Log("[WorldSpaceLensFlare] Populated 8 default sun flare elements. Assign LensFlare_Alpha.png to Element 0 (SunBurst) Image Texture.");
        }

        [ContextMenu("Clear All Elements")]
        void ClearAllElements()
        {
            elements = new FlareElement[0];
            Debug.Log("[WorldSpaceLensFlare] Cleared all flare elements.");
        }

        public static FlareElement[] CreateDefaultSunFlareElements()
        {
            return new FlareElement[]
            {
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Image,
                    name = "SunBurst",
                    tint = new Color(1f, 1f, 1f, 1f),
                    intensity = 150f,
                    modulateByLightColor = true,
                    autoRotate = false,
                    rotation = 0f,
                    scale = Vector2.one,
                    uniformScale = 15f,
                    startingPosition = 0f,
                    textureResolution = 512
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Circle,
                    name = "RedCircle",
                    gradient = 0.7f,
                    falloff = 1f,
                    tint = new Color(1f, 0f, 0f, 1f),
                    intensity = 80f,
                    autoRotate = true,
                    scale = Vector2.one,
                    uniformScale = 0.64f,
                    startingPosition = 0.25f,
                    radialDistortion = true,
                    edgeSize = new Vector2(0.23f, 3.09f),
                    relativeToCenter = true,
                    multipleEnabled = true,
                    count = 5,
                    distribution = FlareElement.DistributionType.Curve,
                    lengthSpread = 1f,
                    colorCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.7f),
                    positionCurveX = new AnimationCurve(
                        new Keyframe(0f, -0.1f), new Keyframe(0.5f, 0.1f), new Keyframe(1f, -0.05f)),
                    positionCurveY = new AnimationCurve(
                        new Keyframe(0f, 0.1f), new Keyframe(0.5f, -0.1f), new Keyframe(1f, 0.05f)),
                    rotationCurve = new AnimationCurve(
                        new Keyframe(0f, 0f), new Keyframe(0.5f, 0.5f), new Keyframe(1f, 0f)),
                    scaleCurve = new AnimationCurve(
                        new Keyframe(0f, 0.8f), new Keyframe(0.5f, 1f), new Keyframe(1f, 1.3f)),
                    textureResolution = 256
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Polygon,
                    name = "OrangePoly",
                    sideCount = 5,
                    roundness = 0f,
                    gradient = 0.6f,
                    falloff = 0.8f,
                    tint = new Color(1f, 0.647f, 0f, 1f),
                    intensity = 50f,
                    autoRotate = true,
                    scale = Vector2.one,
                    uniformScale = 0.84f,
                    startingPosition = 0.15f,
                    radialDistortion = true,
                    edgeSize = new Vector2(1f, 1.5f),
                    relativeToCenter = true,
                    multipleEnabled = true,
                    count = 4,
                    distribution = FlareElement.DistributionType.Curve,
                    lengthSpread = 1f,
                    colorCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0.8f),
                    positionCurveX = new AnimationCurve(
                        new Keyframe(0f, -0.05f), new Keyframe(0.5f, 0.05f), new Keyframe(1f, -0.03f)),
                    positionCurveY = new AnimationCurve(
                        new Keyframe(0f, 0.05f), new Keyframe(0.5f, -0.05f), new Keyframe(1f, 0.03f)),
                    rotationCurve = new AnimationCurve(
                        new Keyframe(0f, 0f), new Keyframe(0.5f, 0.3f), new Keyframe(1f, 0f)),
                    scaleCurve = new AnimationCurve(
                        new Keyframe(0f, 0.9f), new Keyframe(0.5f, 1f), new Keyframe(1f, 1.2f)),
                    textureResolution = 256
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Ring,
                    name = "OrangeRing",
                    gradient = 1f,
                    falloff = 0.723f,
                    ringThickness = 0.25f,
                    noiseAmplitude = 8.7f,
                    noiseRepeat = 1f,
                    noiseSpeed = 0f,
                    tint = new Color(1f, 0.647f, 0f, 1f),
                    intensity = 200f,
                    autoRotate = true,
                    scale = new Vector2(10.34f, 1f),
                    uniformScale = 1.74f,
                    startingPosition = 0.1f,
                    radialDistortion = true,
                    edgeSize = new Vector2(2f, 8f),
                    relativeToCenter = true,
                    textureResolution = 512
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Ring,
                    name = "GreenRingNear",
                    gradient = 1f,
                    falloff = 0.7f,
                    ringThickness = 0.25f,
                    noiseAmplitude = 1f,
                    noiseRepeat = 1f,
                    noiseSpeed = 0f,
                    tint = new Color(0f, 1f, 0f, 1f),
                    intensity = 200f,
                    autoRotate = true,
                    scale = Vector2.one,
                    uniformScale = 1.07f,
                    startingPosition = 0.62f,
                    radialDistortion = true,
                    edgeSize = new Vector2(2f, 8f),
                    relativeToCenter = true,
                    textureResolution = 256
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Ring,
                    name = "GreenRingFar",
                    gradient = 1f,
                    falloff = 0.7f,
                    ringThickness = 0.25f,
                    noiseAmplitude = 1f,
                    noiseRepeat = 1f,
                    noiseSpeed = 0f,
                    tint = new Color(0f, 1f, 0f, 1f),
                    intensity = 200f,
                    autoRotate = true,
                    scale = Vector2.one,
                    uniformScale = 1.07f,
                    startingPosition = 1.58f,
                    radialDistortion = true,
                    edgeSize = new Vector2(2f, 8f),
                    relativeToCenter = true,
                    textureResolution = 256
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Polygon,
                    name = "BluePoly",
                    sideCount = 6,
                    roundness = 0f,
                    gradient = 0.9f,
                    falloff = 0.9f,
                    tint = new Color(0f, 0.157f, 1f, 1f),
                    intensity = 80f,
                    autoRotate = true,
                    scale = Vector2.one,
                    uniformScale = 1.5f,
                    startingPosition = 0.87f,
                    radialDistortion = true,
                    edgeSize = new Vector2(1f, 1.5f),
                    relativeToCenter = true,
                    multipleEnabled = true,
                    count = 4,
                    distribution = FlareElement.DistributionType.Random,
                    lengthSpread = 0.79f,
                    seed = -2,
                    intensityVariation = 0.25f,
                    positionVariationX = 0.75f,
                    positionVariationY = 0f,
                    rotationVariation = 0f,
                    scaleVariation = 0.66f,
                    textureResolution = 256
                },
                new FlareElement
                {
                    shapeType = FlareElement.ShapeType.Circle,
                    name = "PurpleCircle",
                    gradient = 0.15f,
                    falloff = 1f,
                    tint = new Color(0.5f, 0f, 1f, 1f),
                    intensity = 50f,
                    autoRotate = true,
                    scale = new Vector2(1f, 1.4f),
                    uniformScale = 2f,
                    startingPosition = 0.5f,
                    angularOffset = 2f,
                    multipleEnabled = true,
                    count = 3,
                    distribution = FlareElement.DistributionType.Curve,
                    lengthSpread = 0.97f,
                    colorCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f),
                    positionCurveX = AnimationCurve.Linear(0f, 0f, 1f, 0f),
                    positionCurveY = new AnimationCurve(
                        new Keyframe(0f, 0f), new Keyframe(0.7f, 0f), new Keyframe(1f, 0.3f)),
                    rotationCurve = new AnimationCurve(
                        new Keyframe(0f, 0f), new Keyframe(0.5f, 0.4f), new Keyframe(1f, 0f)),
                    scaleCurve = new AnimationCurve(
                        new Keyframe(0f, 0.8f), new Keyframe(0.5f, 1f), new Keyframe(1f, 1.5f)),
                    textureResolution = 256
                }
            };
        }
    }
}
