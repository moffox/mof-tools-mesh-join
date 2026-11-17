using System;
using System.Linq;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;

namespace MofTools.MeshJoin.Editor
{
    internal static class JoinReferences
    {
        internal static Transform Avatar(Component component)
        {
            return nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(component.transform);
        }

        internal static string Path(Transform root, Transform value)
        {
            if (root == value) return "";
            if (!value || !value.IsChildOf(root)) throw new InvalidOperationException(JoinLocalization.Text("参照先が同じアバター内にありません。", "The reference is outside this avatar."));
            return AnimationUtility.CalculateTransformPath(value, root);
        }

        internal static GameObject Unique(Transform root, string path)
        {
            if (path == AvatarObjectReference.AVATAR_ROOT || path == "") return root.gameObject;
            var nodes = new[] { root };
            foreach (var segment in path.Split('/'))
                nodes = nodes.SelectMany(t => t.Cast<Transform>()).Where(t => t.name == segment).ToArray();
            if (nodes.Length != 1) throw new InvalidOperationException(JoinLocalization.Text($"結合先のパス「{path}」は{nodes.Length}件です。対象を一意に指定してください。", $"Target path {path} resolves to {nodes.Length} objects. Specify a unique target."));
            return nodes[0].gameObject;
        }

        internal static SkinnedMeshRenderer Resolve(MeshJoin join, Transform root)
        {
            var so = new SerializedObject(join);
            var reference = so.FindProperty(nameof(MeshJoin.Target));
            var direct = reference.FindPropertyRelative("targetObject").objectReferenceValue as GameObject;
            var path = join.Target?.referencePath;
            GameObject target;
            if (direct && direct.transform.IsChildOf(root))
            {
                path = Path(root, direct.transform);
                target = Unique(root, path);
                if (target != direct) throw new InvalidOperationException(JoinLocalization.Text("結合先の直接参照とパスが一致しません。", "The direct target reference and path do not match."));
            }
            else
            {
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException(JoinLocalization.Text("結合先が未設定です。", "The target is not set."));
                target = Unique(root, path);
            }
            if (target == join.gameObject) throw new InvalidOperationException(JoinLocalization.Text("自分自身には統合できません。", "A mesh cannot be joined to itself."));
            if (target.transform.IsChildOf(join.transform)) throw new InvalidOperationException(JoinLocalization.Text("削除する統合元の子には統合できません。", "The target cannot be a child of the source object being removed."));
            var renderer = target.GetComponent<SkinnedMeshRenderer>();
            if (!renderer || !renderer.sharedMesh) throw new InvalidOperationException(JoinLocalization.Text("結合先にはメッシュ付きのSkinnedMeshRendererが必要です。", "The target requires a SkinnedMeshRenderer with a mesh."));
            return renderer;
        }

        internal static void Refresh(MeshJoin join)
        {
            var root = Avatar(join);
            if (!root) return;
            var target = Resolve(join, root);
            var path = Path(root, target.transform);
            var so = new SerializedObject(join);
            var reference = so.FindProperty(nameof(MeshJoin.Target));
            if (reference.FindPropertyRelative("targetObject").objectReferenceValue == target.gameObject &&
                join.Target.referencePath == path) return;
            Undo.RecordObject(join, JoinLocalization.Text("結合先参照を更新", "Update mesh join target reference"));
            join.Target.Set(target.gameObject);
            PrefabUtility.RecordPrefabInstancePropertyModifications(join);
            EditorUtility.SetDirty(join);
        }
    }
}
