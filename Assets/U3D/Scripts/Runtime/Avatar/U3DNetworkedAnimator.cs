using UnityEngine;

/// <summary>
/// Drives the avatar's Animator parameters.
///
/// Two entry points, because the two directions are genuinely different code rather than
/// one body behind an authority test. ApplyLocalState reads this player's own controller
/// and sets parameters from it, and runs every frame on the local player. ApplyRemoteState
/// writes the same fourteen parameters for another player's body, from the flags and movement
/// their packets described, and is called by the playback buffer rather than from Update so
/// the parameters belong to the moment being shown.
///
/// A remote avatar carries this component and an Animator, and no controller, so
/// ApplyLocalState returns immediately on one and ApplyRemoteState is the only writer.
///
/// Parameters are set in Update, at execution order 102: after the player controller has
/// handled input at 100 and the avatar manager has run at 101, and before Unity evaluates
/// the Animator, which happens after all Update calls.
/// </summary>
[DefaultExecutionOrder(102)]
public class U3DNetworkedAnimator : MonoBehaviour
{
    [Header("Animation Controller")]
    [SerializeField] private RuntimeAnimatorController animatorController;

    private Animator targetAnimator;
    private U3DPlayerController playerController;

    private int hashIsMoving;
    private int hashIsCrouching;
    private int hashIsFlying;
    private int hashIsSwimming;
    private int hashIsGrounded;
    private int hashIsClimbing;
    private int hashIsJumping;
    private int hashIsSeated;
    private int hashIsPushing;
    private int hashIsPulling;
    private int hashMoveSpeed;
    private int hashMoveX;
    private int hashMoveY;
    private int hashJumpTrigger;

    private bool lastIsJumping;

    /// <summary>
    /// True once a real avatar Animator has been bound. Null until U3DAvatarManager
    /// finishes building the avatar and hands one over, which is normal for the first
    /// frames of a player's life and permanent for a player whose avatar FBX is empty.
    /// </summary>
    public bool IsInitialized => targetAnimator != null;

    void Awake()
    {
        // Null on the remote avatar prefab, which carries no controller. ApplyLocalState
        // is the only thing that reads it and it returns early when there is none.
        playerController = GetComponent<U3DPlayerController>();

        CacheParameterIDs();
    }

    void CacheParameterIDs()
    {
        hashIsMoving = Animator.StringToHash("IsMoving");
        hashIsCrouching = Animator.StringToHash("IsCrouching");
        hashIsFlying = Animator.StringToHash("IsFlying");
        hashIsSwimming = Animator.StringToHash("IsSwimming");
        hashIsGrounded = Animator.StringToHash("IsGrounded");
        hashIsClimbing = Animator.StringToHash("IsClimbing");
        hashIsJumping = Animator.StringToHash("IsJumping");
        hashIsSeated = Animator.StringToHash("IsSeated");
        hashIsPushing = Animator.StringToHash("IsPushing");
        hashIsPulling = Animator.StringToHash("IsPulling");
        hashMoveSpeed = Animator.StringToHash("MoveSpeed");
        hashMoveX = Animator.StringToHash("MoveX");
        hashMoveY = Animator.StringToHash("MoveY");
        hashJumpTrigger = Animator.StringToHash("JumpTrigger");
    }

    void Update()
    {
        ApplyLocalState();
    }

    /// <summary>
    /// Reads this player's own controller and writes the Animator parameters from it.
    /// Does nothing on a remote avatar, which has no controller, and nothing before the
    /// avatar manager has bound a real Animator.
    /// </summary>
    void ApplyLocalState()
    {
        if (targetAnimator == null) return;
        if (playerController == null) return;

        bool isMoving = playerController.IsMoving;
        bool isCrouching = playerController.IsCrouching;
        bool isFlying = playerController.IsFlying;
        bool isGrounded = playerController.IsGrounded;
        bool isJumping = playerController.IsJumping;
        bool isSwimming = playerController.IsSwimming;
        bool isClimbing = playerController.IsClimbing;
        bool isSeated = playerController.IsSeated;

        float moveSpeed = isMoving ? playerController.CurrentSpeed : 0f;

        // The controller supplies this already expressed in the avatar's own frame, so a
        // strafe reads as a strafe even when the camera has been orbited away from the
        // body's heading. It was previously derived from a vertical-only velocity vector,
        // whose horizontal components were zero on every frame since inception, so the
        // directional half of the blend tree has never received a signal until now.
        Vector2 moveDirection = playerController.MoveIntent;

        if (playerController.SuppressLocomotion)
        {
            isMoving = false;
            moveSpeed = 0f;
            moveDirection = Vector2.zero;
        }

        // Push and pull animate while the mode is engaged and the player is actually
        // moving, so the body leaves the push cycle the moment they stop rather than at
        // the end of the push. Derived from the same isMoving the speed uses, which is
        // what keeps the local body and every remote copy on one rule.
        bool isPushing = playerController.IsPushing && isMoving;
        bool isPulling = playerController.IsPulling && isMoving;

        targetAnimator.SetBool(hashIsMoving, isMoving);
        targetAnimator.SetBool(hashIsCrouching, isCrouching);
        targetAnimator.SetBool(hashIsFlying, isFlying);
        targetAnimator.SetBool(hashIsSwimming, isSwimming);
        targetAnimator.SetBool(hashIsGrounded, isGrounded);
        targetAnimator.SetBool(hashIsClimbing, isClimbing);
        targetAnimator.SetBool(hashIsJumping, isJumping);
        targetAnimator.SetBool(hashIsSeated, isSeated);
        targetAnimator.SetBool(hashIsPushing, isPushing);
        targetAnimator.SetBool(hashIsPulling, isPulling);

        targetAnimator.SetFloat(hashMoveSpeed, moveSpeed);
        targetAnimator.SetFloat(hashMoveX, moveDirection.x);
        targetAnimator.SetFloat(hashMoveY, moveDirection.y);

        if (isJumping && !lastIsJumping)
            targetAnimator.SetTrigger(hashJumpTrigger);

        lastIsJumping = isJumping;
    }

    /// <summary>
    /// Writes the Animator parameters for another player's body, from the flags and the
    /// movement their packets described. The receiving half of the split this component was
    /// built around: the same fourteen parameters as the local path, from a different source.
    ///
    /// Called by the playback buffer rather than from Update, so the parameters belong to
    /// the moment being shown rather than to whatever arrived most recently.
    /// </summary>
    public void ApplyRemoteState(bool isMoving, bool isCrouching, bool isFlying, bool isSwimming,
            bool isGrounded, bool isClimbing, bool isJumping, bool isSeated,
            bool isPushing, bool isPulling,
            float moveSpeed, Vector2 moveDirection)
    {
        if (targetAnimator == null) return;

        targetAnimator.SetBool(hashIsMoving, isMoving);
        targetAnimator.SetBool(hashIsCrouching, isCrouching);
        targetAnimator.SetBool(hashIsFlying, isFlying);
        targetAnimator.SetBool(hashIsSwimming, isSwimming);
        targetAnimator.SetBool(hashIsGrounded, isGrounded);
        targetAnimator.SetBool(hashIsClimbing, isClimbing);
        targetAnimator.SetBool(hashIsJumping, isJumping);
        targetAnimator.SetBool(hashIsSeated, isSeated);
        targetAnimator.SetBool(hashIsPushing, isPushing);
        targetAnimator.SetBool(hashIsPulling, isPulling);

        targetAnimator.SetFloat(hashMoveSpeed, moveSpeed);
        targetAnimator.SetFloat(hashMoveX, moveDirection.x);
        targetAnimator.SetFloat(hashMoveY, moveDirection.y);

        if (isJumping && !lastIsJumping)
            targetAnimator.SetTrigger(hashJumpTrigger);

        lastIsJumping = isJumping;
    }

    /// <summary>
    /// How fast another player is moving, from the flags their packet carried. Not sent:
    /// the speeds are authored on a prefab every player runs identically, so both machines
    /// reach the same number from the sprint and crouch flags alone, and a derived number
    /// cannot disagree with the body it belongs to the way a sent one can.
    ///
    /// Read off the local player's own controller, which is the one instance of those
    /// authored values reachable from here — a remote avatar carries no controller.
    /// </summary>
    public static float DeriveRemoteSpeed(bool isMoving, bool isSprinting, bool isCrouching)
    {
        if (!isMoving) return 0f;

        U3DPlayerController local = U3DPlayerController.FindLocalPlayer();
        if (local == null) return 0f;

        if (isSprinting) return local.RunSpeed;
        if (isCrouching) return local.WalkSpeed * 0.5f;
        return local.WalkSpeed;
    }

    /// <summary>
    /// Fires a one-shot Trigger parameter. Used for action animations like kick and throw
    /// that originate from interaction components. The trigger name must exist as a Trigger
    /// parameter in U3DAnimatorController.
    ///
    /// Called on the local avatar by the acting player, and on a remote avatar by the
    /// interaction component when that object's event message arrives naming the sender.
    /// Nothing about the trigger travels in the avatar packet. F23
    /// </summary>
    public void TriggerAnimation(string triggerName)
    {
        if (targetAnimator == null) return;
        targetAnimator.SetTrigger(triggerName);
    }

    /// <summary>
    /// Binds the avatar's Animator and assigns the animator controller to it. Called by
    /// U3DAvatarManager once the avatar instance exists. This is the only Animator this
    /// component ever drives — there is no placeholder on the player root and no deferral,
    /// because nothing here needs an Animator before this call arrives.
    /// </summary>
    public void SetAvatarAnimator(Animator avatarAnimator)
    {
        if (avatarAnimator == null) return;

        if (animatorController == null)
        {
            Debug.LogError("U3DNetworkedAnimator: No Animator Controller assigned. Assign U3DAnimatorController in the Inspector — without it this avatar will not animate.");
            return;
        }

        avatarAnimator.runtimeAnimatorController = animatorController;
        targetAnimator = avatarAnimator;
        lastIsJumping = false;
    }
}