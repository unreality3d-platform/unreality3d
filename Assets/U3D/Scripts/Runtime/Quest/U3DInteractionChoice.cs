using System;
using UnityEngine;

namespace U3D
{
    /// <summary>
    /// Represents a single interaction choice with a key and label
    /// Used by QuestGiver and other interaction systems for multiple choice interactions
    /// </summary>
    [System.Serializable]
    public class U3DInteractionChoice
    {
        [Tooltip("The key players press for this choice")]
        public KeyCode choiceKey = KeyCode.E;

        [Tooltip("Text label shown to the player for this choice")]
        public string choiceLabel = "Accept";

        [Tooltip("Internal identifier for this choice (used for scripting)")]
        public string choiceID = "accept";

        /// <summary>
        /// Constructor for easy setup
        /// </summary>
        public U3DInteractionChoice(KeyCode key, string label, string id = "")
        {
            choiceKey = key;
            choiceLabel = label;
            choiceID = string.IsNullOrEmpty(id) ? label.ToLower().Replace(" ", "_") : id;
        }

        /// <summary>
        /// Get display text for UI (e.g., "Accept [E]")
        /// </summary>
        public string GetDisplayText()
        {
            return $"{choiceLabel} [{choiceKey}]";
        }

        /// <summary>
        /// Check if this choice's key was pressed using Input System
        /// </summary>
        public bool WasKeyPressed()
        {
            if (UnityEngine.InputSystem.Keyboard.current == null) return false;

            switch (choiceKey)
            {
                case KeyCode.E:
                    return UnityEngine.InputSystem.Keyboard.current.eKey.wasPressedThisFrame;
                case KeyCode.F:
                    return UnityEngine.InputSystem.Keyboard.current.fKey.wasPressedThisFrame;
                case KeyCode.X:
                    return UnityEngine.InputSystem.Keyboard.current.xKey.wasPressedThisFrame;
                case KeyCode.Q:
                    return UnityEngine.InputSystem.Keyboard.current.qKey.wasPressedThisFrame;
                case KeyCode.R:
                    return UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame;
                case KeyCode.Alpha1:
                    return UnityEngine.InputSystem.Keyboard.current.digit1Key.wasPressedThisFrame;
                case KeyCode.Alpha2:
                    return UnityEngine.InputSystem.Keyboard.current.digit2Key.wasPressedThisFrame;
                case KeyCode.Alpha3:
                    return UnityEngine.InputSystem.Keyboard.current.digit3Key.wasPressedThisFrame;
                case KeyCode.Alpha4:
                    return UnityEngine.InputSystem.Keyboard.current.digit4Key.wasPressedThisFrame;
                case KeyCode.Alpha5:
                    return UnityEngine.InputSystem.Keyboard.current.digit5Key.wasPressedThisFrame;
                default:
                    // Fallback for other keys - can be expanded as needed
                    return false;
            }
        }
    }

    /// <summary>
    /// Event data for interaction choice selections
    /// </summary>
    [System.Serializable]
    public class U3DInteractionChoiceEvent : UnityEngine.Events.UnityEvent<U3DInteractionChoice>
    {
    }
}