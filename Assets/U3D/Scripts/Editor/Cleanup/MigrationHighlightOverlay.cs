using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    /// <summary>
    /// Persistent Scene view highlight for migration issues. When active it draws a colored
    /// wireframe box around every flagged object and rescans automatically as the hierarchy
    /// changes. State lives here (not on the dashboard) so it survives the Asset Cleanup tab
    /// losing focus or the dashboard window closing. The Asset Cleanup category toggle is the
    /// only off switch.
    /// </summary>
    public static class MigrationHighlightOverlay
    {
        private static bool active;
        private static MigrationScanResult scan;

        private static readonly Color ScriptColor = new Color(1f, 0.25f, 0.2f);     // red
        private static readonly Color ReferenceColor = new Color(1f, 0.82f, 0.2f);  // yellow
        private static readonly Color PlaceholderColor = new Color(0.3f, 0.6f, 1f); // blue

        public static bool IsActive => active;
        public static MigrationScanResult LastScan => scan;

        public static void SetActive(bool value)
        {
            if (value == active)
            {
                if (value) Rescan();
                return;
            }

            active = value;

            if (active)
            {
                SceneView.duringSceneGui += OnSceneGUI;
                EditorApplication.hierarchyChanged += Rescan;
                Rescan();
            }
            else
            {
                SceneView.duringSceneGui -= OnSceneGUI;
                EditorApplication.hierarchyChanged -= Rescan;
                SceneView.RepaintAll();
            }
        }

        public static void Rescan()
        {
            scan = MigrationScanner.ScanLoadedScenes();
            SceneView.RepaintAll();
        }

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!active) return;

            // Draw lower-priority categories first so missing scripts (most severe) sit on top.
            DrawHighlightBoxes(scan.placeholders, PlaceholderColor);
            DrawHighlightBoxes(scan.missingReferences, ReferenceColor);
            DrawHighlightBoxes(scan.missingScripts, ScriptColor);
        }

        private static void DrawHighlightBoxes(List<GameObject> objects, Color color)
        {
            if (objects == null) return;

            Handles.color = color;
            foreach (GameObject go in objects)
            {
                if (go == null) continue;
                Bounds b = GetWorldBounds(go);
                Handles.DrawWireCube(b.center, b.size);
            }
        }

        private static Bounds GetWorldBounds(GameObject go)
        {
            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null) return renderer.bounds;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null) return collider.bounds;

            // Empty logic objects (common after migration) get a small marker at their position.
            return new Bounds(go.transform.position, Vector3.one * 0.5f);
        }
    }
}