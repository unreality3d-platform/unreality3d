using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Position marker that lives inside an accessory prefab as a child, marking WHERE the target
    /// bone sits inside the accessory — for a hat, where the base of the skull would be; for glasses,
    /// where the head's center sits behind the lenses. You position it against the accessory you can
    /// see, not against an avatar. At runtime U3DPlayerAttachments slides the accessory so this
    /// marker lands on the real bone, on any rig.
    ///
    /// Target Bone picks which Humanoid bone the marker snaps to — the same bone on any humanoid rig,
    /// regardless of its bone names. Bone Name Override is an optional escape hatch for non-humanoid
    /// rigs or a custom socket: an exact child name to attach to instead.
    ///
    /// The blue arrow gizmo shows the authored forward. Author the accessory's forward to match the
    /// way it should face on the body.
    ///
    /// Added automatically by the Creator Dashboard "Make Attachment" tool to the referenced prefab.
    /// </summary>
    public class U3DAttachmentPoint : MonoBehaviour
    {
        [Tooltip("Which bone on the avatar this accessory rides, by Humanoid role — resolves to the matching bone on any humanoid rig regardless of its bone names.")]
        [SerializeField] private HumanBodyBones targetBone = HumanBodyBones.Head;

        [Tooltip("Optional. For non-humanoid rigs or a custom socket: the exact name of a child object on the avatar to attach to instead of the Humanoid bone above. Leave empty to use the bone role.")]
        [SerializeField] private string boneNameOverride = "";

        public HumanBodyBones TargetBone => targetBone;
        public string BoneNameOverride => boneNameOverride;

        private void OnDrawGizmos()
        {
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.9f);
            Gizmos.DrawSphere(origin, 0.04f);

            Gizmos.color = new Color(0.4f, 0.7f, 1f, 0.8f);
            Vector3 tip = origin + forward * 0.4f;
            Gizmos.DrawLine(origin, tip);

            Vector3 right = transform.right;
            Gizmos.DrawLine(tip, tip - forward * 0.12f + right * 0.08f);
            Gizmos.DrawLine(tip, tip - forward * 0.12f - right * 0.08f);
        }
    }
}