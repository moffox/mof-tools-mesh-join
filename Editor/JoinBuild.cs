using System;
using System.Collections.Generic;
using System.Linq;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

[assembly: ExportsPlugin(typeof(MofTools.MeshJoin.Editor.JoinPlugin))]

namespace MofTools.MeshJoin.Editor
{
    public sealed class JoinPlugin : Plugin<JoinPlugin>
    {
        public override string QualifiedName => "dev.moftools.mesh-join";
        public override string DisplayName => "MofTools メッシュ境界結合";
        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving).BeforePlugin("nadena.dev.modular-avatar")
                .Run("結合先と処理順を確定", JoinBuild.Prepare);
            InPhase(BuildPhase.Transforming).AfterPlugin("nadena.dev.modular-avatar")
                .BeforePlugin("nadena.dev.modular-avatar.late-transform-stages")
                .WithRequiredExtension(typeof(AnimatorServicesContext), s => s.Run("固定削除とメッシュ境界結合", JoinBuild.Execute));
        }
    }

    internal sealed class JoinState
    {
        internal readonly List<JoinEntry> Entries = new List<JoinEntry>();
        internal readonly Dictionary<SkinnedMeshRenderer, int[]> MaterialMaps = new Dictionary<SkinnedMeshRenderer, int[]>();
        internal bool Prepared;
    }
    internal sealed class JoinEntry
    {
        internal MeshJoin Settings;
        internal SkinnedMeshRenderer Source, Target;
    }

    internal static class JoinBuild
    {
        internal static void Prepare(BuildContext context)
        {
            var state = context.GetState<JoinState>();
            var settings = context.AvatarRootObject.GetComponentsInChildren<MeshJoin>(true);
            var sources = new HashSet<GameObject>(settings.Select(s => s.gameObject));
            // GetComponentsInChildren follows hierarchy order; LINQ OrderBy is stable for ties.
            foreach (var join in settings.OrderBy(s => s.Order))
            {
                if (!join.enabled) throw new InvalidOperationException("無効な境界結合コンポーネントがあります。処理を止める場合はコンポーネントを取り外してください。");
                if (float.IsNaN(join.JoinDistance) || float.IsInfinity(join.JoinDistance) || join.JoinDistance < 0)
                    throw new InvalidOperationException("結合距離には0以上の有限値を設定してください。");
                var target = JoinReferences.Resolve(join, context.AvatarRootTransform);
                if (sources.Contains(target.gameObject)) throw new InvalidOperationException("統合元を別の統合先にする連鎖構成には対応していません。同じ最終統合先を指定してください。");
                var source = join.GetComponent<SkinnedMeshRenderer>();
                if (!source || !source.sharedMesh) throw new InvalidOperationException("統合元にメッシュ付きのSkinnedMeshRendererが必要です。");
                ValidateCutterOverlap(context.AvatarRootObject, target);
                state.Entries.Add(new JoinEntry { Settings = join, Source = source, Target = target });
                if (!state.MaterialMaps.ContainsKey(target)) state.MaterialMaps.Add(target, Enumerable.Range(0, target.sharedMaterials.Length).ToArray());
            }
            state.Prepared = true;
        }

        internal static void ValidateCutterOverlap(GameObject root, SkinnedMeshRenderer target)
        {
            foreach (var cutter in root.GetComponentsInChildren<ModularAvatarMeshCutter>(true))
                if (cutter.Object?.Get(cutter) == target.gameObject)
                    throw new InvalidOperationException($"「{target.name}」を指定するMA Mesh Cutterが残っています。二重削除を防ぐため処理を停止します。初回移行済みか確認してください。");
            foreach (var changer in root.GetComponentsInChildren<ModularAvatarShapeChanger>(true))
                if (changer.Shapes.Any(s => s.Object?.Get(changer) == target.gameObject && s.ChangeType.ToString().Contains("Delete")))
                    throw new InvalidOperationException("統合先へのMA Shape Changerによる面削除は併用できません。");
        }

        internal static void Execute(BuildContext context)
        {
            var state = context.GetState<JoinState>();
            if (!state.Prepared) throw new InvalidOperationException("結合前の参照検査が完了していません。");
            foreach (var entry in state.Entries)
            {
                if (!entry.Source || !entry.Target || !entry.Settings) throw new InvalidOperationException("先行プラグインによって統合対象が削除されました。");
                ValidateSourceComponents(entry);
                ValidateReferences(context.AvatarRootObject, entry);
                var settings = entry.Settings; var source = entry.Source; var target = entry.Target;
                int originalSlot = settings.MaterialSlot;
                var slots = state.MaterialMaps[target];
                if (settings.DeleteFaces && (originalSlot < 0 || originalSlot >= slots.Length)) throw new InvalidOperationException("削除する元マテリアルスロットが範囲外です。");
                JoinResult result;
                try
                {
                    if (settings.DeleteFaces) settings.MaterialSlot = slots[originalSlot];
                    result = BoundaryJoin.Merge(target, source, settings);
                }
                finally { settings.MaterialSlot = originalSlot; }
                try
                {
                    JoinAnimation.Rewrite(context.Extension<AnimatorServicesContext>(), target, source, result);
                    UpdateDescriptor(context.AvatarRootObject, source, target, result.Mesh);
                    target.sharedMesh = result.Mesh; target.bones = result.Bones; target.sharedMaterials = result.Materials;
                    foreach (var weight in result.InitialWeights) target.SetBlendShapeWeight(result.Mesh.GetBlendShapeIndex(weight.Key), weight.Value);
                    ApplyRendererSettings(source, target);
                    state.MaterialMaps[target] = slots.Select(i => result.TargetSlots[i]).ToArray();
                    context.AssetSaver.SaveAsset(result.Mesh);
                    Debug.Log($"MofTools: {source.name} → {target.name}: 境界{result.MatchedVertices}頂点、実結合{result.WeldedVertices}頂点。", target);
                    if (result.UnmatchedVertices > 0) Debug.LogWarning($"MofTools: 距離外または対応辺がない境界{result.UnmatchedVertices}頂点は結合せず保持しました。", target);
                    foreach (Transform child in source.transform.Cast<Transform>().ToArray()) child.SetParent(source.transform.parent, true);
                    Object.DestroyImmediate(source.gameObject);
                }
                catch { if (target.sharedMesh != result.Mesh) Object.DestroyImmediate(result.Mesh); throw; }
            }
        }

        private static void ValidateSourceComponents(JoinEntry entry)
        {
            foreach (var component in entry.Source.GetComponents<Component>())
            {
                if (!component) throw new InvalidOperationException("統合元にMissing Scriptがあります。");
                if (component is Transform || component is SkinnedMeshRenderer || component is MeshJoin || component is ModularAvatarMeshSettings) continue;
                throw new InvalidOperationException($"統合元の「{component.GetType().Name}」を保持・移行できないため、削除せず停止します。");
            }
            if (entry.Source.GetComponent<Cloth>()) throw new InvalidOperationException("Clothのあるメッシュには対応していません。");
        }

        private static void ValidateReferences(GameObject root, JoinEntry entry)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component is Transform || component.gameObject == entry.Source.gameObject || component is AvatarTagComponent || component is MeshJoin) continue;
                var so = new SerializedObject(component); var iterator = so.GetIterator();
                while (iterator.Next(true))
                {
                    if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var obj = iterator.objectReferenceValue;
                    if (obj != entry.Source && obj != entry.Source.gameObject && obj != entry.Source.transform) continue;
                    if (component is VRCAvatarDescriptor && obj == entry.Source &&
                        (iterator.propertyPath == "VisemeSkinnedMesh" || iterator.propertyPath == "customEyeLookSettings.eyelidsSkinnedMesh")) continue;
                    throw new InvalidOperationException($"削除する統合元への未対応参照: {component.name}/{component.GetType().Name}.{iterator.propertyPath}。元オブジェクトは削除しません。");
                }
            }
        }

        private static void UpdateDescriptor(GameObject root, SkinnedMeshRenderer source, SkinnedMeshRenderer target, Mesh mesh)
        {
            foreach (var descriptor in root.GetComponentsInChildren<VRCAvatarDescriptor>(true))
            {
                if (descriptor.VisemeSkinnedMesh == source) descriptor.VisemeSkinnedMesh = target;
                if (descriptor.customEyeLookSettings.eyelidsSkinnedMesh != source && descriptor.customEyeLookSettings.eyelidsSkinnedMesh != target) continue;
                var previous = descriptor.customEyeLookSettings.eyelidsSkinnedMesh.sharedMesh;
                var indices = descriptor.customEyeLookSettings.eyelidsBlendshapes;
                for (int i = 0; i < indices.Length; i++)
                    if (indices[i] >= 0)
                    {
                        if (indices[i] >= previous.blendShapeCount) throw new InvalidOperationException("まぶたのBlendShape番号が範囲外です。");
                        indices[i] = mesh.GetBlendShapeIndex(previous.GetBlendShapeName(indices[i]));
                    }
                descriptor.customEyeLookSettings.eyelidsSkinnedMesh = target;
            }
        }

        private static void ApplyRendererSettings(SkinnedMeshRenderer source, SkinnedMeshRenderer target)
        {
            var anchor = source.probeAnchor; var bone = source.rootBone;
            if (anchor == source.transform || bone == source.transform)
            {
                Debug.LogWarning("MofTools: 追加側のアンカーまたはRoot Boneが削除対象自身のため、統合先のアンカー・Root Bone・Boundsを保持しました。", target);
                return;
            }
            target.probeAnchor = anchor;
            if (bone)
            { target.rootBone = bone; target.localBounds = source.localBounds; }
            else
            {
                target.rootBone = null;
                var conversion = target.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                var bounds = source.localBounds; var transformed = new Bounds(conversion.MultiplyPoint3x4(bounds.center), Vector3.zero);
                for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                    transformed.Encapsulate(conversion.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z))));
                target.localBounds = transformed;
            }
        }
    }
}
