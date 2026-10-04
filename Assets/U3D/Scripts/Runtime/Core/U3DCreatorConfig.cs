using UnityEngine;

namespace U3D
{
    [CreateAssetMenu(fileName = "U3DCreatorData", menuName = "U3D/Creator Data")]
    public class U3DCreatorData : ScriptableObject
    {
        [SerializeField] private string paypalEmail = "";

        public string PayPalEmail
        {
            get => paypalEmail;
            set => paypalEmail = value;
        }
    }
}