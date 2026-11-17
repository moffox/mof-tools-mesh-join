using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace MofTools.MeshJoin.Editor
{
    internal sealed class MofToolsWindow : EditorWindow
    {
        private PackageInfo[] packages = System.Array.Empty<PackageInfo>();
        [MenuItem("Tools/MofTools")]
        private static void Open() { GetWindow<MofToolsWindow>(false, "MofTools").Show(); }
        private void OnEnable()
        {
            minSize = new Vector2(360, 160);
            EditorApplication.projectChanged += Refresh;
            LanguagePrefs.RegisterLanguageChangeCallback(this, window => { if (window) window.Repaint(); });
            Refresh();
        }
        private void OnDisable() { EditorApplication.projectChanged -= Refresh; }
        private void Refresh()
        {
            packages = PackageInfo.GetAllRegisteredPackages().Where(p => p.name.StartsWith("dev.moftools.")).OrderBy(p => p.name).ToArray();
            Repaint();
        }
        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("MofTools", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(JoinLocalization.Text("インストール済みのツール", "Installed Tools"));
            EditorGUILayout.Space();
            foreach (var package in packages)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(package.name == "dev.moftools.mesh-join" ? "Mof Mesh Join" : package.displayName);
                    EditorGUILayout.LabelField(package.version, GUILayout.Width(65));
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(JoinLocalization.Text("Add Component → MofTools → Mof Mesh Join から追加できます。",
                "Add the component from Add Component → MofTools → Mof Mesh Join."), MessageType.Info);
        }
    }
}
