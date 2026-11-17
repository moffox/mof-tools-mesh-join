using System;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.vertex_filters;
using UnityEditor;
using UnityEngine;

namespace MofTools.MeshJoin.Editor
{
    [InitializeOnLoad]
    internal static class CutterMigration
    {
        static CutterMigration() { MeshJoin.Created += QueueMigration; }

        private static void QueueMigration(MeshJoin join)
        {
            // AddComponent must register its own Undo before any old component is destroyed.
            EditorApplication.delayCall += () =>
            {
                if (join && !EditorApplication.isPlayingOrWillChangePlaymode) Migrate(join);
            };
        }

        internal static void Migrate(MeshJoin join)
        {
            var cutters = join.GetComponents<ModularAvatarMeshCutter>();
            if (cutters.Length == 0) return;
            var masks = join.GetComponents<VertexFilterByMaskComponent>();
            // Unknown filters must not be destroyed or silently omitted.
            var filters = join.GetComponents<Component>().Where(c => c && c.GetType().Namespace == typeof(VertexFilterByMaskComponent).Namespace).ToArray();
            if (cutters.Length != 1 || masks.Length != 1 || filters.Length != 1 || cutters[0].Inverted)
            {
                Debug.LogWarning("MofTools: Cutterを自動移行できません。非反転のCutterとUVマスク1個の構成に対応しています。既存設定は保持しました。", join);
                return;
            }
            var cutter = cutters[0];
            var mask = masks[0];
            Undo.RecordObject(join, "Mesh Cutterを境界結合へ移行");
            join.Target = cutter.Object.Clone();
            join.DeleteMask = mask.MaskTexture;
            join.MaterialSlot = mask.MaterialIndex;
            join.UVChannel = mask.UVChannel;
            join.DeleteColor = mask.DeleteMode == ByMaskMode.DeleteWhite ? MaskColor.White : MaskColor.Black;
            join.Selection = (FaceSelection)(int)mask.SelectionMode;
            join.DeleteFaces = true;
            Undo.DestroyObjectImmediate(mask);
            Undo.DestroyObjectImmediate(cutter);
            PrefabUtility.RecordPrefabInstancePropertyModifications(join);
            EditorUtility.SetDirty(join);
        }
    }

    [CustomEditor(typeof(MeshJoin))]
    internal sealed class MeshJoinInspector : UnityEditor.Editor
    {
        private string error;
        private void OnEnable() { EditorApplication.hierarchyChanged += Refresh; Refresh(); }
        private void OnDisable() { EditorApplication.hierarchyChanged -= Refresh; }
        private void Refresh()
        {
            if (!target || EditorApplication.isPlayingOrWillChangePlaymode) return;
            error = null;
            try
            {
                var join = (MeshJoin)target;
                JoinReferences.Refresh(join);
                var root = JoinReferences.Avatar(join);
                if (root) JoinBuild.ValidateCutterOverlap(root.gameObject, JoinReferences.Resolve(join, root));
            }
            catch (Exception e) { error = e.Message; }
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            Draw("Target", "結合先"); Draw("JoinDistance", "結合距離（ワールド）"); Draw("Order", "処理順");
            Draw("DeleteFaces", "統合先の面を固定削除");
            if (serializedObject.FindProperty("DeleteFaces").boolValue)
            {
                Draw("DeleteMask", "削除マスク"); Draw("MaterialSlot", "対象マテリアルスロット"); Draw("UVChannel", "UVチャンネル");
                var color = serializedObject.FindProperty("DeleteColor");
                color.enumValueIndex = EditorGUILayout.Popup("削除する色", color.enumValueIndex, new[] { "黒", "白" });
                var selection = serializedObject.FindProperty("Selection");
                selection.enumValueIndex = EditorGUILayout.Popup("面の選択方法", selection.enumValueIndex, new[] { "いずれかの頂点", "すべての頂点", "面の重心" });
            }
            if (serializedObject.ApplyModifiedProperties()) Refresh();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.HelpBox("全BlendShapeが0の基準形状で、削除した切り口と追加側の開口部だけを結合します。距離外は吸着しません。同順位はHierarchy順です。", MessageType.Info);
        }

        private void Draw(string name, string label) { EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(label)); }

        [MenuItem("Tools/MofTools/メッシュ境界結合を追加")]
        private static void Add()
        {
            var go = Selection.activeGameObject;
            if (!go || !go.GetComponent<SkinnedMeshRenderer>())
            { EditorUtility.DisplayDialog("メッシュ境界結合", "SkinnedMeshRendererがあるオブジェクトを選択してください。", "閉じる"); return; }
            if (!go.GetComponent<MeshJoin>())
            {
                Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup();
                var join = Undo.AddComponent<MeshJoin>(go);
                CutterMigration.Migrate(join);
                Undo.CollapseUndoOperations(group);
            }
        }
    }
}
