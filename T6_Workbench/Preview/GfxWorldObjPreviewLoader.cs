using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace T6_Workbench
{
    /// <summary>Result of decoding a GfxWorld inspection OBJ and its optional draw/material sidecars.</summary>
    internal sealed class GfxWorldObjPreviewResult { public Model3DGroup Model { get; set; } public int Vertices { get; set; } public int Triangles { get; set; } public int TexturedSurfaces { get; set; } public int TotalSurfaces { get; set; } public string Status { get; set; } public GfxWorldObjPreviewResult(Model3DGroup Model, int Vertices, int Triangles, int TexturedSurfaces, int TotalSurfaces, string Status) { this.Model = Model; this.Vertices = Vertices; this.Triangles = Triangles; this.TexturedSurfaces = TexturedSurfaces; this.TotalSurfaces = TotalSurfaces; this.Status = Status; } }

    /// <summary>Reads OAT T6 GfxWorld inspection exports and assembles WPF meshes by material.</summary>
    internal static class GfxWorldObjPreviewLoader
    {
        const int MaxBatchVertices = 20000;
        const int MaxVertices = 2_000_000;
        const int MaxTriangles = 4_000_000;
        const int PackedVertexSize = 36;
        const int TexCoordOffset = 20;
        const int NormalOffset = 24;

        struct ObjTriangle { public int A { get; set; } public int B { get; set; } public int C { get; set; } public string Material { get; set; } public int Surface { get; set; } public ObjTriangle(int A, int B, int C, string Material, int Surface) { this.A = A; this.B = B; this.C = C; this.Material = Material; this.Surface = Surface; } }
        sealed class ObjData { public List<Point3D> Positions { get; set; } public List<ObjTriangle> Triangles { get; set; } public ObjData(List<Point3D> Positions, List<ObjTriangle> Triangles) { this.Positions = Positions; this.Triangles = Triangles; } }
        sealed class DrawData { public Vector2[] Uvs { get; set; } public Vector3[] Normals { get; set; } public int SurfaceCount { get; set; } public DrawData(Vector2[] Uvs, Vector3[] Normals, int SurfaceCount) { this.Uvs = Uvs; this.Normals = Normals; this.SurfaceCount = SurfaceCount; } }

        /// <summary>Load geometry and recover UVs, normals, and color maps when matching sidecars are available.</summary>
        public static GfxWorldObjPreviewResult Load(string objPath, string drawPath, string infoPath, IEnumerable<string> assetRoots)
        {
            ObjData obj = ReadObj(objPath);
            DrawData draw = null;
            string status = EnglishText.Text("No draw payload or surface table; showing geometry without textures.");
            if (!string.IsNullOrWhiteSpace(drawPath) && !string.IsNullOrWhiteSpace(infoPath)
                && File.Exists(drawPath) && File.Exists(infoPath))
            {
                try
                {
                    draw = ReadDrawData(drawPath, infoPath, obj.Positions.Count, obj.Triangles.Count);
                    status = EnglishText.Text("UV, normals and materials loaded from OAT sidecar.");
                }
                catch (Exception error) when ((((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.IO.InvalidDataException)) || (error is System.FormatException)) || (error is System.OverflowException)) || (error is System.ArgumentException))
                {
                    status = EnglishText.Text("Sidecar UV/materials could not be read: ") + error.Message;
                }
            }

            string[] roots = (assetRoots ?? Enumerable.Empty<string>()).Where(Directory.Exists).ToArray();
            var resolver = draw != null && roots.Length > 0 ? TextureModule.CreateMaterialResolver(roots, 512) : null;
            var textureCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            var texturedSurfaceIds = new HashSet<int>();
            var model = new Model3DGroup();
            int texturedTriangles = 0;

            var grayBrush = new SolidColorBrush(Color.FromRgb(126, 143, 158));
            grayBrush.Freeze();
            var grayMaterial = new DiffuseMaterial(grayBrush);
            grayMaterial.Freeze();
            var builders = new Dictionary<string, MeshBatchBuilder>(StringComparer.OrdinalIgnoreCase);

            foreach (ObjTriangle triangle in obj.Triangles)
            {
                string key = "__untextured";
                BitmapSource bitmap = null;
                string materialName = GetBaseMaterialName(triangle.Material);
                if (draw != null && resolver != null && materialName != null)
                {
                    if (!textureCache.TryGetValue(materialName, out bitmap))
                    {
                        bitmap = resolver(materialName);
                        textureCache.Add(materialName, bitmap);
                    }
                    if (bitmap != null)
                    {
                        key = materialName;
                        texturedTriangles++;
                        if (triangle.Surface >= 0)
                            texturedSurfaceIds.Add(triangle.Surface);
                    }
                }

                if (!builders.TryGetValue(key, out MeshBatchBuilder builder))
                {
                    Material material = bitmap == null ? grayMaterial : CreateTextureMaterial(bitmap);
                    builder = new MeshBatchBuilder(model, material, obj.Positions, draw?.Uvs, draw?.Normals);
                    builders.Add(key, builder);
                }
                builder.AddTriangle(triangle.A, triangle.B, triangle.C);
            }

            foreach (MeshBatchBuilder builder in builders.Values)
                builder.Flush();
            model.Freeze();

            int totalSurfaces = draw?.SurfaceCount ??
                obj.Triangles.Select(t => t.Surface).Where(i => i >= 0).Distinct().Count();
            string displayStatus = draw == null ? status :
                EnglishText.Format("Textured surfaces: {0:N0}/{1:N0}; ", texturedSurfaceIds.Count, totalSurfaces) +
                EnglishText.Format("triangles with colorMap: {0:N0}. Base color maps; without T6 lightmap and layered decals.", texturedTriangles);
            return new GfxWorldObjPreviewResult(model, obj.Positions.Count, obj.Triangles.Count,
                texturedSurfaceIds.Count, totalSurfaces, displayStatus);
        }

        /// <summary>Keep compatibility for geometry-only callers.</summary>
        public static GfxWorldObjPreviewResult Load(string objPath) => Load(objPath, null, null, null);

        static Material CreateTextureMaterial(BitmapSource image)
        {
            var brush = new ImageBrush(image)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 1, 1),
                ViewportUnits = BrushMappingMode.Absolute
            };
            brush.Freeze();
            var material = new DiffuseMaterial(brush);
            material.Freeze();
            return material;
        }

        /// <summary>OAT names layered materials as *hash(base:overlay); the base layer is the diffuse approximation.</summary>
        static string GetBaseMaterialName(string material)
        {
            if (string.IsNullOrWhiteSpace(material))
                return null;
            string name = material.Trim();
            int open = name.IndexOf('('), close = name.LastIndexOf(')');
            if (open >= 0 && close > open)
                name = name.Substring(open + 1, close - open - 1);
            int separator = name.IndexOf(':');
            if (separator >= 0)
                name = name.Substring(0, separator);
            name = name.Trim().TrimStart(',').Trim().Replace('\\', '/').TrimStart('/');
            return name.Length == 0 || name.StartsWith("*", StringComparison.Ordinal) ? null : name;
        }

        static ObjData ReadObj(string path)
        {
            var positions = new List<Point3D>();
            var triangles = new List<ObjTriangle>();
            string currentMaterial = null;
            int currentSurface = -1;

            foreach (string line in File.ReadLines(path))
            {
                string value = line.Trim();
                if (value.Length < 2 || value[0] == '#')
                    continue;
                if (value.StartsWith("v ", StringComparison.Ordinal))
                {
                    string[] fields = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 4)
                        throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains an invalid vertex."));
                    float x = ParseFloat(fields[1]), y = ParseFloat(fields[2]), z = ParseFloat(fields[3]);
                    if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
                        throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains a non-numeric coordinate."));
                    positions.Add(ToPoint(x, y, z));
                    if (positions.Count > MaxVertices)
                        throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ exceeds the preview limit."));
                }
                else if (value.StartsWith("g ", StringComparison.Ordinal))
                {
                    string group = value.Substring(2).Trim();
                    currentSurface = group.StartsWith("surface_", StringComparison.Ordinal)
                        && int.TryParse(group.Substring(8), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                        ? parsed : -1;
                }
                else if (value.StartsWith("usemtl ", StringComparison.Ordinal))
                {
                    currentMaterial = value.Substring(7).Trim();
                }
                else if (value.StartsWith("f ", StringComparison.Ordinal))
                {
                    string[] fields = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 4)
                        throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains a face with fewer than three vertices."));
                    int first = ParseObjIndex(fields[1], positions.Count);
                    int previous = ParseObjIndex(fields[2], positions.Count);
                    for (int i = 3; i < fields.Length; i++)
                    {
                        int current = ParseObjIndex(fields[i], positions.Count);
                        triangles.Add(new ObjTriangle(first, previous, current, currentMaterial, currentSurface));
                        previous = current;
                        if (triangles.Count > MaxTriangles)
                            throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ exceeds the preview limit."));
                    }
                }
            }

            if (positions.Count == 0 || triangles.Count == 0)
                throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains no displayable triangles."));
            return new ObjData(positions, triangles);
        }

        /// <summary>Decode packed UVs/normals from the matching OAT v2 draw payload.</summary>
        static DrawData ReadDrawData(string drawPath, string infoPath, int objVertexCount, int objTriangleCount)
        {
            byte[] bytes = File.ReadAllBytes(drawPath);
            if (bytes.Length < 40 || !bytes.AsSpan(0, 8).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("OAT6GWD2")))
                throw new InvalidDataException(EnglishText.Text("Invalid GfxWorld draw payload header."));
            uint version = ReadUInt32(bytes, 8);
            uint surfaceCountRaw = ReadUInt32(bytes, 20);
            uint vertexCountRaw = ReadUInt32(bytes, 24);
            uint stream0SizeRaw = ReadUInt32(bytes, 28);
            uint stream1Size = ReadUInt32(bytes, 32);
            uint indexCount = ReadUInt32(bytes, 36);
            long expectedLength = 40L + stream0SizeRaw + stream1Size + 2L * indexCount;
            if (version != 2 || expectedLength != bytes.Length || vertexCountRaw != (uint)objVertexCount
                || surfaceCountRaw > (uint)MaxTriangles || indexCount % 3 != 0 || indexCount / 3 != (uint)objTriangleCount)
                throw new InvalidDataException(EnglishText.Text("Draw payload dimensions do not match the OBJ."));
            if (stream0SizeRaw > int.MaxValue)
                throw new InvalidDataException(EnglishText.Text("GfxWorld vertex stream is too large."));

            int[] surfaceOffsets = ReadSurfaceOffsets(infoPath);
            int surfaceCount = checked((int)surfaceCountRaw);
            int vertexCount = checked((int)vertexCountRaw);
            int stream0Size = checked((int)stream0SizeRaw);
            if (surfaceOffsets.Length != surfaceCount || surfaceOffsets.Length == 0)
                throw new InvalidDataException(EnglishText.Text("Surface table does not match draw payload."));

            int[] groups = surfaceOffsets.Distinct().OrderBy(offset => offset).ToArray();
            var uvs = new Vector2[vertexCount];
            var normals = new Vector3[vertexCount];
            int outputVertex = 0;
            for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                int offset = groups[groupIndex];
                int end = groupIndex + 1 < groups.Length ? groups[groupIndex + 1] : stream0Size;
                if (offset < 0 || (offset & 15) != 0 || end <= offset || end > stream0Size)
                    throw new InvalidDataException(EnglishText.Text("GfxWorld vertex-data offset is invalid."));
                int span = end - offset;
                int count = span / PackedVertexSize;
                int padding = span % PackedVertexSize;
                if (count == 0 || padding >= 16 || outputVertex + count > vertexCount)
                    throw new InvalidDataException(EnglishText.Text("GfxWorld vertex group is invalid."));

                for (int i = 0; i < count; i++)
                {
                    int source = checked(40 + offset + i * PackedVertexSize);
                    int target = outputVertex + i;
                    uint packedUv = ReadUInt32(bytes, source + TexCoordOffset);
                    uvs[target] = new Vector2(
                        HalfToSingle((ushort)(packedUv & 0xffff)),
                        HalfToSingle((ushort)(packedUv >> 16)));
                    Vector3 normal = DecodePackedUnitVector(ReadUInt32(bytes, source + NormalOffset));
                    normals[target] = new Vector3(normal.X, normal.Z, -normal.Y);
                }
                outputVertex += count;
            }

            if (outputVertex != vertexCount)
                throw new InvalidDataException("UV vertex groups do not cover the OBJ vertex list.");
            return new DrawData(uvs, normals, surfaceCount);
        }

        /// <summary>Read vertexDataOffset0 values from OAT's quoted per-surface CSV table.</summary>
        static int[] ReadSurfaceOffsets(string path)
        {
            bool foundHeader = false;
            var offsets = new List<int>();
            foreach (string line in File.ReadLines(path))
            {
                if (!foundHeader)
                {
                    if (line.StartsWith("surfaceIndex,material,", StringComparison.Ordinal))
                        foundHeader = true;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string[] fields = ParseCsvRow(line);
                if (fields.Length < 7 || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset))
                    throw new InvalidDataException(EnglishText.Text("Invalid GfxWorld surface table row."));
                offsets.Add(offset);
            }
            if (!foundHeader || offsets.Count == 0)
                throw new InvalidDataException(EnglishText.Text("The info sidecar has no surface table."));
            return offsets.ToArray();
        }

        /// <summary>Minimal quoted CSV parser for material names that may contain commas.</summary>
        static string[] ParseCsvRow(string row)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < row.Length; i++)
            {
                char ch = row[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < row.Length && row[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                        quoted = !quoted;
                }
                else if (ch == ',' && !quoted)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                    field.Append(ch);
            }
            fields.Add(field.ToString());
            return fields.ToArray();
        }

        static int ParseObjIndex(string token, int positionCount)
        {
            string value = token.Split('/')[0];
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) || raw == 0)
                throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains an invalid index."));
            int index = raw > 0 ? raw - 1 : positionCount + raw;
            if ((uint)index >= (uint)positionCount)
                throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ index exceeds vertex bounds."));
            return index;
        }

        static Point3D ToPoint(float x, float y, float z) => new System.Windows.Media.Media3D.Point3D(x, z, -y);

        static Vector3 DecodePackedUnitVector(uint packed)
        {
            float Component(uint component)
            {
                uint bits = component & 0x3ff;
                bits = unchecked(bits - 2u * (bits & 0x200u) + 0x40400000u);
                float value = Helpers.Int32BitsToSingle(unchecked((int)bits));
                return (value - 3f) * 8208.0312f;
            }

            var result = new Vector3(Component(packed), Component(packed >> 10), Component(packed >> 20));
            return result.LengthSquared() > 1e-10f && IsFinite(result.X) && IsFinite(result.Y) && IsFinite(result.Z)
                ? Vector3.Normalize(result) : Vector3.Zero;
        }

        static float HalfToSingle(ushort half)
        {
            int sign = (half & 0x8000) == 0 ? 1 : -1;
            int exponent = (half >> 10) & 0x1f;
            int mantissa = half & 0x3ff;
            if (exponent == 0)
                return sign * mantissa * (1f / 16777216f);
            if (exponent == 31)
                return mantissa == 0 ? sign * float.PositiveInfinity : float.NaN;
            return sign * (1f + mantissa / 1024f) * (float)Math.Pow(2, exponent - 15);
        }

        static uint ReadUInt32(byte[] data, int offset)
        {
            if (offset < 0 || offset > data.Length - 4)
                throw new InvalidDataException(EnglishText.Text("GfxWorld draw payload is truncated."));
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static float ParseFloat(string value)
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
                throw new InvalidDataException(EnglishText.Text("GfxWorld OBJ contains an invalid coordinate."));
            return result;
        }

        sealed class MeshBatchBuilder
        {
            readonly Model3DGroup output;
            readonly Material material;
            readonly List<Point3D> sourcePositions;
            readonly Vector2[] sourceUvs;
            readonly Vector3[] sourceNormals;
            readonly List<Point3D> positions = new List<System.Windows.Media.Media3D.Point3D>(MaxBatchVertices);
            readonly List<Point> texCoords = new List<System.Windows.Point>(MaxBatchVertices);
            readonly List<Vector3> normals = new List<System.Numerics.Vector3>(MaxBatchVertices);
            readonly List<int> indices = new List<int>(MaxBatchVertices * 3);
            readonly Dictionary<int, int> remap = new Dictionary<int, int>(MaxBatchVertices);

            public MeshBatchBuilder(Model3DGroup output, Material material, List<Point3D> sourcePositions,
                Vector2[] sourceUvs, Vector3[] sourceNormals)
            {
                this.output = output;
                this.material = material;
                this.sourcePositions = sourcePositions;
                this.sourceUvs = sourceUvs;
                this.sourceNormals = sourceNormals;
            }

            public void AddTriangle(int a, int b, int c)
            {
                int newVertices = (remap.ContainsKey(a) ? 0 : 1) + (remap.ContainsKey(b) ? 0 : 1) + (remap.ContainsKey(c) ? 0 : 1);
                if (positions.Count > 0 && positions.Count + newVertices > MaxBatchVertices)
                    Flush();
                indices.Add(Map(a));
                indices.Add(Map(b));
                indices.Add(Map(c));
            }

            int Map(int index)
            {
                if (remap.TryGetValue(index, out int local))
                    return local;
                local = positions.Count;
                remap.Add(index, local);
                positions.Add(sourcePositions[index]);
                Vector2 uv = sourceUvs != null ? sourceUvs[index] : Vector2.Zero;
                texCoords.Add(IsFinite(uv.X) && IsFinite(uv.Y) ? new Point(uv.X, uv.Y) : new Point());
                normals.Add(sourceNormals != null ? sourceNormals[index] : Vector3.Zero);
                return local;
            }

            public void Flush()
            {
                if (indices.Count == 0)
                    return;
                var mesh = new MeshGeometry3D();
                foreach (Point3D position in positions)
                    mesh.Positions.Add(position);
                foreach (Point uv in texCoords)
                    mesh.TextureCoordinates.Add(uv);

                var generatedNormals = new Vector3[positions.Count];
                for (int i = 0; i < indices.Count; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    var pa = new Vector3((float)positions[a].X, (float)positions[a].Y, (float)positions[a].Z);
                    var pb = new Vector3((float)positions[b].X, (float)positions[b].Y, (float)positions[b].Z);
                    var pc = new Vector3((float)positions[c].X, (float)positions[c].Y, (float)positions[c].Z);
                    Vector3 face = Vector3.Cross(pb - pa, pc - pa);
                    if (face.LengthSquared() > 1e-12f)
                    {
                        if (normals[a].LengthSquared() <= 1e-10f)
                            generatedNormals[a] += face;
                        if (normals[b].LengthSquared() <= 1e-10f)
                            generatedNormals[b] += face;
                        if (normals[c].LengthSquared() <= 1e-10f)
                            generatedNormals[c] += face;
                    }
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(c);
                }
                for (int i = 0; i < normals.Count; i++)
                {
                    Vector3 normal = normals[i].LengthSquared() > 1e-10f ? normals[i] : generatedNormals[i];
                    normal = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
                    mesh.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
                }

                mesh.Freeze();
                output.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
                positions.Clear();
                texCoords.Clear();
                normals.Clear();
                indices.Clear();
                remap.Clear();
            }
        }
    }
}
