using System;
using System.Globalization;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.modular_avatar.core.vertex_filters;
using nadena.dev.ndmf.localization;
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
                Debug.LogWarning(JoinLocalization.Text("MofTools: Cutterを自動移行できません。非反転のCutterとUVマスク1個の構成に対応しています。既存設定は保持しました。",
                    "MofTools: Cutter migration requires one non-inverted Cutter and one UV mask filter. The existing configuration was preserved."), join);
                return;
            }
            var cutter = cutters[0];
            var mask = masks[0];
            Undo.RecordObject(join, JoinLocalization.Text("Mesh Cutterを境界結合へ移行", "Migrate Mesh Cutter to Mesh Join"));
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
        private bool joinSettingsExpanded;
        private bool invalidDistance;
        private SkinnedMeshRenderer targetRenderer;
        private Material[] materials = Array.Empty<Material>();
        private void OnEnable()
        {
            EditorApplication.hierarchyChanged += Refresh;
            EditorApplication.projectChanged += Refresh;
            LanguagePrefs.RegisterLanguageChangeCallback(this, editor => { if (editor) editor.Refresh(); });
            Refresh();
        }
        private void OnDisable()
        {
            EditorApplication.hierarchyChanged -= Refresh;
            EditorApplication.projectChanged -= Refresh;
        }
        private void Refresh()
        {
            if (!target || EditorApplication.isPlayingOrWillChangePlaymode) return;
            error = null;
            targetRenderer = null;
            materials = Array.Empty<Material>();
            try
            {
                var join = (MeshJoin)target;
                JoinReferences.Refresh(join);
                var root = JoinReferences.Avatar(join);
                if (root)
                {
                    targetRenderer = JoinReferences.Resolve(join, root);
                    materials = targetRenderer.sharedMaterials;
                    JoinBuild.ValidateCutterOverlap(root.gameObject, targetRenderer);
                }
            }
            catch (Exception e) { error = e.Message; }
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            Draw("Target", JoinLocalization.Text("結合先", "Target"));
            joinSettingsExpanded = EditorGUILayout.Foldout(joinSettingsExpanded, JoinLocalization.Text("結合設定", "Join Settings"), true);
            if (joinSettingsExpanded)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawDistance();
                    Draw("Order", JoinLocalization.Text("処理順", "Processing Order"));
                }
            }
            var operation = serializedObject.FindProperty("DeleteFaces");
            var operationRect = EditorGUILayout.GetControlRect();
            var operationLabel = new GUIContent(JoinLocalization.Text("統合先の処理", "Target Operation"));
            EditorGUI.BeginProperty(operationRect, operationLabel, operation);
            EditorGUI.BeginChangeCheck();
            int nextOperation = EditorGUI.Popup(operationRect, operationLabel.text, operation.boolValue ? 1 : 0,
                new[] { JoinLocalization.Text("なにもしない", "Do Nothing"), JoinLocalization.Text("指定された面を削除する", "Delete Specified Faces") });
            if (EditorGUI.EndChangeCheck()) operation.boolValue = nextOperation == 1;
            EditorGUI.EndProperty();
            if (operation.boolValue)
            {
                DrawMaterialSlot();
                Draw("DeleteMask", JoinLocalization.Text("マスクテクスチャー", "Mask Texture"));
                var color = serializedObject.FindProperty("DeleteColor");
                color.enumValueIndex = EditorGUILayout.Popup(JoinLocalization.Text("削除モード", "Delete Mode"), color.enumValueIndex,
                    new[] { "Delete Black", "Delete White" });
                var selection = serializedObject.FindProperty("Selection");
                selection.enumValueIndex = EditorGUILayout.Popup(JoinLocalization.Text("選択モード", "Selection Mode"), selection.enumValueIndex,
                    new[] { JoinLocalization.Text("任意の頂点", "Any Vertex"), JoinLocalization.Text("すべての頂点", "All Vertices"), JoinLocalization.Text("重心", "Centroid") });
                Draw("UVChannel", JoinLocalization.Text("UV チャンネル", "UV Channel"));
            }
            if (serializedObject.ApplyModifiedProperties()) Refresh();
            if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.HelpBox(JoinLocalization.Text("BlendShapeが0の状態を基準としています。\n削除した面の境界にある頂点から近い頂点を結合します。",
                "Uses the mesh with all BlendShapes set to 0 as its reference.\nJoins nearby vertices along the boundary of the deleted faces."), MessageType.Info);
        }

        private void Draw(string name, string label) { EditorGUILayout.PropertyField(serializedObject.FindProperty(name), new GUIContent(label)); }

        internal static string FormatDistance(float value)
        {
            return value.ToString("0.################################################", CultureInfo.InvariantCulture);
        }

        internal static bool TryParseDistance(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
        }

        private void DrawDistance()
        {
            var property = serializedObject.FindProperty("JoinDistance");
            var rect = EditorGUILayout.GetControlRect();
            var label = new GUIContent(JoinLocalization.Text("結合距離", "Join Distance"),
                JoinLocalization.Text("ワールド座標での許容距離です。距離外の頂点は吸着しません。", "World-space tolerance. Out-of-range vertices are not moved."));
            EditorGUI.BeginProperty(rect, label, property);
            EditorGUI.BeginChangeCheck();
            string text = EditorGUI.DelayedTextField(rect, label, FormatDistance(property.floatValue));
            if (EditorGUI.EndChangeCheck())
            {
                invalidDistance = !TryParseDistance(text, out float value);
                if (!invalidDistance) property.floatValue = value;
            }
            EditorGUI.EndProperty();
            if (invalidDistance) EditorGUILayout.HelpBox(JoinLocalization.Text("0以上の有限な数値を入力してください。値は変更していません。",
                "Enter a finite number greater than or equal to zero. The value was not changed."), MessageType.Warning);
        }

        private void DrawMaterialSlot()
        {
            var property = serializedObject.FindProperty("MaterialSlot");
            var rect = EditorGUILayout.GetControlRect();
            var label = new GUIContent(JoinLocalization.Text("マテリアルスロット", "Material Slot"));
            EditorGUI.BeginProperty(rect, label, property);
            var field = EditorGUI.PrefixLabel(rect, label);
            var dropdown = new Rect(field.x, field.y, Mathf.Min(95, field.width * .4f), field.height);
            var materialRect = new Rect(dropdown.xMax + 3, field.y, Mathf.Max(0, field.xMax - dropdown.xMax - 3), field.height);
            using (new EditorGUI.DisabledScope(!targetRenderer || materials.Length == 0))
            {
                string current = targetRenderer ? $"Element {property.intValue}" : JoinLocalization.Text("Renderer未設定", "Missing Renderer");
                if (EditorGUI.DropdownButton(dropdown, new GUIContent(current), FocusType.Keyboard))
                {
                    var menu = new GenericMenu();
                    for (int i = 0; i < materials.Length; i++)
                    {
                        int selected = i;
                        string name = materials[i] ? materials[i].name : "None";
                        menu.AddItem(new GUIContent($"Element {i}: {name}"), i == property.intValue, () =>
                        {
                            if (!target) return;
                            serializedObject.Update(); serializedObject.FindProperty("MaterialSlot").intValue = selected;
                            serializedObject.ApplyModifiedProperties(); Refresh();
                        });
                    }
                    menu.DropDown(dropdown);
                }
            }
            using (new EditorGUI.DisabledScope(true))
                EditorGUI.ObjectField(materialRect, property.intValue >= 0 && property.intValue < materials.Length ? materials[property.intValue] : null, typeof(Material), false);
            EditorGUI.EndProperty();
        }
    }
}
