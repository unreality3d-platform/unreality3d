using UnityEngine;

namespace U3D
{
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