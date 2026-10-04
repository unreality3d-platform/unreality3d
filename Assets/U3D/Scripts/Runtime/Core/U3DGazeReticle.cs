using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Hides the gaze reticle when the local player is in third-person, shows it otherwise.
    /// Lives on the Reticle GameObject (a child of PlayerCamera in U3D_PlayerController).
    ///
    /// Fail-safe toward visible: the reticle is shown unless we positively know the local
    /// player exists AND is in third-person AND is not in VR. Any uncertainty (player not
    /// resolved yet, VR active) leaves the reticle visible. This matches the design intent
    /// that the reticle is a persistent aim point that should only disappear in the one
    /// case where a head-centered reticle is wrong: third-person, where the camera is
    /// behind the avatar and the reticle would bleed through the avatar's head.
    ///
    /// Local player is resolved lazily via U3DPlayerController.FindLocalPlayer() (the
    /// same accessor U3DMovable and U3DInteractionManager use) and cached once found,
    /// because the player is network-spawned and may not exist on this object's first frame.
    /// </summary>
    public class U3DGazeReticle : MonoBehaviour
    {
        private U3DPlayerController _player;
        private Renderer _reticleRenderer;

        private void Awake()
        {
            _reticleRenderer = GetComponent<Renderer>();
        }

        private void Update()
        {
            // Lazy-resolve and cache the local player. The reticle GameObject exists as a
            // child of the camera, so the rig is present, but the local player may not be
            // spawned yet on this object's early frames. Keep trying until it resolves,
            // then stop.
            if (_player == null)
            {
                _player = U3DPlayerController.FindLocalPlayer();
                if (_player == null) return;
            }

            if (_reticleRenderer == null) return;

            // Hide ONLY when we positively know: local player exists, not first-person,
            // not VR. Everything else (VR, first-person, unresolved) → visible.
            bool hide = !_player.IsFirstPerson && !_player.IsInVRMode;

            if (_reticleRenderer.enabled == hide)
                _reticleRenderer.enabled = !hide;
        }
    }
}