using UnityEngine;
using U3D.Net;
using U3DNet = U3D.Net.Net;

namespace U3D
{
    public enum RideableMovementMode
    {
        Waypoints,
        Rotation,
        Static
    }

    public enum RideableLoopMode
    {
        Loop,
        PingPong
    }

    /// <summary>
    /// Moves a platform along an authored path, or spins it in place, so players can ride it.
    /// Mounting is handled by U3DRideableTrigger on a child collider.
    ///
    /// Every machine runs the route itself. There is no shared clock, so the routes would
    /// drift apart on their own — a tab that stalls loses route steps permanently and never
    /// gets them back. The reporter broadcasts where the platform is and where it is heading,
    /// on reaching each waypoint and at least every two seconds, and every other machine
    /// eases onto that and carries on. A rider is parented to this transform, so correcting
    /// the platform carries them with it and nothing about riders goes on the wire.
    /// </summary>
    [RequireComponent(typeof(NetEntity))]
    public class U3DRideableController : NetComponent
    {
        [Header("Movement")]
        [SerializeField] private RideableMovementMode movementMode = RideableMovementMode.Waypoints;

        [SerializeField] private Transform[] waypoints;
        [SerializeField] private RideableLoopMode loopMode = RideableLoopMode.Loop;
        [SerializeField] private float speed = 3f;
        [SerializeField] private float pauseAtWaypoint = 0f;

        [SerializeField] private Vector3 rotationAxis = Vector3.up;
        [SerializeField] private float rotationSpeed = 45f;

        // How often a correction goes out even when no waypoint is reached. Covers long
        // straight runs and gives a newcomer a corrected platform within this window.
        private const float CorrectionIntervalSeconds = 2f;

        // A correction is absorbed over this long rather than applied at once, so a platform
        // being nudged does not jump under a rider's feet.
        private const float CorrectionEaseDuration = 0.25f;

        // Past these, easing would be a visible slide across the room, so the platform is
        // placed outright. This is a newcomer's first correction, or a machine returning
        // from a long stall.
        private const float PositionSnapDistance = 2f;
        private const float RotationSnapAngle = 30f;

        // Pause remainder in hundredths of a second in the low fifteen bits, ping-pong
        // direction in the top bit. The wire's field set holds no decimal number, and this
        // is two bytes rather than a permanent addition to that set.
        private const int PauseMask = 0x7FFF;
        private const int DirectionBit = 0x8000;

        // World-space waypoint positions cached at Start — waypoints live outside the platform
        // hierarchy so their positions never drift as the platform moves.
        private Vector3[] _waypointPositions;

        private int _currentWaypointIndex;
        private float _pauseTimer;
        private bool _pingPongForward = true;

        private NetMessage _correctionMessage;

        private float _sendTimer;
        private bool _waypointReachedThisStep;

        private Vector3 _positionError;
        private Quaternion _rotationError = Quaternion.identity;
        private bool _hasRotationError;

        private void Start()
        {
            CacheWaypointPositions();

            _pingPongForward = true;
            _currentWaypointIndex = 0;
            _pauseTimer = 0f;
        }

        protected override void OnNetSpawn()
        {
            _correctionMessage = RegisterMessage(NetKeys.RideableCorrection, HandleCorrection);
            _sendTimer = 0f;
        }

        /// <summary>
        /// Reads each waypoint's world position once. A null slot falls back to the platform's own
        /// position; OnValidate warns about that at author time, since a silent fallback reads as a
        /// route the creator did not draw.
        ///
        /// Runs regardless of movement mode. Knowing where the waypoints are is not the same
        /// question as whether the platform is currently following them, and tying the two together
        /// meant a mode set to Waypoints after Start left the route uncached and the platform
        /// stationary with nothing explaining why.
        /// </summary>
        private void CacheWaypointPositions()
        {
            if (waypoints == null || waypoints.Length == 0)
            {
                _waypointPositions = new Vector3[0];
                return;
            }

            _waypointPositions = new Vector3[waypoints.Length];
            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] != null)
                    _waypointPositions[i] = waypoints[i].position;
                else
                    _waypointPositions[i] = transform.position;
            }
        }

        private void FixedUpdate()
        {
            switch (movementMode)
            {
                case RideableMovementMode.Waypoints:
                    TickWaypoints();
                    break;
                case RideableMovementMode.Rotation:
                    TickRotation();
                    break;
                case RideableMovementMode.Static:
                    break;
            }

            AbsorbCorrection();
        }

        private void TickWaypoints()
        {
            if (_waypointPositions == null || _waypointPositions.Length == 0) return;

            if (_pauseTimer > 0f)
            {
                _pauseTimer -= Time.fixedDeltaTime;
                return;
            }

            Vector3 target = _waypointPositions[_currentWaypointIndex];
            Vector3 toTarget = target - transform.position;
            float distanceThisFrame = speed * Time.fixedDeltaTime;

            if (toTarget.magnitude <= distanceThisFrame)
            {
                transform.position = target;
                _pauseTimer = pauseAtWaypoint;
                AdvanceWaypoint();
                _waypointReachedThisStep = true;
            }
            else
            {
                transform.position += toTarget.normalized * distanceThisFrame;
            }
        }

        private void AdvanceWaypoint()
        {
            if (_waypointPositions.Length <= 1) return;

            if (loopMode == RideableLoopMode.Loop)
            {
                _currentWaypointIndex = (_currentWaypointIndex + 1) % _waypointPositions.Length;
            }
            else // PingPong
            {
                if (_pingPongForward)
                {
                    int next = _currentWaypointIndex + 1;
                    if (next >= _waypointPositions.Length)
                    {
                        _currentWaypointIndex = _waypointPositions.Length - 2;
                        _pingPongForward = false;
                    }
                    else
                    {
                        _currentWaypointIndex = next;
                    }
                }
                else
                {
                    int next = _currentWaypointIndex - 1;
                    if (next < 0)
                    {
                        _currentWaypointIndex = 1;
                        _pingPongForward = true;
                    }
                    else
                    {
                        _currentWaypointIndex = next;
                    }
                }
            }
        }

        private void TickRotation()
        {
            float angle = rotationSpeed * Time.fixedDeltaTime;
            transform.Rotate(rotationAxis.normalized, angle, Space.Self);
        }

        // ==================== Correction: sending ====================

        /// <summary>
        /// The reporter speaks for the platform. Nothing claims a rideable, so there is no
        /// owner to ask — this is the same duty as publishing a settle for an object nobody
        /// owns. A correction goes out on reaching a waypoint, which is the one moment every
        /// machine's copy is at an identical authored number, and otherwise on a floor
        /// interval so a long straight run and a newcomer are both covered.
        /// </summary>
        protected override void OnNetTick()
        {
            if (movementMode == RideableMovementMode.Static) return;
            if (!IsLive || _correctionMessage == null) return;
            if (U3DNet.Session == null || !U3DNet.Session.IsReporter)
            {
                _waypointReachedThisStep = false;
                return;
            }

            _sendTimer += Time.fixedDeltaTime;

            if (_waypointReachedThisStep || _sendTimer >= CorrectionIntervalSeconds)
            {
                _correctionMessage.SendToAll(
                    transform.position,
                    transform.rotation,
                    (ushort)_currentWaypointIndex,
                    PackRouteState());

                _sendTimer = 0f;
            }

            _waypointReachedThisStep = false;
        }

        private ushort PackRouteState()
        {
            int packed = Mathf.Clamp(Mathf.RoundToInt(_pauseTimer * 100f), 0, PauseMask);
            if (_pingPongForward) packed |= DirectionBit;
            return (ushort)packed;
        }

        // ==================== Correction: receiving ====================

        private void HandleCorrection(PeerId sender, object[] args)
        {
            if (args == null || args.Length < 4) return;
            if (!(args[0] is Vector3 position)) return;
            if (!(args[1] is Quaternion rotation)) return;
            if (!(args[2] is ushort waypointIndex)) return;
            if (!(args[3] is ushort routeState)) return;

            if (U3DNet.Session == null) return;

            // The reporter hears its own broadcast, because a broadcast is delivered locally
            // inside the Send call. Correcting itself against itself is a no-op that would
            // still run the easing arithmetic every time.
            if (U3DNet.Session.IsReporter) return;

            PeerId reporter = U3DNet.Session.Reporter;
            if (!reporter.IsValid || sender != reporter) return;

            _pingPongForward = (routeState & DirectionBit) != 0;
            _pauseTimer = (routeState & PauseMask) / 100f;

            // An index past the end means the builds disagree about the route. Taking it
            // would index outside the array on the next step, so the position correction is
            // kept and the index left alone.
            if (_waypointPositions != null && waypointIndex < _waypointPositions.Length)
                _currentWaypointIndex = waypointIndex;

            Vector3 positionDelta = position - transform.position;

            if (positionDelta.magnitude > PositionSnapDistance)
            {
                transform.position = position;
                _positionError = Vector3.zero;
            }
            else
            {
                _positionError = positionDelta;
            }

            if (movementMode == RideableMovementMode.Rotation)
            {
                float angle = Quaternion.Angle(transform.rotation, rotation);

                if (angle > RotationSnapAngle)
                {
                    transform.rotation = rotation;
                    _rotationError = Quaternion.identity;
                    _hasRotationError = false;
                }
                else
                {
                    _rotationError = rotation * Quaternion.Inverse(transform.rotation);
                    _hasRotationError = true;
                }
            }
        }

        /// <summary>
        /// Feeds the outstanding correction in a little at a time, on top of whatever the
        /// route did this step. The route keeps running throughout — the correction is an
        /// extra nudge rather than a destination, which is what stops the two fighting.
        /// </summary>
        private void AbsorbCorrection()
        {
            float fraction = Mathf.Clamp01(Time.fixedDeltaTime / CorrectionEaseDuration);

            if (_positionError.sqrMagnitude > 1e-8f)
            {
                Vector3 step = _positionError * fraction;
                transform.position += step;
                _positionError -= step;
            }
            else
            {
                _positionError = Vector3.zero;
            }

            if (_hasRotationError)
            {
                Quaternion step = Quaternion.Slerp(Quaternion.identity, _rotationError, fraction);
                transform.rotation = step * transform.rotation;
                _rotationError = _rotationError * Quaternion.Inverse(step);

                if (Quaternion.Angle(Quaternion.identity, _rotationError) < 0.05f)
                {
                    _rotationError = Quaternion.identity;
                    _hasRotationError = false;
                }
            }
        }

        private void OnValidate()
        {
            if (speed < 0f) speed = 0f;
            if (pauseAtWaypoint < 0f) pauseAtWaypoint = 0f;

            if (movementMode == RideableMovementMode.Rotation && rotationAxis == Vector3.zero)
                Debug.LogWarning($"{name}: Rotation Axis is zero, so this rideable will not turn. Set an axis, for example Y for a carousel.", this);

            if (movementMode != RideableMovementMode.Waypoints) return;

            if (waypoints == null || waypoints.Length == 0)
            {
                Debug.LogWarning($"{name}: Movement Mode is Waypoints but no waypoints are assigned, so this rideable will not move.", this);
                return;
            }

            // Every empty slot is named in one warning. Reporting only the first meant a route with
            // several holes took a round of fix-and-recheck for each one.
            string emptySlots = "";
            int emptyCount = 0;

            for (int i = 0; i < waypoints.Length; i++)
            {
                if (waypoints[i] != null) continue;

                if (emptyCount > 0) emptySlots += ", ";
                emptySlots += i;
                emptyCount++;
            }

            if (emptyCount == 1)
                Debug.LogWarning($"{name}: waypoint {emptySlots} is empty. The platform will travel to its own starting position for that step instead of the route you drew.", this);
            else if (emptyCount > 1)
                Debug.LogWarning($"{name}: waypoints {emptySlots} are empty. The platform will travel to its own starting position for each of those steps instead of the route you drew.", this);
        }
    }
}