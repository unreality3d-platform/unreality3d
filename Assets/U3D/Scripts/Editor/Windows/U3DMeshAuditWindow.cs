using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    public class U3DMeshAuditWindow : EditorWindow
    {
        private class MeshEntry
        {
            public GameObject gameObject;
            public Mesh mesh;
            public string meshName;
            public int triangleCount;
            public int vertexCount;
            public bool isSkinnedMesh;
            public string assetPath;
        }

        private List<MeshEntry> meshEntries = new List<MeshEntry>();
        private Vector2 scrollPosition;
        private int totalTriangles;
        private int totalVertices;

        private bool showSkinnedOnly = false;

        public static void ShowWindow()
        {
            var window = GetWindow<U3DMeshAuditWindow>("Mesh Audit");
            window.minSize = new Vector2(500, 400);
            window.RefreshMeshList();
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Scene Mesh Audit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Shows all meshes in your loaded scenes, sorted largest first. Higher triangle counts are not necessarily a problem. Use this to understand where your polygon budget is going.", MessageType.Info);
            EditorGUILayout.Space(10);

            DrawControls();
            EditorGUILayout.Space(5);
            DrawStatusLine();
            EditorGUILayout.Space(5);
            DrawMeshList();
        }

        private void DrawControls()
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh List"))
            {
                RefreshMeshList();
            }

            showSkinnedOnly = GUILayout.Toggle(showSkinnedOnly, "Skinned only", EditorStyles.miniButton, GUILayout.Width(90));

            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusLine()
        {
            var filtered = GetFilteredEntries();
            EditorGUILayout.LabelField(
                $"{filtered.Count} of {meshEntries.Count} meshes shown | Scene total: {totalTriangles:N0} tris, {totalVertices:N0} verts",
                EditorStyles.miniLabel);
        }

        private void DrawMeshList()
        {
            var filtered = GetFilteredEntries();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            foreach (var entry in filtered)
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

                EditorGUILayout.BeginVertical();

                string typeTag = entry.isSkinnedMesh ? "[Skinned] " : "";
                EditorGUILayout.LabelField($"{typeTag}{entry.gameObject.name}", EditorStyles.boldLabel);

                string meshInfo = $"{entry.triangleCount:N0} tris | {entry.vertexCount:N0} verts";
                if (!string.IsNullOrEmpty(entry.meshName) && entry.meshName != entry.gameObject.name)
                    meshInfo += $" | Mesh: {entry.meshName}";
                if (!string.IsNullOrEmpty(entry.assetPath))
                    meshInfo += $" | {Path.GetFileName(entry.assetPath)}";

                EditorGUILayout.LabelField(meshInfo, EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();

                if (GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(30)))
                {
                    Selection.activeGameObject = entry.gameObject;
                    EditorGUIUtility.PingObject(entry.gameObject);
                    SceneView.lastActiveSceneView?.FrameSelected();
                }

                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.EndScrollView();
        }

        private void RefreshMeshList()
        {
            meshEntries.Clear();
            totalTriangles = 0;
            totalVertices = 0;

            var meshFilters = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            foreach (var mf in meshFilters)
            {
                if (mf.sharedMesh == null) continue;

                var entry = new MeshEntry
                {
                    gameObject = mf.gameObject,
                    mesh = mf.sharedMesh,
                    meshName = mf.sharedMesh.name,
                    triangleCount = mf.sharedMesh.triangles.Length / 3,
                    vertexCount = mf.sharedMesh.vertexCount,
                    isSkinnedMesh = false,
                    assetPath = AssetDatabase.GetAssetPath(mf.sharedMesh)
                };

                meshEntries.Add(entry);
                totalTriangles += entry.triangleCount;
                totalVertices += entry.vertexCount;
            }

            var skinnedRenderers = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
            foreach (var smr in skinnedRenderers)
            {
                if (smr.sharedMesh == null) continue;

                var entry = new MeshEntry
                {
                    gameObject = smr.gameObject,
                    mesh = smr.sharedMesh,
                    meshName = smr.sharedMesh.name,
                    triangleCount = smr.sharedMesh.triangles.Length / 3,
                    vertexCount = smr.sharedMesh.vertexCount,
                    isSkinnedMesh = true,
                    assetPath = AssetDatabase.GetAssetPath(smr.sharedMesh)
                };

                meshEntries.Add(entry);
                totalTriangles += entry.triangleCount;
                totalVertices += entry.vertexCount;
            }

            meshEntries = meshEntries.OrderByDescending(e => e.triangleCount).ToList();
        }

        private List<MeshEntry> GetFilteredEntries()
        {
            if (showSkinnedOnly)
                return meshEntries.Where(e => e.isSkinnedMesh).ToList();

            return meshEntries;
        }
    }
}