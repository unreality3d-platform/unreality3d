using System.Collections.Generic;
using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Placeholder component for missing object references.
    /// Tracks original property information for later restoration.
    /// </summary>
    [System.Serializable]
    public class MissingReferenceInfo
    {
        public string componentType;
        public string propertyName;
        public string expectedType;
        public string propertyPath;
        public string gameObjectPath;

        public MissingReferenceInfo(string componentType, string propertyName, string expectedType, string propertyPath, string gameObjectPath)
        {
            this.componentType = componentType;
            this.propertyName = propertyName;
            this.expectedType = expectedType;
            this.propertyPath = propertyPath;
            this.gameObjectPath = gameObjectPath;
        }
    }

    /// <summary>
    /// Placeholder component that tracks missing object references for later restoration.
    /// Similar to MissingScriptPlaceholder but for missing references within existing components.
    /// Runtime component so it can be attached to scene GameObjects.
    /// </summary>
    [AddComponentMenu("")]
    public class MissingReferencePlaceholder : MonoBehaviour
    {
        [SerializeField, TextArea(2, 4)]
        private string placeholderInfo = "This placeholder tracks missing object references. Use Asset Cleanup Tools to restore them.";

        [SerializeField]
        private List<MissingReferenceInfo> missingReferences = new List<MissingReferenceInfo>();

        /// <summary>
        /// Add information about a missing reference
        /// </summary>
        public void AddMissingReference(string componentType, string propertyName, string expectedType, string propertyPath, string gameObjectPath)
        {
            var info = new MissingReferenceInfo(componentType, propertyName, expectedType, propertyPath, gameObjectPath);
            missingReferences.Add(info);

            UpdatePlaceholderInfo();
        }

        /// <summary>
        /// Get all missing reference information
        /// </summary>
        public List<MissingReferenceInfo> GetMissingReferences()
        {
            return new List<MissingReferenceInfo>(missingReferences);
        }

        /// <summary>
        /// Remove a specific missing reference info
        /// </summary>
        public void RemoveMissingReference(MissingReferenceInfo info)
        {
            missingReferences.Remove(info);
            UpdatePlaceholderInfo();
        }

        /// <summary>
        /// Clear all missing reference information
        /// </summary>
        public void ClearMissingReferences()
        {
            missingReferences.Clear();
            UpdatePlaceholderInfo();
        }

        /// <summary>
        /// Check if this placeholder has any missing references
        /// </summary>
        public bool HasMissingReferences()
        {
            return missingReferences.Count > 0;
        }

        private void UpdatePlaceholderInfo()
        {
            if (missingReferences.Count == 0)
            {
                placeholderInfo = "No missing references tracked. This placeholder can be safely removed.";
            }
            else if (missingReferences.Count == 1)
            {
                var info = missingReferences[0];
                placeholderInfo = $"Tracks 1 missing reference:\n• {info.componentType}.{info.propertyName} (expecting {info.expectedType})";
            }
            else
            {
                placeholderInfo = $"Tracks {missingReferences.Count} missing references:\n";
                for (int i = 0; i < Mathf.Min(3, missingReferences.Count); i++)
                {
                    var info = missingReferences[i];
                    placeholderInfo += $"• {info.componentType}.{info.propertyName}\n";
                }
                if (missingReferences.Count > 3)
                {
                    placeholderInfo += $"• ... and {missingReferences.Count - 3} more";
                }
            }
        }

        private void Awake()
        {
            UpdatePlaceholderInfo();
        }

#if UNITY_EDITOR
        private void Reset()
        {
            placeholderInfo = "Missing reference placeholder - use Asset Cleanup Tools to restore references.";
        }
#endif
    }
}