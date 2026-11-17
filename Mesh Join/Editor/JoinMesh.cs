using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MofTools.MeshJoin.Editor
{
    internal sealed class JoinMesh
    {
        internal sealed class Frame
        {
            internal float Weight;
            internal Vector3[] Position, Normal, Tangent;
        }
        internal sealed class Shape
        {
            internal string Name;
            internal readonly List<Frame> Frames = new List<Frame>();
        }
        internal Vector3[] Positions, Normals;
        internal Vector4[] Tangents;
        internal Color[] Colors;
        internal readonly List<Vector4>[] UV = new List<Vector4>[8];
        internal readonly int[] UVDimension = new int[8];
        internal readonly List<int[]> Triangles = new List<int[]>();
        internal Transform[] Bones;
        internal Matrix4x4[] Bindposes;
        internal List<BoneWeight1>[] Weights;
        internal readonly List<Shape> Shapes = new List<Shape>();
        internal int Count => Positions.Length;

        internal static JoinMesh Read(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            if (!mesh) throw new InvalidOperationException(JoinLocalization.Text("メッシュがありません。", "The mesh is missing."));
            var result = new JoinMesh
            {
                Positions = mesh.vertices, Normals = mesh.normals, Tangents = mesh.tangents,
                Colors = mesh.colors, Bones = renderer.bones, Bindposes = mesh.bindposes
            };
            if (result.Normals.Length != mesh.vertexCount) throw new InvalidOperationException(JoinLocalization.Text("法線のないメッシュには対応していません。", "Meshes without normals are not supported."));
            if (result.Bindposes.Length != result.Bones.Length || result.Bones.Any(b => !b))
                throw new InvalidOperationException(JoinLocalization.Text("ボーンとバインド情報が不整合です。", "Bone references and bind poses are inconsistent."));
            for (int channel = 0; channel < 8; channel++)
            {
                result.UV[channel] = new List<Vector4>();
                var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                if (!mesh.HasVertexAttribute(attribute)) continue;
                result.UVDimension[channel] = mesh.GetVertexAttributeDimension(attribute);
                mesh.GetUVs(channel, result.UV[channel]);
            }
            for (int slot = 0; slot < mesh.subMeshCount; slot++)
            {
                if (mesh.GetTopology(slot) != MeshTopology.Triangles) throw new InvalidOperationException(JoinLocalization.Text("三角形以外のサブメッシュには対応していません。", "Only triangle submeshes are supported."));
                result.Triangles.Add(mesh.GetTriangles(slot));
            }
            result.Weights = new List<BoneWeight1>[mesh.vertexCount];
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                int offset = 0;
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    result.Weights[i] = new List<BoneWeight1>();
                    int count = counts.Length == 0 ? 0 : counts[i];
                    for (int k = 0; k < count; k++)
                    {
                        var weight = weights[offset++];
                        if (weight.boneIndex < 0 || weight.boneIndex >= result.Bones.Length)
                            throw new InvalidOperationException(JoinLocalization.Text("頂点のボーン番号が不正です。", "A vertex has an invalid bone index."));
                        result.Weights[i].Add(weight);
                    }
                    if (count == 0) throw new InvalidOperationException(JoinLocalization.Text("ボーンウェイトのない頂点には対応していません。", "Vertices without bone weights are not supported."));
                }
            }
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var shape = new Shape { Name = mesh.GetBlendShapeName(i) };
                for (int j = 0; j < mesh.GetBlendShapeFrameCount(i); j++)
                {
                    var frame = new Frame { Weight = mesh.GetBlendShapeFrameWeight(i, j), Position = new Vector3[mesh.vertexCount], Normal = new Vector3[mesh.vertexCount], Tangent = new Vector3[mesh.vertexCount] };
                    mesh.GetBlendShapeFrameVertices(i, j, frame.Position, frame.Normal, frame.Tangent);
                    shape.Frames.Add(frame);
                }
                result.Shapes.Add(shape);
            }
            return result;
        }

        internal Mesh Write(string name)
        {
            var mesh = new Mesh { name = name, indexFormat = Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = Positions; mesh.normals = Normals;
            if (Tangents.Length == Count) mesh.tangents = Tangents;
            if (Colors.Length == Count) mesh.colors = Colors;
            for (int channel = 0; channel < 8; channel++)
            {
                if (UVDimension[channel] == 2) mesh.SetUVs(channel, UV[channel].Select(v => new Vector2(v.x, v.y)).ToList());
                else if (UVDimension[channel] == 3) mesh.SetUVs(channel, UV[channel].Select(v => new Vector3(v.x, v.y, v.z)).ToList());
                else if (UVDimension[channel] > 0) mesh.SetUVs(channel, UV[channel]);
            }
            mesh.bindposes = Bindposes;
            using (var counts = new NativeArray<byte>(Weights.Select(w => checked((byte)w.Count)).ToArray(), Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(Weights.SelectMany(w => w).ToArray(), Allocator.Temp)) mesh.SetBoneWeights(counts, weights);
            mesh.subMeshCount = Triangles.Count;
            for (int i = 0; i < Triangles.Count; i++) mesh.SetTriangles(Triangles[i], i, false);
            foreach (var shape in Shapes)
                foreach (var frame in shape.Frames)
                    mesh.AddBlendShapeFrame(shape.Name, frame.Weight, frame.Position, frame.Normal, frame.Tangent);
            mesh.RecalculateBounds(); // Only the generated mesh AABB, never source normals/shape.
            return mesh;
        }

        internal void Transform(Matrix4x4 matrix)
        {
            var normalMatrix = matrix.inverse.transpose;
            float parity = matrix.determinant < 0 ? -1 : 1;
            var normalScale = new float[Count];
            var tangentScale = new float[Count];
            for (int i = 0; i < Count; i++)
            {
                Positions[i] = matrix.MultiplyPoint3x4(Positions[i]);
                var normal = normalMatrix.MultiplyVector(Normals[i]);
                normalScale[i] = normal.magnitude;
                Normals[i] = normal.normalized;
                if (Tangents.Length == Count)
                {
                    Vector3 tangent = matrix.MultiplyVector(Tangents[i]);
                    tangentScale[i] = tangent.magnitude;
                    tangent = Vector3.ProjectOnPlane(tangent, Normals[i]).normalized;
                    Tangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, Tangents[i].w * parity);
                }
            }
            foreach (var shape in Shapes)
                foreach (var frame in shape.Frames)
                    for (int i = 0; i < Count; i++)
                    {
                        frame.Position[i] = matrix.MultiplyVector(frame.Position[i]);
                        frame.Normal[i] = normalMatrix.MultiplyVector(frame.Normal[i]) / Mathf.Max(normalScale[i], 1e-20f);
                        frame.Tangent[i] = matrix.MultiplyVector(frame.Tangent[i]) / Mathf.Max(tangentScale[i], 1e-20f);
                    }
            for (int i = 0; i < Bindposes.Length; i++) Bindposes[i] = Bindposes[i] * matrix.inverse;
            if (parity < 0)
                foreach (var triangle in Triangles)
                    for (int i = 0; i < triangle.Length; i += 3) { int x = triangle[i]; triangle[i] = triangle[i + 1]; triangle[i + 1] = x; }
        }

        internal sealed class Topology
        {
            internal readonly List<List<int>> Groups = new List<List<int>>();
            internal int[] GroupOf;
            internal readonly Dictionary<(int, int), int> Edges = new Dictionary<(int, int), int>();
            internal readonly Dictionary<(int, int), (int, int)> Directions = new Dictionary<(int, int), (int, int)>();
            internal static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
            internal Topology(JoinMesh mesh)
            {
                GroupOf = new int[mesh.Count];
                var positions = new Dictionary<Vector3, int>();
                for (int i = 0; i < mesh.Count; i++)
                {
                    if (!positions.TryGetValue(mesh.Positions[i], out int group))
                    { group = Groups.Count; positions.Add(mesh.Positions[i], group); Groups.Add(new List<int>()); }
                    GroupOf[i] = group; Groups[group].Add(i);
                }
                foreach (var triangles in mesh.Triangles)
                    for (int i = 0; i < triangles.Length; i += 3)
                        for (int j = 0; j < 3; j++)
                        {
                            int a = GroupOf[triangles[i + j]], b = GroupOf[triangles[i + (j + 1) % 3]];
                            if (a == b) throw new InvalidOperationException(JoinLocalization.Text("同位置の頂点を含む退化面があります。境界を安全に判定できません。", "A degenerate face contains coincident vertices. Its boundary cannot be determined safely."));
                            var key = Edge(a, b); Edges.TryGetValue(key, out int n); Edges[key] = n + 1;
                            if (!Directions.ContainsKey(key)) Directions.Add(key, (a, b));
                        }
            }
        }

        internal HashSet<(int, int)> Cut(MeshJoin settings)
        {
            var before = new Topology(this);
            if (!settings.DeleteFaces) return new HashSet<(int, int)>(before.Edges.Where(e => e.Value == 1).Select(e => e.Key));
            if (!settings.DeleteMask) throw new InvalidOperationException(JoinLocalization.Text("面の固定削除には削除マスクが必要です。", "A mask texture is required to delete faces."));
            if (settings.MaterialSlot < 0 || settings.MaterialSlot >= Triangles.Count) throw new InvalidOperationException(JoinLocalization.Text("削除するマテリアルスロットが範囲外です。", "The material slot selected for deletion is out of range."));
            if (settings.UVChannel < 0 || settings.UVChannel > 7 || UV[settings.UVChannel].Count != Count) throw new InvalidOperationException(JoinLocalization.Text("削除に使用するUVチャンネルがありません。", "The UV channel selected for deletion is missing."));
            using (var mask = new MaskSampler(settings.DeleteMask, settings.DeleteColor))
            {
                var uv = UV[settings.UVChannel]; var kept = new List<int>(); var triangles = Triangles[settings.MaterialSlot];
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var a = uv[triangles[i]]; var b = uv[triangles[i + 1]]; var c = uv[triangles[i + 2]];
                    bool remove = settings.Selection == FaceSelection.Centroid ? mask.Selected((a + b + c) / 3) :
                        settings.Selection == FaceSelection.AllVertices ? mask.Selected(a) && mask.Selected(b) && mask.Selected(c) : mask.Selected(a) || mask.Selected(b) || mask.Selected(c);
                    if (!remove) { kept.Add(triangles[i]); kept.Add(triangles[i + 1]); kept.Add(triangles[i + 2]); }
                }
                if (kept.Count == triangles.Length) throw new InvalidOperationException(JoinLocalization.Text("マスクで削除される面がありません。色・UV・スロットを確認してください。", "The mask selects no faces. Check its color, UV channel and material slot."));
                Triangles[settings.MaterialSlot] = kept.ToArray();
            }
            var after = new Topology(this);
            var created = new HashSet<(int, int)>(after.Edges.Where(e => e.Value == 1 && before.Edges.TryGetValue(e.Key, out int n) && n == 2).Select(e => e.Key));
            if (created.Count == 0) throw new InvalidOperationException(JoinLocalization.Text("削除によって接続用の境界辺が生成されませんでした。", "Face deletion did not create a boundary for joining."));
            return created;
        }

        private sealed class MaskSampler : IDisposable
        {
            private readonly Texture2D original;
            private Texture2D copy;
            private readonly Color32[] pixels;
            private readonly byte selected;
            internal MaskSampler(Texture2D texture, MaskColor color)
            {
                original = texture; selected = color == MaskColor.White ? (byte)255 : (byte)0;
                if (texture.isReadable) pixels = texture.GetPixels32();
                else
                {
                    var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    var active = RenderTexture.active;
                    // Change filtering on a temporary clone, never the original texture asset.
                    var sampling = Object.Instantiate(texture);
                    try
                    {
                        sampling.filterMode = FilterMode.Point; Graphics.Blit(sampling, rt); RenderTexture.active = rt;
                        copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                        copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply(); pixels = copy.GetPixels32();
                    }
                    catch { if (copy) Object.DestroyImmediate(copy); throw; }
                    finally { RenderTexture.active = active; RenderTexture.ReleaseTemporary(rt); Object.DestroyImmediate(sampling); }
                }
            }
            internal bool Selected(Vector4 uv)
            {
                int x = Wrap(uv.x, original.width, original.wrapModeU), y = Wrap(uv.y, original.height, original.wrapModeV);
                var pixel = pixels[y * original.width + x]; return pixel.r == selected && pixel.g == selected && pixel.b == selected;
            }
            private static int Wrap(float value, int size, TextureWrapMode mode)
            {
                int i = Mathf.FloorToInt(value * size);
                if (mode == TextureWrapMode.Repeat) return (i % size + size) % size;
                if (mode == TextureWrapMode.Mirror) { i = (i % (size * 2) + size * 2) % (size * 2); return i < size ? i : size * 2 - 1 - i; }
                if (mode == TextureWrapMode.MirrorOnce && i < 0) i = -1 - i;
                return Mathf.Clamp(i, 0, size - 1);
            }
            public void Dispose() { if (copy) Object.DestroyImmediate(copy); }
        }

        internal static Frame Evaluate(Shape shape, float weight, int count)
        {
            var zero = new Frame { Weight = 0, Position = new Vector3[count], Normal = new Vector3[count], Tangent = new Vector3[count] };
            if (shape == null) return zero;
            var frames = shape.Frames.ToList();
            if (!frames.Any(f => f.Weight == 0)) frames.Add(zero);
            frames.Sort((a, b) => a.Weight.CompareTo(b.Weight));
            if (frames.Count == 1) return frames[0];
            int hi = frames.FindIndex(f => f.Weight >= weight);
            hi = hi < 0 ? frames.Count - 1 : Mathf.Clamp(hi, 1, frames.Count - 1);
            var a = frames[hi - 1]; var b = frames[hi];
            float t = (weight - a.Weight) / (b.Weight - a.Weight);
            for (int i = 0; i < count; i++)
            {
                zero.Position[i] = Vector3.LerpUnclamped(a.Position[i], b.Position[i], t);
                zero.Normal[i] = Vector3.LerpUnclamped(a.Normal[i], b.Normal[i], t);
                zero.Tangent[i] = Vector3.LerpUnclamped(a.Tangent[i], b.Tangent[i], t);
            }
            return zero;
        }
    }
}
