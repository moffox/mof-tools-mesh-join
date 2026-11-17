using UnityEditor;
using UnityEngine;
using System.Linq;

namespace MofTools.MeshJoin.Editor
{
    internal static class SceneIconDefaults
    {
        private static string PreferenceKey => "MofTools.MeshJoin.SceneIconDefault:" + Application.dataPath;

        // Register the component in Unity's annotation list so its icon can be controlled independently.
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected)]
        private static void RegisterAnnotation(MeshJoin component, GizmoType type) { }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.delayCall += TryInitialize;
            SceneView.duringSceneGui += OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        private static void OnSceneGUI(SceneView view) { TryInitialize(); }
        private static void TryInitialize()
        {
            ApplyOnce();
            if (EditorPrefs.GetBool(PreferenceKey, false)) Cleanup();
        }
        private static void Cleanup()
        {
            EditorApplication.delayCall -= TryInitialize;
            SceneView.duringSceneGui -= OnSceneGUI;
            AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
        }

        internal static void ApplyOnce()
        {
            // Apply only the initial preference. Later choices in Unity's Gizmos menu are preserved.
            if (EditorPrefs.GetBool(PreferenceKey, false)) return;
            var info = GizmoUtility.GetGizmoInfo().FirstOrDefault(g => (g.script as MonoScript)?.GetClass() == typeof(MeshJoin));
            if (info == null) return;
            info.iconEnabled = false;
            GizmoUtility.ApplyGizmoInfo(info, false);
            EditorPrefs.SetBool(PreferenceKey, true);
        }
    }
}
