using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace U3D.Editor
{
    public class CheckFixTab : ICreatorTab
    {
        public string TabName => "Optimize";
        public bool IsComplete => false;
        public System.Action<int> OnRequestTabSwitch { get; set; }

        private enum OptimizeSection { AssetOptimization, MigrationCleanup }
        private OptimizeSection selectedSection = OptimizeSection.AssetOptimization;

        private List<CreatorTool> optimizationTools;
        private Vector2 optimizationScrollPosition;

        private MigrationToolsCategory migrationTools;
        private Vector2 cleanupScrollPosition;

        private GUIStyle sectionButtonStyle;
        private GUIStyle activeSectionButtonStyle;
        private bool stylesInitialized = false;

        public void Initialize()
        {
            InitializeOptimizationTools();
            migrationTools = new MigrationToolsCategory();
        }

        private void InitializeStyles()
        {
            if (stylesInitialized) return;
            try
            {
                if (EditorStyles.miniButton == null) return;

                // Let Unity's default button style handle text color for theme compatibility
                sectionButtonStyle = new GUIStyle(EditorStyles.miniButton);

                activeSectionButtonStyle = new GUIStyle(EditorStyles.miniButtonMid)
                {
                    fontStyle = FontStyle.Bold
                };

                stylesInitialized = true;
            }
            catch (System.Exception)
            {
                return;
            }
        }

        private void InitializeOptimizationTools()
        {
            optimizationTools = new List<CreatorTool>
            {
                new CreatorTool("Optimize Textures", "Bulk set WebGL platform overrides for textures grouped by type within your content folders", OptimizeAllTextures),
                new CreatorTool("Optimize Audio", "Bulk set WebGL platform overrides for audio clips with presets for music, UI, and one-shot sounds", OptimizeAllAudio),
                new CreatorTool("Mesh Audit", "View meshes in your scene sorted by triangle count to identify optimization targets", OpenMeshAudit),
                new CreatorTool("🚧 Analyze Build Size", "Show largest assets and estimated build size (coming soon)", null),
                new CreatorTool("🚧 Find Resources Usage", "Identify Resources folder usage that forces assets into the build (coming soon)", null)
            };
        }

        public void DrawTab()
        {
            InitializeStyles();

            EditorGUILayout.Space(10);

            EditorGUILayout.BeginHorizontal();

            string[] sectionNames = { "Asset Optimization", "Migration Cleanup" };
            for (int i = 0; i < sectionNames.Length; i++)
            {
                OptimizeSection section = (OptimizeSection)i;
                bool isActive = selectedSection == section;
                GUIStyle style = stylesInitialized
                    ? (isActive ? activeSectionButtonStyle : sectionButtonStyle)
                    : (isActive ? EditorStyles.miniButtonMid : EditorStyles.miniButton);

                if (GUILayout.Button(sectionNames[i], style, GUILayout.Height(28)))
                {
                    selectedSection = section;
                }
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            switch (selectedSection)
            {
                case OptimizeSection.AssetOptimization:
                    DrawAssetOptimizationSection();
                    break;
                case OptimizeSection.MigrationCleanup:
                    DrawMigrationCleanupSection();
                    break;
            }
        }

        private void DrawAssetOptimizationSection()
        {
            EditorGUILayout.LabelField("WebGL Asset Optimization", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Tools to reduce build size and improve WebGL performance. These set per-asset overrides that the build pipeline respects as-is.", MessageType.Info);
            EditorGUILayout.Space(10);

            optimizationScrollPosition = EditorGUILayout.BeginScrollView(optimizationScrollPosition);

            foreach (var tool in optimizationTools)
            {
                DrawOptimizationTool(tool);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawMigrationCleanupSection()
        {
            cleanupScrollPosition = EditorGUILayout.BeginScrollView(cleanupScrollPosition);
            migrationTools.DrawCategory();
            EditorGUILayout.EndScrollView();
        }

        private void DrawOptimizationTool(CreatorTool tool)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            float windowWidth = EditorGUIUtility.currentViewWidth;

            if (windowWidth < 400f)
            {
                EditorGUILayout.LabelField(tool.title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(tool.description, EditorStyles.wordWrappedMiniLabel);

                EditorGUI.BeginDisabledGroup(tool.action == null);
                if (GUILayout.Button(tool.action != null ? "Open" : "Coming Soon", GUILayout.Height(35)))
                {
                    tool.action?.Invoke();
                }
                EditorGUI.EndDisabledGroup();
            }
            else
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(tool.title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(tool.description, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();

                EditorGUI.BeginDisabledGroup(tool.action == null);
                if (GUILayout.Button(tool.action != null ? "Open" : "Coming Soon", GUILayout.Width(100), GUILayout.Height(35)))
                {
                    tool.action?.Invoke();
                }
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void OptimizeAllTextures() => U3DTextureOptimizationWindow.ShowWindow();
        private void OptimizeAllAudio() => U3DAudioOptimizationWindow.ShowWindow();
        private void OpenMeshAudit() => U3DMeshAuditWindow.ShowWindow();
    }
}