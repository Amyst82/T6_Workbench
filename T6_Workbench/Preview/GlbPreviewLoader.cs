using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using NQuaternion = System.Numerics.Quaternion;

namespace T6_Workbench
{
    /// <summary>
    /// GlbPreviewResult. Read GLB geometry and basic material color into a WPF model preview.
    /// </summary>
    public sealed class GlbPreviewMaterialVariant
    {
        public MeshGeometry3D Geometry { get; set; }

        public Material Solid { get; set; }

        public Material Shaded { get; set; }

        public GlbPreviewMaterialVariant(MeshGeometry3D Geometry, Material Solid, Material Shaded)
        {
            this.Geometry = Geometry;
            this.Solid = Solid;
            this.Shaded = Shaded;
        }
    }
    /// <summary>Decoded geometry plus two switchable presentation materials per GLB primitive.</summary>
    public sealed class GlbPreviewResult
    {
        public Model3DGroup Model { get; set; }

        public int Vertices { get; set; }

        public int Triangles { get; set; }

        public int Parts { get; set; }

        public IReadOnlyList<GlbPreviewMaterialVariant> MaterialVariants { get; set; }

        public int TexturedParts { get; set; }

        public int MissingUvParts { get; set; }

        public int OatVertices { get; set; }

        public int OatSurfaces { get; set; }

        public int LargestOatSurfaceVertices { get; set; }

        public bool OatLodVertexLimitExceeded { get; set; }

        public bool OatSurfaceLimitExceeded { get; set; }

        public GlbPreviewResult(Model3DGroup Model, int Vertices, int Triangles, int Parts, IReadOnlyList<GlbPreviewMaterialVariant> MaterialVariants, int TexturedParts, int MissingUvParts, int OatVertices, int OatSurfaces, int LargestOatSurfaceVertices, bool OatLodVertexLimitExceeded, bool OatSurfaceLimitExceeded)
        {
            this.Model = Model;
            this.Vertices = Vertices;
            this.Triangles = Triangles;
            this.Parts = Parts;
            this.MaterialVariants = MaterialVariants;
            this.TexturedParts = TexturedParts;
            this.MissingUvParts = MissingUvParts;
            this.OatVertices = OatVertices;
            this.OatSurfaces = OatSurfaces;
            this.LargestOatSurfaceVertices = LargestOatSurfaceVertices;
            this.OatLodVertexLimitExceeded = OatLodVertexLimitExceeded;
            this.OatSurfaceLimitExceeded = OatSurfaceLimitExceeded;
        }
    }

    /// <summary>Small, dependency-free GLB 2.0 preview reader for OAT's static XModel exports.</summary>
    internal static class GlbPreviewLoader
    {
        /// <summary>
        /// Decode and validate the supported preview format into WPF data.
        /// </summary>
        public static GlbPreviewResult Load(string path, Func<string, BitmapSource> resolveT6Material = null, CancellationToken cancellation = default, string imageBasePath = null)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(path);
            if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != 0x46546c67)
                throw new InvalidDataException(EnglishText.Text("File is not a GLB 2.0."));
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != 2)
                throw new InvalidDataException(EnglishText.Text("Preview supports GLB version 2."));
            int offset = 12;
            int jsonLength = ReadChunkLength(data, ref offset, 0x4e4f534a);
            using (var json = JsonDocument.Parse(data.AsMemory(offset, jsonLength)))
            {
                offset += jsonLength;
                byte[] binary = null;
                if (offset + 8 <= data.Length)
                {
                    int binLength = ReadChunkLength(data, ref offset, 0x004e4942);
                    binary = data.AsSpan(offset, binLength).ToArray();
                }
                if (binary == null)
                    throw new InvalidDataException(EnglishText.Text("GLB has no BIN buffer for geometry."));
                var root = json.RootElement;
                var accessors = root.GetProperty("accessors").EnumerateArray().ToArray();
                var views = root.GetProperty("bufferViews").EnumerateArray().ToArray();
                var meshes = root.GetProperty("meshes").EnumerateArray().ToArray();
                var materials = root.TryGetProperty("materials", out var materialElement) ? materialElement.EnumerateArray().ToArray() : new System.Text.Json.JsonElement[0];
                var textures = root.TryGetProperty("textures", out var textureElement) ? textureElement.EnumerateArray().ToArray() : new System.Text.Json.JsonElement[0];
                var images = LoadImages(root, views, binary, imageBasePath ?? path, cancellation);
                var output = new Model3DGroup();
                int vertexTotal = 0, triangleTotal = 0, partTotal = 0;
                int texturedPartTotal = 0, missingUvTotal = 0;
                int oatVertexTotal = 0, oatSurfaceTotal = 0, largestOatSurfaceVertexCount = 0;
                var variants = new List<GlbPreviewMaterialVariant>();
                Func<Vector3, Vector3> identity = point => point;
                if (root.TryGetProperty("nodes", out var nodeElement))
                {
                    var nodes = nodeElement.EnumerateArray().ToArray();
                    if (root.TryGetProperty("scenes", out var sceneElement) && sceneElement.GetArrayLength() > 0)
                    {
                        int sceneIndex = root.TryGetProperty("scene", out var defaultScene) ? defaultScene.GetInt32() : 0;
                        if ((uint)sceneIndex >= (uint)sceneElement.GetArrayLength())
                        {
                            sceneIndex = 0;
                        }
                        var scene = sceneElement[sceneIndex];
                        if (scene.TryGetProperty("nodes", out var roots))
                        {
                            foreach (var node in roots.EnumerateArray())
                            {
                                VisitNode(node.GetInt32(), identity);
                            }
                        }
                    }
                    else
                    {
                        var childNodes = new HashSet<int>();
                        foreach (var node in nodes)
                        {
                            if (node.TryGetProperty("children", out var children))
                            {
                                foreach (var child in children.EnumerateArray())
                                {
                                    childNodes.Add(child.GetInt32());
                                }
                            }
                        }
                        for (int i = 0; i < nodes.Length; i++)
                        {
                            if (!childNodes.Contains(i))
                            {
                                VisitNode(i, identity);
                            }
                        }
                    }
                    void VisitNode(int nodeIndex, Func<Vector3, Vector3> parent)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if ((uint)nodeIndex >= (uint)nodes.Length)
                            throw new InvalidDataException(EnglishText.Text("GLB contains an invalid node reference."));
                        var node = nodes[nodeIndex];
                        var local = CreateNodeTransform(node);
                        Vector3 Transform(Vector3 point) => parent(local(point));
                        if (node.TryGetProperty("mesh", out var meshIndex))
                        {
                            AddMesh(meshIndex.GetInt32(), Transform);
                        }
                        if (node.TryGetProperty("children", out var children))
                        {
                            foreach (var child in children.EnumerateArray())
                            {
                                VisitNode(child.GetInt32(), Transform);
                            }
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        AddMesh(i, identity);
                    }
                }
                if (partTotal == 0)
                    throw new InvalidDataException(EnglishText.Text("GLB contains no displayable triangle meshes."));
                // Freeze all worker-created Freezables. The result can now cross to WPF's UI
                // dispatcher safely, while the window creates its mutable display wrappers there.
                output.Freeze();
                return new GlbPreviewResult(output, vertexTotal, triangleTotal, partTotal, variants, texturedPartTotal, missingUvTotal,
                    oatVertexTotal, oatSurfaceTotal, largestOatSurfaceVertexCount,
                    oatVertexTotal > ushort.MaxValue + 1, oatSurfaceTotal > byte.MaxValue);
                void AddMesh(int meshIndex, Func<Vector3, Vector3> transform)
                {
                    if ((uint)meshIndex >= (uint)meshes.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid mesh reference."));
                    foreach (var primitive in meshes[meshIndex].GetProperty("primitives").EnumerateArray())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                        {
                            continue;
                        }
                        if (!primitive.TryGetProperty("attributes", out var attributes)
                            || !attributes.TryGetProperty("POSITION", out var positionAccessor))
                        {
                            continue;
                        }
                        var points = ReadVector3(positionAccessor.GetInt32());
                        if (points.Length < 3)
                        {
                            continue;
                        }
                        var indices = primitive.TryGetProperty("indices", out var indexAccessor) ? ReadIndices(indexAccessor.GetInt32()) : Enumerable.Range(0, points.Length).ToArray();
                        if (indices.Length < 3 || indices.Length % 3 != 0)
                            throw new InvalidDataException(EnglishText.Text("GLB mesh contains an incomplete triangle list."));
                        var weightCounts = ReadWeightCounts(attributes, points.Length);
                        int maximumWeightCount = 0;
                        foreach (int vertexIndex in indices)
                        {
                            if ((uint)vertexIndex >= (uint)points.Length)
                                throw new InvalidDataException(EnglishText.Text("GLB contains an index outside the mesh."));
                            maximumWeightCount = Math.Max(maximumWeightCount, weightCounts[vertexIndex]);
                        }
                        int maxVerticesPerSurface = maximumWeightCount > 1 ? (int)short.MaxValue : (int)ushort.MaxValue;
                        AddOatSurfaceStats(indices, maxVerticesPerSurface);
                        var geometry = new MeshGeometry3D();
                        var transformed = new Vector3[points.Length];
                        for (int i = 0; i < points.Length; i++)
                        {
                            if ((i & 2047) == 0)
                            {
                                cancellation.ThrowIfCancellationRequested();
                            }
                            transformed[i] = transform(points[i]);
                            if (!Helpers.IsFinite(transformed[i].X) || !Helpers.IsFinite(transformed[i].Y) || !Helpers.IsFinite(transformed[i].Z))
                                throw new InvalidDataException(EnglishText.Text("GLB contains an invalid vertex position."));
                            geometry.Positions.Add(new Point3D(transformed[i].X, transformed[i].Y, transformed[i].Z));
                        }
                        var materialColor = GetMaterialColor(primitive, materials);
                        var uv = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor) ? ReadVector2(uvAccessor.GetInt32()) : null;
                        bool hasUv = uv != null && uv.Length == points.Length;
                        if (hasUv)
                        {
                            foreach (var coordinate in uv)
                            {
                                geometry.TextureCoordinates.Add(new Point(coordinate.X, coordinate.Y));
                            }
                        }
                        else
                        {
                            for (int i = 0; i < points.Length; i++)
                            {
                                geometry.TextureCoordinates.Add(new Point(0, 0));
                            }
                        }
                        var normals = new Vector3[points.Length];
                        for (int i = 0; i < indices.Length; i += 3)
                        {
                            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                            if ((uint)a >= (uint)points.Length || (uint)b >= (uint)points.Length || (uint)c >= (uint)points.Length)
                                throw new InvalidDataException(EnglishText.Text("GLB contains an index outside the mesh."));
                            geometry.TriangleIndices.Add(a);
                            geometry.TriangleIndices.Add(b);
                            geometry.TriangleIndices.Add(c);
                            var normal = Vector3.Cross(transformed[b] - transformed[a], transformed[c] - transformed[a]);
                            if (normal.LengthSquared() > 1e-12f)
                            {
                                normals[a] += normal;
                                normals[b] += normal;
                                normals[c] += normal;
                            }
                        }
                        foreach (var normal in normals)
                        {
                            var n = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.UnitY;
                            geometry.Normals.Add(new Vector3D(n.X, n.Y, n.Z));
                        }
                        var solidBrush = new SolidColorBrush(Color.FromRgb(165, 172, 183));
                        solidBrush.Freeze();
                        // DiffuseMaterial keeps the solid-grey mode neutral while preserving visible shape and lighting.
                        var solidMaterial = new DiffuseMaterial(solidBrush);
                        solidMaterial.Freeze();
                        BitmapSource baseColor = GetBaseColorImage(primitive, materials, textures, images);
                        if (baseColor == null && hasUv && resolveT6Material != null &&
                            GetMaterialName(primitive, materials) is string materialName)
                        {
                            baseColor = resolveT6Material(materialName);
                        }
                        Material shadedMaterial = CreateShadedMaterial(primitive, materials, baseColor, hasUv, materialColor);
                        if (baseColor != null && hasUv)
                        {
                            texturedPartTotal++;
                        }
                        else if (baseColor != null)
                        {
                            missingUvTotal++;
                        }
                        geometry.Freeze();
                        var model = new GeometryModel3D(geometry, shadedMaterial) { BackMaterial = shadedMaterial };
                        model.Freeze();
                        output.Children.Add(model);
                        variants.Add(new GlbPreviewMaterialVariant(geometry, solidMaterial, shadedMaterial));
                        vertexTotal += points.Length;
                        triangleTotal += indices.Length / 3;
                        partTotal++;
                    }
                }
                Vector3[] ReadVector3(int accessorIndex)
                {
                    var accessor = GetAccessor(accessorIndex, "VEC3", 5126);
                    var view = views[accessor.GetProperty("bufferView").GetInt32()];
                    int start = view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0;
                    start += accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0;
                    int stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : 12;
                    int count = accessor.GetProperty("count").GetInt32();
                    EnsureRange(start, count, stride, 12);
                    var result = new Vector3[count];
                    for (int i = 0; i < count; i++)
                    {
                        int at = start + i * stride;
                        result[i] = new Vector3(ReadFloat(at), ReadFloat(at + 4), ReadFloat(at + 8));
                    }
                    return result;
                }
                Vector2[] ReadVector2(int accessorIndex)
                {
                    if ((uint)accessorIndex >= (uint)accessors.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid UV accessor."));
                    var accessor = GetAccessor(accessorIndex, "VEC2", 5126);
                    var view = views[accessor.GetProperty("bufferView").GetInt32()];
                    int start = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
                        (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
                    int stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : 8;
                    int count = accessor.GetProperty("count").GetInt32();
                    EnsureRange(start, count, stride, 8);
                    var result = new Vector2[count];
                    for (int i = 0; i < count; i++)
                    {
                        int at = start + i * stride;
                        result[i] = new Vector2(ReadFloat(at), ReadFloat(at + 4));
                    }
                    return result;
                }
                int[] ReadIndices(int accessorIndex)
                {
                    if ((uint)accessorIndex >= (uint)accessors.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid index accessor."));
                    var accessor = accessors[accessorIndex];
                    if (accessor.GetProperty("type").GetString() != "SCALAR")
                        throw new InvalidDataException(EnglishText.Text("GLB indices are not scalar."));
                    int componentType = accessor.GetProperty("componentType").GetInt32();
                    int elementSize;
                    switch (componentType)
                    {
                        case 5121:
                            elementSize = 1;
                            break;
                        case 5123:
                            elementSize = 2;
                            break;
                        case 5125:
                            elementSize = 4;
                            break;
                        default:
                            throw new InvalidDataException(EnglishText.Text("Unknown GLB index type."));
                    }
                    var view = views[accessor.GetProperty("bufferView").GetInt32()];
                    int start = view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0;
                    start += accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0;
                    int count = accessor.GetProperty("count").GetInt32();
                    int stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : elementSize;
                    EnsureRange(start, count, stride, elementSize);
                    var result = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        int at = start + i * stride;
                        switch (componentType)
                        {
                            case 5121:
                                result[i] = binary[at];
                                break;
                            case 5123:
                                result[i] = BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(at, 2));
                                break;
                            default:
                                result[i] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(at, 4)));
                                break;
                        }
                    }
                    return result;
                }
                int[] ReadWeightCounts(JsonElement attributes, int expectedVertexCount)
                {
                    var weightCounts = new int[expectedVertexCount];
                    if (!attributes.TryGetProperty("WEIGHTS_0", out var weightAccessorReference))
                        return weightCounts;
                    int weightAccessorIndex = weightAccessorReference.GetInt32();
                    if ((uint)weightAccessorIndex >= (uint)accessors.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid weight accessor."));
                    var weightAccessor = accessors[weightAccessorIndex];
                    if (weightAccessor.GetProperty("type").GetString() != "VEC4"
                        || (weightAccessor.TryGetProperty("sparse", out var sparseWeights) && sparseWeights.ValueKind != JsonValueKind.Undefined))
                        throw new InvalidDataException(EnglishText.Text("OAT expects a regular WEIGHTS_0 accessor of type VEC4."));
                    int componentType = weightAccessor.GetProperty("componentType").GetInt32();
                    int componentSize;
                    switch (componentType)
                    {
                        case 5121:
                            componentSize = 1;
                            break;
                        case 5123:
                            componentSize = 2;
                            break;
                        case 5126:
                            componentSize = 4;
                            break;
                        default:
                            throw new InvalidDataException(EnglishText.Text("OAT does not support this WEIGHTS_0 type."));
                    }
                    int accessorVertexCount = weightAccessor.GetProperty("count").GetInt32();
                    if (accessorVertexCount != expectedVertexCount)
                        throw new InvalidDataException(EnglishText.Text("WEIGHTS_0 weight count does not match vertex count."));
                    int viewIndex = weightAccessor.GetProperty("bufferView").GetInt32();
                    if ((uint)viewIndex >= (uint)views.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid WEIGHTS_0 bufferView."));
                    var view = views[viewIndex];
                    int start = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0)
                        + (weightAccessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
                    int elementSize = componentSize * 4;
                    int stride = view.TryGetProperty("byteStride", out var strideElement) ? strideElement.GetInt32() : elementSize;
                    EnsureRange(start, accessorVertexCount, stride, elementSize);
                    const float oatZeroWeightThreshold = 1.192092896E-07f;
                    for (int vertexIndex = 0; vertexIndex < accessorVertexCount; vertexIndex++)
                    {
                        int vertexStart = start + vertexIndex * stride;
                        int nonZeroWeightCount = 0;
                        for (int influenceIndex = 0; influenceIndex < 4; influenceIndex++)
                        {
                            int componentStart = vertexStart + influenceIndex * componentSize;
                            float weightValue = (componentType == 5121 ? binary[componentStart] : (componentType == 5123 ? BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(componentStart, 2)) : ReadFloat(componentStart)
    ));
                            if (Math.Abs(weightValue) >= oatZeroWeightThreshold)
                            {
                                nonZeroWeightCount++;
                            }
                        }
                        weightCounts[vertexIndex] = nonZeroWeightCount;
                    }
                    return weightCounts;
                }
                void AddOatSurfaceStats(int[] indices, int maxVerticesPerSurface)
                {
                    var surfaceVertices = new HashSet<int>();
                    int faceCount = 0;
                    for (int faceStart = 0; faceStart < indices.Length; faceStart += 3)
                    {
                        int newVertexCount = 0;
                        for (int cornerIndex = 0; cornerIndex < 3; cornerIndex++)
                        {
                            int vertexIndex = indices[faceStart + cornerIndex];
                            if (surfaceVertices.Contains(vertexIndex))
                            {
                                continue;
                            }
                            bool alreadyCounted = false;
                            for (int previousCornerIndex = 0; previousCornerIndex < cornerIndex; previousCornerIndex++)
                            {
                                if (indices[faceStart + previousCornerIndex] == vertexIndex)
                                {
                                    alreadyCounted = true;
                                    break;
                                }
                            }
                            if (!alreadyCounted)
                            {
                                newVertexCount++;
                            }
                        }
                        bool exceedsVertexLimit = surfaceVertices.Count + newVertexCount > maxVerticesPerSurface;
                        bool exceedsTriangleLimit = faceCount >= ushort.MaxValue;
                        if (faceCount > 0 && (exceedsVertexLimit || exceedsTriangleLimit))
                        {
                            FinishOatSurface();
                        }
                        surfaceVertices.Add(indices[faceStart]);
                        surfaceVertices.Add(indices[faceStart + 1]);
                        surfaceVertices.Add(indices[faceStart + 2]);
                        faceCount++;
                    }
                    FinishOatSurface();
                    void FinishOatSurface()
                    {
                        if (faceCount == 0)
                            return;
                        oatVertexTotal += surfaceVertices.Count;
                        oatSurfaceTotal++;
                        largestOatSurfaceVertexCount = Math.Max(largestOatSurfaceVertexCount, surfaceVertices.Count);
                        surfaceVertices.Clear();
                        faceCount = 0;
                    }
                }
                JsonElement GetAccessor(int index, string type, int componentType)
                {
                    if ((uint)index >= (uint)accessors.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid position accessor."));
                    var accessor = accessors[index];
                    if (accessor.GetProperty("type").GetString() != type || accessor.GetProperty("componentType").GetInt32() != componentType
                        || accessor.TryGetProperty("sparse", out var sparseAccessor))
                        throw new InvalidDataException(EnglishText.Text("Preview supports only regular float32 positions."));
                    int viewIndex = accessor.GetProperty("bufferView").GetInt32();
                    if ((uint)viewIndex >= (uint)views.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid bufferView."));
                    return accessor;
                }
                void EnsureRange(int start, int count, int stride, int elementSize)
                {
                    if (start < 0 || count < 0 || stride < elementSize
                        || (long)start + (long)Math.Max(0, count - 1) * stride + elementSize > binary.Length)
                        throw new InvalidDataException(EnglishText.Text("GLB accesses beyond the bounds of the BIN buffer."));
                }
                float ReadFloat(int at) => Helpers.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(at, 4)));
            }
        }

        /// <summary>Read the source material slot name so the project can resolve its exported T6 material.</summary>
        static string GetMaterialName(JsonElement primitive, JsonElement[] materials)
        {
            if (!primitive.TryGetProperty("material", out var slot) ||
                (uint)slot.GetInt32() >= (uint)materials.Length)
                return null;
            var material = materials[slot.GetInt32()];
            return material.TryGetProperty("name", out var name) ? name.GetString() : null;
        }

        /// <summary>
        /// Resolves or reads the ChunkLength value. Read GLB geometry and basic material color into a WPF model preview.
        /// </summary>
        static int ReadChunkLength(byte[] data, ref int offset, uint expectedType)
        {
            if (offset + 8 > data.Length)
                throw new InvalidDataException(EnglishText.Text("GLB has an incomplete chunk header."));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            offset += 8;
            if (type != expectedType || length > int.MaxValue || offset + (long)length > data.Length)
                throw new InvalidDataException(EnglishText.Text("GLB contains an invalid or truncated chunk."));
            return (int)length;
        }

        /// <summary>
        /// Performs the NodeTransform create operation. Read GLB geometry and basic material color into a WPF model preview.
        /// </summary>
        static Func<Vector3, Vector3> CreateNodeTransform(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var matrixElement))
            {
                var m = matrixElement.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (m.Length != 16)
                    throw new InvalidDataException(EnglishText.Text("GLB node matrix must contain 16 values."));
                return point => new Vector3(m[0] * point.X + m[4] * point.Y + m[8] * point.Z + m[12],
                    m[1] * point.X + m[5] * point.Y + m[9] * point.Z + m[13],
                    m[2] * point.X + m[6] * point.Y + m[10] * point.Z + m[14]);
            }
            var translation = node.TryGetProperty("translation", out var t) ? new Vector3(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle()) : Vector3.Zero;
            var scale = node.TryGetProperty("scale", out var s) ? new Vector3(s[0].GetSingle(), s[1].GetSingle(), s[2].GetSingle()) : Vector3.One;
            var rotation = node.TryGetProperty("rotation", out var r)
                ? NQuaternion.Normalize(new NQuaternion(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle()))
                : NQuaternion.Identity;
            return point => Vector3.Transform(point * scale, rotation) + translation;
        }

        /// <summary>
        /// Resolves or reads the MaterialColor value. Read GLB geometry and basic material color into a WPF model preview.
        /// </summary>
        static Color GetMaterialColor(JsonElement primitive, JsonElement[] materials)
        {
            if (!primitive.TryGetProperty("material", out var materialIndex) || (uint)materialIndex.GetInt32() >= (uint)materials.Length)
                return Color.FromRgb(170, 178, 188);
            var material = materials[materialIndex.GetInt32()];
            if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr) || !pbr.TryGetProperty("baseColorFactor", out var factor))
                return Color.FromRgb(170, 178, 188);
            var values = factor.EnumerateArray().Select(v => Helpers.Clamp((int)Math.Round(v.GetSingle() * 255), 0, 255)).ToArray();
            return values.Length == 4 ? Color.FromArgb((byte)values[3], (byte)values[0], (byte)values[1], (byte)values[2]) : Color.FromRgb(170, 178, 188);
        }

        /// <summary>Decode GLB image sources once so materials can share immutable WPF bitmaps.</summary>
        static BitmapSource[] LoadImages(JsonElement root, JsonElement[] views, byte[] binary, string glbPath, CancellationToken cancellation)
        {
            if (!root.TryGetProperty("images", out var imageArray))
                return new BitmapSource[0];
            var result = new BitmapSource[imageArray.GetArrayLength()];
            int index = 0;
            foreach (var image in imageArray.EnumerateArray())
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] bytes = null;
                if (image.TryGetProperty("bufferView", out var viewValue))
                {
                    int viewIndex = viewValue.GetInt32();
                    if ((uint)viewIndex >= (uint)views.Length)
                    {
                        index++;
                        continue;
                    }
                    var view = views[viewIndex];
                    int start = view.TryGetProperty("byteOffset", out var startValue) ? startValue.GetInt32() : 0;
                    int length = view.GetProperty("byteLength").GetInt32();
                    if (start < 0 || length <= 0 || (long)start + length > binary.Length)
                    {
                        index++;
                        continue;
                    }
                    bytes = binary.AsSpan(start, length).ToArray();
                }
                else if (image.TryGetProperty("uri", out var uriValue) && uriValue.GetString() is string uri)
                {
                    try
                    {
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            int comma = uri.IndexOf(',');
                            if (comma > 0 && uri.Substring(0, (comma) - (0)).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                            {
                                bytes = Convert.FromBase64String(uri.Substring((comma + 1)));
                            }
                        }
                        else if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && absolute.IsFile)
                        {
                            bytes = File.ReadAllBytes(absolute.LocalPath);
                        }
                        else if (!Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri))
                        {
                            bytes = File.ReadAllBytes(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(glbPath), Uri.UnescapeDataString(uri.Replace('/', Path.DirectorySeparatorChar)))));
                        }
                    }
                    catch (Exception error) when ((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.FormatException)) || (error is System.ArgumentException))
                    {
                    }
                }
                if ((bytes != null) && (bytes.Length > 0))
                {
                    try
                    {
                        // GLB exports may reference or embed DDS directly. WPF's BitmapDecoder does not
                        // understand DDS, so route it through the same decoder used by the texture tab.
                        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("DDS ")))
                        {
                            var dds = TextureModule.LoadDds(bytes);
                            if ((long)dds.Width * dds.Height <= 64_000_000)
                            {
                                result[index] = dds.Image;
                            }
                        }
                        else
                        {
                            using (var stream = new MemoryStream(bytes, writable: false))
                            {
                                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                if (decoder.Frames.Count > 0)
                                {
                                    var frame = decoder.Frames[0];
                                    if ((long)frame.PixelWidth * frame.PixelHeight <= 64_000_000)
                                    {
                                        frame.Freeze();
                                        result[index] = frame;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception error) when ((((((error is System.IO.IOException) || (error is System.NotSupportedException)) || (error is System.InvalidOperationException)) || (error is System.FormatException)) || (error is System.ArgumentException)) || (error is System.Runtime.InteropServices.COMException))
                    {
                    }
                }
                index++;
            }
            return result;
        }

        /// <summary>Resolve the glTF material's base-color texture through its texture and image tables.</summary>
        static BitmapSource GetBaseColorImage(JsonElement primitive, JsonElement[] materials, JsonElement[] textures, BitmapSource[] images)
        {
            if (!primitive.TryGetProperty("material", out var slot) || (uint)slot.GetInt32() >= (uint)materials.Length)
                return null;
            var material = materials[slot.GetInt32()];
            if (!material.TryGetProperty("pbrMetallicRoughness", out var pbr) ||
                !pbr.TryGetProperty("baseColorTexture", out var textureInfo) ||
                !textureInfo.TryGetProperty("index", out var textureSlot))
                return null;
            int textureIndex = textureSlot.GetInt32();
            if ((uint)textureIndex >= (uint)textures.Length ||
                !textures[textureIndex].TryGetProperty("source", out var source))
                return null;
            int imageIndex = source.GetInt32();
            return (uint)imageIndex < (uint)images.Length ? images[imageIndex] : null;
        }
        // Convert only the preview bitmap; source DDS and GLB files remain untouched.
        static BitmapSource ApplyAlphaMode(BitmapSource source, string mode, double cutoff, byte factorAlpha)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            bool mask = mode.Equals("MASK", StringComparison.OrdinalIgnoreCase);
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = mask ? (byte)(pixels[i] / 255.0 * (factorAlpha / 255.0) >= cutoff ? 255 : 0) : (byte)255;
            }
            var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        /// <summary>Approximate GLB PBR with a base-color image/factor, diffuse light and metallic highlight.</summary>
        static Material CreateShadedMaterial(JsonElement primitive, JsonElement[] materials, BitmapSource bitmap, bool hasUv, Color baseColor)
        {
            // glTF defaults to OPAQUE: an alpha channel may contain unrelated T6 shader data.
            // Only BLEND or MASK materials may use texture alpha for display.
            string alphaMode = "OPAQUE";
            double alphaCutoff = 0.5;
            if (primitive.TryGetProperty("material", out var alphaSlot) && (uint)alphaSlot.GetInt32() < (uint)materials.Length)
            {
                var alphaMaterial = materials[alphaSlot.GetInt32()];
                if (alphaMaterial.TryGetProperty("alphaMode", out var mode))
                {
                    alphaMode = mode.GetString() ?? "OPAQUE";
                }
                if (alphaMaterial.TryGetProperty("alphaCutoff", out var cutoff))
                {
                    alphaCutoff = Helpers.Clamp(cutoff.GetDouble(), 0, 1);
                }
            }
            if (bitmap != null && hasUv && !alphaMode.Equals("BLEND", StringComparison.OrdinalIgnoreCase))
            {
                bitmap = ApplyAlphaMode(bitmap, alphaMode, alphaCutoff, baseColor.A);
            }
            if (bitmap == null && alphaMode.Equals("OPAQUE", StringComparison.OrdinalIgnoreCase))
            {
                baseColor.A = 255;
            }
            Brush brush = bitmap != null && hasUv
                ? (Brush)new ImageBrush(bitmap)
                {
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.Tile,
                    // GLB UVs address the texture atlas in normalized 0..1 space. RelativeToBoundingBox
                    // remaps that atlas per mesh and can put unrelated atlas regions on neighboring parts.
                    Viewport = new Rect(0, 0, 1, 1),
                    ViewportUnits = BrushMappingMode.Absolute
                }
                : new SolidColorBrush(baseColor);
            brush.Freeze();
            var group = new MaterialGroup();
            group.Children.Add(new DiffuseMaterial(brush));
            if (primitive.TryGetProperty("material", out var slot) && (uint)slot.GetInt32() < (uint)materials.Length)
            {
                var material = materials[slot.GetInt32()];
                if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
                {
                    float metallic = pbr.TryGetProperty("metallicFactor", out var m) ? Helpers.Clamp(m.GetSingle(), 0, 1) : 1;
                    float roughness = pbr.TryGetProperty("roughnessFactor", out var r) ? Helpers.Clamp(r.GetSingle(), 0, 1) : 1;
                    if (metallic > 0.01f)
                    {
                        var specular = new SolidColorBrush(Color.FromRgb((byte)(baseColor.R * metallic), (byte)(baseColor.G * metallic), (byte)(baseColor.B * metallic)));
                        specular.Freeze();
                        group.Children.Add(new SpecularMaterial(specular, Math.Max(2, 128 * (1 - roughness))));
                    }
                }
            }
            group.Freeze();
            return group;
        }
    }
}
