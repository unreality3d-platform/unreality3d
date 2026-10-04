using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace U3D
{
    /// <summary>
    /// Controller for Shop Object monetization tool.
    /// Manages multiple items with individual PayPal dual transactions.
    /// </summary>
    public class ShopController : MonoBehaviour
    {
        [System.Serializable]
        public class ShopItem
        {
            public string itemName = "Item";
            public string description = "Shop item description";
            public float price = 5.00f;
            public Sprite icon;
        }

        [Header("Shop Configuration")]
        [SerializeField] private List<ShopItem> shopItems = new List<ShopItem>();
        [SerializeField] private Transform itemContainer;
        [SerializeField] private bool autoSetupItems = true;

        private void Start()
        {
            if (autoSetupItems)
            {
                SetupDefaultItems();
            }
            CreateItemUI();
        }

        private void SetupDefaultItems()
        {
            if (shopItems.Count == 0)
            {
                shopItems.Add(new ShopItem { itemName = "Premium Avatar", description = "Exclusive creator avatar", price = 10.00f });
                shopItems.Add(new ShopItem { itemName = "Special Effect", description = "Unique visual effect", price = 5.00f });
                shopItems.Add(new ShopItem { itemName = "Creator Pack", description = "Bundle of creator content", price = 15.00f });
            }
        }

        private void CreateItemUI()
        {
            var scrollView = GetComponentInChildren<ScrollRect>();
            if (scrollView != null)
            {
                itemContainer = scrollView.content;
            }

            if (itemContainer == null)
            {
                Debug.LogWarning("Shop Controller: No item container found. Make sure there's a ScrollRect with content.");
                return;
            }

            foreach (var item in shopItems)
            {
                CreateShopItemButton(item);
            }
        }

        private void CreateShopItemButton(ShopItem item)
        {
            var tmpResources = new TMP_DefaultControls.Resources();
            GameObject itemButton = TMP_DefaultControls.CreateButton(tmpResources);
            itemButton.name = $"Item_{item.itemName}";
            itemButton.transform.SetParent(itemContainer, false);

            var buttonRect = itemButton.GetComponent<RectTransform>();
            buttonRect.sizeDelta = new Vector2(250, 40);

            U3DUIStyle.ApplyButtonStyle(itemButton, $"{item.itemName} - ${item.price:F2}");

            // Add PayPalDualTransaction to each item
            var dualTransaction = itemButton.AddComponent<PayPalDualTransaction>();
            dualTransaction.SetItemDetails(item.itemName, item.description, item.price);
            dualTransaction.SetVariableAmount(false);

            // Auto-assign UI references for shop item buttons
            var itemButton_Button = itemButton.GetComponent<Button>();
            dualTransaction.AssignEssentialReferences(itemButton_Button, null);

            // Set PayPal email from centralized system
            var creatorData = Resources.Load<U3DCreatorData>("U3DCreatorData");
            if (creatorData != null && !string.IsNullOrEmpty(creatorData.PayPalEmail))
            {
                dualTransaction.SetCreatorPayPalEmail(creatorData.PayPalEmail);
            }
        }

        /// <summary>
        /// Add a new shop item dynamically
        /// </summary>
        public void AddShopItem(string name, string description, float price)
        {
            shopItems.Add(new ShopItem { itemName = name, description = description, price = price });
            if (itemContainer != null)
            {
                CreateShopItemButton(shopItems[shopItems.Count - 1]);
            }
        }

        /// <summary>
        /// Clear all shop items
        /// </summary>
        public void ClearShopItems()
        {
            shopItems.Clear();
            if (itemContainer != null)
            {
                for (int i = itemContainer.childCount - 1; i >= 0; i--)
                {
                    Destroy(itemContainer.GetChild(i).gameObject);
                }
            }
        }

        /// <summary>
        /// Get current shop items
        /// </summary>
        public List<ShopItem> GetShopItems()
        {
            return new List<ShopItem>(shopItems);
        }

        /// <summary>
        /// Set the item container manually
        /// </summary>
        public void SetItemContainer(Transform container)
        {
            itemContainer = container;
        }
    }
}