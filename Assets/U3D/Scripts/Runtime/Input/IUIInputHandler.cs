using UnityEngine;

/// <summary>
/// Universal interface for all UI components that need input priority
/// Supports: PayPal, Quizzes, Guest Books, Forms, Dialogs, Menus, etc.
/// </summary>
public interface IUIInputHandler
{
    /// <summary>
    /// Returns true when this UI component needs input priority
    /// </summary>
    bool IsUIFocused();

    /// <summary>
    /// Descriptive name for debugging and logging
    /// </summary>
    string GetHandlerName();

    /// <summary>
    /// Priority level for input handling (higher = more important)
    /// </summary>
    int GetInputPriority();

    /// <summary>
    /// Whether this UI should block ALL input or just movement
    /// </summary>
    bool ShouldBlockAllInput();
}