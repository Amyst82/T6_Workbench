using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Windows.Media.Media3D;
using Quaternion = System.Numerics.Quaternion;

namespace T6_Workbench
{
    // Decodes an animated GLB once; each frame skins vertices on the CPU for the WPF viewport.
    internal sealed class AnimatedGlbPreview
    {
        sealed class Node 
        { 
            public Matrix4x4? Matrix { get; set; } 
            public Vector3 Translation { get; set; } 
            public Quaternion Rotation { get; set; } 
            public Vector3 Scale { get; set; } 
            public int Parent { get; set; } 
            public Node(Matrix4x4? Matrix, Vector3 Translation, Quaternion Rotation, Vector3 Scale, int Parent) 
            { 
                this.Matrix = Matrix; 
                this.Translation = Translation; 
                this.Rotation = Rotation; 
                this.Scale = Scale; 
                this.Parent = Parent; 
            } 
        }
        sealed class Skin 
        { 
            public int[] Joints { get; set; } 
            public Matrix4x4[] InverseBind { get; set; } 
            public Skin(int[] Joints, Matrix4x4[] InverseBind) 
            { 
                this.Joints = Joints; 
                this.InverseBind = InverseBind; 
            } 
        }
        sealed class Part 
        { 
            public int Node { get; set; } 
            public int Skin { get; set; } 
            public Vector3[] Positions { get; set; } 
            public int[][] Joints { get; set; } 
            public float[][] Weights { get; set; }
            public Part(int Node, int Skin, Vector3[] Positions, int[][] Joints, float[][] Weights) 
            { 
                this.Node = Node; 
                this.Skin = Skin; 
                this.Positions = Positions; 
                this.Joints = Joints; 
                this.Weights = Weights; 
            } 
        }
        sealed class Track 
        { 
            public int Node { get; set; } 
            public string Path { get; set; } 
            public float[][] Times { get; set; } 
            public float[][] Values { get; set; } 
            public string Interpolation { get; set; } 
            public Track(int Node, string Path, float[][] Times, float[][] Values, string Interpolation) 
            { 
                this.Node = Node; 
                this.Path = Path; 
                this.Times = Times; 
                this.Values = Values; 
                this.Interpolation = Interpolation; 
            } 
        }
        readonly Node[] nodes;
        readonly Skin[] skins;
        readonly Part[] parts;
        readonly Track[] tracks;
        public double Duration { get; }
        public int PartCount => parts.Length;

        AnimatedGlbPreview(Node[] nodes, Skin[] skins, Part[] parts, Track[] tracks, double duration)
        { 
            this.nodes = nodes; 
            this.skins = skins; 
            this.parts = parts; 
            this.tracks = tracks; 
            Duration = duration; 
        }

        public static AnimatedGlbPreview Load(string path, CancellationToken token)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != 0x46546c67) 
                throw new InvalidDataException(EnglishText.Text("Invalid GLB animation."));
            int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12, 4));
            using (var doc = JsonDocument.Parse(data.AsMemory(20, length)))
            {
                int binStart = 20 + length + 8;
                var root = doc.RootElement;
                var views = root.GetProperty("bufferViews").EnumerateArray().ToArray();
                var accessors = root.GetProperty("accessors").EnumerateArray().ToArray();
                var ns = root.GetProperty("nodes").EnumerateArray().ToArray();
                int[] parents = Enumerable.Repeat(-1, ns.Length).ToArray();
                for (int i = 0; i < ns.Length; i++)
                {
                    if (ns[i].TryGetProperty("children", out var children))
                    {
                        foreach (var c in children.EnumerateArray())
                        {
                            int n = c.GetInt32();
                            if ((uint)n >= ns.Length || parents[n] >= 0)
                                throw new InvalidDataException(EnglishText.Text("Invalid GLB hierarchy.")); parents[n] = i;
                        }
                    }
                }
                float[] Values(JsonElement obj, string key, float[] fallback) => obj.TryGetProperty(key, out var a) ? a.EnumerateArray().Select(x => x.GetSingle()).ToArray() : fallback;
                var nodes = ns.Select((n, i) => {
                    var t = Values(n, "translation", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new float[] { 0 }, new float[] { 0 })), new float[] { 0 }))); var r = Values(n, "rotation", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new float[] { 0 }, new float[] { 0 })), new float[] { 0 })), new float[] { 1 }))); var s = Values(n, "scale", System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new float[] { 1 }, new float[] { 1 })), new float[] { 1 })));
                    Matrix4x4? m = null; if (n.TryGetProperty("matrix", out var jm)) { var a = jm.EnumerateArray().Select(x => x.GetSingle()).ToArray(); m = Matrix(a); }
                    return new Node(m, new Vector3(t[0], t[1], t[2]), Quaternion.Normalize(new Quaternion(r[0], r[1], r[2], r[3])), new Vector3(s[0], s[1], s[2]), parents[i]);
                }).ToArray();
                float[][] Access(int index, int width)
                {
                    var a = accessors[index]; if (!a.TryGetProperty("bufferView", out var vi)) throw new InvalidDataException(EnglishText.Text("Sparse GLB accessor is not supported in preview."));
                    var v = views[vi.GetInt32()]; int type = a.GetProperty("componentType").GetInt32(); int bytes = type == 5126 || type == 5125 ? 4 : type == 5123 ? 2 : type == 5121 ? 1 : throw new InvalidDataException(EnglishText.Text("Unknown GLB accessor type."));
                    int count = a.GetProperty("count").GetInt32(); int stride = v.TryGetProperty("byteStride", out var st) ? st.GetInt32() : bytes * width;
                    int start = binStart + (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
                    if (start < binStart || count < 0 || stride < bytes * width || (long)start + (long)Math.Max(0, count - 1) * stride + bytes * width > data.Length) throw new InvalidDataException(EnglishText.Text("GLB accessor extends beyond the file bounds."));
                    bool norm = a.TryGetProperty("normalized", out var no) && no.GetBoolean(); var result = new float[count][];
                    for (int i = 0; i < count; i++) 
                    { 
                        var row = new float[width]; 
                        for (int j = 0; j < width; j++) 
                        { 
                            int k = start + i * stride + j * bytes; 
                            row[j] = (type == 5126 ? BitConverter.ToSingle(data, k) : (type == 5125 ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(k, 4)) : (type == 5123 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(k, 2)) : data[k])));
                            if (norm && type == 5121)
                            {
                                row[j] /= 255;
                            }
                            else if (norm && type == 5123)
                            {
                                row[j] /= 65535;
                            } 
                        } 
                        result[i] = row; 
                    }
                    return result;
                }
                var skins = root.GetProperty("skins").EnumerateArray().Select(s => {
                    int[] joints = s.GetProperty("joints").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    var inv = s.TryGetProperty("inverseBindMatrices", out var im) ? Access(im.GetInt32(), 16).Select(Matrix).ToArray() : Enumerable.Repeat(Matrix4x4.Identity, joints.Length).ToArray();
                    if (joints.Length != inv.Length || joints.Any(j => (uint)j >= nodes.Length)) 
                        throw new InvalidDataException(EnglishText.Text("Invalid GLB skin.")); 
                    return new Skin(joints, inv);
                }).ToArray();
                var meshes = root.GetProperty("meshes").EnumerateArray().ToArray(); var parts = new List<Part>();
                int scene = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
                void Visit(int index)
                {
                    token.ThrowIfCancellationRequested(); var n = ns[index];
                    if (n.TryGetProperty("mesh", out var mesh)) 
                        foreach (var prim in meshes[mesh.GetInt32()].GetProperty("primitives").EnumerateArray())
                        {
                            if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) 
                                continue;
                            var attrs = prim.GetProperty("attributes"); if (!attrs.TryGetProperty("POSITION", out var pos)) 
                                continue;
                            var positions = Access(pos.GetInt32(), 3).Select(a => new Vector3(a[0], a[1], a[2])).ToArray(); 
                            if (positions.Length < 3) 
                                continue;
                            int skin = n.TryGetProperty("skin", out var sk) ? sk.GetInt32() : -1; int[][] joints = null; float[][] weights = null;
                            if (skin >= 0) { if ((uint)skin >= skins.Length || !attrs.TryGetProperty("JOINTS_0", out var ja) || !attrs.TryGetProperty("WEIGHTS_0", out var wa)) throw new InvalidDataException(EnglishText.Text("GLB skin does not contain JOINTS_0/WEIGHTS_0.")); joints = Access(ja.GetInt32(), 4).Select(a => a.Select(x => (int)x).ToArray()).ToArray(); weights = Access(wa.GetInt32(), 4); if (joints.Length != positions.Length || weights.Length != positions.Length) throw new InvalidDataException(EnglishText.Text("GLB weights do not match vertices.")); }
                            parts.Add(new Part(index, skin, positions, joints, weights));
                        }
                    if (n.TryGetProperty("children", out var ch)) 
                        foreach (var c in ch.EnumerateArray())
                        {
                            Visit(c.GetInt32());
                        }
                }
                foreach (var n in root.GetProperty("scenes")[scene].GetProperty("nodes").EnumerateArray())
                {
                    Visit(n.GetInt32());
                }
                var anim = root.GetProperty("animations")[0]; var samplers = anim.GetProperty("samplers"); var tracks = new List<Track>();
                foreach (var c in anim.GetProperty("channels").EnumerateArray())
                {
                    var target = c.GetProperty("target"); string pathName = target.GetProperty("path").GetString(); if ((!(((pathName == "rotation") || (pathName == "translation")) || (pathName == "scale")))) continue;
                    var sampler = samplers[c.GetProperty("sampler").GetInt32()]; string interpolation = sampler.TryGetProperty("interpolation", out var ip) ? ip.GetString() : "LINEAR"; if ((!((interpolation == "LINEAR") || (interpolation == "STEP")))) throw new InvalidDataException(EnglishText.Text("Animation interpolation is not supported in preview."));
                    var times = Access(sampler.GetProperty("input").GetInt32(), 1); var vals = Access(sampler.GetProperty("output").GetInt32(), pathName == "rotation" ? 4 : 3); if (times.Length != vals.Length || times.Length == 0) throw new InvalidDataException(EnglishText.Text("Invalid animation keys."));
                    tracks.Add(new Track(target.GetProperty("node").GetInt32(), pathName, times, vals, interpolation));
                }
                return new AnimatedGlbPreview(nodes, skins, parts.ToArray(), tracks.ToArray(), tracks.Count == 0 ? 0 : tracks.Max(t => t.Times[t.Times.Length - (1)][0]));
            }
        }
        static Matrix4x4 Matrix(float[] a) 
        { 
            if (a.Length != 16) 
                throw new InvalidDataException(EnglishText.Text("Invalid GLB matrix.")); 
            return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]); 
        }
        public Point3DCollection[] Sample(double seconds)
        {
            var t = nodes.Select(n => n.Translation).ToArray(); var r = nodes.Select(n => n.Rotation).ToArray(); var s = nodes.Select(n => n.Scale).ToArray();
            foreach (var track in tracks)
            {
                int upper = Array.FindIndex(track.Times, x => x[0] >= seconds); 
                if (upper < 0)
                {
                    upper = track.Times.Length - 1;
                }
                int lo = Math.Max(0, upper - 1); 
                float span = track.Times[upper][0] - track.Times[lo][0]; 
                float f = upper == 0 || span <= 0 ? 0 : (float)((seconds - track.Times[lo][0]) / span); 
                f = Math.Max(0, Math.Min(1, f)); 
                if (track.Interpolation == "STEP" && f < 1)
                {
                    f = 0;
                }
                float[] a = track.Values[lo], b = track.Values[upper];
                if (track.Path == "rotation") 
                { 
                    var q = Quaternion.Slerp(new Quaternion(a[0], a[1], a[2], a[3]), new Quaternion(b[0], b[1], b[2], b[3]), f); 
                    r[track.Node] = Quaternion.Normalize(q); 
                }
                else 
                { 
                    var v = Vector3.Lerp(new Vector3(a[0], a[1], a[2]), new Vector3(b[0], b[1], b[2]), f); 
                    if (track.Path == "translation")
                    {
                        t[track.Node] = v;
                    }
                    else
                    {
                        s[track.Node] = v;
                    }
                }
            }
            var world = new Matrix4x4[nodes.Length]; var done = new bool[nodes.Length];
            Matrix4x4 World(int i) 
            { 
                if (done[i])
                {
                    return world[i];
                }
                var n = nodes[i]; 
                var local = n.Matrix ?? (Matrix4x4.CreateScale(s[i]) * Matrix4x4.CreateFromQuaternion(r[i]) * Matrix4x4.CreateTranslation(t[i])); 
                world[i] = n.Parent < 0 ? local : local * World(n.Parent); 
                done[i] = true; 
                return world[i]; 
            }
            for (int i = 0; i < nodes.Length; i++)
            {
                World(i);
            }
            var matrices = skins.Select(skin => skin.Joints.Select((joint, i) => skin.InverseBind[i] * world[joint]).ToArray()).ToArray();
            return parts.Select(part => { var output = new Point3DCollection(part.Positions.Length); for (int i = 0; i < part.Positions.Length; i++) { Vector3 p; if (part.Skin < 0) p = Vector3.Transform(part.Positions[i], world[part.Node]); else { p = Vector3.Zero; float sum = 0; for (int k = 0; k < 4; k++) { float w = part.Weights[i][k]; if (w <= 0) continue; int j = part.Joints[i][k]; if ((uint)j >= matrices[part.Skin].Length) throw new InvalidDataException(EnglishText.Text("Invalid GLB bone index.")); p += Vector3.Transform(part.Positions[i], matrices[part.Skin][j]) * w; sum += w; } if (sum == 0) p = Vector3.Transform(part.Positions[i], world[part.Node]); } output.Add(new Point3D(p.X, p.Y, p.Z)); } return output; }).ToArray();
        }
    }
}
