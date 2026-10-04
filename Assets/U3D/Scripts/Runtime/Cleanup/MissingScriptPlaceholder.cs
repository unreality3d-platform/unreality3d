using UnityEngine;
using UnityEngine.Serialization;

namespace U3D
{
    /// <summary>
    /// Placeholder component to mark where missing scripts were removed.
    /// Part of the U3D SDK Asset Cleanup system. Runtime component (not editor-only)
    /// so it can be attached to scene GameObjects; its Inspector is provided by
    /// MissingScriptPlaceholderEditor in the Editor assembly.
    /// </summary>
    public class MissingScriptPlaceholder : MonoBehaviour
    {
        [SerializeField]
        [TextArea(3, 5)]
        private string missingScriptNote = "⚠️ This component replaced a missing script.\n\nUse 'Remove Missing Script Placeholders' tool to clean up when ready.";

        [SerializeField]
        private string replacementDateTime = "";

        [FormerlySerializedAs("u3dSuggestion")]
        [SerializeField]
        private string suggestedSearchTerm = "";

        public string SuggestedSearchTerm => suggestedSearchTerm;
        public string ReplacementDateTime => replacementDateTime;

        /// <summary>
        /// Sets the suggested search term. Called by the editor cleanup tool at
        /// placeholder-creation time, since ComponentSuggestions lives in the
        /// editor assembly and this runtime class can't reference it directly.
        /// </summary>
        public void SetSuggestedSearchTerm(string term)
        {
            suggestedSearchTerm = term;
        }

        /// <summary>
        /// Sets the replacement timestamp. Called by the editor cleanup tool at
        /// placeholder-creation time. Editor AddComponent calls don't fire Awake,
        /// so this can't be done lazily on first run.
        /// </summary>
        public void SetReplacementDateTime(string dateTime)
        {
            if (string.IsNullOrEmpty(replacementDateTime))
            {
                replacementDateTime = dateTime;
            }
        }
    }
}