using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;

namespace MofTools.MeshJoin.Editor
{
    internal static class JoinAnimation
    {
        private sealed class Edit
        {
            internal VirtualClip Clip;
            internal EditorCurveBinding Old, New;
            internal bool ObjectCurve, Remove;
            internal string Origin;
        }

        internal static void Rewrite(AnimatorServicesContext services, SkinnedMeshRenderer target, SkinnedMeshRenderer source, JoinResult result)
        {
            var remapper = services.ObjectPathRemapper;
            var targetPaths = new HashSet<string>(remapper.GetAllPathsForObject(target.gameObject));
            var sourcePaths = new HashSet<string>(remapper.GetAllPathsForObject(source.gameObject));
            var destination = remapper.GetVirtualPathForObject(target.gameObject);
            targetPaths.Add(destination); sourcePaths.Add(remapper.GetVirtualPathForObject(source.gameObject));
            var clips = services.ControllerContext.Controllers.Values.SelectMany(c => c.AllReachableNodes()).OfType<VirtualClip>().Distinct().ToArray();
            var edits = new List<Edit>();
            foreach (var clip in clips)
            {
                foreach (var binding in clip.GetObjectCurveBindings())
                    if (clip.GetObjectCurve(binding).Any(k => k.value == source || k.value == source.gameObject || k.value == source.transform))
                        throw new InvalidOperationException(JoinLocalization.Text("アニメーションのオブジェクト参照カーブが、削除する統合元自身を参照しています。自動移行できません。", "An object-reference animation curve references the source object being removed. Automatic migration is not supported."));
                foreach (var binding in clip.GetFloatCurveBindings()) Plan(clip, binding, false);
                foreach (var binding in clip.GetObjectCurveBindings()) Plan(clip, binding, true);
            }
            // Different states/clips of one original binding are legal. Distinct original bindings
            // collapsing to one destination have potentially independent drivers and must not be overwritten.
            foreach (var group in edits.Where(e => !e.Remove).GroupBy(e => (e.New.propertyName, e.ObjectCurve)))
                if (group.Select(e => e.Origin).Distinct().Count() > 1)
                    throw new InvalidOperationException(JoinLocalization.Text($"アニメーション競合: {destination}/{group.Key.propertyName}へ異なる元プロパティの駆動が集約されます。ステート・レイヤー間の排他性は推測しません。", $"Animation conflict: independent source properties map to {destination}/{group.Key.propertyName}. Exclusivity between states or layers is not assumed."));
            var movedClips = new HashSet<VirtualClip>(edits.Where(e => !e.Remove && sourcePaths.Contains(e.Old.path)).Select(e => e.Clip));
            foreach (var layer in services.ControllerContext.Controllers.Values.SelectMany(c => c.Layers))
            {
                if (layer.AvatarMask == null || layer.StateMachine == null || !layer.StateMachine.AllReachableNodes().OfType<VirtualClip>().Any(movedClips.Contains)) continue;
                var elements = layer.AvatarMask.Elements;
                foreach (string sourcePath in sourcePaths)
                    if (elements.TryGetValue(sourcePath, out float sourceWeight) && elements.TryGetValue(destination, out float targetWeight) && sourceWeight != targetWeight)
                        throw new InvalidOperationException(JoinLocalization.Text("移行元と統合先のAvatarMask設定が異なり、アニメーションの適用範囲を維持できません。", "The source and target AvatarMask settings differ. The animation scope cannot be preserved."));
            }
            foreach (var edit in edits)
            {
                if (edit.ObjectCurve)
                {
                    var curve = edit.Clip.GetObjectCurve(edit.Old);
                    edit.Clip.SetObjectCurve(edit.Old, null);
                    if (!edit.Remove) edit.Clip.SetObjectCurve(edit.New, curve);
                }
                else
                {
                    var curve = edit.Clip.GetFloatCurve(edit.Old);
                    edit.Clip.SetFloatCurve(edit.Old, null);
                    if (!edit.Remove) edit.Clip.SetFloatCurve(edit.New, curve);
                }
            }

            void Plan(VirtualClip clip, EditorCurveBinding binding, bool objectCurve)
            {
                bool fromSource = sourcePaths.Contains(binding.path), fromTarget = targetPaths.Contains(binding.path);
                if (!fromSource && !fromTarget) return;
                bool active = binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive";
                bool enabled = typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled";
                if (fromSource && (active || enabled))
                { edits.Add(new Edit { Clip = clip, Old = binding, Remove = true, ObjectCurve = objectCurve }); return; }
                if (fromTarget && (active || enabled)) return;
                bool rendererBinding = typeof(Renderer).IsAssignableFrom(binding.type);
                if (!rendererBinding)
                {
                    if (fromSource) throw new InvalidOperationException(JoinLocalization.Text($"削除するオブジェクトに未対応のアニメーションがあります: {binding.propertyName}", $"Unsupported animation on the object being removed: {binding.propertyName}"));
                    return;
                }
                var next = binding; next.path = destination; next.type = typeof(SkinnedMeshRenderer);
                if (binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    string name = binding.propertyName.Substring("blendShape.".Length);
                    if (result.Mesh.GetBlendShapeIndex(name) < 0) throw new InvalidOperationException(JoinLocalization.Text($"アニメーションが存在しないBlendShape「{name}」を参照しています。", $"An animation references a missing BlendShape: {name}."));
                }
                else
                {
                    var slot = Regex.Match(binding.propertyName, @"^m_Materials\.Array\.data\[(\d+)\]$");
                    if (slot.Success)
                    {
                        int index = int.Parse(slot.Groups[1].Value); var map = fromSource ? result.SourceSlots : result.TargetSlots;
                        if (index >= map.Length) throw new InvalidOperationException(JoinLocalization.Text("アニメーションのマテリアルスロットが範囲外です。", "An animated material slot is out of range."));
                        next.propertyName = $"m_Materials.Array.data[{map[index]}]";
                    }
                    else if (binding.propertyName.StartsWith("material.", StringComparison.Ordinal))
                        throw new InvalidOperationException(JoinLocalization.Text($"Renderer全体のマテリアルプロパティ操作「{binding.propertyName}」は統合で影響範囲が変わるため移行できません。スロット差し替えには対応しています。", $"Renderer-wide material animation {binding.propertyName} cannot be migrated because merging changes its scope. Material-slot swaps are supported."));
                    else if (fromSource) throw new InvalidOperationException(JoinLocalization.Text($"統合元Rendererの未対応アニメーションです: {binding.propertyName}", $"Unsupported source Renderer animation: {binding.propertyName}"));
                    else return;
                }
                edits.Add(new Edit { Clip = clip, Old = binding, New = next, ObjectCurve = objectCurve, Origin = (fromSource ? "source:" : "target:") + binding.propertyName });
            }
        }
    }
}
