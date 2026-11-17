using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MofTools.MeshJoin.Editor
{
    internal sealed class JoinResult
    {
        internal Mesh Mesh;
        internal Material[] Materials;
        internal Transform[] Bones;
        internal int[] TargetSlots, SourceSlots;
        internal Dictionary<string, float> InitialWeights;
        internal int MatchedVertices, UnmatchedVertices, WeldedVertices;
        internal Vector3[] SeamPositions;
    }

    internal static class BoundaryJoin
    {
        internal static JoinResult Merge(SkinnedMeshRenderer target, SkinnedMeshRenderer source, MeshJoin settings)
        {
            var body = JoinMesh.Read(target); var part = JoinMesh.Read(source);
            foreach (var bone in body.Bones.Concat(part.Bones).Distinct())
                if (source.transform.IsChildOf(bone) != target.transform.IsChildOf(bone))
                    throw new InvalidOperationException("片側のRenderer自身がボーン配下にあり、ポーズで結合基準が変わる構成です。Rendererは共通の非変形階層へ配置してください。ボーンのポーズをメッシュへ焼き込む処理は行いません。");
            var cutEdges = body.Cut(settings);
            var toBody = target.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
            if (Mathf.Abs(toBody.determinant) < 1e-12f) throw new InvalidOperationException("ゼロスケールのメッシュには対応していません。");
            part.Transform(toBody);
            var bt = new JoinMesh.Topology(body); var pt = new JoinMesh.Topology(part);
            var partEdges = new HashSet<(int, int)>(pt.Edges.Where(e => e.Value == 1).Select(e => e.Key));
            var bodyGroups = new HashSet<int>(cutEdges.SelectMany(e => new[] { e.Item1, e.Item2 }));
            var partGroups = new HashSet<int>(partEdges.SelectMany(e => new[] { e.Item1, e.Item2 }));
            var match = new Dictionary<int, int>(); var used = new HashSet<int>();
            foreach (int pg in partGroups.OrderBy(i => i))
            {
                var world = target.transform.TransformPoint(part.Positions[pt.Groups[pg][0]]);
                int found = -1;
                foreach (int bg in bodyGroups)
                {
                    var other = target.transform.TransformPoint(body.Positions[bt.Groups[bg][0]]);
                    if (Vector3.Distance(world, other) > settings.JoinDistance) continue;
                    if (found >= 0) throw new InvalidOperationException("結合距離内に複数の境界頂点があります。対応を一意にできません。");
                    found = bg;
                }
                if (found < 0) continue;
                if (!used.Add(found)) throw new InvalidOperationException("複数の追加側境界頂点が同じ結合先に対応しています。");
                match.Add(pg, found);
            }
            // A vertex without a corresponding boundary edge is not a valid seam match.
            var connected = new HashSet<int>();
            foreach (var edge in partEdges)
            {
                if (!match.TryGetValue(edge.Item1, out int a) || !match.TryGetValue(edge.Item2, out int b)) continue;
                if (!cutEdges.Contains(JoinMesh.Topology.Edge(a, b))) throw new InvalidOperationException("境界の頂点位置は一致しますが、対応する辺構造が異なります。");
                var pd = pt.Directions[edge];
                var bd = bt.Directions[JoinMesh.Topology.Edge(a, b)];
                if (match[pd.Item1] != bd.Item2 || match[pd.Item2] != bd.Item1)
                    throw new InvalidOperationException("対応する境界辺の面の向きが一致しません。反転面を確認してください。");
                connected.Add(edge.Item1); connected.Add(edge.Item2);
            }
            foreach (int key in match.Keys.Except(connected).ToArray()) match.Remove(key);
            if (match.Count == 0) throw new InvalidOperationException("結合距離内に、対応する切り口がありません。範囲外の頂点は移動しません。");
            foreach (int g in match.Keys) ValidateGroup(part, pt.Groups[g]);
            foreach (int g in match.Values) ValidateGroup(body, bt.Groups[g]);

            int offset = body.Count; int count = body.Count + part.Count;
            var output = new JoinMesh
            {
                Positions = body.Positions.Concat(part.Positions).ToArray(), Normals = body.Normals.Concat(part.Normals).ToArray(),
                Tangents = Expand(body.Tangents, body.Count, Vector4.zero).Concat(Expand(part.Tangents, part.Count, Vector4.zero)).ToArray(),
                Colors = Expand(body.Colors, body.Count, Color.white).Concat(Expand(part.Colors, part.Count, Color.white)).ToArray(),
                Weights = body.Weights.Select(w => w.ToList()).Concat(part.Weights.Select(w => w.ToList())).ToArray()
            };
            if (body.Tangents.Length == 0 && part.Tangents.Length == 0) output.Tangents = Array.Empty<Vector4>();
            if (body.Colors.Length == 0 && part.Colors.Length == 0) output.Colors = Array.Empty<Color>();
            var bones = body.Bones.ToList(); var binds = body.Bindposes.ToList(); var boneMap = new int[part.Bones.Length];
            for (int i = 0; i < part.Bones.Length; i++)
            {
                int found = -1;
                for (int j = 0; j < bones.Count; j++) if (bones[j] == part.Bones[i] && MatrixEqual(binds[j], part.Bindposes[i])) { found = j; break; }
                if (found < 0) { found = bones.Count; bones.Add(part.Bones[i]); binds.Add(part.Bindposes[i]); }
                boneMap[i] = found;
            }
            output.Bones = bones.ToArray(); output.Bindposes = binds.ToArray();
            for (int i = offset; i < count; i++) output.Weights[i] = output.Weights[i].Select(w => new BoneWeight1 { boneIndex = boneMap[w.boneIndex], weight = w.weight }).ToList();
            for (int channel = 0; channel < 8; channel++)
            {
                output.UVDimension[channel] = Mathf.Max(body.UVDimension[channel], part.UVDimension[channel]);
                output.UV[channel] = (body.UV[channel].Count == body.Count ? body.UV[channel] : Enumerable.Repeat(Vector4.zero, body.Count))
                    .Concat(part.UV[channel].Count == part.Count ? part.UV[channel] : Enumerable.Repeat(Vector4.zero, part.Count)).ToList();
                if (body.UVDimension[channel] != part.UVDimension[channel]) Debug.LogWarning($"MofTools: UV{channel}の構成が異なります。不足する成分・チャンネルは0で補完します。", settings);
            }
            var worldNormal = target.transform.worldToLocalMatrix.transpose;
            var fromWorldNormal = target.transform.localToWorldMatrix.transpose;
            var seamVertices = new HashSet<int>();
            foreach (var pair in match)
            {
                var targetGroup = bt.Groups[pair.Value]; var sourceGroup = pt.Groups[pair.Key]; int winner = offset + sourceGroup[0];
                var bn = targetGroup.Select(i => worldNormal.MultiplyVector(output.Normals[i]).normalized).Aggregate(Vector3.zero, (a, b) => a + b).normalized;
                var pn = sourceGroup.Select(i => worldNormal.MultiplyVector(output.Normals[offset + i]).normalized).Aggregate(Vector3.zero, (a, b) => a + b).normalized;
                if ((bn + pn).sqrMagnitude < 1e-12f) throw new InvalidOperationException("境界法線が正反対で平均できません。面の向きを確認してください。");
                var normal = fromWorldNormal.MultiplyVector((bn + pn).normalized).normalized;
                foreach (int i in targetGroup.Concat(sourceGroup.Select(i => i + offset)))
                {
                    seamVertices.Add(i); output.Positions[i] = output.Positions[winner]; output.Weights[i] = output.Weights[winner].ToList(); output.Normals[i] = normal;
                    if (output.Tangents.Length == count)
                    {
                        var tangent = output.Tangents[i]; var aligned = Vector3.ProjectOnPlane(tangent, normal).normalized;
                        output.Tangents[i] = new Vector4(aligned.x, aligned.y, aligned.z, tangent.w);
                    }
                }
            }

            var names = body.Shapes.Select(s => s.Name).Concat(part.Shapes.Select(s => s.Name)).Distinct().ToArray();
            foreach (string name in names)
            {
                var bs = body.Shapes.FirstOrDefault(s => s.Name == name); var ps = part.Shapes.FirstOrDefault(s => s.Name == name);
                var shape = new JoinMesh.Shape { Name = name };
                var frameWeights = (bs?.Frames.Select(f => f.Weight) ?? Enumerable.Empty<float>()).Concat(ps?.Frames.Select(f => f.Weight) ?? Enumerable.Empty<float>()).Distinct().OrderBy(w => w);
                foreach (float weight in frameWeights)
                {
                    var bf = JoinMesh.Evaluate(bs, weight, body.Count); var pf = JoinMesh.Evaluate(ps, weight, part.Count);
                    var frame = new JoinMesh.Frame { Weight = weight, Position = bf.Position.Concat(pf.Position).ToArray(), Normal = bf.Normal.Concat(pf.Normal).ToArray(), Tangent = bf.Tangent.Concat(pf.Tangent).ToArray() };
                    foreach (var pair in match)
                    {
                        int winner = ps != null ? offset + pt.Groups[pair.Key][0] : bt.Groups[pair.Value][0];
                        foreach (int i in bt.Groups[pair.Value].Concat(pt.Groups[pair.Key].Select(i => offset + i)))
                        {
                            frame.Position[i] = frame.Position[winner]; frame.Normal[i] = frame.Normal[winner];
                            // Tangents remain specific to each UV island, including handedness.
                            if (output.Tangents.Length == count)
                            {
                                Vector3 baseTangent = output.Tangents[i];
                                frame.Tangent[i] = Vector3.ProjectOnPlane(baseTangent + frame.Tangent[i], (output.Normals[i] + frame.Normal[i]).normalized).normalized - baseTangent;
                            }
                        }
                    }
                    shape.Frames.Add(frame);
                }
                output.Shapes.Add(shape);
            }
            var materials = new List<Material>();
            int[] MapSlots(SkinnedMeshRenderer renderer, JoinMesh mesh, int vertexOffset)
            {
                if (renderer.sharedMaterials.Length != mesh.Triangles.Count || renderer.sharedMaterials.Any(m => !m))
                    throw new InvalidOperationException("マテリアル数とサブメッシュ数が不一致、または未設定のマテリアルがあります。");
                var map = new int[mesh.Triangles.Count];
                for (int slot = 0; slot < map.Length; slot++)
                {
                    var material = renderer.sharedMaterials[slot]; int index = materials.IndexOf(material);
                    if (index < 0) { index = materials.Count; materials.Add(material); output.Triangles.Add(Array.Empty<int>()); }
                    map[slot] = index;
                    output.Triangles[index] = output.Triangles[index].Concat(mesh.Triangles[slot].Select(i => i + vertexOffset)).ToArray();
                }
                return map;
            }
            var result = new JoinResult { TargetSlots = MapSlots(target, body, 0), SourceSlots = MapSlots(source, part, offset), InitialWeights = new Dictionary<string, float>() };
            result.SeamPositions = match.Keys.Select(g => output.Positions[offset + pt.Groups[g][0]]).ToArray();
            foreach (string name in names)
            {
                int index = source.sharedMesh.GetBlendShapeIndex(name);
                result.InitialWeights[name] = index >= 0 ? source.GetBlendShapeWeight(index) : target.GetBlendShapeWeight(target.sharedMesh.GetBlendShapeIndex(name));
            }
            // Only corresponding seam vertices can actually collapse. Attribute differences retain splits.
            var remap = Enumerable.Range(0, count).ToArray();
            foreach (var pair in match)
            {
                var candidates = bt.Groups[pair.Value].Concat(pt.Groups[pair.Key].Select(i => offset + i)).ToArray();
                for (int i = 0; i < candidates.Length; i++)
                    for (int j = 0; j < i; j++)
                        if (AttributesEqual(output, candidates[i], candidates[j])) { remap[candidates[i]] = remap[candidates[j]]; result.WeldedVertices++; break; }
            }
            foreach (var triangles in output.Triangles) for (int i = 0; i < triangles.Length; i++) triangles[i] = remap[triangles[i]];
            Compact(output);
            result.Mesh = output.Write(target.sharedMesh.name + " (MofTools結合)"); result.Bones = output.Bones; result.Materials = materials.ToArray();
            result.MatchedVertices = match.Count; result.UnmatchedVertices = partGroups.Count - match.Count;
            return result;
        }

        private static T[] Expand<T>(T[] values, int count, T fallback) => values.Length == count ? values : Enumerable.Repeat(fallback, count).ToArray();
        private static bool MatrixEqual(Matrix4x4 a, Matrix4x4 b)
        { for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-6f) return false; return true; }
        private static void ValidateGroup(JoinMesh mesh, List<int> group)
        {
            int first = group[0];
            foreach (int i in group.Skip(1))
            {
                if (!mesh.Weights[first].SequenceEqual(mesh.Weights[i])) throw new InvalidOperationException("同位置の境界頂点に異なるウェイトがあります。UV分割か別面かを安全に判定できません。");
                foreach (var shape in mesh.Shapes)
                    foreach (var frame in shape.Frames)
                        if (!frame.Position[first].Equals(frame.Position[i])) throw new InvalidOperationException("同位置の境界頂点に異なるBlendShape変形があります。");
            }
        }
        private static bool AttributesEqual(JoinMesh mesh, int a, int b)
        {
            if (!mesh.Positions[a].Equals(mesh.Positions[b]) || !mesh.Normals[a].Equals(mesh.Normals[b]) || !mesh.Weights[a].SequenceEqual(mesh.Weights[b])) return false;
            if (mesh.Tangents.Length > 0 && !mesh.Tangents[a].Equals(mesh.Tangents[b])) return false;
            if (mesh.Colors.Length > 0 && !mesh.Colors[a].Equals(mesh.Colors[b])) return false;
            for (int c = 0; c < 8; c++) if (mesh.UVDimension[c] > 0 && !mesh.UV[c][a].Equals(mesh.UV[c][b])) return false;
            foreach (var shape in mesh.Shapes) foreach (var f in shape.Frames)
                if (!f.Position[a].Equals(f.Position[b]) || !f.Normal[a].Equals(f.Normal[b]) || !f.Tangent[a].Equals(f.Tangent[b])) return false;
            return true;
        }
        private static void Compact(JoinMesh mesh)
        {
            var used = mesh.Triangles.SelectMany(t => t).Distinct().OrderBy(i => i).ToArray();
            var map = new int[mesh.Count]; for (int i = 0; i < used.Length; i++) map[used[i]] = i;
            T[] Select<T>(T[] array) => array.Length == 0 ? array : used.Select(i => array[i]).ToArray();
            mesh.Positions = Select(mesh.Positions); mesh.Normals = Select(mesh.Normals); mesh.Tangents = Select(mesh.Tangents); mesh.Colors = Select(mesh.Colors); mesh.Weights = Select(mesh.Weights);
            for (int c = 0; c < 8; c++) mesh.UV[c] = used.Select(i => mesh.UV[c][i]).ToList();
            foreach (var shape in mesh.Shapes) foreach (var frame in shape.Frames) { frame.Position = Select(frame.Position); frame.Normal = Select(frame.Normal); frame.Tangent = Select(frame.Tangent); }
            foreach (var triangles in mesh.Triangles) for (int i = 0; i < triangles.Length; i++) triangles[i] = map[triangles[i]];
        }
    }
}
