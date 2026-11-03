using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace U3D.UI
{
    /// <summary>
    /// One slot in the player menu bar: a button with the item's name, its stack count and
    /// the key that uses it. U3DPlayerMenu fills it in and handles the press.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DPlayerMenuSlot : MonoBehaviour
    {
        [SerializeField] private Button button;
        [SerializeField] private TextMeshProUGUI nameLabel;
        [SerializeField] private TextMeshProUGUI countLabel;
        [SerializeField] private TextMeshProUGUI keyLabel;

        public Button Button => button;

        public void Show(string itemName, int count, string key)
        {
            if (nameLabel != null) nameLabel.text = itemName;
            if (countLabel != null) countLabel.text = count > 1 ? "x" + count : string.Empty;
            if (keyLabel != null) keyLabel.text = key;
        }
    }
}
