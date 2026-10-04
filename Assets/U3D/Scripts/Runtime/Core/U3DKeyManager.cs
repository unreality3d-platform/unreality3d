using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace U3D
{
    /// <summary>
    /// Centralized key management system that protects Creator's PlayerController configuration
    /// and prevents conflicts between different U3D systems
    /// </summary>
    public static class U3DKeyManager
    {
        /// <summary>
        /// Keys currently reserved by the Player Controller
        /// This list is populated from the actual InputActions asset
        /// </summary>
        private static Dictionary<KeyCode, string> reservedKeys = new Dictionary<KeyCode, string>();

        /// <summary>
        /// The interact key configured in PlayerController (cached for Quest system)
        /// </summary>
        private static KeyCode playerInteractKey = KeyCode.R;

        /// <summary>
        /// Recommended alternative keys for different interaction systems
        /// </summary>
        private static readonly Dictionary<KeyCode, List<KeyCode>> keyAlternatives = new Dictionary<KeyCode, List<KeyCode>>()
        {
            { KeyCode.R, new List<KeyCode> { KeyCode.F, KeyCode.T, KeyCode.G, KeyCode.X } },
            { KeyCode.E, new List<KeyCode> { KeyCode.F, KeyCode.T, KeyCode.G, KeyCode.X } },
            { KeyCode.F, new List<KeyCode> { KeyCode.T, KeyCode.G, KeyCode.X, KeyCode.V } },
            { KeyCode.C, new List<KeyCode> { KeyCode.V, KeyCode.B, KeyCode.N, KeyCode.X } },
            { KeyCode.Space, new List<KeyCode> { KeyCode.LeftControl, KeyCode.RightControl } }
        };

        /// <summary>
        /// Default safe interaction keys that are rarely used by character controllers
        /// </summary>
        private static readonly List<KeyCode> safeInteractionKeys = new List<KeyCode>()
        {
            KeyCode.F, KeyCode.T, KeyCode.G, KeyCode.X,
            KeyCode.V, KeyCode.B, KeyCode.Z,
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5
        };

        /// <summary>
        /// Initialize key tracking from current PlayerController setup
        /// </summary>
        public static void InitializeFromPlayerController(U3DPlayerController playerController)
        {
            reservedKeys.Clear();

            if (playerController != null)
            {
                var inputActions = playerController.GetComponent<PlayerInput>()?.actions;
                if (inputActions != null)
                {
                    ScanInputActionsForKeys(inputActions);
                    CachePlayerInteractKey(inputActions);
                }
            }

            Debug.Log($"U3DKeyManager: Tracked {reservedKeys.Count} reserved keys from PlayerController. Interact key: {playerInteractKey}");
        }

        /// <summary>
        /// Cache the interact key from PlayerController for Quest system
        /// </summary>
        private static void CachePlayerInteractKey(InputActionAsset inputActions)
        {
            foreach (var actionMap in inputActions.actionMaps)
            {
                foreach (var action in actionMap.actions)
                {
                    if (action.name.ToLower().Contains("interact") || action.name.ToLower().Contains("use"))
                    {
                        foreach (var binding in action.bindings)
                        {
                            if (binding.path.StartsWith("<Keyboard>/"))
                            {
                                KeyCode keyCode = GetKeyCodeFromPath(binding.path);
                                if (keyCode != KeyCode.None)
                                {
                                    playerInteractKey = keyCode;
                                    return;
                                }
                            }
                        }
                    }
                }
            }

            playerInteractKey = KeyCode.E;
        }

        /// <summary>
        /// Get the interact key configured in PlayerController
        /// </summary>
        public static KeyCode GetPlayerInteractionKey()
        {
            return playerInteractKey;
        }

        /// <summary>
        /// Scan InputActions asset for currently assigned keys
        /// </summary>
        private static void ScanInputActionsForKeys(InputActionAsset inputActions)
        {
            foreach (var actionMap in inputActions.actionMaps)
            {
                foreach (var action in actionMap.actions)
                {
                    foreach (var binding in action.bindings)
                    {
                        if (binding.path.StartsWith("<Keyboard>/"))
                        {
                            KeyCode keyCode = GetKeyCodeFromPath(binding.path);
                            if (keyCode != KeyCode.None && !reservedKeys.ContainsKey(keyCode))
                            {
                                reservedKeys[keyCode] = $"{actionMap.name}.{action.name}";
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Convert InputSystem path to KeyCode
        /// </summary>
        private static KeyCode GetKeyCodeFromPath(string path)
        {
            switch (path.ToLower())
            {
                case "<keyboard>/e": return KeyCode.E;
                case "<keyboard>/f": return KeyCode.F;
                case "<keyboard>/c": return KeyCode.C;
                case "<keyboard>/r": return KeyCode.R;
                case "<keyboard>/t": return KeyCode.T;
                case "<keyboard>/g": return KeyCode.G;
                case "<keyboard>/q": return KeyCode.Q;
                case "<keyboard>/x": return KeyCode.X;
                case "<keyboard>/z": return KeyCode.Z;
                case "<keyboard>/v": return KeyCode.V;
                case "<keyboard>/b": return KeyCode.B;
                case "<keyboard>/space": return KeyCode.Space;
                case "<keyboard>/leftshift": return KeyCode.LeftShift;
                case "<keyboard>/tab": return KeyCode.Tab;
                case "<keyboard>/numlock": return KeyCode.Numlock;
                case "<keyboard>/1": return KeyCode.Alpha1;
                case "<keyboard>/2": return KeyCode.Alpha2;
                case "<keyboard>/3": return KeyCode.Alpha3;
                case "<keyboard>/4": return KeyCode.Alpha4;
                case "<keyboard>/5": return KeyCode.Alpha5;
                // Add more mappings as needed
                default: return KeyCode.None;
            }
        }

        /// <summary>
        /// Check if a key is available for use (not reserved by PlayerController)
        /// </summary>
        public static bool IsKeyAvailable(KeyCode key)
        {
            return !reservedKeys.ContainsKey(key);
        }

        /// <summary>
        /// Get conflict information for a key
        /// </summary>
        public static string GetKeyConflictInfo(KeyCode key)
        {
            if (reservedKeys.TryGetValue(key, out string assignedTo))
            {
                return $"{key} key already assigned to Character Controller: {assignedTo}";
            }
            return null;
        }

        /// <summary>
        /// Get the first available alternative for a conflicted key
        /// </summary>
        public static KeyCode GetSafeAlternative(KeyCode requestedKey)
        {
            // If requested key is available, use it
            if (IsKeyAvailable(requestedKey))
                return requestedKey;

            // Try specific alternatives for this key
            if (keyAlternatives.TryGetValue(requestedKey, out List<KeyCode> alternatives))
            {
                foreach (KeyCode alt in alternatives)
                {
                    if (IsKeyAvailable(alt))
                        return alt;
                }
            }

            // Fall back to safe interaction keys
            foreach (KeyCode safeKey in safeInteractionKeys)
            {
                if (IsKeyAvailable(safeKey))
                    return safeKey;
            }

            // Last resort: return original key with warning
            Debug.LogWarning($"U3DKeyManager: No safe alternative found for {requestedKey}. Using original key - may cause conflicts!");
            return requestedKey;
        }

        /// <summary>
        /// Get recommended interaction key for new systems
        /// </summary>
        public static KeyCode GetRecommendedInteractionKey()
        {
            // Priority order for interaction keys
            KeyCode[] preferredKeys = { KeyCode.R, KeyCode.T, KeyCode.G, KeyCode.Q };

            foreach (KeyCode key in preferredKeys)
            {
                if (IsKeyAvailable(key))
                    return key;
            }

            // Fall back to numbered keys
            for (int i = 1; i <= 5; i++)
            {
                KeyCode numKey = (KeyCode)System.Enum.Parse(typeof(KeyCode), $"Alpha{i}");
                if (IsKeyAvailable(numKey))
                    return numKey;
            }

            return KeyCode.R; // Ultimate fallback
        }

        /// <summary>
        /// Get all currently reserved keys for debugging
        /// </summary>
        public static Dictionary<KeyCode, string> GetReservedKeys()
        {
            return new Dictionary<KeyCode, string>(reservedKeys);
        }

        /// <summary>
        /// Validate interaction choice and suggest alternatives if needed
        /// </summary>
        public static U3DInteractionChoiceValidation ValidateInteractionChoice(U3DInteractionChoice choice)
        {
            bool isValid = IsKeyAvailable(choice.choiceKey);
            string conflictInfo = GetKeyConflictInfo(choice.choiceKey);
            KeyCode suggestedKey = isValid ? choice.choiceKey : GetSafeAlternative(choice.choiceKey);

            return new U3DInteractionChoiceValidation
            {
                originalChoice = choice,
                isValid = isValid,
                conflictMessage = conflictInfo,
                suggestedKey = suggestedKey,
                suggestedChoice = isValid ? choice : new U3DInteractionChoice(suggestedKey, choice.choiceLabel, choice.choiceID)
            };
        }
    }

    /// <summary>
    /// Validation result for interaction choices
    /// </summary>
    [System.Serializable]
    public class U3DInteractionChoiceValidation
    {
        public U3DInteractionChoice originalChoice;
        public bool isValid;
        public string conflictMessage;
        public KeyCode suggestedKey;
        public U3DInteractionChoice suggestedChoice;
    }

    /// <summary>
    /// Auto-start prevention system for quests with QuestGivers
    /// </summary>
    public static class U3DQuestAutoStartPrevention
    {
        /// <summary>
        /// Check if quest should be prevented from auto-starting due to QuestGiver
        /// </summary>
        public static bool ShouldPreventAutoStart(U3DQuest quest)
        {
            if (quest == null) return false;

            // Search for any QuestGiver that references this quest
            U3DQuestGiver[] allQuestGivers = Object.FindObjectsByType<U3DQuestGiver>(FindObjectsSortMode.None);

            foreach (U3DQuestGiver giver in allQuestGivers)
            {
                // Use reflection to access private field safely
                var questField = typeof(U3DQuestGiver).GetField("questToGive",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (questField != null)
                {
                    U3DQuest giverQuest = questField.GetValue(giver) as U3DQuest;
                    if (giverQuest == quest)
                    {
                        return true; // Quest has a QuestGiver, should not auto-start
                    }
                }
            }

            return false; // No QuestGiver found, auto-start is OK
        }

        /// <summary>
        /// Validate quest configuration and show warnings if needed
        /// </summary>
        public static void ValidateQuestConfiguration(U3DQuest quest)
        {
            if (quest == null) return;

#if UNITY_EDITOR
            bool hasQuestGiver = ShouldPreventAutoStart(quest);

            UnityEditor.SerializedObject serializedQuest = new UnityEditor.SerializedObject(quest);
            UnityEditor.SerializedProperty autoStartProp = serializedQuest.FindProperty("startAutomatically");

            if (autoStartProp != null && autoStartProp.boolValue && hasQuestGiver)
            {
                // Show warning and optionally fix
                bool fix = UnityEditor.EditorUtility.DisplayDialog(
                    "Quest Configuration Conflict",
                    $"Quest '{quest.questTitle}' has Auto Start enabled but is assigned to a Quest Giver.\n\n" +
                    "Quests with Quest Givers should NOT auto-start as they wait for player interaction.\n\n" +
                    "Would you like to automatically disable Auto Start for this quest?",
                    "Yes, Fix It",
                    "Keep Current Settings"
                );

                if (fix)
                {
                    autoStartProp.boolValue = false;
                    serializedQuest.ApplyModifiedProperties();
                    Debug.Log($"Fixed quest '{quest.questTitle}': Disabled auto-start due to QuestGiver dependency");
                }
                else
                {
                    Debug.LogWarning($"Quest '{quest.questTitle}' has conflicting configuration: Auto Start + QuestGiver");
                }
            }
#endif
        }
    }
}