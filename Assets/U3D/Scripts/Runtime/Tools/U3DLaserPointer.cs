using UnityEngine;

namespace U3D
{

    [DisallowMultipleComponent]
    public class U3DLaserPointer : MonoBehaviour
    {
        [Header("Rig References")]
        [Tooltip("Beam emits from here and fires along its forward (+Z). Falls back to this transform if empty.")]
        [SerializeField] private Transform tip;
        [Tooltip("Beam mesh child. Length runs along local +Z, pivot at the emitting (near) end. Its modelled size is measured automatically.")]
        [SerializeField] private Transform beam;
        [Tooltip("Dot mesh child placed where the beam lands. A small sphere reads from every viewpoint.")]
        [SerializeField] private Transform dot;

        [Header("Targeting")]
        [Tooltip("Beam length in metres, and how far it looks for a surface when Stop At Surfaces is on.")]
        [SerializeField] private float maxRange = 50f;
        [Tooltip("On: beam stops at the first surface and the dot lands there. Off: beam is always full length and passes through everything — rendered geometry hides the far part, so it still reads as landing on the wall. Turn off in scenes with no colliders.")]
        [SerializeField] private bool stopAtSurfaces = true;
        [Tooltip("Coarse layer filter. Leave at Everything; the rig and tag exclusions below do the real work.")]
        [SerializeField] private LayerMask hitMask = ~0;
        [Tooltip("Beam also passes through anything with this tag and that tag's children (e.g. avatars). Clear to let the beam land on tagged objects.")]
        [SerializeField] private string ignoreTag = "Player";

        [Header("Beam Look")]
        [Tooltip("Beam radius in metres. Independent of how the beam mesh was modelled and of any scale on this prefab.")]
        [SerializeField] private float beamRadius = 0.005f;
        [SerializeField] private float growDuration = 0.15f;

        [Header("Dot Look")]
        [Tooltip("Dot diameter in metres.")]
        [SerializeField] private float dotBaseScale = 0.05f;
        [SerializeField] private float pulseScale = 3f;
        [SerializeField] private float pulseDuration = 0.4f;

        [Header("Startup")]
        [SerializeField] private bool startActive = false;

        private static readonly RaycastHit[] _hitBuffer = new RaycastHit[32];

        private bool _active;
        private float _growT;
        private float _pulseT;
        private bool _pulsing;

        private Vector3 _beamMeshSize = Vector3.one;
        private Vector3 _dotMeshSize = Vector3.one;

        public bool IsActive => _active;

        private void Awake()
        {
            if (tip == null) tip = transform;
            _beamMeshSize = MeasureMesh(beam);
            _dotMeshSize = MeasureMesh(dot);
            SetVisualsVisible(false);
            if (startActive) Activate();
        }

        public void Activate() => _active = true;

        public void Deactivate() => _active = false;

        public void Toggle()
        {
            if (_active) Deactivate();
            else Activate();
        }

        public void Pulse()
        {
            _pulseT = 0f;
            _pulsing = true;
        }

        private void Update()
        {
            float target = _active ? 1f : 0f;
            _growT = growDuration <= 0f
                ? target
                : Mathf.MoveTowards(_growT, target, Time.deltaTime / growDuration);

            float pulseMul = 1f;
            if (_pulsing)
            {
                _pulseT += Time.deltaTime;
                if (pulseDuration <= 0f || _pulseT >= pulseDuration)
                    _pulsing = false;
                else
                    pulseMul = Mathf.Lerp(1f, pulseScale, Mathf.Sin((_pulseT / pulseDuration) * Mathf.PI));
            }

            float distance = maxRange;
            bool landed = false;

            if (stopAtSurfaces && TryGetBeamTarget(out RaycastHit hit))
            {
                distance = hit.distance;
                landed = true;
            }

            // How far the beam currently reaches. At rest this is 0 (fully retracted).
            float visibleLength = distance * _growT;

            // Beam shows only while active/growing, and retracts to nothing at rest.
            bool beamVisible = _growT > 0f;
            if (beam != null)
            {
                if (beam.gameObject.activeSelf != beamVisible) beam.gameObject.SetActive(beamVisible);
                if (beamVisible)
                {
                    float diameter = beamRadius * 2f;
                    beam.SetPositionAndRotation(tip.position, Quaternion.LookRotation(tip.forward));
                    beam.localScale = WorldSizeToLocalScale(beam, new Vector3(diameter, diameter, visibleLength), _beamMeshSize);
                }
            }

            // Dot rides the beam's leading end: parked at the tip when at rest, carried
            // out to the surface as the beam grows. Aimed at nothing, it stays at the tip
            // and only shows at rest, so it never floats in empty space.
            if (dot != null)
            {
                bool showDot = landed || _growT <= 0f;
                if (showDot)
                {
                    if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
                    float d = dotBaseScale * pulseMul;
                    dot.position = tip.position + tip.forward * (landed ? visibleLength : 0f);
                    dot.localScale = WorldSizeToLocalScale(dot, new Vector3(d, d, d), _dotMeshSize);
                }
                else if (dot.gameObject.activeSelf)
                {
                    dot.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Modelled size of a mesh in its own space, before any transform scale.</summary>
        private static Vector3 MeasureMesh(Transform t)
        {
            if (t == null) return Vector3.one;
            if (t.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
            {
                Vector3 s = mf.sharedMesh.bounds.size;
                return new Vector3(
                    Mathf.Max(s.x, 0.0001f),
                    Mathf.Max(s.y, 0.0001f),
                    Mathf.Max(s.z, 0.0001f));
            }
            return Vector3.one;
        }

        /// <summary>
        /// Turns a desired world size into a localScale, cancelling both the mesh's modelled
        /// size and any scale inherited from the parent, so the inspector fields mean metres.
        /// </summary>
        private static Vector3 WorldSizeToLocalScale(Transform t, Vector3 worldSize, Vector3 meshSize)
        {
            Vector3 p = t.parent != null ? t.parent.lossyScale : Vector3.one;
            return new Vector3(
                worldSize.x / (meshSize.x * Mathf.Max(Mathf.Abs(p.x), 0.0001f)),
                worldSize.y / (meshSize.y * Mathf.Max(Mathf.Abs(p.y), 0.0001f)),
                worldSize.z / (meshSize.z * Mathf.Max(Mathf.Abs(p.z), 0.0001f)));
        }

        private bool TryGetBeamTarget(out RaycastHit best)
        {
            best = default;

            int count = Physics.RaycastNonAlloc(tip.position, tip.forward, _hitBuffer, maxRange, hitMask, QueryTriggerInteraction.Ignore);
            if (count <= 0) return false;

            Transform self = transform;
            float nearest = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                Collider c = _hitBuffer[i].collider;
                if (c == null) continue;

                Transform ht = c.transform;
                if (ht == self || ht.IsChildOf(self)) continue;
                if (HasIgnoredTag(ht)) continue;

                if (_hitBuffer[i].distance < nearest)
                {
                    nearest = _hitBuffer[i].distance;
                    best = _hitBuffer[i];
                    found = true;
                }
            }

            return found;
        }

        private bool HasIgnoredTag(Transform t)
        {
            if (string.IsNullOrEmpty(ignoreTag)) return false;

            while (t != null)
            {
                if (t.CompareTag(ignoreTag)) return true;
                t = t.parent;
            }

            return false;
        }

        private void SetVisualsVisible(bool visible)
        {
            if (beam != null && beam.gameObject.activeSelf != visible) beam.gameObject.SetActive(visible);
            if (!visible && dot != null && dot.gameObject.activeSelf) dot.gameObject.SetActive(false);
        }
    }

}