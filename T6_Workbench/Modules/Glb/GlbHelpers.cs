using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    #region GlbComparer
    /// <summary>
    /// GlbComparer. Semantic GLB mesh, skin and material comparison.
    /// </summary>
    internal static class GlbComparer
    {
        /// <summary>
        /// Glb. Semantic GLB mesh, skin and material comparison.
        /// </summary>
        sealed class Glb { public byte[] Bytes { get; set; } public JsonDocument Json { get; set; } public int BinaryOffset { get; set; } public Glb(byte[] Bytes, JsonDocument Json, int BinaryOffset) { this.Bytes = Bytes; this.Json = Json; this.BinaryOffset = BinaryOffset; } }
        static Glb Read(string file)
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("glTF")) || BitConverter.ToInt32(bytes, 4) != 2 || BitConverter.ToInt32(bytes, 8) != bytes.Length)
                throw new InvalidDataException(EnglishText.Text("Invalid GLB: ") + file);
            int offset = 12, binary = -1;
            JsonDocument doc = null;
            while (offset < bytes.Length)
            {
                int length = BitConverter.ToInt32(bytes, offset), type = BitConverter.ToInt32(bytes, offset + 4);
                offset += 8;
                if (length < 0 || offset + length > bytes.Length)
                    throw new InvalidDataException("GLB chunk out of bounds.");
                if (type == 0x4e4f534a)
                    doc = JsonDocument.Parse(bytes.AsMemory(offset, length));
                if (type == 0x004e4942)
                    binary = offset;
                offset += length;
            }
            return new GlbComparer.Glb(bytes, doc ?? throw new InvalidDataException("No GLB JSON."), binary >= 0 ? binary : throw new InvalidDataException("No GLB binary."));
        }
        /// <summary>
        /// Resolves or reads the Int value. Semantic GLB mesh, skin and material comparison.
        /// </summary>
        static int GetInt(JsonElement element, string key, int fallback = 0) => element.TryGetProperty(key, out var p) ? p.GetInt32() : fallback;
        static int ComponentSize(int type)
        {
            switch (type)
            {
                case 5120:
                case 5121: return 1;
                case 5122:
                case 5123: return 2;
                case 5125:
                case 5126: return 4;
                default: throw new InvalidDataException("Component type.");
            }
        }
        static int Components(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT4": return 16;
                default: throw new InvalidDataException("Accessor shape.");
            }
        }
        static double[] Values(Glb glb, int index)
        {
            var root = glb.Json.RootElement;
            var accessor = root.GetProperty("accessors")[index];
            var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
            int count = accessor.GetProperty("count").GetInt32(), components = Components(accessor.GetProperty("type").GetString());
            int type = accessor.GetProperty("componentType").GetInt32(), size = ComponentSize(type);
            int offset = glb.BinaryOffset + GetInt(view, "byteOffset") + GetInt(accessor, "byteOffset");
            int stride = GetInt(view, "byteStride", components * size);
            bool normalized = accessor.TryGetProperty("normalized", out var n) && n.GetBoolean();
            var values = new double[count * components];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < components; c++)
                {
                    int pos = offset + stride * i + size * c;
                    double value;
                    switch (type)
                    {
                        case 5120:
                            value = (sbyte)glb.Bytes[pos];
                            break;
                        case 5121:
                            value = glb.Bytes[pos];
                            break;
                        case 5122:
                            value = BitConverter.ToInt16(glb.Bytes, pos);
                            break;
                        case 5123:
                            value = BitConverter.ToUInt16(glb.Bytes, pos);
                            break;
                        case 5125:
                            value = BitConverter.ToUInt32(glb.Bytes, pos);
                            break;
                        case 5126:
                            value = BitConverter.ToSingle(glb.Bytes, pos);
                            break;
                        default:
                            throw new InvalidDataException();
                    }
                    if (normalized)
                        switch (type)
                        {
                            case 5120:
                                value = Math.Max(value / 127, -1);
                                break;
                            case 5121:
                                value = value / 255;
                                break;
                            case 5122:
                                value = Math.Max(value / 32767, -1);
                                break;
                            case 5123:
                                value = value / 65535;
                                break;
                            default:
                                break;
                        }
                    if (!Helpers.IsFinite(value))
                        throw new InvalidDataException("Non-finite GLB value.");
                    values[i * components + c] = value;
                }
            return values;
        }

        static string[] BoneNames(JsonElement root)
        {
            if (!root.TryGetProperty("skins", out var skins))
                return new string[0];
            var nodes = root.GetProperty("nodes");
            return skins.EnumerateArray().SelectMany(skin => skin.GetProperty("joints").EnumerateArray())
                .Select(index => nodes[index.GetInt32()].GetProperty("name").GetString() ?? "")
                .Order(StringComparer.Ordinal).ToArray();
        }

        static Dictionary<string, int> TriangleInventory(Glb glb)
        {
            var root = glb.Json.RootElement;
            var materials = root.TryGetProperty("materials", out var found) ? found : default;
            var cache = new Dictionary<int, double[]>();
            double[] Accessor(int index)
            {
                if (!cache.TryGetValue(index, out var values))
                    cache[index] = values = Values(glb, index);
                return values;
            }
            string Vertex(double[] positions, int index)
            {
                int at = checked(index * 3);
                if (at < 0 || at + 2 >= positions.Length)
                    throw new InvalidDataException(EnglishText.Text("Vertex index outside GLB bounds."));
                return string.Join(",", Enumerable.Range(0, 3)
                    .Select(axis => Math.Round(positions[at + axis] * 1000, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
            }
            var inventory = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
                foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                {
                    var positions = Accessor(primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                    var indices = Accessor(primitive.GetProperty("indices").GetInt32());
                    if (indices.Length % 3 != 0)
                        throw new InvalidDataException(EnglishText.Text("Incomplete triangle list."));
                    var materialIndex = primitive.GetProperty("material").GetInt32();
                    if (materials.ValueKind != JsonValueKind.Array || materialIndex < 0 || materialIndex >= materials.GetArrayLength())
                        throw new InvalidDataException(EnglishText.Text("Invalid GLB material."));
                    var material = (materials[materialIndex].GetProperty("name").GetString() ?? "").TrimStart(',');
                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        var vertices = Enumerable.Range(0, 3)
                            .Select(corner => Vertex(positions, checked((int)indices[i + corner])))
                            .Order(StringComparer.Ordinal);
                        string key = material + ":" + string.Join("|", vertices);
                        inventory[key] = inventory.GetValueOrDefault(key) + 1;
                    }
                }
            return inventory;
        }
        public static void EditFirstPosition(string file, float amount)
        {
            var glb = Read(file);
            using (glb.Json)
            {
                var root = glb.Json.RootElement;
                int index = root.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
                var accessor = root.GetProperty("accessors")[index];
                if (GetInt(accessor, "componentType") != 5126)
                    throw new InvalidDataException("Position is not float32.");
                var view = root.GetProperty("bufferViews")[GetInt(accessor, "bufferView")];
                int offset = glb.BinaryOffset + GetInt(view, "byteOffset") + GetInt(accessor, "byteOffset");
                BitConverter.GetBytes(BitConverter.ToSingle(glb.Bytes, offset) + amount).CopyTo(glb.Bytes, offset);
                File.WriteAllBytes(file, glb.Bytes);
            }
        }

        /// <summary>
        /// Performs the  compare operation. Semantic GLB mesh, skin and material comparison.
        /// </summary>
        public static string Compare(string first, string second, Action<string, double> measurement = null, bool measureOnly = false)
        {
            var a = Read(first);
            var b = Read(second);
            using (a.Json)
            using (b.Json)
            {
                var ar = a.Json.RootElement;
                var br = b.Json.RootElement;
                int vertices = 0, indices = 0, boneCount = 0;
                var positions = new HashSet<int>();
                double max = 0;
                void CompareValues(int ai, int bi, string semantic, double tolerance)
                {
                    var av = Values(a, ai);
                    var bv = Values(b, bi);
                    if (av.Length != bv.Length)
                        throw new InvalidDataException(semantic + ": different element counts.");
                    double semanticMax = 0;
                    string failure = null;
                    for (int i = 0; i < av.Length; i++)
                    {
                        double difference = Math.Abs(av[i] - bv[i]);
                        max = Math.Max(max, difference);
                        semanticMax = Math.Max(semanticMax, difference);
                        if (difference > tolerance && failure == null)
                            failure = $"{semantic}[{i}]: {av[i]:G9} -> {bv[i]:G9}, delta={difference:G6} > {tolerance}.";
                    }
                    measurement?.Invoke(semantic, semanticMax);
                    if (failure != null && !measureOnly)
                        throw new InvalidDataException(failure);
                }
                var am = ar.GetProperty("meshes");
                var bm = br.GetProperty("meshes");
                if (am.GetArrayLength() != bm.GetArrayLength())
                    throw new InvalidDataException("Mesh count differs.");
                for (int m = 0; m < am.GetArrayLength(); m++)
                {
                    var ap = am[m].GetProperty("primitives");
                    var bp = bm[m].GetProperty("primitives");
                    if (ap.GetArrayLength() != bp.GetArrayLength())
                        throw new InvalidDataException("Primitive count differs.");
                    for (int p = 0; p < ap.GetArrayLength(); p++)
                    {
                        var attributes = ap[p].GetProperty("attributes");
                        var other = bp[p].GetProperty("attributes");
                        if (attributes.EnumerateObject().Count() != other.EnumerateObject().Count())
                            throw new InvalidDataException("Attribute count differs.");
                        foreach (var attribute in attributes.EnumerateObject())
                        {
                            double tolerance = attribute.Name.StartsWith("JOINTS") ? 0 : attribute.Name.StartsWith("NORMAL") ? 0.02 : 0.0002;
                            CompareValues(attribute.Value.GetInt32(), other.GetProperty(attribute.Name).GetInt32(), attribute.Name, tolerance);
                            if (attribute.Name == "POSITION" && positions.Add(attribute.Value.GetInt32()))
                                vertices += Values(a, attribute.Value.GetInt32()).Length / 3;
                        }
                        CompareValues(GetInt(ap[p], "indices"), GetInt(bp[p], "indices"), "indices", 0);
                        indices += Values(a, GetInt(ap[p], "indices")).Length;
                    }
                }
                // Bone hierarchy and transforms, in addition to skin weights / inverse bind matrices.
                var an = ar.GetProperty("nodes");
                var bn = br.GetProperty("nodes");
                if (an.GetArrayLength() != bn.GetArrayLength())
                    throw new InvalidDataException("Node count differs.");
                for (int i = 0; i < an.GetArrayLength(); i++)
                    foreach (var key in new[] { "name", "children", "translation", "rotation", "scale", "matrix", "mesh", "skin" })
                    {
                        bool ha = an[i].TryGetProperty(key, out var av), hb = bn[i].TryGetProperty(key, out var bv);
                        if (ha != hb)
                            throw new InvalidDataException("Node property differs: " + key);
                        if (!ha)
                            continue;
                        if ((((key == "translation") || (key == "rotation")) || (key == "scale")) || (key == "matrix"))
                        {
                            var aa = av.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                            var ba = bv.EnumerateArray().Select(v => v.GetDouble()).ToArray();
                            if (key == "rotation" && aa.Length == 4 && ba.Length == 4 && aa.Zip(ba).Sum(v => v.First * v.Second) < 0)
                                ba = ba.Select(v => -v).ToArray(); // Quaternion double cover: q and -q represent the same rotation.
                            if (aa.Length != ba.Length || aa.Zip(ba).Any(v => Math.Abs(v.First - v.Second) > 0.0002))
                                throw new InvalidDataException($"Bone node {i}: {key} differs.");
                        }
                        else if (av.GetRawText() != bv.GetRawText())
                            throw new InvalidDataException($"Node {i}: {key} differs.");
                    }
                bool hasSkin = ar.TryGetProperty("skins", out var ask), otherSkin = br.TryGetProperty("skins", out var bsk);
                if (hasSkin != otherSkin)
                    throw new InvalidDataException("Skin lost.");
                if (hasSkin)
                {
                    if (ask.GetArrayLength() != bsk.GetArrayLength())
                        throw new InvalidDataException("Skin count differs.");
                    for (int i = 0; i < ask.GetArrayLength(); i++)
                    {
                        if (ask[i].GetProperty("joints").GetRawText() != bsk[i].GetProperty("joints").GetRawText())
                            throw new InvalidDataException("Skin joints differ.");
                        boneCount += ask[i].GetProperty("joints").GetArrayLength();
                        CompareValues(GetInt(ask[i], "inverseBindMatrices"), GetInt(bsk[i], "inverseBindMatrices"), "inverseBindMatrices", 0.0002);
                    }
                }
                return $"GLB: {vertices} vertices, {indices / 3} triangles, {boneCount} bones; attributes/hierarchy/bind matrices checked, max delta={max:G6}.";
            }
        }

        /// <summary>
        /// Performs the FloatingTestAddition has operation. Semantic GLB mesh, skin and material comparison.
        /// </summary>
        public static bool HasFloatingTestAddition(string file)
        {
            var glb = Read(file);
            using (glb.Json)
                return glb.Json.RootElement.GetProperty("nodes").EnumerateArray()
                    .Any(node => node.TryGetProperty("name", out var name) && name.GetString() == "floating_TEST_near_front_sight");
        }

        /// <summary>
        /// Performs the WithAddedMesh compare operation. Semantic GLB mesh, skin and material comparison.
        /// </summary>
        public static string CompareEditedGeometry(string edited, string rebuilt)
        {
            var a = Read(edited);
            var b = Read(rebuilt);
            using (a.Json)
            using (b.Json)
            {
                var first = TriangleInventory(a);
                var second = TriangleInventory(b);
                if (first.Count != second.Count || first.Any(item => !second.TryGetValue(item.Key, out int count) || count != item.Value))
                    throw new InvalidDataException(EnglishText.Format("Geometry differs after build: triangles {0} -> {1}.", first.Values.Sum(), second.Values.Sum()));
                var leftBones = BoneNames(a.Json.RootElement);
                var rightBones = BoneNames(b.Json.RootElement);
                if (!leftBones.SequenceEqual(rightBones))
                    throw new InvalidDataException(EnglishText.Text("Bone list differs after build."));
                return EnglishText.Format("GLB: {0} triangles and {1} bones; geometry/materials matched within a tolerance of 0.001. Weights and live animation have not been tested.", first.Values.Sum(), leftBones.Length);
            }
        }

        public static string CompareWithAddedMesh(string edited, string roundtrip)
        {
            var a = Read(edited);
            var b = Read(roundtrip);
            using (a.Json)
            using (b.Json)
            {
                var ar = a.Json.RootElement;
                var br = b.Json.RootElement;
                var am = ar.GetProperty("meshes");
                var bm = br.GetProperty("meshes");
                var addedNode = ar.GetProperty("nodes").EnumerateArray().FirstOrDefault(node =>
                    node.TryGetProperty("name", out var name) && name.GetString() == "floating_TEST_near_front_sight");
                if (addedNode.ValueKind == JsonValueKind.Undefined || !addedNode.TryGetProperty("mesh", out var addedMeshProp))
                    throw new InvalidDataException("Floating TEST mesh marker is missing.");
                int addedMeshIndex = addedMeshProp.GetInt32();
                if (addedMeshIndex != am.GetArrayLength() - 1 || bm.GetArrayLength() != am.GetArrayLength())
                    throw new InvalidDataException("Roundtrip mesh layout does not preserve the added TEST mesh.");

                var addedPrimitive = am[addedMeshIndex].GetProperty("primitives")[0];
                int addedPositionAccessor = addedPrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                int addedVertexCount = ar.GetProperty("accessors")[addedPositionAccessor].GetProperty("count").GetInt32();
                var firstPrimitive = am[0].GetProperty("primitives")[0];
                int basePositionAccessor = firstPrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                int baseVertexCount = ar.GetProperty("accessors")[basePositionAccessor].GetProperty("count").GetInt32();
                var outputPosition = bm[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
                int outputVertexCount = br.GetProperty("accessors")[outputPosition].GetProperty("count").GetInt32();
                if (outputVertexCount != baseVertexCount + addedVertexCount)
                    throw new InvalidDataException($"Roundtrip vertex count {outputVertexCount} does not equal base+added ({baseVertexCount}+{addedVertexCount}).");

                int baseMeshes = addedMeshIndex;
                double maxDelta = 0;
                void CompareSegment(int sourceAccessor, int targetAccessor, int startElement, double tolerance, string semantic)
                {
                    var sourceValues = Values(a, sourceAccessor);
                    var targetValues = Values(b, targetAccessor);
                    int components = Components(ar.GetProperty("accessors")[sourceAccessor].GetProperty("type").GetString());
                    int start = startElement * components;
                    if (start + sourceValues.Length > targetValues.Length)
                        throw new InvalidDataException(semantic + ": roundtrip accessor segment is too short.");
                    for (int i = 0; i < sourceValues.Length; i++)
                    {
                        double delta = Math.Abs(sourceValues[i] - targetValues[start + i]);
                        maxDelta = Math.Max(maxDelta, delta);
                        if (delta > tolerance)
                            throw new InvalidDataException($"{semantic}[{i}]: delta={delta:G6} > {tolerance}.");
                    }
                }
                void CompareMultisetSegment(int sourceAccessor, int targetAccessor, int startElement, double tolerance, string semantic)
                {
                    var sourceValues = Values(a, sourceAccessor);
                    var targetValues = Values(b, targetAccessor);
                    int components = Components(ar.GetProperty("accessors")[sourceAccessor].GetProperty("type").GetString());
                    int start = startElement * components;
                    if (start + sourceValues.Length > targetValues.Length)
                        throw new InvalidDataException(semantic + ": roundtrip accessor segment is too short.");
                    int digits = tolerance >= 0.01 ? 2 : tolerance == 0 ? 0 : 4;
                    Dictionary<string, int> Counts(double[] values, int offset, int length, int itemComponents, int itemDigits)
                    {
                        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (int i = 0; i < length; i += itemComponents)
                        {
                            string key = string.Join(",", Enumerable.Range(0, itemComponents).Select(c =>
                            {
                                double rounded = Math.Round(values[offset + i + c], itemDigits);
                                if (rounded == 0)
                                    rounded = 0; // Normalize negative zero after coordinate handedness conversion.
                                return rounded.ToString($"F{itemDigits}", System.Globalization.CultureInfo.InvariantCulture);
                            }));
                            counts[key] = counts.GetValueOrDefault(key) + 1;
                        }
                        return counts;
                    }
                    var expected = Counts(sourceValues, 0, sourceValues.Length, components, digits);
                    var actual = Counts(targetValues, start, sourceValues.Length, components, digits);
                    if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var count) || count != pair.Value))
                        throw new InvalidDataException(semantic + ": additive mesh vertex attributes differ after roundtrip.");
                }
                string MaterialName(JsonElement root, JsonElement primitive)
                {
                    int index = primitive.GetProperty("material").GetInt32();
                    return root.GetProperty("materials")[index].GetProperty("name").GetString() ?? "";
                }

                for (int mesh = 0; mesh < am.GetArrayLength(); mesh++)
                {
                    var sourcePrimitives = am[mesh].GetProperty("primitives");
                    var targetPrimitives = bm[mesh].GetProperty("primitives");
                    if (sourcePrimitives.GetArrayLength() != targetPrimitives.GetArrayLength())
                        throw new InvalidDataException($"Mesh {mesh} primitive count differs.");
                    for (int primitiveIndex = 0; primitiveIndex < sourcePrimitives.GetArrayLength(); primitiveIndex++)
                    {
                        var sourcePrimitive = sourcePrimitives[primitiveIndex];
                        var targetPrimitive = targetPrimitives[primitiveIndex];
                        if (MaterialName(ar, sourcePrimitive) != MaterialName(br, targetPrimitive))
                            throw new InvalidDataException($"Mesh {mesh} material differs.");
                        int sourceIndices = sourcePrimitive.GetProperty("indices").GetInt32();
                        int targetIndices = targetPrimitive.GetProperty("indices").GetInt32();
                        var si = Values(a, sourceIndices);
                        var ti = Values(b, targetIndices);
                        if (mesh == addedMeshIndex)
                        {
                            if (ti.Length != si.Length)
                                throw new InvalidDataException("Added TEST mesh index count changed.");
                            int sourcePosAccessor = sourcePrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                            int targetPosAccessor = targetPrimitive.GetProperty("attributes").GetProperty("POSITION").GetInt32();
                            var sourcePositions = Values(a, sourcePosAccessor);
                            var targetPositions = Values(b, targetPosAccessor);
                            var sourceTriangles = new Dictionary<string, int>(StringComparer.Ordinal);
                            var targetTriangles = new Dictionary<string, int>(StringComparer.Ordinal);
                            void AddTriangles(Dictionary<string, int> destination, double[] positions, double[] indexValues, int vertexStart, int vertexCount, bool absoluteIndices)
                            {
                                for (int i = 0; i < indexValues.Length; i += 3)
                                {
                                    var points = new List<string>(3);
                                    for (int c = 0; c < 3; c++)
                                    {
                                        int vertex = (int)indexValues[i + c];
                                        if (absoluteIndices)
                                            vertex -= vertexStart;
                                        if (vertex < 0 || vertex >= vertexCount)
                                            throw new InvalidDataException("Added TEST mesh index is outside its vertex segment.");
                                        int offset = (vertexStart + vertex) * 3;
                                        points.Add(string.Join(",", Enumerable.Range(0, 3).Select(axis =>
                                            Math.Round(positions[offset + axis], 4).ToString("F4", System.Globalization.CultureInfo.InvariantCulture))));
                                    }
                                    points.Sort(StringComparer.Ordinal);
                                    string key = string.Join("|", points);
                                    destination[key] = destination.GetValueOrDefault(key) + 1;
                                }
                            }
                            AddTriangles(sourceTriangles, sourcePositions, si, 0, addedVertexCount, false);
                            AddTriangles(targetTriangles, targetPositions, ti, baseVertexCount, addedVertexCount, true);
                            if (sourceTriangles.Count != targetTriangles.Count || sourceTriangles.Any(pair =>
                                    !targetTriangles.TryGetValue(pair.Key, out var count) || count != pair.Value))
                                throw new InvalidDataException("Added TEST mesh triangles differ after roundtrip.");
                        }
                        else
                        {
                            if (!si.SequenceEqual(ti))
                                throw new InvalidDataException($"Base mesh {mesh} indices changed.");
                        }

                        var sourceAttributes = sourcePrimitive.GetProperty("attributes");
                        var targetAttributes = targetPrimitive.GetProperty("attributes");
                        foreach (var attribute in sourceAttributes.EnumerateObject())
                        {
                            if (!targetAttributes.TryGetProperty(attribute.Name, out var targetAccessor))
                                throw new InvalidDataException($"Roundtrip lost {attribute.Name}.");
                            int start = mesh == addedMeshIndex ? baseVertexCount : 0;
                            double tolerance = attribute.Name.StartsWith("JOINTS", StringComparison.Ordinal) ? 0 :
                                attribute.Name.StartsWith("NORMAL", StringComparison.Ordinal) ? 0.02 : 0.0002;
                            if (mesh == addedMeshIndex)
                                CompareMultisetSegment(attribute.Value.GetInt32(), targetAccessor.GetInt32(), start, tolerance, attribute.Name);
                            else
                                CompareSegment(attribute.Value.GetInt32(), targetAccessor.GetInt32(), start, tolerance, attribute.Name);
                        }
                    }
                }

                // Check that the original skeletal joints and inverse bind matrices remain intact.
                var ask = ar.GetProperty("skins");
                var bsk = br.GetProperty("skins");
                if (ask.GetArrayLength() != bsk.GetArrayLength())
                    throw new InvalidDataException("Skin count differs.");
                int bones = 0;
                for (int skin = 0; skin < ask.GetArrayLength(); skin++)
                {
                    var sourceJoints = ask[skin].GetProperty("joints");
                    var targetJoints = bsk[skin].GetProperty("joints");
                    if (sourceJoints.GetArrayLength() != targetJoints.GetArrayLength())
                        throw new InvalidDataException("Skin joint count differs.");
                    bones += sourceJoints.GetArrayLength();
                    for (int joint = 0; joint < sourceJoints.GetArrayLength(); joint++)
                    {
                        var sourceNode = ar.GetProperty("nodes")[sourceJoints[joint].GetInt32()];
                        var targetNode = br.GetProperty("nodes")[targetJoints[joint].GetInt32()];
                        string name = sourceNode.GetProperty("name").GetString() ?? "";
                        string outputName = targetNode.GetProperty("name").GetString() ?? "";
                        if (name != outputName)
                            throw new InvalidDataException("Skin joint order changed.");
                        foreach (var key in new[] { "translation", "rotation", "scale", "matrix" })
                        {
                            bool sourceHas = sourceNode.TryGetProperty(key, out var sourceTransform);
                            bool targetHas = targetNode.TryGetProperty(key, out var targetTransform);
                            if (sourceHas != targetHas)
                                throw new InvalidDataException($"Skin joint {name}: {key} presence changed.");
                            if (!sourceHas)
                                continue;
                            var sourceValues = sourceTransform.EnumerateArray().Select(value => value.GetDouble()).ToArray();
                            var targetValues = targetTransform.EnumerateArray().Select(value => value.GetDouble()).ToArray();
                            if (key == "rotation" && sourceValues.Length == 4 && targetValues.Length == 4 &&
                                sourceValues.Zip(targetValues).Sum(value => value.First * value.Second) < 0)
                                targetValues = targetValues.Select(value => -value).ToArray();
                            if (sourceValues.Length != targetValues.Length || sourceValues.Zip(targetValues)
                                .Any(value => Math.Abs(value.First - value.Second) > 0.0002))
                                throw new InvalidDataException($"Skin joint {name}: {key} changed.");
                        }
                        bool sourceChildren = sourceNode.TryGetProperty("children", out var sourceChildList);
                        bool targetChildren = targetNode.TryGetProperty("children", out var targetChildList);
                        if (sourceChildren != targetChildren || sourceChildren && sourceChildList.GetRawText() != targetChildList.GetRawText())
                            throw new InvalidDataException($"Skin joint {name}: hierarchy changed.");
                    }
                    CompareSegment(ask[skin].GetProperty("inverseBindMatrices").GetInt32(),
                        bsk[skin].GetProperty("inverseBindMatrices").GetInt32(), 0, 0.0002, "inverseBindMatrices");
                }
                if (addedVertexCount < 1)
                    throw new InvalidDataException("Added mesh is empty.");
                int addedTriangles = Values(a, addedPrimitive.GetProperty("indices").GetInt32()).Length / 3;
                return $"Additive GLB: {baseMeshes} base meshes and {addedVertexCount} TEST vertices/{addedTriangles} triangles preserved; {bones} bones; max delta={maxDelta:G6}.";
            }
        }
    }
    #endregion

    #region GlbImportNormalizer
    /// <summary>Prepare Blender GLB meshes for the current OAT XModel loader without changing the source file.</summary>
    internal static class GlbImportNormalizer
    {
        /// <summary>
        /// Result. Validate and normalize supported GLB node transforms and material slots.
        /// </summary>
        public sealed class Result { public byte[] Bytes { get; set; } public string[] Changes { get; set; } public Result(byte[] Bytes, string[] Changes) { this.Bytes = Bytes; this.Changes = Changes; } }

        /// <summary>
        /// Validate GLB chunks and bake supported unskinned node transforms into positions and normals; return new bytes and an audit list.
        /// </summary>
        public static Result Normalize(string file, ISet<string> knownMaterialNames = null)
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 28 || !data.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("glTF")) || Read32(data, 4) != 2 || Read32(data, 8) != data.Length)
                throw new InvalidDataException(EnglishText.Text("A valid GLB 2.0 file is required."));
            int jsonLength = Read32(data, 12);
            if (Read32(data, 16) != 0x4e4f534a || jsonLength < 0 || 20L + jsonLength + 8 > data.Length)
                throw new InvalidDataException(EnglishText.Text("Invalid GLB JSON chunk."));
            int binHeader = 20 + jsonLength;
            int binLength = Read32(data, binHeader);
            if (Read32(data, binHeader + 4) != 0x004e4942 || binLength < 0 || (long)binHeader + 8 + binLength != data.Length)
                throw new InvalidDataException(EnglishText.Text("Invalid GLB BIN chunk."));
            var root = JsonNode.Parse(data.AsSpan(20, jsonLength))?.AsObject()
                ?? throw new InvalidDataException(EnglishText.Text("GLB is missing its JSON object."));
            var binary = data.AsSpan(binHeader + 8, binLength).ToArray();
            var meshes = root["meshes"]?.AsArray() ?? throw new InvalidDataException(EnglishText.Text("GLB is missing meshes."));
            var nodes = root["nodes"]?.AsArray() ?? throw new InvalidDataException(EnglishText.Text("GLB is missing nodes."));
            var accessors = root["accessors"]?.AsArray() ?? throw new InvalidDataException(EnglishText.Text("GLB is missing accessors."));
            var views = root["bufferViews"]?.AsArray() ?? throw new InvalidDataException(EnglishText.Text("GLB is missing bufferViews."));
            var materials = root["materials"]?.AsArray();
            var changes = new List<string>();
            NormalizeMaterialArray(materials, knownMaterialNames, changes);
            var meshUsers = new int[meshes.Count];
            foreach (var nodeValue in nodes)
                if (nodeValue is JsonObject node && node["mesh"] is JsonNode meshRef)
                    meshUsers[meshRef.GetValue<int>()]++;

            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var node = nodes[nodeIndex]?.AsObject();
                if (node == null || (!(node["mesh"] is JsonNode meshValue)))
                    continue;
                int meshIndex = meshValue.GetValue<int>();
                if ((uint)meshIndex >= (uint)meshes.Count)
                    throw new InvalidDataException(EnglishText.Text("Invalid mesh index."));
                var mesh = meshes[meshIndex].AsObject();
                var primitives = mesh["primitives"]?.AsArray() ?? throw new InvalidDataException(EnglishText.Text("Mesh is missing primitives."));
                foreach (var primitiveValue in primitives)
                {
                    var primitive = primitiveValue.AsObject();
                    if (primitive["material"] != null)
                        continue;
                    if (materials == null || materials.Count == 0)
                        throw new InvalidDataException(EnglishText.Text("Mesh without a material: assign a material in Blender."));
                    primitive["material"] = 0;
                    changes.Add(EnglishText.Format("Mesh {0}: existing material 0 was assigned.", mesh["name"]?.GetValue<string>() ?? meshIndex.ToString()));
                }

                bool transformed = node["matrix"] != null || node["translation"] != null || node["rotation"] != null || node["scale"] != null;
                if (!transformed)
                    continue;
                if (node["skin"] != null)
                    throw new InvalidDataException(EnglishText.Text("Mesh has a skin and a separate transform: apply the transform in Blender before exporting."));
                if (meshUsers[meshIndex] != 1)
                    throw new InvalidDataException(EnglishText.Text("One mesh is used by multiple transformed nodes: apply transforms in Blender."));
                if (nodes.Any(parent => parent?["children"] is JsonArray children &&
                    children.Any(child => child?.GetValue<int>() == nodeIndex &&
                        (parent?["matrix"] != null || parent?["translation"] != null || parent?["rotation"] != null || parent?["scale"] != null))))
                    throw new InvalidDataException(EnglishText.Text("Mesh has a transformed parent node: apply transforms in Blender."));
                var transform = Transform(node);
                if (!Matrix4x4.Invert(transform, out var inverse))
                    throw new InvalidDataException(EnglishText.Text("Cannot bake transform with zero scale."));
                var normalMatrix = Matrix4x4.Transpose(inverse);
                var processed = new HashSet<int>();
                foreach (var primitiveValue in primitives)
                {
                    var attributes = primitiveValue["attributes"]?.AsObject() ?? throw new InvalidDataException(EnglishText.Text("Mesh is missing attributes."));
                    foreach (var semantic in new[] { "POSITION", "NORMAL" })
                    {
                        if ((!(attributes[semantic] is JsonNode accessorValue)))
                            continue;
                        int accessorIndex = accessorValue.GetValue<int>();
                        if (!processed.Add(accessorIndex))
                            continue;
                        if ((uint)accessorIndex >= (uint)accessors.Count)
                            throw new InvalidDataException(EnglishText.Text("Invalid accessor."));
                        var accessor = accessors[accessorIndex].AsObject();
                        if (accessor["type"]?.GetValue<string>() != "VEC3" || accessor["componentType"]?.GetValue<int>() != 5126)
                            throw new InvalidDataException(EnglishText.Text("Baking transforms requires float32 VEC3 positions and normals."));
                        int viewIndex = accessor["bufferView"]?.GetValue<int>() ?? throw new InvalidDataException(EnglishText.Text("No bufferView."));
                        if ((uint)viewIndex >= (uint)views.Count)
                            throw new InvalidDataException(EnglishText.Text("Invalid bufferView."));
                        var view = views[viewIndex].AsObject();
                        int start = (view["byteOffset"]?.GetValue<int>() ?? 0) + (accessor["byteOffset"]?.GetValue<int>() ?? 0);
                        int count = accessor["count"].GetValue<int>();
                        int stride = view["byteStride"]?.GetValue<int>() ?? 12;
                        if (start < 0 || count < 0 || stride < 12 || (long)start + Math.Max(0, count - 1L) * stride + 12 > binary.Length)
                            throw new InvalidDataException(EnglishText.Text("Accessor exceeds the bounds of BIN."));
                        for (int i = 0; i < count; i++)
                        {
                            int at = start + i * stride;
                            var vector = new Vector3(ReadFloat(binary, at), ReadFloat(binary, at + 4), ReadFloat(binary, at + 8));
                            vector = semantic == "POSITION" ? Vector3.Transform(vector, transform)
                                : Vector3.Normalize(Vector3.TransformNormal(vector, normalMatrix));
                            if (!Helpers.IsFinite(vector.X) || !Helpers.IsFinite(vector.Y) || !Helpers.IsFinite(vector.Z))
                                throw new InvalidDataException(EnglishText.Text("Transform produced invalid coordinates."));
                            WriteFloat(binary, at, vector.X);
                            WriteFloat(binary, at + 4, vector.Y);
                            WriteFloat(binary, at + 8, vector.Z);
                        }
                    }
                }
                node.Remove("matrix");
                node.Remove("translation");
                node.Remove("rotation");
                node.Remove("scale");
                changes.Add(EnglishText.Format("Mesh {0}: transform baked into vertices.", mesh["name"]?.GetValue<string>() ?? meshIndex.ToString()));
            }
            if (changes.Count == 0)
                return new GlbImportNormalizer.Result(data, new string[0]);
            var json = JsonSerializer.SerializeToUtf8Bytes(root);
            int padded = (json.Length + 3) & ~3;
            var output = new byte[checked(12 + 8 + padded + 8 + binary.Length)];
            Buffer.BlockCopy(System.Text.Encoding.UTF8.GetBytes("glTF"), 0, output, 0, 4);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), output.Length);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(12), padded);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 0x4e4f534a);
            Buffer.BlockCopy(json, 0, output, 20, json.Length);
            output.AsSpan(20 + json.Length, padded - json.Length).Fill(0x20);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(20 + padded), binary.Length);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24 + padded), 0x004e4942);
            binary.CopyTo(output.AsSpan(28 + padded));
            return new GlbImportNormalizer.Result(output, changes.ToArray());
        }

        /// <summary>
        /// Repair material names in a GLB owned by a build/export stage, without touching geometry or any source project file.
        /// This also handles OAT's comma-prefixed material names and Blender's .001 duplicate-name suffix when the base
        /// material is present in the project's asset list.
        /// </summary>
        public static string[] NormalizeMaterialNamesInPlace(string file, ISet<string> knownMaterialNames = null)
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("glTF")) || Read32(data, 4) != 2 || Read32(data, 8) != data.Length)
                throw new InvalidDataException(EnglishText.Text("A valid GLB 2.0 file is required."));

            var chunks = new List<(uint Type, byte[] Data)>();
            JsonObject root = null;
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                int size = Read32(data, offset);
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
                offset += 8;
                if (size < 0 || offset + (long)size > data.Length)
                    throw new InvalidDataException(EnglishText.Text("Invalid GLB chunk length."));
                var payload = data.AsSpan(offset, size).ToArray();
                if (type == 0x4e4f534a)
                {
                    if (root != null)
                        throw new InvalidDataException(EnglishText.Text("GLB contains more than one JSON chunk."));
                    root = JsonNode.Parse(payload)?.AsObject() ?? throw new InvalidDataException(EnglishText.Text("GLB is missing its JSON object."));
                }
                chunks.Add((type, payload));
                offset += size;
            }
            if (offset != data.Length || root == null)
                throw new InvalidDataException(EnglishText.Text("GLB chunks are corrupt or JSON chunk is missing."));

            var changes = new List<string>();
            NormalizeMaterialArray(root["materials"]?.AsArray(), knownMaterialNames, changes);
            if (changes.Count == 0)
                return new string[0];

            // Rebuild only the JSON chunk and preserve every other chunk byte-for-byte.
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(root);
            int paddedJsonLength = checked((json.Length + 3) & ~3);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("glTF"));
                    writer.Write(2);
                    writer.Write(0); // Patched with the final byte count after all chunks are written.
                    foreach (var chunk in chunks)
                    {
                        if (chunk.Type == 0x4e4f534a)
                        {
                            writer.Write(paddedJsonLength);
                            writer.Write(chunk.Type);
                            writer.Write(json);
                            for (int i = json.Length; i < paddedJsonLength; i++)
                                writer.Write((byte)0x20);
                        }
                        else
                        {
                            writer.Write(chunk.Data.Length);
                            writer.Write(chunk.Type);
                            writer.Write(chunk.Data);
                        }
                    }
                }
                byte[] rewritten = stream.ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(8, 4), rewritten.Length);
                File.WriteAllBytes(file, rewritten);
                return changes.ToArray();
            }
        }

        /// <summary>Canonicalize GLB material names and report any repair for the operation log.</summary>
        static void NormalizeMaterialArray(JsonArray materials, ISet<string> knownMaterialNames, List<string> changes)
        {
            if (materials == null)
                return;
            foreach (var value in materials)
            {
                if ((!(value is JsonObject material)) || (!(material["name"] is JsonValue nameValue)) ||
                    !nameValue.TryGetValue<string>(out string original) || string.IsNullOrWhiteSpace(original))
                    continue;
                string normalized = AssetNameNormalizer.NormalizeName(original);
                if (knownMaterialNames != null && !knownMaterialNames.Contains(normalized))
                {
                    if (TryResolveUnqualifiedMaterialName(normalized, knownMaterialNames, out string qualifiedName))
                        normalized = qualifiedName;
                    else if (TryResolveBlenderDuplicate(normalized, knownMaterialNames, out string baseName))
                        normalized = baseName;
                }
                if (normalized.Split('/').Any(part => ((part == "") || (part == ".")) || (part == "..")))
                    throw new InvalidDataException("Invalid GLB material path: " + original);
                if (string.Equals(original, normalized, StringComparison.Ordinal))
                    continue;
                material["name"] = normalized;
                changes.Add($"GLB material name normalized: '{original}' -> '{normalized}'.");
            }
        }

        /// <summary>Map Blender's auto-generated .001 suffix only when the unsuffixed game material is known to exist.</summary>
        static bool TryResolveBlenderDuplicate(string name, ISet<string> knownMaterialNames, out string baseName)
        {
            baseName = "";
            int dot = name.LastIndexOf('.');
            if (dot <= 0 || name.Length - dot != 4)
                return false;
            for (int i = dot + 1; i < name.Length; i++)
                if (!char.IsDigit(name[i]))
                    return false;
            string candidate = name.Substring(0, dot);
            if (knownMaterialNames.Contains(candidate))
            {
                baseName = candidate;
                return true;
            }
            return TryResolveUnqualifiedMaterialName(candidate, knownMaterialNames, out baseName);
        }

        /// <summary>Resolve a Blender material name without its T6 folder when exactly one asset matches.</summary>
        static bool TryResolveUnqualifiedMaterialName(string name, ISet<string> knownMaterialNames, out string qualifiedName)
        {
            qualifiedName = "";
            if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
                return false;

            string matchingName = "";
            int matchCount = 0;
            foreach (string knownName in knownMaterialNames)
            {
                int separator = knownName.LastIndexOf('/');
                string leafName = separator >= 0 ? knownName.Substring(separator + 1) : knownName;
                if (!leafName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                matchingName = knownName;
                matchCount++;
                if (matchCount > 1)
                    return false;
            }

            if (matchCount != 1)
                return false;

            qualifiedName = matchingName;
            return true;
        }

        /// <summary>
        /// Resolves or reads the 32 value. Validate and normalize supported GLB node transforms and material slots.
        /// </summary>
        static int Read32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        /// <summary>
        /// Resolves or reads the Float value. Validate and normalize supported GLB node transforms and material slots.
        /// </summary>
        static float ReadFloat(byte[] data, int offset) => Helpers.Int32BitsToSingle(Read32(data, offset));
        /// <summary>
        /// Updates, serializes or converts the Float value. Validate and normalize supported GLB node transforms and material slots.
        /// </summary>
        static void WriteFloat(byte[] data, int offset, float value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), Helpers.SingleToInt32Bits(value));
        static Matrix4x4 Transform(JsonObject node)
        {
            if (node["matrix"] is JsonArray values)
            {
                if (values.Count != 16)
                    throw new InvalidDataException(EnglishText.Text("GLB matrix must have 16 values."));
                var f = values.Select(v => v.GetValue<float>()).ToArray();
                return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7],
                    f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            }
            Vector3 Vec3(JsonNode value, Vector3 fallback) => value is JsonArray a
                            ? new Vector3(a[0].GetValue<float>(), a[1].GetValue<float>(), a[2].GetValue<float>()) : fallback;
            var scale = Vec3(node["scale"], Vector3.One);
            var translation = Vec3(node["translation"], Vector3.Zero);
            var rotation = node["rotation"] is JsonArray q
                ? Quaternion.Normalize(new Quaternion(q[0].GetValue<float>(), q[1].GetValue<float>(), q[2].GetValue<float>(), q[3].GetValue<float>()))
                : Quaternion.Identity;
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        }
    }
    #endregion

    #region GlbMaterialDocument
    /// <summary>
    /// Reads only GLB JSON for navigation and rewrites material references without touching geometry,
    /// skinning, animation or unknown binary chunks. Mesh indices, not names, identify editable parts.
    /// </summary>
    internal sealed class GlbMaterialDocument
    {
        readonly JsonObject root;
        readonly int tailOffset;

        internal GlbMaterialDocument(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (stream.Length < 20 || reader.ReadUInt32() != 0x46546c67 || reader.ReadUInt32() != 2 ||
                reader.ReadUInt32() != stream.Length)
                        throw new InvalidDataException(EnglishText.Text("A valid GLB 2.0 file is required."));
                    int length = reader.ReadInt32();
                    if (reader.ReadUInt32() != 0x4e4f534a || length < 2 || length % 4 != 0 ||
                        length > 64 * 1024 * 1024 || 20L + length > stream.Length)
                        throw new InvalidDataException(EnglishText.Text("Corrupt GLB JSON chunk."));
                    root = JsonNode.Parse(Encoding.UTF8.GetString(reader.ReadBytes(length)))?.AsObject()
                        ?? throw new InvalidDataException(EnglishText.Text("GLB is missing its JSON object."));
                    tailOffset = 20 + length;
                    // Check chunk boundaries now; their contents are deliberately opaque to this editor.
                    while (stream.Position < stream.Length)
                    {
                        if (stream.Length - stream.Position < 8)
                            throw new InvalidDataException(EnglishText.Text("Incomplete GLB chunk."));
                        uint size = reader.ReadUInt32();
                        _ = reader.ReadUInt32();
                        if (size % 4 != 0 || stream.Position + size > stream.Length)
                            throw new InvalidDataException(EnglishText.Text("Invalid GLB chunk length."));
                        stream.Position += size;
                    }
                }
            }
        }

        internal IReadOnlyList<ModelMeshPart> Parts()
        {
            var meshes = root["meshes"] as JsonArray ?? throw new InvalidDataException(EnglishText.Text("GLB is missing meshes."));
            var materials = root["materials"] as JsonArray;
            var result = new List<ModelMeshPart>();
            for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
            {
                var mesh = meshes[meshIndex].AsObject();
                string name = mesh["name"]?.GetValue<string>() ?? "Mesh " + meshIndex;
                var primitives = mesh["primitives"].AsArray();
                for (int primitiveIndex = 0; primitiveIndex < primitives.Count; primitiveIndex++)
                {
                    int index = primitives[primitiveIndex]?["material"]?.GetValue<int>() ?? -1;
                    if (index < -1 || (index >= 0 && (materials == null || index >= materials.Count)))
                        throw new InvalidDataException(EnglishText.Text("GLB contains an invalid material index."));
                    string material = index < 0 ? EnglishText.Text("(without a material)") :
                        materials[index]?["name"]?.GetValue<string>() ?? "Material " + index;
                    result.Add(new ModelMeshPart(meshIndex, primitiveIndex, name, material));
                }
            }
            return result;
        }

        internal void Assign(int meshIndex, int? primitiveIndex, string materialName)
        {
            var meshes = root["meshes"].AsArray();
            if ((uint)meshIndex >= (uint)meshes.Count)
                throw new InvalidDataException(EnglishText.Text("The mesh has already changed. Refresh the tree."));
            var primitives = meshes[meshIndex]["primitives"].AsArray();
            if (primitiveIndex.HasValue && (uint)primitiveIndex.Value >= (uint)primitives.Count)
                throw new InvalidDataException(EnglishText.Text("The surface has already changed. Refresh the tree."));
            var materials = root["materials"] as JsonArray;
            if (materials == null)
                root["materials"] = materials = new JsonArray();
            int slot = materials.Count;
            // Never rename a shared slot: other primitives may still reference it. The T6 material lives
            // in materials/<name>.json; glTF PBR/embedded textures are not a replacement for that shader.
            materials.Add(new JsonObject { ["name"] = materialName });
            for (int i = 0; i < primitives.Count; i++)
                if (!primitiveIndex.HasValue || i == primitiveIndex.Value)
                    primitives[i]["material"] = slot;
        }

        internal void Save(string source, string destination)
        {
            byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
            int padded = checked((json.Length + 3) & ~3);
            using (var input = File.OpenRead(source))
            {
                input.Position = tailOffset;
                using (var output = File.Create(destination))
                {
                    using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
                    {
                        writer.Write(0x46546c67u);
                        writer.Write(2u);
                        writer.Write(checked((uint)(20L + padded + input.Length - tailOffset)));
                        writer.Write(padded);
                        writer.Write(0x4e4f534au);
                        writer.Write(json);
                        for (int i = json.Length; i < padded; i++)
                            writer.Write((byte)' ');
                        input.CopyTo(output);
                    }
                }
            }
        }
    }
    #endregion

    #region MeshMaterialService
    /// <summary>A mesh primitive is one independently assignable material surface in a GLB.</summary>
    public sealed class ModelMeshPart { public int MeshIndex { get; set; } public int PrimitiveIndex { get; set; } public string MeshName { get; set; } public string MaterialName { get; set; } public ModelMeshPart(int MeshIndex, int PrimitiveIndex, string MeshName, string MaterialName) { this.MeshIndex = MeshIndex; this.PrimitiveIndex = PrimitiveIndex; this.MeshName = MeshName; this.MaterialName = MaterialName; } }
    /// <summary>Project-relative geometry path; it always resolves edits before the exported original.</summary>
    public sealed class ModelLodFile { public int Index { get; set; } public string RelativePath { get; set; } public ModelLodFile(int Index, string RelativePath) { this.Index = Index; this.RelativePath = RelativePath; } }
    /// <summary>An exported T6 template and the semantics supported by its shader.</summary>
    public sealed class MeshMaterialTemplate
    {
        public string Name { get; set; }
        public string File { get; set; }
        public string Technique { get; set; }
        public string[] Semantics { get; set; }
        public MeshMaterialTemplate(string Name, string File, string Technique, string[] Semantics) { this.Name = Name; this.File = File; this.Technique = Technique; this.Semantics = Semantics; }
        public override string ToString() => Name + " · " + Technique;
    }
    /// <summary>A project texture that can be selected without knowing its absolute path.</summary>
    public sealed class ProjectTexture
    {
        public string Name { get; set; }
        public string File { get; set; }
        public ProjectTexture(string Name, string File) { this.Name = Name; this.File = File; }
        public override string ToString() => Name;
    }
    /// <summary>
    /// Assignment targets one LOD and mesh; null PrimitiveIndex means all surfaces of that mesh.
    /// TextureFiles maps T6 semantics to PNG/DDS/IWI paths. Empty values keep the template's map.
    /// </summary>
    public sealed class MeshMaterialAssignment { public string RelativeGlb { get; set; } public int MeshIndex { get; set; } public int? PrimitiveIndex { get; set; } public string MaterialName { get; set; } public string TemplateFile { get; set; } public IReadOnlyDictionary<string, string> TextureFiles { get; set; } public MeshMaterialAssignment(string RelativeGlb, int MeshIndex, int? PrimitiveIndex, string MaterialName, string TemplateFile, IReadOnlyDictionary<string, string> TextureFiles) { this.RelativeGlb = RelativeGlb; this.MeshIndex = MeshIndex; this.PrimitiveIndex = PrimitiveIndex; this.MaterialName = MaterialName; this.TemplateFile = TemplateFile; this.TextureFiles = TextureFiles; } }

    /// <summary>
    /// Project-level material workflow, independent of window controls. The exported asset tree is
    /// immutable: new materials, DDS textures and the edited GLB are committed under edits only.
    /// </summary>
    internal sealed class MeshMaterialService
    {
        /// <summary>Use the XModel LOD table, rather than guessing LOD ownership from filenames.</summary>
        public IReadOnlyList<ModelLodFile> Lods(WorkbenchProject project, string modelName)
        {
            var descriptor = Effective(project, "xmodel/" + modelName + ".json");
            using (var json = JsonDocument.Parse(File.ReadAllText(descriptor)))
            {
                var result = new List<ModelLodFile>();
                int index = 0;
                foreach (var lod in json.RootElement.GetProperty("lods").EnumerateArray())
                {
                    string relative = lod.TryGetProperty("file", out var file) ? file.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(relative) && relative.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = project.AssetPath(relative); // Reject rooted/escaping paths before exposing them to the UI.
                        result.Add(new ModelLodFile(index, relative.Replace('\\', '/')));
                    }
                    index++;
                }
                return result;
            }
        }

        /// <summary>Lazy tree expansion reads JSON metadata, not the potentially large vertex buffer.</summary>
        public IReadOnlyList<ModelMeshPart> Parts(WorkbenchProject project, string relativeGlb) =>
            new GlbMaterialDocument(Effective(project, relativeGlb)).Parts();

        /// <summary>Return effective templates with a color map; optional maps follow the chosen shader.</summary>
        public IReadOnlyList<MeshMaterialTemplate> Templates(WorkbenchProject project)
        {
            var result = new List<MeshMaterialTemplate>();
            foreach (var pair in Files(project, "materials", ".json"))
            {
                try
                {
                    var json = JsonNode.Parse(File.ReadAllText(pair.Value)).AsObject();
                    var technique = json["techniqueSet"]?.GetValue<string>();
                    var semantics = (json["textures"] as JsonArray)?.OfType<JsonObject>()
                        .Select(t => t["semantic"]?.GetValue<string>()).Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? new string[0];
                    if (string.IsNullOrWhiteSpace(technique) || !semantics.Contains("colorMap", StringComparer.OrdinalIgnoreCase))
                        continue;
                    result.Add(new MeshMaterialTemplate(pair.Key, pair.Value, technique, semantics));
                }
                catch (Exception ex) when (((ex is System.Text.Json.JsonException) || (ex is System.InvalidOperationException)) || (ex is System.FormatException)) { }
            }
            return result.OrderBy(t => t.Technique.StartsWith("mc_sw4_3d_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>List DDS/IWI/PNG choices, preferring edited versions over the project source.</summary>
        public IReadOnlyList<ProjectTexture> Textures(WorkbenchProject project) =>
            Files(project, "images", ".dds", ".iwi", ".png").Select(p => new ProjectTexture(p.Key, p.Value))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>
        /// Prepare every output before committing. A fresh material identity avoids changing other meshes
        /// that share the old material. A failed/cancelled conversion leaves the current assignment intact.
        /// </summary>
        public async Task<OperationResult> ApplyAsync(WorkbenchProject project, MeshMaterialAssignment assignment,
            string binDirectory, Action<string> log = null, CancellationToken cancellation = default)
        {
            TextureModule.ValidateMaterialName(assignment.MaterialName);
            if (!assignment.RelativeGlb.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(EnglishText.Text("Assignment is only supported for GLB."));
            string materialRelative = "materials/" + assignment.MaterialName + ".json";
            string materialTarget = project.EditPath(materialRelative);
            var known = AssetNameNormalizer.ReadMaterialNames(project.Inventory, project.Zone);
            if (known.Contains(assignment.MaterialName) || File.Exists(project.AssetPath(materialRelative)) || File.Exists(materialTarget))
                throw new InvalidDataException(EnglishText.Text("This material name already exists. Choose a new name to change only the selected part."));
            string source = Effective(project, assignment.RelativeGlb);
            string sourceHash = Workspace.Hash(source);
            var document = new GlbMaterialDocument(source);
            document.Assign(assignment.MeshIndex, assignment.PrimitiveIndex, assignment.MaterialName);
            // Validate the chosen template before launching any converter.
            _ = TextureModule.CreateMaterial(assignment.TemplateFile, new Dictionary<string, string>());
            string stage = Path.Combine(project.Builds, ".mesh-material-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var prepared = new List<(string Source, string Target)>();
            var installed = new List<string>();
            bool committed = false;
            try
            {
                var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                int count = 0;
                foreach (var pair in assignment.TextureFiles.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    string imageName = assignment.MaterialName.Replace('/', '_') + "_" + pair.Key;
                    TextureModule.ValidateMaterialName(imageName);
                    string relative = "images/" + imageName + ".dds";
                    string target = project.EditPath(relative);
                    if (File.Exists(target) || File.Exists(project.AssetPath(relative)) ||
                        File.Exists(Path.ChangeExtension(target, ".iwi")) || File.Exists(project.AssetPath("images/" + imageName + ".iwi")))
                        throw new IOException(EnglishText.Text("Texture name is already in use: ") + imageName + EnglishText.Text(". Choose another material name."));
                    string textureStage = Path.Combine(stage, (count++).ToString());
                    await TextureModule.ConvertAsync(new string[] { pair.Value }, binDirectory, textureStage, "DDS", "AUTO", log, cancellation);
                    prepared.Add((Path.Combine(textureStage, Path.GetFileNameWithoutExtension(pair.Value) + ".dds"), target));
                    images.Add(pair.Key, imageName);
                }
                string material = TextureModule.CreateMaterial(assignment.TemplateFile, images);
                string stagedMaterial = Path.Combine(stage, "material.json");
                File.WriteAllText(stagedMaterial, material, new UTF8Encoding(false));
                prepared.Add((stagedMaterial, materialTarget));
                string stagedGlb = Path.Combine(stage, "model.glb");
                document.Save(source, stagedGlb);
                // An external Blender export while this dialog was open must not silently lose edits.
                if (Workspace.Hash(Effective(project, assignment.RelativeGlb)) != sourceHash)
                    throw new IOException(EnglishText.Text("GLB changed during material assignment. Refresh the tree and try again."));
                cancellation.ThrowIfCancellationRequested();
                foreach (var pair in prepared)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pair.Target));
                    File.Move(pair.Source, pair.Target); // No overwrite: all generated asset names are private.
                    installed.Add(pair.Target);
                }
                string modelTarget = project.EditPath(assignment.RelativeGlb);
                Directory.CreateDirectory(Path.GetDirectoryName(modelTarget));
                // Commit the GLB last, atomically on the project volume. No model can reference half-created files.
                if (File.Exists(modelTarget))
                    File.Replace(stagedGlb, modelTarget, null);
                else
                    File.Move(stagedGlb, modelTarget);
                committed = true;
                log?.Invoke($"Material assignment: {assignment.RelativeGlb}, mesh {assignment.MeshIndex}, " +
                    $"surface {assignment.PrimitiveIndex?.ToString() ?? "all"} -> {assignment.MaterialName}");
                return new OperationResult(project.Edits, EnglishText.Text("Material assigned: ") + assignment.MaterialName, new string[0]);
            }
            finally
            {
                if (!committed)
                    foreach (string file in installed.AsEnumerable().Reverse())
                        File.Delete(file);
                // Only this operation's uniquely named scratch directory is removed.
                try
                {
                    Directory.Delete(stage, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Resolve the latest edit without modifying the original exported asset.</summary>
        public static string Effective(WorkbenchProject project, string relative)
        {
            string edited = project.EditPath(relative);
            return File.Exists(edited) ? edited : project.AssetPath(relative);
        }

        static Dictionary<string, string> Files(WorkbenchProject project, string folder, params string[] extensions)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string parent in new[] { project.Dependencies, project.Assets, project.Edits })
            {
                string root = Path.Combine(parent, folder);
                if (!Directory.Exists(root))
                    continue;
                // Prefer DDS over IWI within one layer; the outer loop always gives edits precedence.
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(file => file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                    result[Path.ChangeExtension(Helpers.GetRelativePath(root, file), null).Replace('\\', '/')] = file;
            }
            return result;
        }
    }
    #endregion

    #region GlbHelpers
    /// <summary>GLB and model helpers used by the public GlbModule facade.</summary>
    internal static class GlbHelpers
    {
        internal static GlbPreviewResult LoadPreview(string path, Func<string, BitmapSource> resolver,
            CancellationToken cancellation = default) =>
            GlbPreviewLoader.Load(path, resolver, cancellation);

        internal static GlbImportResult NormalizeImport(string file, ISet<string> knownMaterialNames)
        {
            var result = GlbImportNormalizer.Normalize(file, knownMaterialNames);
            return new GlbImportResult(result.Bytes, result.Changes);
        }

        internal static string[] NormalizeMaterialNamesInPlace(string file, ISet<string> knownMaterialNames) =>
            GlbImportNormalizer.NormalizeMaterialNamesInPlace(file, knownMaterialNames);

        internal static string Compare(string first, string second,
            Action<string, double> measurement, bool measureOnly) =>
            GlbComparer.Compare(first, second, measurement, measureOnly);

        internal static string CompareEditedGeometry(string edited, string rebuilt) =>
            GlbComparer.CompareEditedGeometry(edited, rebuilt);

        internal static string CompareWithAddedMesh(string edited, string roundtrip) =>
            GlbComparer.CompareWithAddedMesh(edited, roundtrip);

        internal static bool HasFloatingTestAddition(string file) => GlbComparer.HasFloatingTestAddition(file);
        internal static void EditFirstPosition(string file, float amount) => GlbComparer.EditFirstPosition(file, amount);
    }
    #endregion
}
