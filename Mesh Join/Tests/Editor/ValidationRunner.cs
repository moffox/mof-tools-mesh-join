using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MofTools.MeshJoin.Editor;
using nadena.dev.modular_avatar.core;
using nadena.dev.ndmf;
using nadena.dev.ndmf.animator;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using Object = UnityEngine.Object;

namespace MofTools.MeshJoin.Tests
{
    public static class ValidationRunner
    {
        private static readonly List<string> Results = new List<string>();
        private static readonly List<Object> Owned = new List<Object>();
        private static int checks;
        private static string output;

        public static void Run()
        {
            if (!Application.isBatchMode)
            {
                EditorWindow.GetWindow<SceneView>().Show();
                EditorApplication.delayCall += Execute;
            }
            else Execute();
        }

        private static void Execute()
        {
            output = Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MeshJoin).Assembly).resolvedPath, ".validation");
            Directory.CreateDirectory(output);
            int code = 0;
            try
            {
                RunCase("UI設定・言語・アイコン・パッケージ配置", InterfaceSettings);
                RunCase("境界・UV・法線・ウェイト・BlendShape", Synthetic);
                RunCase("距離外・曖昧参照・Cutter二重処理", Rejections);
                RunCase("削除で新しくできた切り口への限定", CutBoundary);
                RunCase("複数パーツ・同順位・後側優先", MultipleParts);
                RunCase("アニメーション移行・競合検出", AnimationCases);
                RunCase("保存シーンの複製によるNDMFビルド", RealAvatar);
                Results.Add($"PASS: {checks} checks");
            }
            catch (Exception e) { code = 1; Results.Add("FAIL: " + e); Debug.LogException(e); }
            finally
            {
                foreach (var obj in Owned.Where(o => o).Reverse()) Object.DestroyImmediate(obj);
                File.WriteAllLines(Path.Combine(output, "validation.txt"), Results);
                Debug.Log(string.Join("\n", Results));
                EditorApplication.Exit(code);
            }
        }
        private static void RunCase(string name, Action action) { action(); Results.Add("PASS: " + name); }
        private static T Own<T>(T value) where T : Object { Owned.Add(value); return value; }
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static void Throws(Action action, string message)
        { bool threw = false; try { action(); } catch (InvalidOperationException) { threw = true; } Check(threw, message); }

        private static GameObject Root()
        {
            var root = Own(new GameObject("ValidationAvatar")); root.AddComponent<VRCAvatarDescriptor>(); return root;
        }
        private static SkinnedMeshRenderer Quad(GameObject root, string name, float x, bool uv1)
        {
            var go = new GameObject(name); go.transform.SetParent(root.transform, false);
            var bone = new GameObject(name + "Bone"); bone.transform.SetParent(root.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Own(new Mesh { name = name });
            mesh.vertices = new[] { new Vector3(x, 0, 0), new Vector3(x + 1, 0, 0), new Vector3(x + 1, 1, 0), new Vector3(x, 1, 0) };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 4).ToArray();
            mesh.tangents = Enumerable.Repeat(new Vector4(1, 0, 0, 1), 4).ToArray();
            mesh.uv = mesh.vertices.Select(v => new Vector2(v.x, v.y)).ToArray();
            if (uv1) mesh.uv2 = Enumerable.Repeat(new Vector2(.2f, .3f), 4).ToArray();
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4).ToArray();
            renderer.sharedMesh = mesh; renderer.bones = new[] { bone.transform }; renderer.rootBone = bone.transform;
            var material = Own(new Material(Shader.Find("Standard"))); renderer.sharedMaterial = material;
            return renderer;
        }

        private static void Synthetic()
        {
            var root = Root(); var target = Quad(root, "Body", 0, false); var source = Quad(root, "Part", 1, true);
            source.sharedMaterial = target.sharedMaterial;
            source.sharedMesh.normals = Enumerable.Repeat(new Vector3(0, 1, 1).normalized, 4).ToArray();
            var join = source.gameObject.AddComponent<MeshJoin>(); join.Target.Set(target.gameObject); join.DeleteFaces = false;
            var targetDelta = Enumerable.Repeat(new Vector3(0, 0, .1f), 4).ToArray();
            var sourceDelta = Enumerable.Repeat(new Vector3(0, .2f, 0), 4).ToArray();
            var zero = new Vector3[4];
            target.sharedMesh.AddBlendShapeFrame("Shared", 50, targetDelta, zero, zero);
            target.sharedMesh.AddBlendShapeFrame("Shared", 100, targetDelta.Select(v => v * 2).ToArray(), zero, zero);
            target.sharedMesh.AddBlendShapeFrame("BodyOnly", 100, targetDelta, zero, zero);
            source.sharedMesh.AddBlendShapeFrame("Shared", 100, sourceDelta, zero, zero);
            source.sharedMesh.AddBlendShapeFrame("PartOnly", 100, sourceDelta, zero, zero);
            target.SetBlendShapeWeight(0, 10); source.SetBlendShapeWeight(0, 35);
            var originalTarget = target.sharedMesh.vertices.ToArray();
            var result = BoundaryJoin.Merge(target, source, join); Own(result.Mesh);
            Check(result.MatchedVertices == 2, "境界2頂点だけ対応すること");
            Check(result.Mesh.vertexCount == 8, "UV1が異なる頂点を分割して保持すること");
            Check(result.Materials.Length == 1 && result.SourceSlots[0] == 0, "同じMaterialのスロット統合");
            Check(result.InitialWeights["Shared"] == 35, "追加側の初期値優先");
            Check(result.Mesh.GetBlendShapeFrameCount(result.Mesh.GetBlendShapeIndex("Shared")) == 2, "異なるフレーム重みを保持");
            var uv = new List<Vector2>(); result.Mesh.GetUVs(1, uv);
            Check(uv.Take(4).All(v => v == Vector2.zero), "不足UVチャンネルは0");
            Check(uv.Skip(4).All(v => v == new Vector2(.2f, .3f)), "追加側UV1保持");
            var positions = result.Mesh.vertices; var normals = result.Mesh.normals; var weights = result.Mesh.boneWeights;
            var expectedNormal = (Vector3.forward + new Vector3(0, 1, 1).normalized).normalized;
            foreach (var group in Enumerable.Range(0, positions.Length).Where(i => positions[i].x == 1).GroupBy(i => positions[i]))
            {
                var indices = group.ToArray();
                Check(indices.Length == 2, "UV分割境界の両側を保持");
                Check(indices.All(i => Vector3.Distance(normals[i], expectedNormal) < 1e-5f), "境界法線平均");
                Check(weights[indices[0]].boneIndex0 == weights[indices[1]].boneIndex0, "境界ウェイト一致");
                var dp = new Vector3[positions.Length]; var dn = new Vector3[positions.Length]; var dt = new Vector3[positions.Length];
                int shape = result.Mesh.GetBlendShapeIndex("Shared"); result.Mesh.GetBlendShapeFrameVertices(shape, 1, dp, dn, dt);
                Check(indices.All(i => Vector3.Distance(dp[i], sourceDelta[0]) < 1e-6f), "同名BlendShapeの境界は追加側優先");
                shape = result.Mesh.GetBlendShapeIndex("BodyOnly"); result.Mesh.GetBlendShapeFrameVertices(shape, 0, dp, dn, dt);
                Check(indices.All(i => Vector3.Distance(dp[i], targetDelta[0]) < 1e-6f), "本体のみの形状も追加側境界へ伝達");
            }
            Check(target.sharedMesh.vertices.SequenceEqual(originalTarget), "元Mesh未変更");
            Check(result.Mesh.normals[0] == Vector3.forward, "境界以外の法線未変更");
            var part2 = Quad(root, "WeldPart", 1, false); part2.sharedMaterial = target.sharedMaterial;
            var second = part2.gameObject.AddComponent<MeshJoin>(); second.Target.Set(target.gameObject); second.DeleteFaces = false;
            var welded = BoundaryJoin.Merge(target, part2, second); Own(welded.Mesh);
            // Shape attributes may retain splits; remove shapes to test physical vertex collapse separately.
            var plainTarget = Quad(root, "PlainBody", 0, false); part2.sharedMaterial = plainTarget.sharedMaterial;
            second.Target.Set(plainTarget.gameObject);
            var plain = BoundaryJoin.Merge(plainTarget, part2, second); Own(plain.Mesh);
            Check(plain.Mesh.vertexCount == 6 && plain.WeldedVertices == 2, "属性共通の境界は実際に頂点を統合");
        }

        private static void Rejections()
        {
            var root = Root(); var target = Quad(root, "Body", 0, false); var source = Quad(root, "Part", 1.01f, false);
            var join = source.gameObject.AddComponent<MeshJoin>(); join.DeleteFaces = false; join.Target.Set(target.gameObject);
            Throws(() => BoundaryJoin.Merge(target, source, join), "距離外を吸着しない");
            var duplicate = new GameObject("Body"); duplicate.transform.SetParent(root.transform, false);
            Throws(() => JoinReferences.Resolve(join, root.transform), "同一パスを拒否");
            Object.DestroyImmediate(duplicate);
            var cutter = source.gameObject.AddComponent<ModularAvatarMeshCutter>(); cutter.Object.Set(target.gameObject);
            Throws(() => JoinBuild.ValidateCutterOverlap(root, target), "Cutter二重処理拒否");
            Object.DestroyImmediate(cutter);
        }

        private static void CutBoundary()
        {
            var root = Root(); var target = Quad(root, "CutBody", 0, false); var source = Quad(root, "CutPart", 1, false);
            var mesh = target.sharedMesh;
            mesh.Clear();
            mesh.vertices = new[] { new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,1,0), new Vector3(0,1,0), new Vector3(2,0,0), new Vector3(2,1,0) };
            mesh.normals = Enumerable.Repeat(Vector3.forward, 6).ToArray();
            mesh.uv = mesh.vertices.Select(v => new Vector2(v.x / 2, v.y)).ToArray();
            mesh.triangles = new[] { 0,1,2,0,2,3,1,4,5,1,5,2 };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 6).ToArray();
            var texture = Own(new Texture2D(2, 1, TextureFormat.RGB24, false)); texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels(new[] { Color.black, Color.white }); texture.Apply();
            var join = source.gameObject.AddComponent<MeshJoin>(); join.Target.Set(target.gameObject); join.DeleteMask = texture; join.DeleteFaces = true;
            source.sharedMaterial = target.sharedMaterial;
            var result = BoundaryJoin.Merge(target, source, join); Own(result.Mesh);
            Check(result.MatchedVertices == 2, "新しくできた切り口だけに対応");
            Check(result.Mesh.triangles.Length == 12, "削除2面・追加2面");
            Check(result.SeamPositions.All(v => v.x == 1), "既存の外周境界は結合しない");
        }

        private static void MultipleParts()
        {
            var root = Root(); var target = Quad(root, "MultiBody", 0, false); var first = Quad(root, "First", 1, false); var last = Quad(root, "Last", 0, false);
            last.sharedMesh.vertices = last.sharedMesh.vertices.Select(v => v + Vector3.up).ToArray();
            first.sharedMaterial = target.sharedMaterial; last.sharedMaterial = target.sharedMaterial;
            var zero = new Vector3[4];
            first.sharedMesh.AddBlendShapeFrame("Same", 100, Enumerable.Repeat(Vector3.forward * .1f, 4).ToArray(), zero, zero);
            last.sharedMesh.AddBlendShapeFrame("Same", 100, Enumerable.Repeat(Vector3.forward * .2f, 4).ToArray(), zero, zero);
            first.SetBlendShapeWeight(0, 20); last.SetBlendShapeWeight(0, 60);
            var a = first.gameObject.AddComponent<MeshJoin>(); a.Target.Set(target.gameObject); a.DeleteFaces = false;
            var b = last.gameObject.AddComponent<MeshJoin>(); b.Target.Set(target.gameObject); b.DeleteFaces = false;
            a.Order = b.Order = 5;
            var context = new BuildContext(root, null);
            JoinBuild.Prepare(context);
            Check(context.GetState<JoinState>().Entries[0].Source == first && context.GetState<JoinState>().Entries[1].Source == last, "同順位のHierarchy順");
            var one = BoundaryJoin.Merge(target, first, a); Own(one.Mesh);
            target.sharedMesh = one.Mesh; target.bones = one.Bones; target.sharedMaterials = one.Materials;
            target.SetBlendShapeWeight(0, one.InitialWeights["Same"]);
            var two = BoundaryJoin.Merge(target, last, b); Own(two.Mesh);
            Check(two.InitialWeights["Same"] == 60, "複数パーツの同名初期値は最後を採用");
            var dp = new Vector3[two.Mesh.vertexCount]; var dn = new Vector3[dp.Length]; var dt = new Vector3[dp.Length];
            two.Mesh.GetBlendShapeFrameVertices(0, 0, dp, dn, dt);
            var vertices = two.Mesh.vertices;
            Check(Enumerable.Range(0, dp.Length).Where(i => vertices[i].y == 1 && vertices[i].x <= 1).All(i => Mathf.Abs(dp[i].z - .2f) < 1e-6f), "最後のパーツの境界変形を採用");
            var runRoot = Root(); var runBody = Quad(runRoot,"RunBody",0,false); var runFirst = Quad(runRoot,"RunFirst",1,false); var runLast = Quad(runRoot,"RunLast",0,false);
            runLast.sharedMesh.vertices = runLast.sharedMesh.vertices.Select(v => v + Vector3.up).ToArray();
            runFirst.sharedMaterial = runBody.sharedMaterial; runLast.sharedMaterial = runBody.sharedMaterial;
            foreach (var renderer in new[] { runFirst, runLast })
            { var item = renderer.gameObject.AddComponent<MeshJoin>(); item.Target.Set(runBody.gameObject); item.DeleteFaces = false; }
            var expectedAnchor = runLast.bones[0]; runLast.probeAnchor = expectedAnchor; runLast.localBounds = new Bounds(Vector3.one,new Vector3(5,6,7));
            var runContext = new BuildContext(runRoot,null);
            runContext.ActivateExtensionContextRecursive<AnimatorServicesContext>();
            try
            {
                JoinBuild.Prepare(runContext); JoinBuild.Execute(runContext);
                Check(runBody.probeAnchor == expectedAnchor && runBody.rootBone == expectedAnchor, "最後の追加側アンカー・Root Boneを使用");
                Check(runBody.localBounds.size == new Vector3(5,6,7), "最後の追加側Boundsを使用");
                Check(!runFirst && !runLast, "複数追加Rendererの削除");
            }
            finally { runContext.DeactivateAllExtensionContexts(); runContext.AssetSaver.Dispose(); }
        }

        private static void AnimationCases()
        {
            var root = Root(); var target = Quad(root, "AnimatedBody", 0, false); var source = Quad(root, "AnimatedPart", 1, false);
            source.sharedMaterial = target.sharedMaterial;
            var zero = new Vector3[4]; target.sharedMesh.AddBlendShapeFrame("Same",100,zero,zero,zero); source.sharedMesh.AddBlendShapeFrame("Same",100,zero,zero,zero);
            var join = source.gameObject.AddComponent<MeshJoin>(); join.Target.Set(target.gameObject); join.DeleteFaces = false;
            var result = BoundaryJoin.Merge(target, source, join); Own(result.Mesh);
            var context = new BuildContext(root, null);
            var services = context.ActivateExtensionContextRecursive<AnimatorServicesContext>();
            try
            {
                var cc = services.ControllerContext;
                var controller = VirtualAnimatorController.Create(cc.CloneContext, "Validation"); cc.Controllers["validation"] = controller;
                var layer = controller.AddLayer(LayerPriority.Default, "Validation");
                var sm = VirtualStateMachine.Create(cc.CloneContext, "States"); layer.StateMachine = sm;
                VirtualClip AddClip(string name)
                {
                    var clip = VirtualClip.Create(name); sm.AddState(name, clip); return clip;
                }
                var clip = AddClip("Source");
                var shapeBinding = EditorCurveBinding.FloatCurve("AnimatedPart", typeof(SkinnedMeshRenderer), "blendShape.Same");
                var activeBinding = EditorCurveBinding.FloatCurve("AnimatedPart", typeof(GameObject), "m_IsActive");
                clip.SetFloatCurve(shapeBinding, AnimationCurve.Linear(0,0,1,100)); clip.SetFloatCurve(activeBinding, AnimationCurve.Constant(0,1,0));
                var bodyActive = EditorCurveBinding.FloatCurve("AnimatedBody",typeof(GameObject),"m_IsActive"); clip.SetFloatCurve(bodyActive, AnimationCurve.Constant(0,1,1));
                var slotBinding = EditorCurveBinding.PPtrCurve("AnimatedPart",typeof(SkinnedMeshRenderer),"m_Materials.Array.data[0]");
                clip.SetObjectCurve(slotBinding, new[] { new ObjectReferenceKeyframe { time = 0, value = source.sharedMaterial } });
                JoinAnimation.Rewrite(services,target,source,result);
                Check(!clip.GetFloatCurveBindings().Any(b => b.path == "AnimatedPart"), "追加側ON/OFFを除去");
                Check(clip.GetFloatCurve(bodyActive) != null, "統合先ON/OFF維持");
                Check(clip.GetFloatCurveBindings().Any(b => b.path == "AnimatedBody" && b.propertyName == "blendShape.Same"), "BlendShapeパス移行");
                Check(clip.GetObjectCurveBindings().Any(b => b.path == "AnimatedBody" && b.propertyName == "m_Materials.Array.data[0]"), "マテリアル差し替え移行");
                var conflict = AddClip("IndependentSource"); conflict.SetFloatCurve(shapeBinding,AnimationCurve.Constant(0,1,20));
                Throws(() => JoinAnimation.Rewrite(services,target,source,result), "異なる元Rendererの同名アニメーション競合");
                conflict.SetFloatCurve(shapeBinding,null);
                var material = EditorCurveBinding.PPtrCurve("AnimatedPart",typeof(SkinnedMeshRenderer),"m_Materials.Array.data[0]");
                conflict.SetObjectCurve(material,new[] { new ObjectReferenceKeyframe { time = 0, value = source.sharedMaterial } });
                Throws(() => JoinAnimation.Rewrite(services,target,source,result), "マテリアル差し替え競合");
            }
            finally { context.DeactivateAllExtensionContexts(); context.AssetSaver.Dispose(); }
        }

        private static void RealAvatar()
        {
            var paths = new[] { "Assets/New Scene.unity", "Assets/BodyParts/Body_Parts.prefab", "Assets/BodyParts/Body_Parts.fbx", "Assets/Mofuaki/Chalo/Chalo1.prefab", "Assets/Mofuaki/Chalo/FBX/Chalo.fbx", "Assets/BodyParts/New Animation.anim", "Assets/BodyParts/New Animator Controller.controller" };
            var hashes = paths.ToDictionary(p => p, Hash);
            var scene = EditorSceneManager.OpenScene("Assets/New Scene.unity", OpenSceneMode.Additive);
            GameObject clone = null;
            try
            {
                var avatar = scene.GetRootGameObjects().First(g => g.GetComponent<VRCAvatarDescriptor>());
                clone = Object.Instantiate(avatar); clone.name = "MofToolsValidationClone";
                var part = clone.transform.Find("Body_Parts/b_tail_shark.002").GetComponent<SkinnedMeshRenderer>();
                var target = clone.transform.Find("b_tail_shark").GetComponent<SkinnedMeshRenderer>();
                var targetObject = target.gameObject; var targetRenderer = target;
                Undo.IncrementCurrentGroup(); int undoGroup = Undo.GetCurrentGroup();
                var join = Undo.AddComponent<MeshJoin>(part.gameObject);
                if (part.GetComponent<ModularAvatarMeshCutter>()) CutterMigration.Migrate(join);
                Check(!part.GetComponent<ModularAvatarMeshCutter>() && join.DeleteMask, "初回Cutter移行と削除");
                Undo.FlushUndoRecordObjects(); Undo.CollapseUndoOperations(undoGroup); Undo.PerformUndo();
                Check(!part.GetComponent<MeshJoin>() && part.GetComponent<ModularAvatarMeshCutter>(), "Cutter移行のUndo");
                Undo.PerformRedo(); join = part.GetComponent<MeshJoin>();
                Check(join && join.DeleteMask && !part.GetComponent<ModularAvatarMeshCutter>(), "Cutter移行のRedo");
                Check(JoinReferences.Resolve(join, clone.transform) == target, "シーンの結合先解決");
                var expectedAnchor = clone.transform.Find("Armature/Hips");
                var meshSettings = part.gameObject.AddComponent<ModularAvatarMeshSettings>();
                meshSettings.InheritProbeAnchor = ModularAvatarMeshSettings.InheritMode.Set;
                meshSettings.ProbeAnchor = new AvatarObjectReference(expectedAnchor.gameObject);
                meshSettings.InheritBounds = ModularAvatarMeshSettings.InheritMode.Set;
                meshSettings.RootBone = new AvatarObjectReference(expectedAnchor.gameObject);
                meshSettings.Bounds = new Bounds(Vector3.zero, Vector3.one * 4);
                foreach (var transform in clone.GetComponentsInChildren<Transform>(true))
                    if (PrefabUtility.IsOutermostPrefabInstanceRoot(transform.gameObject)) PrefabUtility.UnpackPrefabInstance(transform.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                var originalMesh = target.sharedMesh;
                var originalMaterials = target.sharedMaterials;
                RenderEvidence(part, "input-part.png");
                RenderEvidence(target, "input-tail.png");
                var cutPreview = BoundaryJoin.Merge(target, part, join); Own(cutPreview.Mesh);
                Check(cutPreview.MatchedVertices == 16, "実データの16境界頂点");
                Check(cutPreview.Mesh.GetBlendShapeIndex("Key 2") >= 0, "実データのBlendShape保持");
                Results.Add($"実データ予備結合: 対応{cutPreview.MatchedVertices}、未対応{cutPreview.UnmatchedVertices}、実結合{cutPreview.WeldedVertices}、出力頂点{cutPreview.Mesh.vertexCount}");
                var context = AvatarProcessor.ProcessAvatar(clone, nadena.dev.ndmf.platform.AmbientPlatform.DefaultPlatform);
                Check(context.Successful, "NDMFビルドがエラーなしで完了");
                Check(targetObject && targetRenderer && targetObject.name == "b_tail_shark", "統合先GameObjectとRendererを保持");
                Check(!clone.transform.Find("Body_Parts/b_tail_shark.002"), "追加RendererのGameObject削除");
                Check(target.sharedMesh != originalMesh, "生成Meshを使用");
                Check(target.sharedMesh.GetBlendShapeIndex("Key 2") >= 0, "NDMF後のKey 2保持");
                Check(target.probeAnchor == expectedAnchor && target.rootBone == expectedAnchor, "MA Mesh Settings反映後のアンカーとRoot Bone");
                Check(target.localBounds.size == Vector3.one * 4, "MA Mesh Settings反映後のBounds");
                CheckDeformation(target, cutPreview.SeamPositions);
                RenderEvidence(target, "joined-shape100.png");
                target.SetBlendShapeWeight(target.sharedMesh.GetBlendShapeIndex("Key 2"),0);
                RenderEvidence(target, "joined-base.png");
                RenderEvidence(target, "joined-static.png", false);
                Check(clone.GetComponentsInChildren<Transform>(true).Any(t => t.name.Contains("tail.005")), "追加側の必要ボーン保持");
                Check(!clone.GetComponentsInChildren<Transform>(true).Any(t => t.name.StartsWith("NaN")), "NaN非表示ボーンを生成しない");
                var descriptor = clone.GetComponent<VRCAvatarDescriptor>();
                var clips = descriptor.baseAnimationLayers.Where(l => l.animatorController).SelectMany(l => l.animatorController.animationClips).Distinct().ToArray();
                var bindings = clips.SelectMany(AnimationUtility.GetCurveBindings).ToArray();
                Check(bindings.Any(b => b.path == "b_tail_shark" && b.propertyName == "blendShape.Key 2"), "アニメーションを統合先へ移行");
                Check(!bindings.Any(b => b.path.EndsWith("b_tail_shark.002")), "統合元のON/OFF・アニメーション参照除去");
                Check(bindings.Any(b => b.path == "b_tail_shark" && b.propertyName == "m_IsActive"), "統合先自身のON/OFF保持");
                Check(descriptor.expressionsMenu, "メニュー保持");
                Check(originalMesh == AssetDatabase.LoadAllAssetsAtPath("Assets/Mofuaki/Chalo/FBX/Chalo.fbx").OfType<Mesh>().First(m => m.name == "b_tail_shark"), "元メッシュ参照保持");
                Results.Add("NDMF出力: " + target.sharedMesh.vertexCount + "頂点、" + target.sharedMaterials.Length + "マテリアル、" + target.sharedMesh.blendShapeCount + "BlendShape");
            }
            finally
            {
                if (clone) Object.DestroyImmediate(clone);
                EditorSceneManager.CloseScene(scene, true);
                foreach (string p in paths) Check(hashes[p] == Hash(p), "元ファイル未変更: " + p);
            }
        }
        private static string Hash(string path)
        { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path))); }

        private static void InterfaceSettings()
        {
            var root = Root(); var source = Quad(root,"UI Source",0,false);
            var join = source.gameObject.AddComponent<MeshJoin>();
            Check(!join.DeleteFaces, "新規追加時の統合先の処理はなにもしない");
            Check(MeshJoinInspector.FormatDistance(0.00001f) == "0.00001", "結合距離を指数表記にしない");
            Check(MeshJoinInspector.FormatDistance(0) == "0", "距離0の小数表示");
            Check(MeshJoinInspector.TryParseDistance("0.00001",out float distance) && distance == 0.00001f, "小数表記の入力を保存");
            Check(MeshJoinInspector.TryParseDistance("1e-05",out distance) && MeshJoinInspector.FormatDistance(distance) == "0.00001", "指数入力も通常の小数表記で表示");
            Check(!MeshJoinInspector.TryParseDistance("NaN",out _) && !MeshJoinInspector.TryParseDistance("Infinity",out _) && !MeshJoinInspector.TryParseDistance("-1",out _), "距離の不正な入力を拒否");
            join.DeleteFaces = true;
            string saved = EditorJsonUtility.ToJson(join);
            join.DeleteFaces = false;
            EditorJsonUtility.FromJsonOverwrite(saved,join);
            Check(join.DeleteFaces, "既存の固定削除設定を維持");
            var menu = (AddComponentMenu)Attribute.GetCustomAttribute(typeof(MeshJoin),typeof(AddComponentMenu));
            Check(menu.componentMenu == "MofTools/MA Mesh Join by Mof", "Add Componentの英語名");
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MeshJoin).Assembly);
            Check(package.displayName == "MofTools" && package.version == "0.4.0", "MofToolsパッケージ表示とバージョン");
            string scriptPath = "Packages/" + package.name + "/Mesh Join/Runtime/MeshJoin.cs";
            string iconPath = "Packages/" + package.name + "/Mesh Join/Editor/Icons/MeshJoin.png";
            Check(AssetDatabase.AssetPathToGUID(scriptPath) == "f5094d0c42ae5251ccbbdb59f60ea1fc", "移動後もスクリプトGUIDを維持");
            var importer = AssetImporter.GetAtPath(scriptPath) as MonoImporter;
            Check(importer && AssetDatabase.GetAssetPath(importer.GetIcon()) == iconPath, "コンポーネントアイコン設定");
            EditorPrefs.DeleteKey("MofTools.MeshJoin.SceneIconDefault:" + Application.dataPath);
            SceneIconDefaults.ApplyOnce();
            var info = GizmoUtility.GetGizmoInfo().FirstOrDefault(g => (g.script as MonoScript)?.GetClass() == typeof(MeshJoin));
            Check(info != null && !info.iconEnabled, $"シーンのアイコンを初期状態で非表示 (found={info != null}, hasIcon={info?.hasIcon}, enabled={info?.iconEnabled})");
            info.iconEnabled = true; GizmoUtility.ApplyGizmoInfo(info, false);
            SceneIconDefaults.ApplyOnce();
            info = GizmoUtility.GetGizmoInfo().FirstOrDefault(g => (g.script as MonoScript)?.GetClass() == typeof(MeshJoin));
            Check(info != null && info.iconEnabled, "後から手動で有効にしたアイコン設定を保持");
            info.iconEnabled = false; GizmoUtility.ApplyGizmoInfo(info, false);
            Check(AssetDatabase.AssetPathToGUID(iconPath) != "a8edd5bd1a0a64a40aa99cc09fb5f198", "コピーしたアイコンには固有GUIDを付与");
            string language = LanguagePrefs.Language;
            try
            {
                LanguagePrefs.Language = "ja-jp";
                Check(JoinLocalization.Text("結合設定","Join Settings") == "結合設定", "MA言語設定の日本語に追従");
                try { JoinReferences.Resolve(join,root.transform); } catch (InvalidOperationException e) { Check(e.Message == "結合先が未設定です。", "日本語の診断表示"); }
                LanguagePrefs.Language = "en-us";
                Check(JoinLocalization.Text("結合設定","Join Settings") == "Join Settings", "MA言語設定の英語に追従");
                try { JoinReferences.Resolve(join,root.transform); } catch (InvalidOperationException e) { Check(e.Message == "The target is not set.", "英語の診断表示"); }
                LanguagePrefs.Language = "fr-fr";
                Check(JoinLocalization.Text("結合設定","Join Settings") == "Join Settings", "未対応言語は英語");
            }
            finally { LanguagePrefs.Language = language; }
        }

        private static void CheckDeformation(SkinnedMeshRenderer renderer, Vector3[] seamPositions)
        {
            var positions = renderer.sharedMesh.vertices;
            var groups = seamPositions.Select(p => Enumerable.Range(0, positions.Length).Where(i => Vector3.Distance(positions[i],p)<1e-7f).ToArray()).ToArray();
            Check(groups.Length == 16 && groups.All(g => g.Length >= 2), "変形検証用の16境界位置の両側を保持");
            var bone = renderer.bones.First(t => t.name.Contains("tail.005")); var rotation = bone.localRotation;
            var baked = Own(new Mesh());
            foreach (float weight in new[] { 0f, 50f, 100f })
            {
                renderer.SetBlendShapeWeight(renderer.sharedMesh.GetBlendShapeIndex("Key 2"),weight);
                bone.localRotation = rotation * Quaternion.Euler(25,15,5);
                renderer.BakeMesh(baked); var deformed = baked.vertices;
                foreach (var group in groups) Check(group.All(i => Vector3.Distance(deformed[i],deformed[group[0]])<1e-6f), "ポーズとBlendShape変形後も境界が離れない");
            }
            bone.localRotation = rotation;
        }

        private static void RenderEvidence(SkinnedMeshRenderer renderer, string filename, bool bake = true)
        {
            var baked = Own(new Mesh());
            if (bake) renderer.BakeMesh(baked); else { Object.DestroyImmediate(baked); baked = Object.Instantiate(renderer.sharedMesh); Owned.Add(baked); }
            var preview = Own(new GameObject("MofToolsGeometryPreview")); preview.layer = 30;
            preview.transform.SetPositionAndRotation(renderer.transform.position,renderer.transform.rotation); preview.transform.localScale = renderer.transform.lossyScale;
            preview.AddComponent<MeshFilter>().sharedMesh = baked;
            var material = Own(new Material(Shader.Find("Standard"))); material.color = new Color(.65f,.76f,.85f); material.SetFloat("_Glossiness",.25f);
            preview.AddComponent<MeshRenderer>().sharedMaterials = Enumerable.Repeat(material,baked.subMeshCount).ToArray();
            var cameraObject = Own(new GameObject("MofToolsValidationCamera")); var camera = cameraObject.AddComponent<Camera>();
            var center = renderer.transform.TransformPoint(baked.bounds.center);
            camera.transform.position = center + new Vector3(.8f,.35f,.2f); camera.transform.LookAt(center);
            camera.orthographic = true; camera.orthographicSize = .42f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.12f,.14f,.17f); camera.cullingMask = 1<<30;
            var lightObject = Own(new GameObject("MofToolsValidationLight")); var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f; light.cullingMask = 1<<30; light.transform.rotation = Quaternion.Euler(35,-40,0);
            var rt = RenderTexture.GetTemporary(960,960,24); var previous = RenderTexture.active;
            var image = Own(new Texture2D(960,960,TextureFormat.RGB24,false));
            try
            { camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0,0,960,960),0,0); image.Apply(); File.WriteAllBytes(Path.Combine(output,filename),image.EncodeToPNG()); }
            finally { camera.targetTexture = null; RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); }
            Object.DestroyImmediate(preview); Object.DestroyImmediate(cameraObject); Object.DestroyImmediate(lightObject);
        }
    }
}
