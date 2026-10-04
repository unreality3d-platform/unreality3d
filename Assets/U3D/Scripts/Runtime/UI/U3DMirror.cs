using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Renders a static reflection from the mirror surface outward. The reflection camera
    /// sits at the mirror surface and looks along its forward axis — like a security camera
    /// pointed at the room. The view does not follow the player.
    /// </summary>
    public class U3DMirror : MonoBehaviour
    {
        [Tooltip("The camera that renders the reflection. Set automatically when the mirror is created.")]
        public Camera reflectionCamera;

        [Tooltip("The quad that displays the reflection. Used to position and aim the reflection camera.")]
        public Transform mirrorSurface;

        void Awake()
        {
            AlignCameraToSurface();
        }

        void OnValidate()
        {
            // Keep the camera aligned in the editor if the creator moves or rotates the mirror.
            AlignCameraToSurface();
        }

        private void AlignCameraToSurface()
        {
            if (reflectionCamera == null || mirrorSurface == null) return;

            reflectionCamera.transform.position = mirrorSurface.position;
            // Face opposite the mirror surface — the camera looks out at the room, not through the back of the mirror.
            reflectionCamera.transform.rotation = mirrorSurface.rotation * Quaternion.Euler(0f, 180f, 0f);

            // Match aspect to the render texture so the image isn't stretched.
            if (reflectionCamera.targetTexture != null)
            {
                RenderTexture rt = reflectionCamera.targetTexture;
                reflectionCamera.aspect = (float)rt.width / rt.height;
            }
        }
    }
}