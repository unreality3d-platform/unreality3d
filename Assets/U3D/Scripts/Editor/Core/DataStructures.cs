using System;
using System.Collections.Generic;
using UnityEngine;

namespace U3D.Editor
{
    [System.Serializable]
    public class CreatorTool
    {
        public string title;
        public string description;
        public System.Action action;
        public Texture2D icon;
        public bool requiresSelection;

        public CreatorTool(string title, string description, System.Action action, bool requiresSelection = false)
        {
            this.title = title;
            this.description = description;
            this.action = action;
            this.requiresSelection = requiresSelection;
        }
    }

    public enum ValidationSeverity
    {
        Info,
        Warning,
        Error,
        Critical
    }

    [System.Serializable]
    public class ValidationResult
    {
        public bool passed;
        public string message;
        public List<UnityEngine.Object> affectedObjects;
        public ValidationSeverity severity;

        public ValidationResult(bool passed, string message, ValidationSeverity severity = ValidationSeverity.Info)
        {
            this.passed = passed;
            this.message = message;
            this.severity = severity;
            this.affectedObjects = new List<UnityEngine.Object>();
        }
    }
}