using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MofTools.MeshJoin.Editor
{
    internal sealed class FaceDeletionPreview : IRenderFilter
    {
        private static readonly TogglablePreviewNode EnableNode = TogglablePreviewNode.Create(
            () => JoinLocalization.Text("Mesh Join 面削除", "Mesh Join Face Deletion"), "dev.moftools.mesh-join/face-deletion", true);

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes() { yield return EnableNode; }
        public bool IsEnabled(ComputeContext context) => context.Observe(EnableNode.IsEnabled);

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var targets = new Dictionary<SkinnedMeshRenderer, List<MeshJoin>>();
            foreach (var root in context.GetAvatarRoots())
            {
                // Resolving references is read-only. Observe names and hierarchy for path references.
                foreach (var t in context.GetComponentsInChildren<Transform>(root, true))
                    context.Observe(t.gameObject, g => g.name);
                foreach (var join in context.GetComponentsInChildren<MeshJoin>(root, true))
                {
                    context.Observe(join);
                    if (!join.DeleteFaces || !join.DeleteMask || !context.ActiveInHierarchy(join.gameObject)) continue;
                    try
                    {
                        var target = JoinReferences.Resolve(join, root.transform);
                        if (!targets.TryGetValue(target, out var list)) targets.Add(target, list = new List<MeshJoin>());
                        list.Add(join);
                    }
                    catch (InvalidOperationException) { /* Incomplete settings remain editable without a preview error. */ }
                }
            }
            return targets.Select(p => RenderGroup.For(p.Key).WithData(p.Value.OrderBy(j => j.Order).ToArray(),
                (a, b) => a.SequenceEqual(b))).ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            var pair = proxyPairs.Single();
            var joins = group.GetData<MeshJoin[]>();
            foreach (var join in joins)
            {
                if (!join) continue;
                context.Observe(join);
                if (join && join.DeleteMask) context.Observe(join.DeleteMask);
            }
            var proxy = pair.Item2 as SkinnedMeshRenderer;
            if (proxy && proxy.sharedMesh) context.Observe(proxy.sharedMesh);
            return Task.FromResult<IRenderFilterNode>(new Node(GenerateMesh(proxy ? proxy.sharedMesh : null, joins)));
        }

        internal static Mesh GenerateMesh(Mesh mesh, IEnumerable<MeshJoin> joins)
        {
            if (!mesh) return null;
            Mesh generated = null;
            try
            {
                foreach (var join in joins)
                {
                    if (!join || !join.gameObject.activeInHierarchy || !join.DeleteFaces || !join.DeleteMask || join.MaterialSlot < 0 ||
                        join.MaterialSlot >= mesh.subMeshCount || join.UVChannel < 0 || join.UVChannel > 7 ||
                        mesh.GetTopology(join.MaterialSlot) != MeshTopology.Triangles) continue;
                    var uv = new List<Vector4>(); mesh.GetUVs(join.UVChannel, uv);
                    if (uv.Count != mesh.vertexCount) continue;
                    var input = generated ? generated : mesh;
                    var triangles = input.GetTriangles(join.MaterialSlot);
                    var kept = JoinMesh.KeepMaskedFaces(triangles, uv, join.DeleteMask, join.DeleteColor, join.Selection);
                    if (kept.Length == triangles.Length) continue;
                    if (!generated)
                    {
                        generated = Object.Instantiate(mesh);
                        generated.name = mesh.name + " (MofTools Face Preview)";
                        generated.hideFlags = HideFlags.HideAndDontSave;
                    }
                    generated.SetTriangles(kept, join.MaterialSlot, false);
                }
                return generated;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is UnityException)
            {
                if (generated) Object.DestroyImmediate(generated);
                return null;
            }
        }

        private sealed class Node : IRenderFilterNode
        {
            private Mesh mesh;
            internal Node(Mesh mesh) { this.mesh = mesh; }
            public RenderAspects WhatChanged => RenderAspects.Mesh;
            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (mesh && proxy is SkinnedMeshRenderer renderer) renderer.sharedMesh = mesh;
            }
            public void Dispose() { if (mesh) Object.DestroyImmediate(mesh); mesh = null; }
        }
    }
}
