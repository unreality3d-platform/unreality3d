using UnityEngine;
using UnityEngine.Events;
using System.Collections;

namespace U3D
{
    /// <summary>
    /// Animates one blend shape on a SkinnedMeshRenderer toward a target weight.
    /// Wire its public methods to any U3D trigger event (Enter, Exit, Trigger Zone,
    /// Interact) or to U3DGrabbable.OnImpact in the Inspector.
    ///
    /// Local visual effect: it morphs on whichever client runs the event that calls it.
    /// It does not sync the blend shape across the network.
    /// </summary>
    public class U3DBlendShape : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("The SkinnedMeshRenderer that has the blend shape. Auto-filled from this object or its children when you add the component.")]
        [SerializeField] private SkinnedMeshRenderer skinnedMesh;

        [Tooltip("Which blend shape to drive (0 if the mesh has only one)")]
        [SerializeField] private int blendShapeIndex = 0;

        [Header("Weights")]
        [Tooltip("The 'changed' weight Morph In animates toward (normally 0–100)")]
        [SerializeField] private float fullWeight = 100f;

        [Tooltip("The 'resting' weight Morph Out animates toward (normally 0–100)")]
        [SerializeField] private float restingWeight = 0f;

        [Header("Animation")]
        [Tooltip("How long an animated morph takes, in seconds. 0 = instant.")]
        [SerializeField] private float morphDuration = 1f;

        [Tooltip("Easing curve for the morph")]
        [SerializeField] private AnimationCurve morphCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Events")]
        public UnityEvent OnMorphStarted;
        public UnityEvent OnMorphComplete;

        private Coroutine _morphCoroutine;

        private void Reset()
        {
            if (skinnedMesh == null)
                skinnedMesh = GetComponentInChildren<SkinnedMeshRenderer>();
        }

        private void Awake()
        {
            if (skinnedMesh == null)
                skinnedMesh = GetComponentInChildren<SkinnedMeshRenderer>();
        }

        // ---- Parameterless methods (easiest to wire from a UnityEvent) ----

        /// <summary>Animate toward Full Weight.</summary>
        public void MorphIn() => AnimateTo(fullWeight);

        /// <summary>Animate toward Resting Weight.</summary>
        public void MorphOut() => AnimateTo(restingWeight);

        /// <summary>Animate toward whichever of Full / Resting weight is farther from the current weight.</summary>
        public void Toggle()
        {
            if (!Ready()) return;
            float current = skinnedMesh.GetBlendShapeWeight(blendShapeIndex);
            float target = Mathf.Abs(current - fullWeight) < Mathf.Abs(current - restingWeight)
                ? restingWeight
                : fullWeight;
            AnimateTo(target);
        }

        // ---- Float methods (type the exact target in the event field) ----

        /// <summary>Animate toward an explicit target weight over Morph Duration.</summary>
        public void AnimateTo(float targetWeight)
        {
            if (!Ready()) return;

            if (_morphCoroutine != null)
                StopCoroutine(_morphCoroutine);

            _morphCoroutine = StartCoroutine(MorphCoroutine(targetWeight));
        }

        /// <summary>Set the blend shape weight immediately, with no animation.</summary>
        public void SetWeight(float targetWeight)
        {
            if (!Ready()) return;

            if (_morphCoroutine != null)
            {
                StopCoroutine(_morphCoroutine);
                _morphCoroutine = null;
            }

            skinnedMesh.SetBlendShapeWeight(blendShapeIndex, targetWeight);
        }

        private IEnumerator MorphCoroutine(float targetWeight)
        {
            OnMorphStarted?.Invoke();

            float startWeight = skinnedMesh.GetBlendShapeWeight(blendShapeIndex);

            if (morphDuration > 0f)
            {
                float elapsed = 0f;
                while (elapsed < morphDuration)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / morphDuration);
                    float curved = morphCurve.Evaluate(t);
                    skinnedMesh.SetBlendShapeWeight(blendShapeIndex, Mathf.Lerp(startWeight, targetWeight, curved));
                    yield return null;
                }
            }

            skinnedMesh.SetBlendShapeWeight(blendShapeIndex, targetWeight);
            _morphCoroutine = null;
            OnMorphComplete?.Invoke();
        }

        private bool Ready()
        {
            if (skinnedMesh == null)
            {
                Debug.LogWarning($"U3DBlendShape on '{name}': No SkinnedMeshRenderer assigned.");
                return false;
            }
            return true;
        }

        private void OnValidate()
        {
            if (blendShapeIndex < 0) blendShapeIndex = 0;
            if (morphDuration < 0f) morphDuration = 0f;
        }
    }
}