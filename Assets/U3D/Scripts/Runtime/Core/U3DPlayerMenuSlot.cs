using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace U3D.UI
{
    /// <summary>
    /// One slot in the player menu bar: a button with a name, a stack count, the key that uses it,
    /// and an icon. U3DPlayerMenu fills it in and handles the press. The icon is created the first
    /// time one is shown, behind the labels.
    /// </summary>
    [DisallowMultipleComponent]
    public class U3DPlayerMenuSlot : MonoBehaviour
    {
        private const float IconInset = 6f;

        [SerializeField] private Button button;
        [SerializeField] private TextMeshProUGUI nameLabel;
        [SerializeField] private TextMeshProUGUI countLabel;
        [SerializeField] private TextMeshProUGUI keyLabel;
        [Tooltip("Created the first time an icon is shown when left empty.")]
        [SerializeField] private Image icon;

        public Button Button => button;

        public void Show(string itemName, int count, string key)
        {
            if (nameLabel != null) nameLabel.text = itemName;
            if (countLabel != null) countLabel.text = count > 1 ? "x" + count : string.Empty;
            if (keyLabel != null) keyLabel.text = key;
        }

        /// <summary>Shows a sprite in the slot, or hides the icon when sprite is null.</summary>
        public void ShowIcon(Sprite sprite)
        {
            if (sprite == null)
            {
                if (icon != null) icon.enabled = false;
                return;
            }

            if (icon == null) icon = CreateIcon();
            icon.sprite = sprite;
            icon.enabled = true;
        }

        private Image CreateIcon()
        {
            GameObject iconObj = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconObj.layer = gameObject.layer;

            RectTransform rect = (RectTransform)iconObj.transform;
            rect.SetParent(transform, false);
            rect.SetSiblingIndex(0);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(IconInset, IconInset);
            rect.offsetMax = new Vector2(-IconInset, -IconInset);

            Image image = iconObj.GetComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            return image;
        }
    }
}