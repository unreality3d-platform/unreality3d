// U3DVRTeleporter.cs
// Assets/U3D/Scripts/Runtime/XR/U3DVRTeleporter.cs

using UnityEngine;

namespace U3D.XR
{
    /// <summary>
    /// VR teleport with click-to-arm gesture and flick-forward-to-fire.
    ///
    /// Three states:
    ///  - Off: normal locomotion, no visuals, no teleport.
    ///  - Armed (idle): locomotion suppressed, no visuals. Stick click toggles back to Off.
    ///  - Aiming: stick pushed forward past 0.8. Arc + reticle visible. Releasing below 0.5
    ///    fires teleport (if valid) and returns to Armed. Stick click cancels back to Off.
    ///
    /// Appearance is driven entirely by the assigned ArcMaterial. Validity is communicated
    /// by reticle presence: reticle appears only at valid landing points.
    /// </summary>
    public class U3DVRTeleporter : MonoBehaviour
    {
        // ── Tuning constants (not creator-exposed) -----------------------------────
        private const float ArcVelocity = 12f;
        private const float ArcGravity = 14f;
        private const int ArcSegments = 30;
        private const float MaxArcDistance = 25f;
        private const float ArcStepTime = 0.1f;

        private const float ArcLineWidth = 0.02f;
        private const float ReticleRadius = 0.4f;
        private const int ReticleSegments = 32;

        private const float AimEnterThreshold = 0.8f;
        private const float AimReleaseThreshold = 0.5f;

        // Head-local offset for arc origin: down 0.3m, forward 0.2m from the eye position.
        // Keeps the arc visible without obstructing forward view.
        private static readonly Vector3 ArcOriginHeadLocalOffset = new Vector3(0f, -0.3f, 0.2f);

        // Material slot — assigned by U3DPlayerController immediately after AddComponent
        // and before Initialize(). The arc and reticle render in whatever color/opacity
        // this material defines.
        public Material ArcMaterial;

        // ── State ----------------------------------------------------------────────────
        private enum TeleportState { Off, Armed, Aiming }
        private TeleportState _state = TeleportState.Off;
        private bool _hasValidTarget;
        private Vector3 _targetPosition;

        // Visuals
        private LineRenderer _arcLine;
        private LineRenderer _reticleLine;
        private GameObject _visualRoot;

        // Owner references
        private U3DPlayerController _controller;
        private Camera _playerCamera;
        private int _teleportLayerMask;

        // ── Public API ----------------------------------------------------------───────

        /// <summary>True while the teleporter is consuming the left stick (Armed or Aiming).</summary>
        public bool IsArmed => _state != TeleportState.Off;

        public void Initialize(U3DPlayerController controller, Camera camera)
        {
            _controller = controller;
            _playerCamera = camera;
            _teleportLayerMask = ~(LayerMask.GetMask("Ignore Raycast") | LayerMask.GetMask("Player"));
            BuildVisuals();
            HideVisuals();
        }

        public void UpdateCamera(Camera camera)
        {
            _playerCamera = camera;
        }

        void OnDestroy()
        {
            if (_visualRoot != null)
                Destroy(_visualRoot);
        }

        /// <summary>
        /// Called when the Teleport button (left stick click) is pressed.
        /// Toggles between Off and Armed. If currently Aiming, click cancels back to Off
        /// without firing.
        /// </summary>
        public void OnTeleportButtonPressed()
        {
            if (_state == TeleportState.Off)
            {
                _state = TeleportState.Armed;
            }
            else
            {
                _state = TeleportState.Off;
                _hasValidTarget = false;
                HideVisuals();
            }
        }

        /// <summary>
        /// Called every fixed step while in VR mode. Compares the stick against the
        /// arm and release thresholds and returns true if locomotion should be
        /// suppressed. Safe to call zero, one or more times per frame: every branch
        /// tests the stick's current position rather than an edge, and a released
        /// stick cannot re-cross the enter threshold, so a repeat call cannot fire
        /// a second teleport. Drawing lives in RenderAim, which runs at frame rate.
        /// </summary>
        public bool Tick(float stickY)
        {
            switch (_state)
            {
                case TeleportState.Off:
                    return false;

                case TeleportState.Armed:
                    if (stickY > AimEnterThreshold)
                        _state = TeleportState.Aiming;
                    return true;

                case TeleportState.Aiming:
                    if (stickY < AimReleaseThreshold)
                    {
                        // Release: fire if valid, then return to Armed (ready to flick again).
                        if (_hasValidTarget)
                            ExecuteTeleport();
                        _hasValidTarget = false;
                        _state = TeleportState.Armed;
                        HideVisuals();
                    }
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Traces the arc and updates the visuals. Called from the player controller's
        /// LateUpdate, after the avatar's bones are posed and after the Tracked Pose
        /// Driver has written the camera's rotation for this frame. Both values this
        /// method reads — the VR eye position and camera forward — change every frame,
        /// so tracing on the fixed step left the arc trailing the head, worst on a
        /// headset whose display rate is above the physics rate. Does nothing unless
        /// the stick is pushed forward past the aim threshold.
        /// </summary>
        public void RenderAim()
        {
            if (_state != TeleportState.Aiming) return;
            SimulateAndDraw();
        }

        /// <summary>Cleans up state without firing — call on VR exit.</summary>
        public void CancelAim()
        {
            _state = TeleportState.Off;
            _hasValidTarget = false;
            HideVisuals();
        }

        // ── Internal ----------------------------------------------------------─────────

        private void SimulateAndDraw()
        {
            if (_playerCamera == null || _controller == null)
                return;

            // Origin: anchored to the same eye position the camera uses in VR, then offset
            // down and forward in head-local space so the arc doesn't obstruct forward view.
            Vector3 eyePos = _controller.GetVREyePosition();
            Vector3 camRight = _playerCamera.transform.right;
            Vector3 camUp = _playerCamera.transform.up;
            Vector3 camForward = _playerCamera.transform.forward;

            Vector3 origin = eyePos
                + camRight * ArcOriginHeadLocalOffset.x
                + camUp * ArcOriginHeadLocalOffset.y
                + camForward * ArcOriginHeadLocalOffset.z;

            // Aim direction: camera-forward, clamped so we never launch above horizontal.
            Vector3 aimDir = new Vector3(camForward.x, Mathf.Min(camForward.y, 0f), camForward.z);
            if (aimDir.sqrMagnitude < 0.0001f)
                aimDir = Vector3.down;
            else
                aimDir.Normalize();

            Vector3 launchVelocity = aimDir * ArcVelocity;

            Vector3[] points = new Vector3[ArcSegments];
            _hasValidTarget = false;
            _targetPosition = Vector3.zero;

            Transform controllerTransform = _controller.transform;
            int hitIndex = -1;

            for (int i = 0; i < ArcSegments; i++)
            {
                float t = i * ArcStepTime;
                points[i] = origin + launchVelocity * t + Vector3.down * (ArcGravity * t * t * 0.5f);

                if (i == 0) continue;

                Vector3 segStart = points[i - 1];
                Vector3 segEnd = points[i];
                Vector3 dir = segEnd - segStart;
                float segLen = dir.magnitude;

                if (Vector3.Distance(origin, segEnd) > MaxArcDistance)
                {
                    TruncateArc(points, i);
                    hitIndex = i - 1;
                    break;
                }

                if (segLen > 0.0001f)
                {
                    RaycastHit[] hits = Physics.RaycastAll(segStart, dir / segLen, segLen, _teleportLayerMask);
                    RaycastHit best = default;
                    bool foundValid = false;
                    float closest = float.MaxValue;

                    for (int h = 0; h < hits.Length; h++)
                    {
                        var hit = hits[h];
                        if (hit.collider.isTrigger) continue;
                        if (hit.collider.transform == controllerTransform ||
                            hit.collider.transform.IsChildOf(controllerTransform))
                            continue;
                        if (hit.distance < closest)
                        {
                            closest = hit.distance;
                            best = hit;
                            foundValid = true;
                        }
                    }

                    if (foundValid)
                    {
                        points[i] = best.point;
                        TruncateArc(points, i + 1);
                        _hasValidTarget = true;
                        _targetPosition = best.point;
                        hitIndex = i;
                        break;
                    }
                }
            }

            if (hitIndex < 0) hitIndex = ArcSegments - 1;

            DrawArc(points);
            DrawReticle(_targetPosition, _hasValidTarget);
            ShowVisuals();
        }

        private void TruncateArc(Vector3[] points, int usedCount)
        {
            if (usedCount <= 0 || usedCount >= points.Length) return;
            Vector3 last = points[usedCount - 1];
            for (int j = usedCount; j < points.Length; j++)
                points[j] = last;
        }

        private void ExecuteTeleport()
        {
            if (_controller == null) return;

            CharacterController cc = _controller.CharacterController;
            float playerHeight = cc != null ? cc.height : 2f;
            Vector3 teleportPos = _targetPosition;
            teleportPos.y += playerHeight * 0.5f + 0.1f;

            // SetPosition handles unparenting, CC toggle, velocity zero, and network writes.
            _controller.SetPosition(teleportPos);
        }

        private void BuildVisuals()
        {
            _visualRoot = new GameObject("VRTeleportVisuals");
            _visualRoot.transform.SetParent(null);

            _arcLine = CreateLineRenderer("ArcLine", ArcLineWidth, ArcSegments);
            _reticleLine = CreateLineRenderer("ReticleLine", ArcLineWidth * 1.5f, ReticleSegments + 1);

            _arcLine.transform.SetParent(_visualRoot.transform, false);
            _reticleLine.transform.SetParent(_visualRoot.transform, false);
        }

        private LineRenderer CreateLineRenderer(string goName, float width, int positionCount)
        {
            var go = new GameObject(goName);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.positionCount = positionCount;

            // Initialize all positions to zero and disable until first use so a stray
            // enable doesn't render a degenerate line at world origin.
            for (int i = 0; i < positionCount; i++)
                lr.SetPosition(i, Vector3.zero);
            lr.enabled = false;

            lr.numCapVertices = 4;
            lr.numCornerVertices = 4;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            // Material drives all appearance. If unassigned, the LineRenderer renders
            // nothing — better than magenta from a stripped Shader.Find result.
            if (ArcMaterial != null)
                lr.material = ArcMaterial;

            return lr;
        }

        private void DrawArc(Vector3[] points)
        {
            _arcLine.positionCount = points.Length;
            _arcLine.SetPositions(points);
        }

        private void DrawReticle(Vector3 center, bool valid)
        {
            if (!valid)
            {
                _reticleLine.enabled = false;
                return;
            }

            Vector3 up = Vector3.up * 0.01f;
            for (int i = 0; i <= ReticleSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / ReticleSegments;
                Vector3 point = center + up + new Vector3(
                    Mathf.Cos(angle) * ReticleRadius,
                    0f,
                    Mathf.Sin(angle) * ReticleRadius);
                _reticleLine.SetPosition(i, point);
            }
        }

        private void ShowVisuals()
        {
            if (_arcLine != null) _arcLine.enabled = true;
            if (_reticleLine != null) _reticleLine.enabled = _hasValidTarget;
        }

        private void HideVisuals()
        {
            if (_arcLine != null) _arcLine.enabled = false;
            if (_reticleLine != null) _reticleLine.enabled = false;
        }
    }
}