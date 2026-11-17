using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MofTools.Editor
{
    public sealed class MofToolsWindow : EditorWindow
    {
        private PackageInfo[] packages = Array.Empty<PackageInfo>();
        private DependencyStatus[] dependencies = Array.Empty<DependencyStatus>();
        private DependencyStatus[] missingDependencies = Array.Empty<DependencyStatus>();
        private string language;
        private Vector2 scroll;
        [MenuItem("Tools/Mof ツール一覧")]
        public static void Open() { GetWindow<MofToolsWindow>(false, "Mof ツール一覧").Show(); }
        private void OnEnable()
        {
            minSize = new Vector2(440, 300);
            EditorApplication.projectChanged += Refresh;
            Events.registeredPackages += PackagesChanged;
            Refresh();
        }
        private void OnDisable()
        {
            EditorApplication.projectChanged -= Refresh;
            Events.registeredPackages -= PackagesChanged;
        }
        private void PackagesChanged(PackageRegistrationEventArgs _) { Refresh(); }
        private void Refresh()
        {
            packages = PackageInfo.GetAllRegisteredPackages().Where(p => p.name.StartsWith("dev.moftools.")).OrderBy(p => p.name).ToArray();
            dependencies = ToolDependencies.GetStatus();
            missingDependencies = dependencies.Where(s => !s.Compatible).ToArray();
            language = ToolLocalization.Language;
            Repaint();
        }
        private void OnInspectorUpdate()
        {
            if (language == ToolLocalization.Language) return;
            language = ToolLocalization.Language; Repaint();
        }
        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(ToolLocalization.Text("Mof ツール一覧", "Mof Tools"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(ToolLocalization.Text("インストール済みのツール", "Installed Tools"));
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var package in packages)
            {
                bool meshJoin = package.name == "dev.moftools.mesh-join";
                var missing = meshJoin ? missingDependencies : Array.Empty<DependencyStatus>();
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var icon = EditorGUIUtility.IconContent(missing.Length > 0 ? "console.warnicon" : "console.infoicon");
                    GUILayout.Label(icon, GUILayout.Width(24), GUILayout.Height(24));
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(meshJoin ? "MA Mesh Join by Mof" : package.displayName, EditorStyles.boldLabel);
                            EditorGUILayout.LabelField(package.version, GUILayout.Width(65));
                        }
                        foreach (var status in missing) EditorGUILayout.LabelField(status.Error, EditorStyles.wordWrappedLabel);
                        if (meshJoin)
                        {
                            EditorGUILayout.Space(3);
                            EditorGUILayout.LabelField(ToolLocalization.Text("前提アセット", "Required Assets"), EditorStyles.boldLabel);
                            foreach (var status in dependencies)
                            {
                                string version = status.Installed ? status.InstalledVersion : ToolLocalization.Text("未導入", "Not installed");
                                EditorGUILayout.LabelField($"{status.DisplayName}: {version}", EditorStyles.wordWrappedLabel);
                                EditorGUILayout.LabelField(ToolLocalization.Text($"必要: {status.RequiredVersion}", $"Required: {status.RequiredVersion}"), EditorStyles.miniLabel);
                            }
                        }
                        if (missing.Length > 0)
                        {
                            EditorGUILayout.Space(3);
                            foreach (var status in missing) EditorGUILayout.LabelField(status.Guidance, EditorStyles.wordWrappedLabel);
                        }
                        else if (meshJoin)
                            EditorGUILayout.LabelField(ToolLocalization.Text("前提アセットは揃っています。", "All required assets are available."), EditorStyles.wordWrappedLabel);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
            if (missingDependencies.Length == 0)
                EditorGUILayout.HelpBox(ToolLocalization.Text("Add Component → MofTools → MA Mesh Join by Mof から追加できます。",
                    "Add the component from Add Component → MofTools → MA Mesh Join by Mof."), MessageType.Info);
        }
    }
}
