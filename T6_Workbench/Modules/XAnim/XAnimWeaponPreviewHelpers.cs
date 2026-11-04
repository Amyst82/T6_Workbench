using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace T6_Workbench
{
    /// <summary>Transform that moves a weapon's grip anchor into a selected animated hand bone.</summary>
    internal sealed class WeaponGripAlignment 
    { 
        public Matrix4x4 Correction { get; set; } 
        public string AnchorName { get; set; } 
        public bool HasCustomGripAnchor { get; set; } 
        public WeaponGripAlignment(Matrix4x4 Correction, string AnchorName, bool HasCustomGripAnchor) 
        { 
            this.Correction = Correction; 
            this.AnchorName = AnchorName; 
            this.HasCustomGripAnchor = HasCustomGripAnchor;   
        } 
    }
    internal sealed class WeaponPreviewAttachment 
    { 
        public Matrix4x4 Correction { get; set; } 
        public string AnchorName { get; set; } 
        public bool HasCustomGripAnchor { get; set; } 
        public int ParentNodeIndex { get; set; } 
        public string ParentNodeName { get; set; } 
        public string HandSide { get; set; } 
        public float ReferenceTime { get; set; } 
        public bool HasHandTarget { get; set; } 
        public WeaponPreviewAttachment(Matrix4x4 Correction, string AnchorName, bool HasCustomGripAnchor, int ParentNodeIndex, string ParentNodeName, string HandSide, float ReferenceTime, bool HasHandTarget) 
        { 
            this.Correction = Correction; 
            this.AnchorName = AnchorName; 
            this.HasCustomGripAnchor = HasCustomGripAnchor; 
            this.ParentNodeIndex = ParentNodeIndex; 
            this.ParentNodeName = ParentNodeName; 
            this.HandSide = HandSide; 
            this.ReferenceTime = ReferenceTime; 
            this.HasHandTarget = HasHandTarget; 
        } 
    }

    /// <summary>Small GLB transform helpers used when combining XAnim hands and a weapon model.</summary>
    internal static class XAnimWeaponPreviewHelpers
    {
        sealed class AnimationChannelData 
        { 
            public int NodeIndex { get; set; } 
            public string Path { get; set; } 
            public float[][] Times { get; set; } 
            public float[][] Values { get; set; } 
            public string Interpolation { get; set; } 
            public AnimationChannelData(int NodeIndex, string Path, float[][] Times, float[][] Values, string Interpolation) 
            { 
                this.NodeIndex = NodeIndex; 
                this.Path = Path; 
                this.Times = Times; 
                this.Values = Values; 
                this.Interpolation = Interpolation; 
            } 
        }
        sealed class AnimationPose 
        { 
            public Dictionary<(int NodeIndex, string Path), float[]> Values { get; set; } 
            public float Time { get; set; } 
            public AnimationPose(Dictionary<(int NodeIndex, string Path), float[]> Values, float Time) 
            { 
                this.Values = Values; this.Time = Time; 
            } 
        }

        static readonly string[] GripAnchorNames =
        Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "workbench_weapon_grip" }, new string[] { "weapon_grip" })), new string[] { "grip_point" })), new string[] { "hand_grip" })), new string[] { "tag_weapon" })), new string[] { "tag_weapon_mount"}));

        /// <summary>Find a semantic grip pivot and return the inverse transform needed to mount it on tag_weapon.</summary>
        internal static WeaponGripAlignment FindGripAlignment(JsonObject weapon, JsonArray sceneRoots)
        {
            if ((!(weapon["nodes"] is JsonArray nodes)) || nodes.Count == 0)
                throw new InvalidDataException(EnglishText.Text("Weapon GLB has no nodes to determine the grip."));

            Dictionary<int, Matrix4x4> sceneTransforms = ReadSceneTransforms(nodes, sceneRoots);
            foreach (string anchorName in GripAnchorNames)
            {
                int[] matches = Enumerable.Range(0, nodes.Count)
                    .Where(nodeIndex => sceneTransforms.ContainsKey(nodeIndex)
                        && string.Equals((string)nodes[nodeIndex]?["name"], anchorName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                    continue;

                int anchorIndex = SelectAnchor(matches, weapon);
                if (!Matrix4x4.Invert(sceneTransforms[anchorIndex], out Matrix4x4 correction))
                    throw new InvalidDataException(EnglishText.Text("Weapon grip point has a non-invertible transform: ") + anchorName);
                return new WeaponGripAlignment(correction, anchorName, true);
            }

            // j_gun is a useful T6 attachment pivot, but it is not guaranteed to sit at the hand's grip point.
            int[] gunPivots = Enumerable.Range(0, nodes.Count)
                .Where(nodeIndex => sceneTransforms.ContainsKey(nodeIndex) && string.Equals((string)nodes[nodeIndex]?["name"], "j_gun", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (gunPivots.Length > 0)
            {
                int gunPivotIndex = SelectAnchor(gunPivots, weapon);
                if (!Matrix4x4.Invert(sceneTransforms[gunPivotIndex], out Matrix4x4 gunCorrection))
                    throw new InvalidDataException(EnglishText.Text("Bone j_gun has a non-invertible transform."));
                return new WeaponGripAlignment(gunCorrection, "j_gun", false);
            }

            // Keep the GLB's original scene-root placement when it has no attachment or grip pivot.
            return new WeaponGripAlignment(Matrix4x4.Identity, "scene root", false);
        }

        /// <summary>Fit the weapon pivot to the animated palm while preserving the weapon mount's facing.</summary>
        internal static WeaponPreviewAttachment FindHandAttachment(JsonObject hands, byte[] handsBinary,
            JsonObject weapon, JsonArray weaponSceneRoots, string weaponPath, int mountNodeIndex)
        {
            WeaponGripAlignment grip = FindGripAlignment(weapon, weaponSceneRoots);
            string side = IsLeftHandWeapon(weaponPath) ? "le" : "ri";
            JsonArray handsNodes = RequireArray(hands, "nodes");
            int targetNodeIndex = FindNode(handsNodes, "j_webbing_" + side);
            if (targetNodeIndex < 0)
                targetNodeIndex = FindNode(handsNodes, "j_wrist_" + side);

            JsonArray handsScenes = RequireArray(hands, "scenes");
            int handsSceneIndex = (int?)hands["scene"] ?? 0;
            if ((uint)handsSceneIndex >= handsScenes.Count)
                throw new InvalidDataException(EnglishText.Text("Hands GLB has no active scene."));
            JsonArray handsSceneRoots = RequireArray(handsScenes[handsSceneIndex].AsObject(), "nodes");
            AnimationPose pose = ReadFinalAnimationPose(hands, handsBinary);
            Dictionary<int, Matrix4x4> handsTransforms = ReadSceneTransforms(handsNodes, handsSceneRoots, pose.Values);

            if (targetNodeIndex < 0 || !handsTransforms.TryGetValue(targetNodeIndex, out Matrix4x4 handWorld))
                return new WeaponPreviewAttachment(grip.Correction, grip.AnchorName, grip.HasCustomGripAnchor,
                    mountNodeIndex, "tag_weapon", side, pose.Time, false);

            if (!handsTransforms.TryGetValue(mountNodeIndex, out Matrix4x4 mountWorld)
                || !Matrix4x4.Decompose(mountWorld, out Vector3 mountScale, out Quaternion mountRotation, out Vector3 mountTranslation)
                || !Matrix4x4.Invert(handWorld, out Matrix4x4 inverseHandWorld))
                return new WeaponPreviewAttachment(grip.Correction, grip.AnchorName, grip.HasCustomGripAnchor,
                    mountNodeIndex, "tag_weapon", side, pose.Time, false);

            // Keep the engine mount's facing but move the grip point to the palm in the final XAnim pose.
            Matrix4x4 desiredGripWorld = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(mountRotation))
                * Matrix4x4.CreateTranslation(handWorld.M41, handWorld.M42, handWorld.M43);
            Matrix4x4 handRelativeGrip = desiredGripWorld * inverseHandWorld;
            Matrix4x4 correction = grip.Correction * handRelativeGrip;
            string parentName = (string)handsNodes[targetNodeIndex]?["name"] ?? "j_webbing_" + side;
            return new WeaponPreviewAttachment(correction, grip.AnchorName, grip.HasCustomGripAnchor,
                targetNodeIndex, parentName, side, pose.Time, true);
        }

        /// <summary>Convert a row-vector System.Numerics matrix into glTF's column-major matrix array.</summary>
        internal static JsonArray ToGlbMatrix(Matrix4x4 matrix) => new JsonArray(
            JsonValue.Create(matrix.M11), JsonValue.Create(matrix.M12), JsonValue.Create(matrix.M13), JsonValue.Create(matrix.M14),
            JsonValue.Create(matrix.M21), JsonValue.Create(matrix.M22), JsonValue.Create(matrix.M23), JsonValue.Create(matrix.M24),
            JsonValue.Create(matrix.M31), JsonValue.Create(matrix.M32), JsonValue.Create(matrix.M33), JsonValue.Create(matrix.M34),
            JsonValue.Create(matrix.M41), JsonValue.Create(matrix.M42), JsonValue.Create(matrix.M43), JsonValue.Create(matrix.M44));

        /// <summary>Resolve duplicate marker names by preferring a skin's declared skeleton root.</summary>
        static int SelectAnchor(int[] matches, JsonObject weapon)
        {
            if (matches.Length == 1)
                return matches[0];

            int[] skeletonMatches = (weapon["skins"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Where(skin => skin["skeleton"] != null)
                .Select(skin => skin["skeleton"].GetValue<int>())
                .Where(matches.Contains)
                .Distinct()
                .ToArray();
            if (skeletonMatches.Length == 1)
                return skeletonMatches[0];

            throw new InvalidDataException(EnglishText.Text("Weapon GLB has multiple grip nodes with the same name. Keep one pivot node named grip or j_gun."));
        }

        /// <summary>Build each node's transform in active-scene space using its parent chain.</summary>
        static Dictionary<int, Matrix4x4> ReadSceneTransforms(JsonArray nodes, JsonArray sceneRoots) => ReadSceneTransforms(nodes, sceneRoots, new Dictionary<(int NodeIndex, string Path), float[]>());

        static Dictionary<int, Matrix4x4> ReadSceneTransforms(JsonArray nodes, JsonArray sceneRoots,
            Dictionary<(int NodeIndex, string Path), float[]> poseValues)
        {
            var transforms = new Dictionary<int, Matrix4x4>();
            var activePath = new HashSet<int>();
            foreach (JsonNode sceneRoot in sceneRoots)
            {
                if (sceneRoot == null)
                    throw new InvalidDataException("GLB active scene contains an empty root-node reference.");
                ReadNodeTransform(sceneRoot.GetValue<int>(), Matrix4x4.Identity, nodes, transforms, activePath, poseValues);
            }
            return transforms;
        }

        /// <summary>Walk one scene branch and reject invalid node references or cycles.</summary>
        static void ReadNodeTransform(int nodeIndex, Matrix4x4 parentTransform, JsonArray nodes,
            Dictionary<int, Matrix4x4> transforms, HashSet<int> activePath,
            Dictionary<(int NodeIndex, string Path), float[]> poseValues)
        {
            if ((uint)nodeIndex >= nodes.Count || (!(nodes[nodeIndex] is JsonObject node)))
                throw new InvalidDataException("GLB scene references an invalid weapon node.");
            if (!activePath.Add(nodeIndex))
                throw new InvalidDataException("GLB weapon node hierarchy contains a cycle.");
            if (transforms.ContainsKey(nodeIndex))
                throw new InvalidDataException("GLB weapon node is referenced more than once in its scene.");

            Matrix4x4 worldTransform = ReadLocalTransform(nodeIndex, node, poseValues) * parentTransform;
            transforms.Add(nodeIndex, worldTransform);
            if (node["children"] is JsonArray children)
            {
                foreach (JsonNode child in children)
                {
                    if (child == null)
                    {
                        throw new InvalidDataException("GLB weapon node contains an empty child reference.");
                    }
                    ReadNodeTransform(child.GetValue<int>(), worldTransform, nodes, transforms, activePath, poseValues);
                }
            }
            activePath.Remove(nodeIndex);
        }

        /// <summary>Read either a glTF matrix or its translation/rotation/scale representation.</summary>
        static Matrix4x4 ReadLocalTransform(int nodeIndex, JsonObject node,
            Dictionary<(int NodeIndex, string Path), float[]> poseValues)
        {
            if (node["matrix"] is JsonArray matrix)
            {
                if (matrix.Count != 16)
                    throw new InvalidDataException("GLB weapon node matrix must contain 16 values.");
                if (poseValues.Keys.Any(key => key.NodeIndex == nodeIndex))
                    throw new InvalidDataException(EnglishText.Text("GLB animation modifies a node defined by a matrix; this track cannot be safely applied."));
                return new Matrix4x4(
                    Number(matrix[0]), Number(matrix[1]), Number(matrix[2]), Number(matrix[3]),
                    Number(matrix[4]), Number(matrix[5]), Number(matrix[6]), Number(matrix[7]),
                    Number(matrix[8]), Number(matrix[9]), Number(matrix[10]), Number(matrix[11]),
                    Number(matrix[12]), Number(matrix[13]), Number(matrix[14]), Number(matrix[15]));
            }

            Vector3 translation = poseValues.TryGetValue((nodeIndex, "translation"), out float[] animatedTranslation)
                ? ReadVector3(animatedTranslation)
                : ReadVector3(node["translation"] as JsonArray, Vector3.Zero);
            Vector3 scale = poseValues.TryGetValue((nodeIndex, "scale"), out float[] animatedScale)
                ? ReadVector3(animatedScale)
                : ReadVector3(node["scale"] as JsonArray, Vector3.One);
            Quaternion rotation = poseValues.TryGetValue((nodeIndex, "rotation"), out float[] animatedRotation)
                ? ReadQuaternion(animatedRotation)
                : ReadQuaternion(node["rotation"] as JsonArray);
            return Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(translation);
        }

        /// <summary>Read a glTF vector, or use the supplied default when the property is omitted.</summary>
        static Vector3 ReadVector3(JsonArray values, Vector3 fallback)
        {
            if (values == null)
                return fallback;
            if (values.Count != 3)
                throw new InvalidDataException("GLB weapon vector must contain three values.");
            return new Vector3(Number(values[0]), Number(values[1]), Number(values[2]));
        }

        static Vector3 ReadVector3(float[] values)
        {
            if (values.Length != 3)
                throw new InvalidDataException("GLB animated vector must contain three values.");
            return new Vector3(values[0], values[1], values[2]);
        }

        /// <summary>Read and normalize a glTF quaternion, defaulting to identity rotation.</summary>
        static Quaternion ReadQuaternion(JsonArray values)
        {
            if (values == null)
                return Quaternion.Identity;
            if (values.Count != 4)
                throw new InvalidDataException("GLB weapon quaternion must contain four values.");
            var rotation = new Quaternion(Number(values[0]), Number(values[1]), Number(values[2]), Number(values[3]));
            if (rotation.LengthSquared() < 0.000001f)
                throw new InvalidDataException("GLB weapon quaternion is zero length.");
            return Quaternion.Normalize(rotation);
        }

        static Quaternion ReadQuaternion(float[] values)
        {
            if (values.Length != 4)
                throw new InvalidDataException("GLB animated quaternion must contain four values.");
            var rotation = new Quaternion(values[0], values[1], values[2], values[3]);
            if (rotation.LengthSquared() < 0.000001f)
                throw new InvalidDataException("GLB animated quaternion is zero length.");
            return Quaternion.Normalize(rotation);
        }

        /// <summary>Read the final keyed pose from the last animation, which is the XAnim just exported.</summary>
        static AnimationPose ReadFinalAnimationPose(JsonObject root, byte[] binary)
        {
            if ((!(root["animations"] is JsonArray animations)) || animations.Count == 0)
                return new AnimationPose(new Dictionary<(int NodeIndex, string Path), float[]>(), 0);

            JsonObject animation = animations.OfType<JsonObject>().Last();
            JsonArray samplers = RequireArray(animation, "samplers");
            JsonArray channels = RequireArray(animation, "channels");
            var channelData = new List<AnimationChannelData>();
            float referenceTime = 0;
            foreach (JsonObject channel in channels.OfType<JsonObject>())
            {
                if ((!(channel["target"] is JsonObject target)) || target["node"] == null || target["path"] == null)
                    continue;
                int samplerIndex = channel["sampler"]?.GetValue<int>() ?? -1;
                if ((uint)samplerIndex >= samplers.Count || (!(samplers[samplerIndex] is JsonObject sampler
))
                    || sampler["input"] == null || sampler["output"] == null)
                    continue;

                float[][] times = ReadFloatAccessor(root, binary, sampler["input"].GetValue<int>());
                float[][] values = ReadFloatAccessor(root, binary, sampler["output"].GetValue<int>());
                if (times.Length == 0 || values.Length == 0)
                    continue;
                referenceTime = Math.Max(referenceTime, times[times.Length - 1][0]);
                channelData.Add(new AnimationChannelData(target["node"].GetValue<int>(),
                    target["path"].GetValue<string>(), times, values,
                    (string)sampler["interpolation"] ?? "LINEAR"));
            }

            var poseValues = new Dictionary<(int NodeIndex, string Path), float[]>();
            foreach (AnimationChannelData channel in channelData)
                poseValues[(channel.NodeIndex, channel.Path)] = SampleChannel(channel, referenceTime);
            return new AnimationPose(poseValues, referenceTime);
        }

        /// <summary>Read a float accessor from the GLB binary chunk.</summary>
        static float[][] ReadFloatAccessor(JsonObject root, byte[] binary, int accessorIndex)
        {
            JsonArray accessors = RequireArray(root, "accessors");
            JsonArray views = RequireArray(root, "bufferViews");
            if ((uint)accessorIndex >= accessors.Count || (!(accessors[accessorIndex] is JsonObject accessor)))
                throw new InvalidDataException("GLB animation references an invalid accessor.");
            if ((int?)accessor["componentType"] != 5126)
                throw new NotSupportedException(EnglishText.Text("Only float accessors are supported for an XAnim pose."));

            int componentCount;
            switch ((string)accessor["type"])
            {
                case "SCALAR":
                    componentCount = 1;
                    break;
                case "VEC3":
                    componentCount = 3;
                    break;
                case "VEC4":
                    componentCount = 4;
                    break;
                default:
                    throw new NotSupportedException(EnglishText.Text("Unsupported GLB animation accessor type."));
            }
            if (accessor["bufferView"] == null)
                throw new NotSupportedException(EnglishText.Text("GLB animation accessor without bufferView is not yet supported."));
            int viewIndex = accessor["bufferView"].GetValue<int>();
            if ((uint)viewIndex >= views.Count || (!(views[viewIndex] is JsonObject view)))
                throw new InvalidDataException("GLB animation references an invalid bufferView.");
            if ((int?)view["buffer"] is int bufferIndex && bufferIndex != 0)
                throw new NotSupportedException("GLB animation uses more than one binary buffer.");

            int count = accessor["count"]?.GetValue<int>() ?? 0;
            int stride = (int?)view["byteStride"] ?? componentCount * sizeof(float);
            int start = ((int?)view["byteOffset"] ?? 0) + ((int?)accessor["byteOffset"] ?? 0);
            if (count < 0 || stride < componentCount * sizeof(float) || start < 0)
                throw new InvalidDataException("GLB animation accessor has invalid bounds.");
            var result = new float[count][];
            for (int itemIndex = 0; itemIndex < count; itemIndex++)
            {
                result[itemIndex] = new float[componentCount];
                for (int componentIndex = 0; componentIndex < componentCount; componentIndex++)
                {
                    int offset = checked(start + itemIndex * stride + componentIndex * sizeof(float));
                    if ((uint)(offset + sizeof(float)) > binary.Length)
                        throw new InvalidDataException("GLB animation accessor extends beyond its binary chunk.");
                    float value = BitConverter.ToSingle(binary, offset);
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        throw new InvalidDataException("GLB animation contains a non-finite value.");
                    result[itemIndex][componentIndex] = value;
                }
            }
            return result;
        }

        /// <summary>Sample one animation channel at the final XAnim key time.</summary>
        static float[] SampleChannel(AnimationChannelData channel, float time)
        {
            if (channel.Values.Length == 1 || time <= channel.Times[0][0])
                return (float[])channel.Values[0].Clone();
            int lastIndex = channel.Times.Length - 1;
            if (time >= channel.Times[lastIndex][0])
                return (float[])channel.Values[Math.Min(lastIndex, channel.Values.Length - 1)].Clone();

            int lowerIndex = 0;
            while (lowerIndex + 1 < channel.Times.Length && channel.Times[lowerIndex + 1][0] < time)
                lowerIndex++;
            int upperIndex = lowerIndex + 1;
            if (upperIndex >= channel.Values.Length || channel.Interpolation == "STEP")
                return (float[])channel.Values[Math.Min(lowerIndex, channel.Values.Length - 1)].Clone();

            float lowerTime = channel.Times[lowerIndex][0];
            float upperTime = channel.Times[upperIndex][0];
            float amount = upperTime > lowerTime ? (time - lowerTime) / (upperTime - lowerTime) : 1;
            float[] lower = channel.Values[lowerIndex];
            float[] upper = channel.Values[upperIndex];
            if (channel.Path == "rotation" && lower.Length == 4 && upper.Length == 4)
            {
                Quaternion rotation = Quaternion.Slerp(
                    new Quaternion(lower[0], lower[1], lower[2], lower[3]),
                    new Quaternion(upper[0], upper[1], upper[2], upper[3]), amount);
                rotation = Quaternion.Normalize(rotation);
                return Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new float[] { rotation.X }, new float[] { rotation.Y })), new float[] { rotation.Z })), new float[] { rotation.W }));
            }

            return lower.Select((value, index) => value + (upper[index] - value) * amount).ToArray();
        }

        static int FindNode(JsonArray nodes, string name)
        {
            for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                if (string.Equals((string)nodes[nodeIndex]?["name"], name, StringComparison.OrdinalIgnoreCase))
                {
                    return nodeIndex;
                }
            }
            return -1;
        }

        static bool IsLeftHandWeapon(string weaponPath)
        {
            string name = Path.GetFileNameWithoutExtension(weaponPath);
            return name.IndexOf("_lh", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_left", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static JsonArray RequireArray(JsonObject value, string key)
        {
            if (value[key] is JsonArray result)
                return result;
            throw new InvalidDataException("GLB is missing required array: " + key);
        }

        /// <summary>Read one JSON number as a finite single-precision transform component.</summary>
        static float Number(JsonNode value)
        {
            if (value == null)
                throw new InvalidDataException("GLB weapon transform contains a missing number.");
            double number = value.GetValue<double>();
            if (double.IsNaN(number) || double.IsInfinity(number) || number > float.MaxValue || number < -float.MaxValue)
                throw new InvalidDataException("GLB weapon transform contains a non-finite number.");
            return (float)number;
        }
    }
}
