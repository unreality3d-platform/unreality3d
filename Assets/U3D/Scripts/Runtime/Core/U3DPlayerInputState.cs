using UnityEngine;

/// <summary>
/// Local input snapshot for the local player. Not networked: in Shared Mode the local
/// player holds state authority over its own object, so input is consumed on the same
/// machine that produced it and never needs serializing.
/// Pressed fields are one-shot edges, cleared when the struct is consumed.
/// Held fields are level state, refreshed every Update.
/// </summary>
public struct U3DPlayerInputState
{
    public Vector2 MovementInput;
    public Vector2 LookInput;
    public float PerspectiveScroll;

    public bool JumpPressed;
    public bool JumpHeld;
    public bool SprintPressed;
    public bool CrouchPressed;
    public bool CrouchHeld;
    public bool FlyPressed;
    public bool InteractPressed;
    public bool TeleportPressed;
    public bool AutoRunTogglePressed;
    public bool RemovePressed;
    public bool ZoomHeld;

    public bool LeftMouseHeld;
    public bool RightMouseHeld;
    public bool BothMouseHeld;

    public bool StrafeLeft;
    public bool StrafeRight;
    public bool TurnLeft;
    public bool TurnRight;
}