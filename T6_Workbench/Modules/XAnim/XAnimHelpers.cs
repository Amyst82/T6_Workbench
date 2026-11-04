using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace T6_Workbench
{
    /// <summary>Private codec and file helpers used by <see cref="XAnimModule"/>.</summary>
    internal static class XAnimHelpers
    {
        const ushort T6Version = 19;
        const byte LoopedFlag = 0x01;
        const byte DeltaFlag = 0x02;
        const byte Delta3DFlag = 0x04;
        const byte StreamableFlag = 0x08;
        const byte T6CompatFlag = 0x80;
        // OAT reconstructs the omitted quaternion component from 0x3FFF0001 = 32767².
        const float QuaternionScale = 32767.0f;

        sealed class NativeClip
        {
            internal byte[] Prefix = new byte[0];
            internal byte[] NotifyData = new byte[0];
            internal byte[] FlipMask = new byte[0];
            internal byte[] HalfMask = new byte[0];
            internal List<BoneTrack> Bones = new List<XAnimHelpers.BoneTrack>(new XAnimHelpers.BoneTrack[0]);
            internal int LoopFrameCount;
            internal int FrameRate;
            internal bool Looped;
            internal byte Flags;
            internal ushort HeaderFrameCount;
            internal int BoneCount => Bones.Count;
        }

        sealed class BoneTrack
        {
            internal string Name = "";
            internal byte[] RawQuaternion = new byte[0];
            internal byte[] RawTranslation = new byte[0];
            internal bool HasQuaternion;
            internal bool HasTranslation;
            internal bool HalfQuaternion;
            internal bool FlipQuaternion;
            internal int[] QuaternionIndices = new int[0];
            internal Quaternion[] Quaternions = new Quaternion[0];
            internal int[] TranslationIndices = new int[0];
            internal Vector3[] Translations = new Vector3[0];
        }

        sealed class GlbFile { public JsonObject Root { get; set; } public byte[] Binary { get; set; } public GlbFile(JsonObject Root, byte[] Binary) { this.Root = Root; this.Binary = Binary; } }
        sealed class AnimationChannel { public int BoneNode { get; set; } public string BoneName { get; set; } public string Path { get; set; } public float[] Times { get; set; } public float[][] Values { get; set; } public string Interpolation { get; set; } public AnimationChannel(int BoneNode, string BoneName, string Path, float[] Times, float[][] Values, string Interpolation) { this.BoneNode = BoneNode; this.BoneName = BoneName; this.Path = Path; this.Times = Times; this.Values = Values; this.Interpolation = Interpolation; } }

        /// <summary>Rank model GLBs by exact joint-name coverage for all animated bone tracks.</summary>
        internal static IReadOnlyList<XAnimModelChoice> FindCompatibleModels(WorkbenchProject project, string assetName)
        {
            var clip = ReadNative(FindNative(project, assetName));
            var animatedBones = clip.Bones.Where(bone => bone.HasQuaternion || bone.HasTranslation).ToArray();
            var modelRoots = new[] { Path.Combine(project.Edits, "model_export"), project.AssetPath("model_export"), Path.Combine(project.Dependencies, "model_export"), Path.Combine(AppContext.BaseDirectory, "resources", "xanim") }
                .Where(Directory.Exists).ToArray();
            if (modelRoots.Length == 0)
                return new XAnimModelChoice[0];
            var choices = new List<XAnimModelChoice>();
            var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string modelRoot in modelRoots)
                foreach (string file in Directory.EnumerateFiles(modelRoot, "*.glb", SearchOption.AllDirectories))
                {
                    try
                    {
                        string relative = Helpers.GetRelativePath(modelRoot, file).Replace('\\', '/');
                        if (!seenModels.Add(relative))
                            continue;
                        var jointNames = ReadJointNames(ReadGlb(file).Root);
                        var missingBones = animatedBones.Where(bone => !jointNames.Contains(bone.Name)).Select(bone => bone.Name).ToArray();
                        int matched = animatedBones.Length - missingBones.Length;
                        if (matched > 0)
                            choices.Add(new XAnimModelChoice(Path.GetFileNameWithoutExtension(file), file, matched, animatedBones.Length, jointNames.Count,
                                Path.GetFullPath(file).Equals(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "resources", "xanim", "fbi_viewhands.glb")), StringComparison.OrdinalIgnoreCase), missingBones));
                    }
                    catch (Exception error) when (((((error is System.IO.IOException) || (error is System.IO.InvalidDataException)) || (error is System.Text.Json.JsonException)) || (error is System.InvalidOperationException)) || (error is System.ArgumentException)) { }
                }
            return choices
                .OrderByDescending(choice => assetName.StartsWith("viewmodel_", StringComparison.OrdinalIgnoreCase) && choice.IsBundledHands)
                .ThenByDescending(choice => choice.MatchedBones / (double)choice.RequiredBones)
                .ThenByDescending(choice => choice.MatchedBones)
                .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();
        }

        internal static XAnimClipInfo Describe(WorkbenchProject project, string assetName)
        {
            var clip = ReadNative(FindNative(project, assetName));
            return new XAnimClipInfo(T6Version, clip.LoopFrameCount, clip.FrameRate, clip.BoneCount,
                clip.Bones.Count(bone => bone.HasQuaternion || bone.HasTranslation), clip.Looped,
                (clip.Flags & DeltaFlag) != 0 || ((clip.Flags & T6CompatFlag) != 0 && (clip.Flags & Delta3DFlag) != 0));
        }

        internal static XAnimDependencyReport FindDependencies(WorkbenchProject project, string assetName)
        {
            var clip = ReadNative(FindNative(project, assetName));
            var animated = clip.Bones.Where(bone => bone.HasQuaternion || bone.HasTranslation)
                .Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var weapons = new List<XAnimDependencyWeapon>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int unreadable = 0;
            foreach (string root in new[] { project.Edits, project.Assets, project.Dependencies })
            {
                string folder = Path.Combine(root, "weapons");
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string relative = Helpers.GetRelativePath(folder, file);
                    if (!seen.Add(relative)) continue;
                    try
                    {
                        var weapon = WeaponModule.LoadPreview(file, Path.GetFileNameWithoutExtension(file));
                        var animationFields = weapon.OrderedFields.Where(field =>
                            field.Value.Trim().Equals(assetName, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (animationFields.Length == 0) continue;
                        var models = new List<XAnimDependencyModel>();
                        foreach (var field in weapon.OrderedFields.Where(field =>
                            IsModelSlot(field.Name, "gunModel") || IsModelSlot(field.Name, "attachViewModel"))
                            .Where(field =>
                            !string.IsNullOrWhiteSpace(field.Value)))
                        {
                            string name = field.Value.Trim();
                            try
                            {
                                string definition = ProjectFile("xmodel/" + name + ".json");
                                if (definition == null)
                                {
                                    models.Add(new XAnimDependencyModel(field.Name, name, null, new string[0], EnglishText.Text("Model JSON not found")));
                                    continue;
                                }
                                using (var json = JsonDocument.Parse(File.ReadAllText(definition)))
                                {
                                    string relativeGlb = json.RootElement.GetProperty("lods").EnumerateArray()
                                    .Select(lod => lod.TryGetProperty("file", out var value) ? value.GetString() : null)
                                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));
                                    string glb = relativeGlb == null ? null : ProjectFile(relativeGlb);
                                    if (glb == null)
                                    {
                                        models.Add(new XAnimDependencyModel(field.Name, name, null, new string[0], EnglishText.Text("Model GLB not found")));
                                        continue;
                                    }
                                    var joints = ReadJointNames(ReadGlb(glb).Root);
                                    string slot = field.Name.StartsWith("attachViewModel", StringComparison.OrdinalIgnoreCase)
                                        ? field.Name.Substring("attachViewModel".Length) : "";
                                    float Number(string key)
                                    {
                                        if (!weapon.Fields.TryGetValue(key + slot, out var value) || string.IsNullOrWhiteSpace(value)) return 0;
                                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number) || float.IsNaN(number) || float.IsInfinity(number))
                                            throw new InvalidDataException(EnglishText.Text("Invalid model offset ") + key + slot);
                                        return number;
                                    }
                                    weapon.Fields.TryGetValue("attachViewModelTag" + slot, out string tag);
                                    models.Add(new XAnimDependencyModel(field.Name, name, glb, animated.Where(joints.Contains).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(), null,
                                        Number("attachViewModelOffsetX"), Number("attachViewModelOffsetY"), Number("attachViewModelOffsetZ"),
                                        Number("attachViewModelOffsetPitch"), Number("attachViewModelOffsetYaw"), Number("attachViewModelOffsetRoll"), tag));
                                }
                            }
                            catch (Exception error) when (((((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.IO.InvalidDataException)) || (error is System.Text.Json.JsonException)) || (error is System.InvalidOperationException)) || (error is System.ArgumentException)) || (error is KeyNotFoundException))
                            {
                                models.Add(new XAnimDependencyModel(field.Name, name, null, new string[0], EnglishText.Text("Could not verify model: ") + error.Message));
                            }
                        }
                        foreach (var field in animationFields)
                            weapons.Add(new XAnimDependencyWeapon(weapon.AssetName, field.Name, models.ToArray()));
                    }
                    catch (Exception error) when ((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.IO.InvalidDataException)) || (error is System.ArgumentException))
                    {
                        unreadable++;
                    }
                }
            }
            return new XAnimDependencyReport(weapons.ToArray(), unreadable);
            bool IsModelSlot(string name, string prefix) =>
                            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            name.Skip(prefix.Length).All(char.IsDigit);

            string ProjectFile(string relative)
            {
                // ChildPath rejects absolute paths and traversal outside the project.
                foreach (string path in new[] { project.EditPath(relative), project.AssetPath(relative), Path.Combine(project.Dependencies, relative) })
                {
                    string dependencyRoot = Path.GetFullPath(project.Dependencies).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string full = Path.GetFullPath(path);
                    if (path.StartsWith(project.Dependencies, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(dependencyRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(EnglishText.Text("Invalid model dependency path."));
                    if (File.Exists(full)) return full;
                }
                return null;
            }
        }

        /// <summary>Combine OAT's model GLB with decoded XAnim channels in one Blender-ready GLB file.</summary>
        internal static string ExportToGlb(WorkbenchProject project, string assetName, string modelGlb, string outputGlb)
        {
            if (!File.Exists(modelGlb))
                throw new FileNotFoundException(EnglishText.Text("Model GLB not found."), modelGlb);
            var clip = ReadNative(FindNative(project, assetName));
            var glb = ReadGlb(modelGlb);
            var joints = FindJointNodes(glb.Root);
            var animatedBones = clip.Bones.Where(bone => bone.HasQuaternion || bone.HasTranslation).ToArray();
            var missing = animatedBones.Where(bone => !joints.ContainsKey(bone.Name))
                .Select(bone => bone.Name).ToArray();
            if (missing.Length == animatedBones.Length)
                throw new InvalidDataException(EnglishText.Text("The selected GLB has no bones with XAnim tracks. Matches: 0/") + animatedBones.Length);

            var nodes = RequireArray(glb.Root, "nodes");
            var samplers = new JsonArray();
            var channels = new JsonArray();
            var rootJointNodes = FindRootJointNodes(glb.Root);
            int timeAccessor = -1;
            foreach (var bone in clip.Bones)
            {
                if (!joints.TryGetValue(bone.Name, out int nodeIndex))
                    continue;
                bool rootJoint = rootJointNodes.Contains(nodeIndex);
                if (bone.HasQuaternion)
                {
                    var values = bone.Quaternions.Select(value => ToGlbQuaternion(value, rootJoint)).ToArray();
                    AddChannel("rotation", bone.QuaternionIndices, values.Select(value => new[] { value.X, value.Y, value.Z, value.W }).ToArray());
                }
                if (bone.HasTranslation)
                {
                    var values = bone.Translations.Select(value => ToGlbTranslation(value, rootJoint)).ToArray();
                    AddChannel("translation", bone.TranslationIndices, values.Select(value => new[] { value.X, value.Y, value.Z }).ToArray());
                }

                void AddChannel(string path, int[] frameIndices, float[][] values)
                {
                    if (frameIndices.Length == 0 || values.Length != frameIndices.Length)
                        return;
                    if (timeAccessor < 0)
                    {
                        var times = frameIndices.Select(frame => frame / (float)clip.FrameRate).ToArray();
                        timeAccessor = AddFloatAccessor(glb.Root, glb.Binary, times.Select(time => new[] { time }).ToArray(), "SCALAR", includeBounds: true);
                        // Accessors are shared only when every channel uses the same frame indices.
                    }
                    int inputAccessor = timeAccessor;
                    if (!FrameIndicesMatch(glb.Root, glb.Binary, timeAccessor, frameIndices, clip.FrameRate))
                    {
                        var times = frameIndices.Select(frame => frame / (float)clip.FrameRate).ToArray();
                        inputAccessor = AddFloatAccessor(glb.Root, glb.Binary, times.Select(time => new[] { time }).ToArray(), "SCALAR", includeBounds: true);
                    }
                    int outputAccessor = AddFloatAccessor(glb.Root, glb.Binary, values, values[0].Length == 4 ? "VEC4" : "VEC3", includeBounds: false);
                    int samplerIndex = samplers.Count;
                    samplers.Add(new JsonObject { ["input"] = inputAccessor, ["output"] = outputAccessor, ["interpolation"] = "LINEAR" });
                    channels.Add(new JsonObject
                    {
                        ["sampler"] = samplerIndex,
                        ["target"] = new JsonObject { ["node"] = nodeIndex, ["path"] = path }
                    });
                }
            }
            if (channels.Count == 0)
                throw new InvalidDataException(EnglishText.Text("XAnim has no rotation or position tracks to export."));

            var animations = glb.Root["animations"] as JsonArray;
            if (animations == null)
            {
                animations = new JsonArray();
                glb.Root["animations"] = animations;
            }
            // Give the clip its source asset name so Blender's action is recognizable on re-import.
            animations.Add(new JsonObject { ["name"] = assetName, ["samplers"] = samplers, ["channels"] = channels });

            // Blender's glTF importer maps node extras to object custom properties. The asset identity lets
            // Workbench validate that a returned GLB belongs to this clip without external sidecar files.
            int metadataNode = rootJointNodes.Count > 0 ? rootJointNodes.First() : joints.Values.First();
            var metadata = nodes[metadataNode]["extras"] as JsonObject;
            if (metadata == null)
            {
                metadata = new JsonObject();
                nodes[metadataNode]["extras"] = metadata;
            }
            metadata["t6XAnimAsset"] = assetName;
            metadata["t6XAnimVersion"] = T6Version;
            metadata["t6XAnimFrameRate"] = clip.FrameRate;
            metadata["t6XAnimLooped"] = clip.Looped;
            metadata["t6XAnimMatchedBones"] = animatedBones.Length - missing.Length;
            metadata["t6XAnimRequiredBones"] = animatedBones.Length;
            metadata["t6XAnimUnmatchedBones"] = new JsonArray(missing.Select(name => (JsonNode)JsonValue.Create(name)).ToArray());
            WriteGlb(glb, outputGlb);
            return outputGlb;
        }

        // Add the weapon tracks after the two independent skins have been merged.
        // The attached weapon root is already driven by tag_weapon; do not apply a
        // second root motion to it. Only previously unmatched weapon joints are used.
        internal static void AddWeaponTracks(WorkbenchProject project, string assetName, string file, int weaponNodeOffset, Action<string> warning)
        {
            var clip = ReadNative(FindNative(project, assetName));
            var glb = ReadGlb(file);
            var nodes = RequireArray(glb.Root, "nodes");
            var animation = RequireArray(glb.Root, "animations").OfType<JsonObject>()
                .Single(item => string.Equals((string)item["name"], assetName, StringComparison.OrdinalIgnoreCase));
            var channels = RequireArray(animation, "channels");
            var samplers = RequireArray(animation, "samplers");
            var existingNames = new HashSet<string>(channels.OfType<JsonObject>()
                .Select(channel => (string)nodes[GetInt(channel["target"].AsObject(), "node")]?["name"] ?? ""), StringComparer.OrdinalIgnoreCase);
            var weaponJoints = RequireArray(glb.Root, "skins").OfType<JsonObject>()
                .SelectMany(skin => RequireArray(skin, "joints")).Select(item => item.GetValue<int>())
                .Where(index => index >= weaponNodeOffset).Distinct().ToArray();
            var weaponNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var weaponRoots = FindRootJointNodes(glb.Root);
            foreach (int index in weaponJoints)
            {
                string name = (string)nodes[index]?["name"] ?? "";
                if (name.Length > 0 && !weaponNames.TryAdd(name, index))
                    throw new InvalidDataException(EnglishText.Text("Weapon has a duplicate bone name: ") + name);
            }
            int count = 0;
            foreach (var bone in clip.Bones)
            {
                if (existingNames.Contains(bone.Name) || !weaponNames.TryGetValue(bone.Name, out int nodeIndex) ||
                    bone.Name.Equals("j_gun", StringComparison.OrdinalIgnoreCase)) continue;
                if (bone.HasQuaternion)
                    AddChannel(nodeIndex, "rotation", bone.QuaternionIndices,
                        bone.Quaternions.Select(value => ToGlbQuaternion(value, weaponRoots.Contains(nodeIndex))).Select(value => new[] { value.X, value.Y, value.Z, value.W }).ToArray());
                if (bone.HasTranslation)
                    AddChannel(nodeIndex, "translation", bone.TranslationIndices,
                        bone.Translations.Select(value => ToGlbTranslation(value, weaponRoots.Contains(nodeIndex))).Select(value => new[] { value.X, value.Y, value.Z }).ToArray());
                if (bone.HasQuaternion || bone.HasTranslation) count++;
            }
            if (count == 0) warning?.Invoke(EnglishText.Text("No additional XAnim tracks found for the weapon model; check model and animation compatibility."));
            WriteGlb(glb, file);

            void AddChannel(int nodeIndex, string path, int[] indices, float[][] values)
            {
                if (indices.Length == 0 || indices.Length != values.Length) return;
                var times = indices.Select(frame => new[] { frame / (float)clip.FrameRate }).ToArray();
                int input = AddFloatAccessor(glb.Root, glb.Binary, times, "SCALAR", includeBounds: true);
                int output = AddFloatAccessor(glb.Root, glb.Binary, values, values[0].Length == 4 ? "VEC4" : "VEC3", includeBounds: false);
                int samplerIndex = samplers.Count;
                samplers.Add(new JsonObject { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" });
                channels.Add(new JsonObject { ["sampler"] = samplerIndex, ["target"] = new JsonObject { ["node"] = nodeIndex, ["path"] = path } });
            }
        }

        /// <summary>Rebuild edited GLB channels as T6 full-quaternion/full-translation tracks.</summary>
        internal static string ImportFromGlb(WorkbenchProject project, string assetName, string editedGlb, int? selectedSkin = null)
        {
            if (!File.Exists(editedGlb))
                throw new FileNotFoundException(EnglishText.Text("Modified GLB not found."), editedGlb);
            string source = FindNative(project, assetName);
            var clip = ReadNative(source);
            var glb = ReadGlb(editedGlb);
            var channels = ReadAnimationChannels(glb.Root, glb.Binary, assetName);
            var rootJointNodes = FindClipRootJointNodes(glb.Root, clip, selectedSkin);
            var selectedNodes = FindSelectedRigNodes(glb.Root, rootJointNodes);
            var clipNames = clip.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var primaryChannels = channels.Where(channel => selectedNodes.Contains(channel.BoneNode) && clipNames.Contains(channel.BoneName)).ToArray();
            var primaryNames = primaryChannels.Select(channel => channel.BoneName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var extraChannels = channels.Where(channel => !selectedNodes.Contains(channel.BoneNode) &&
                clipNames.Contains(channel.BoneName) && !primaryNames.Contains(channel.BoneName)).ToArray();
            foreach (var group in extraChannels.GroupBy(channel => channel.BoneName, StringComparer.OrdinalIgnoreCase))
                if (group.Select(channel => channel.BoneNode).Distinct().Count() > 1)
                    throw new InvalidDataException(EnglishText.Text("Multiple bones found in additional skeletons ") + group.Key + EnglishText.Text("; cannot select the track automatically."));
            var relevantChannels = primaryChannels.Concat(extraChannels).ToArray();
            var allRootJoints = FindRootJointNodes(glb.Root);
            if (relevantChannels.Length == 0)
                throw new InvalidDataException(EnglishText.Text("No animation to import was found in the selected GLB."));
            double lastTime = relevantChannels.SelectMany(channel => channel.Times).Max();
            if (double.IsNaN(lastTime) || double.IsInfinity(lastTime) || lastTime < 0)
                throw new InvalidDataException(EnglishText.Text("GLB contains invalid animation time."));
            // Frame zero is a sample too; keep the source length if the GLB ends earlier.
            double lastFrame = Math.Ceiling(lastTime * clip.FrameRate - 0.0001);
            if (lastFrame >= 4096)
                throw new InvalidDataException(EnglishText.Text("XAnim supports no more than 4096 frames."));
            int frameCount = Math.Max(clip.LoopFrameCount, checked((int)lastFrame + 1));
            bool extended = frameCount > clip.LoopFrameCount;
            if (extended && (clip.Looped || (clip.Flags & DeltaFlag) != 0 ||
                ((clip.Flags & T6CompatFlag) != 0 && (clip.Flags & Delta3DFlag) != 0)))
                throw new NotSupportedException(EnglishText.Text("Extending a looping XAnim or an XAnim with a delta track is not yet supported."));
            var changed = new Dictionary<string, (float[][] Rotation, float[][] Translation)>(StringComparer.OrdinalIgnoreCase);
            foreach (var channel in relevantChannels)
            {
                if (!clip.Bones.Any(bone => bone.Name.Equals(channel.BoneName, StringComparison.OrdinalIgnoreCase)))
                    continue; // Blender may add tracks for non-XAnim bones; never import them.
                changed.TryGetValue(channel.BoneName, out var current);
                if (channel.Path == "rotation")
                {
                    if (current.Rotation != null)
                        throw new InvalidDataException(EnglishText.Text("GLB contains a duplicate rotation channel for ") + channel.BoneName);
                    current.Rotation = SampleChannel(channel, frameCount, clip.FrameRate);
                    if (allRootJoints.Contains(channel.BoneNode))
                        for (int frame = 0; frame < current.Rotation.Length; frame++)
                        {
                            var value = current.Rotation[frame];
                            var native = ToNativeQuaternion(new Quaternion(value[0], value[1], value[2], value[3]), rootJoint: true);
                            current.Rotation[frame] = Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new float[] { native.X }, new float[] { native.Y })), new float[] { native.Z })), new float[] { native.W }));
                        }
                }
                else if (channel.Path == "translation")
                {
                    if (current.Translation != null)
                        throw new InvalidDataException(EnglishText.Text("GLB contains a duplicate translation channel for ") + channel.BoneName);
                    current.Translation = SampleChannel(channel, frameCount, clip.FrameRate);
                    if (allRootJoints.Contains(channel.BoneNode))
                        for (int frame = 0; frame < current.Translation.Length; frame++)
                        {
                            var value = current.Translation[frame];
                            var native = ToNativeTranslation(new Vector3(value[0], value[1], value[2]), rootJoint: true);
                            current.Translation[frame] = Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new float[] { native.X }, new float[] { native.Y })), new float[] { native.Z }));
                        }
                }
                changed[channel.BoneName] = current;
            }
            if (changed.Count == 0)
                throw new InvalidDataException(EnglishText.Text("No animation to import was found in the selected GLB."));

            var output = new MemoryStream();
            var prefix = (byte[])clip.Prefix.Clone();
            if (extended)
                BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(2, 2), checked((ushort)frameCount));
            output.Write(prefix, 0, prefix.Length);
            byte[] flipMask = (byte[])clip.FlipMask.Clone();
            byte[] halfMask = (byte[])clip.HalfMask.Clone();
            for (int index = 0; index < clip.Bones.Count; index++)
            {
                var bone = clip.Bones[index];
                changed.TryGetValue(bone.Name, out var update);
                if (update.Rotation != null || (extended && bone.HasQuaternion))
                {
                    SetBit(flipMask, index, false);
                    SetBit(halfMask, index, false);
                }
            }
            output.Write(flipMask, 0, flipMask.Length);
            output.Write(halfMask, 0, halfMask.Length);
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var bone in clip.Bones)
                    WriteCString(writer, bone.Name);
                foreach (var bone in clip.Bones)
                {
                    changed.TryGetValue(bone.Name, out var update);
                    // Raw tracks encode indices relative to the old frame count. Rebuild
                    // untouched tracks when extending, preserving their old timing and
                    // holding the final pose through the new tail.
                    if (extended && update.Rotation == null && bone.HasQuaternion)
                        update.Rotation = SampleNativeTrack(bone.Name, "rotation", bone.QuaternionIndices,
                            bone.Quaternions.Select(q => new[] { q.X, q.Y, q.Z, q.W }).ToArray(), frameCount, clip.FrameRate);
                    if (extended && update.Translation == null && bone.HasTranslation)
                        update.Translation = SampleNativeTrack(bone.Name, "translation", bone.TranslationIndices,
                            bone.Translations.Select(v => new[] { v.X, v.Y, v.Z }).ToArray(), frameCount, clip.FrameRate);
                    if (update.Rotation == null)
                        writer.Write(bone.RawQuaternion);
                    else
                        WriteFullQuaternionTrack(writer, update.Rotation);
                    if (update.Translation == null)
                        writer.Write(bone.RawTranslation);
                    else
                        WriteTranslationTrack(writer, update.Translation);
                }
                writer.Write(clip.NotifyData);
            }
            byte[] rebuilt = output.ToArray();
            // A parse-after-write check catches count/bitmask/offset mistakes before project files change.
            _ = ReadNative(rebuilt, assetName);
            string relative = RelativeToProjectAssets(project, source);
            string target = project.EditPath(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string staged = target + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(staged, rebuilt);
                if (File.Exists(target))
                    File.Replace(staged, target, null);
                else
                    File.Move(staged, target);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
            return target;
        }

        static string FindNative(WorkbenchProject project, string assetName)
        {
            string fileName = Path.GetFileName(assetName);
            foreach (string root in new[] { project.Edits, project.Assets, project.Dependencies })
            {
                string folder = Path.Combine(root, "xanim");
                if (!Directory.Exists(folder))
                    continue;
                var matches = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(file => Path.GetFileNameWithoutExtension(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 1)
                    return matches[0];
                if (matches.Length > 1)
                    throw new IOException(EnglishText.Text("Multiple XAnim files found named ") + assetName + ".");
            }
            throw new FileNotFoundException(EnglishText.Text("Compiled XAnim file not found in the project's assets/dependencies."), assetName);
        }

        static string RelativeToProjectAssets(WorkbenchProject project, string source)
        {
            foreach (string root in new[] { project.Edits, project.Assets, project.Dependencies })
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullSource = Path.GetFullPath(source);
                if (fullSource.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    return Helpers.GetRelativePath(root, fullSource);
            }
            throw new InvalidDataException(EnglishText.Text("Source XAnim is outside the project folders."));
        }

        static NativeClip ReadNative(string file) => ReadNative(File.ReadAllBytes(file), Path.GetFileName(file));

        static NativeClip ReadNative(byte[] bytes, string name)
        {
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    if (bytes.Length < 8)
                        throw new InvalidDataException(EnglishText.Text("XAnim file is too short: ") + name);
                    ushort version = reader.ReadUInt16();
                    if (version != T6Version)
                        throw new InvalidDataException(EnglishText.Format("Expected T6 XAnim version 19, received version {0}: {1}", version, name));
                    ushort headerFrames = reader.ReadUInt16();
                    ushort boneCount = reader.ReadUInt16();
                    byte flags = reader.ReadByte();
                    _ = reader.ReadByte(); // T6 asset type; preserved verbatim in Prefix.
                    int frameRate = reader.ReadUInt16();
                    if (boneCount == 0 || boneCount > 512)
                        throw new InvalidDataException(EnglishText.Text("Invalid XAnim bone count: ") + boneCount);
                    bool looped = (flags & LoopedFlag) != 0;
                    int loopFrameCount = looped ? headerFrames + 1 : headerFrames;
                    if (loopFrameCount < 1 || loopFrameCount > 4096)
                        throw new InvalidDataException(EnglishText.Text("Invalid XAnim length: ") + loopFrameCount);
                    if (frameRate <= 0)
                        frameRate = 30;
                    bool t6Compat = (flags & T6CompatFlag) != 0;
                    bool delta3D = t6Compat && (flags & Delta3DFlag) != 0;
                    if ((flags & StreamableFlag) != 0 && !t6Compat)
                        _ = reader.ReadSingle();
                    if ((flags & DeltaFlag) != 0 || delta3D)
                        SkipDeltaTrack(reader, loopFrameCount, loopFrameCount - 1 < 256, delta3D);

                    int tableOffset = checked((int)stream.Position);
                    int maskSize = (boneCount + 7) / 8;
                    byte[] flipMask = ReadExactly(reader, maskSize);
                    byte[] halfMask = ReadExactly(reader, maskSize);
                    var bones = new List<BoneTrack>(boneCount);
                    for (int i = 0; i < boneCount; i++)
                    {
                        string boneName = ReadCString(reader, 512);
                        if (string.IsNullOrWhiteSpace(boneName))
                            throw new InvalidDataException(EnglishText.Text("XAnim contains an unnamed bone."));
                        bones.Add(new BoneTrack { Name = boneName, FlipQuaternion = GetBit(flipMask, i), HalfQuaternion = GetBit(halfMask, i) });
                    }
                    foreach (var bone in bones)
                    {
                        int start = checked((int)stream.Position);
                        ReadQuaternionTrack(reader, bone, loopFrameCount, loopFrameCount - 1 < 256);
                        bone.RawQuaternion = bytes.AsSpan(start, checked((int)stream.Position) - start).ToArray();
                        start = checked((int)stream.Position);
                        ReadTranslationTrack(reader, bone, loopFrameCount, loopFrameCount - 1 < 256);
                        bone.RawTranslation = bytes.AsSpan(start, checked((int)stream.Position) - start).ToArray();
                    }
                    int notifyOffset = checked((int)stream.Position);
                    ReadNotifyBlock(reader, loopFrameCount);
                    if (stream.Position != stream.Length)
                        throw new InvalidDataException(EnglishText.Text("Extra bytes remain after XAnim notify data."));
                    return new NativeClip
                    {
                        Prefix = bytes.AsSpan(0, tableOffset).ToArray(),
                        NotifyData = bytes.AsSpan(notifyOffset).ToArray(),
                        FlipMask = flipMask,
                        HalfMask = halfMask,
                        Bones = bones,
                        LoopFrameCount = loopFrameCount,
                        HeaderFrameCount = headerFrames,
                        FrameRate = frameRate,
                        Looped = looped,
                        Flags = flags
                    };
                }
            }
        }

        static void SkipDeltaTrack(BinaryReader reader, int loopFrames, bool byteIndices, bool delta3D)
        {
            SkipQuaternionDelta(reader, loopFrames, byteIndices, delta3D ? 3 : 1);
            ushort count = reader.ReadUInt16();
            if (count == 0)
                return;
            if (count == 1)
            {
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                _ = reader.ReadSingle();
                return;
            }
            SkipIndices(reader, count, loopFrames, byteIndices);
            bool small = reader.ReadByte() != 0;
            for (int i = 0; i < 6; i++)
                _ = reader.ReadSingle();
            SkipBytes(reader, checked(count * (small ? 3 : 6)));
        }

        static void SkipQuaternionDelta(BinaryReader reader, int loopFrames, bool byteIndices, int storedComponents)
        {
            ushort count = reader.ReadUInt16();
            if (count == 0)
                return;
            if (count > 1)
                SkipIndices(reader, count, loopFrames, byteIndices);
            SkipBytes(reader, checked(count * storedComponents * 2));
        }

        static void SkipIndices(BinaryReader reader, int count, int loopFrames, bool byteIndices)
        {
            if (count >= loopFrames)
                return;
            SkipBytes(reader, checked(count * (byteIndices ? 1 : 2)));
        }

        static void ReadQuaternionTrack(BinaryReader reader, BoneTrack bone, int loopFrames, bool byteIndices)
        {
            int count = reader.ReadUInt16();
            if (count == 0)
                return;
            bone.HasQuaternion = true;
            bone.QuaternionIndices = ReadIndices(reader, count, loopFrames, byteIndices);
            var frames = new Quaternion[count];
            for (int frame = 0; frame < count; frame++)
            {
                float x, y, z, w;
                if (bone.HalfQuaternion)
                {
                    x = reader.ReadInt16() / QuaternionScale;
                    // XQuat2 stores one component; OAT reconstructs its paired W component.
                    w = (float)Math.Sqrt(Math.Max(0, 1 - x * x));
                    y = 0;
                    z = 0;
                }
                else
                {
                    x = reader.ReadInt16() / QuaternionScale;
                    y = reader.ReadInt16() / QuaternionScale;
                    z = reader.ReadInt16() / QuaternionScale;
                    w = (float)Math.Sqrt(Math.Max(0, 1 - x * x - y * y - z * z));
                }
                var value = new Quaternion(x, y, z, w);
                if (bone.FlipQuaternion)
                    value = Negate(value);
                if (frame > 0 && Quaternion.Dot(frames[frame - 1], value) < 0)
                    value = Negate(value);
                frames[frame] = Normalize(value);
            }
            bone.Quaternions = frames;
        }

        static void ReadTranslationTrack(BinaryReader reader, BoneTrack bone, int loopFrames, bool byteIndices)
        {
            int count = reader.ReadUInt16();
            if (count == 0)
                return;
            bone.HasTranslation = true;
            bone.TranslationIndices = ReadIndices(reader, count, loopFrames, byteIndices);
            if (count == 1)
            {
                bone.Translations = new Vector3[] { new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()) };
                return;
            }
            bool small = reader.ReadByte() != 0;
            var mins = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var sizeRaw = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            // The compiled size is the full range; the frame value is normalized below.
            var size = sizeRaw;
            var values = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                float x, y, z;
                if (small)
                {
                    x = reader.ReadByte() / 255.0f;
                    y = reader.ReadByte() / 255.0f;
                    z = reader.ReadByte() / 255.0f;
                }
                else
                {
                    x = reader.ReadUInt16() / 65535.0f;
                    y = reader.ReadUInt16() / 65535.0f;
                    z = reader.ReadUInt16() / 65535.0f;
                }
                values[index] = mins + Vector3.Multiply(size, new Vector3(x, y, z));
            }
            bone.Translations = values;
        }

        static int[] ReadIndices(BinaryReader reader, int count, int loopFrames, bool byteIndices)
        {
            // Constant quaternion/translation tracks omit their single-frame index.
            if (count == 1)
                return new int[] { 0 };
            if (count < 0 || count > loopFrames)
                throw new InvalidDataException(EnglishText.Text("XAnim key count exceeds clip length."));
            if (count >= loopFrames)
                return Enumerable.Range(0, count).ToArray();
            var indices = new int[count];
            for (int i = 0; i < count; i++)
                indices[i] = byteIndices ? reader.ReadByte() : reader.ReadUInt16();
            if (indices.Any(index => index >= loopFrames))
                throw new InvalidDataException(EnglishText.Text("XAnim frame index is outside the clip."));
            return indices;
        }

        static void ReadNotifyBlock(BinaryReader reader, int loopFrames)
        {
            int count = reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                _ = ReadCString(reader, 512);
                ushort frame = reader.ReadUInt16();
                if (frame > loopFrames)
                    throw new InvalidDataException(EnglishText.Text("XAnim notify points outside the clip."));
            }
        }

        static IReadOnlyList<AnimationChannel> ReadAnimationChannels(JsonObject root, byte[] binary, string assetName)
        {
            var animations = RequireArray(root, "animations");
            JsonObject animation = animations.OfType<JsonObject>().FirstOrDefault(item =>
                string.Equals((string)item["name"], assetName, StringComparison.OrdinalIgnoreCase));
            if (animation == null && animations.Count == 1)
                animation = animations[0] as JsonObject;
            if (animation == null)
                throw new InvalidDataException(EnglishText.Text("GLB must contain one animation named ") + assetName + ".");
            var nodes = RequireArray(root, "nodes");
            var samplers = RequireArray(animation, "samplers");
            var channels = RequireArray(animation, "channels");
            var result = new List<AnimationChannel>();
            foreach (var channelNode in channels.OfType<JsonObject>())
            {
                var target = channelNode["target"] as JsonObject ?? throw new InvalidDataException(EnglishText.Text("GLB animation channel without target."));
                int nodeIndex = GetInt(target, "node");
                if ((uint)nodeIndex >= (uint)nodes.Count)
                    throw new InvalidDataException(EnglishText.Text("GLB animation channel with an invalid node reference."));
                string boneName = (string)nodes[nodeIndex]?["name"] ?? "";
                string path = (string)target["path"] ?? "";
                if (path == "scale")
                {
                    int scaleIndex = GetInt(channelNode, "sampler");
                    if ((uint)scaleIndex >= (uint)samplers.Count)
                        throw new InvalidDataException(EnglishText.Text("GLB scale channel references an invalid sampler."));
                    var scaleSampler = samplers[scaleIndex].AsObject();
                    if (string.Equals((string)scaleSampler["interpolation"], "CUBICSPLINE", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(EnglishText.Text("CUBICSPLINE scale is not supported."));
                    var scales = ReadAccessorFloats(root, binary, GetInt(scaleSampler, "output"), "VEC3");
                    if (scales.Length == 0 || scales.Any(scale => scale.Any(component => Math.Abs(component - 1f) > 0.0001f)))
                        throw new InvalidDataException(EnglishText.Text("XAnim does not support changing bone scale: ") + boneName);
                    continue;
                }
                if ((!((path == "rotation") || (path == "translation"))))
                    throw new InvalidDataException(EnglishText.Text("XAnim cannot import GLB channel: ") + path + EnglishText.Text(". Weights are not supported."));
                int samplerIndex = GetInt(channelNode, "sampler");
                if ((uint)samplerIndex >= (uint)samplers.Count)
                    throw new InvalidDataException(EnglishText.Text("GLB animation references an invalid sampler."));
                var sampler = samplers[samplerIndex].AsObject();
                string interpolation = (string)sampler["interpolation"] ?? "LINEAR";
                if (interpolation.Equals("CUBICSPLINE", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Text("GLB uses CUBICSPLINE; switch animation keys to Linear before exporting from Blender."));
                float[][] times = ReadAccessorFloats(root, binary, GetInt(sampler, "input"), "SCALAR");
                float[][] values = ReadAccessorFloats(root, binary, GetInt(sampler, "output"), path == "rotation" ? "VEC4" : "VEC3");
                if (times.Length != values.Length || times.Length == 0)
                    throw new InvalidDataException(EnglishText.Text("GLB animation sampler has an invalid key count."));
                result.Add(new AnimationChannel(nodeIndex, boneName, path, times.Select(value => value[0]).ToArray(), values, interpolation));
            }
            return result;
        }

        static float[][] SampleChannel(AnimationChannel channel, int frameCount, int frameRate)
        {
            var result = new float[frameCount][];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float time = frame / (float)frameRate;
                int upper = Array.FindIndex(channel.Times, value => value >= time);
                if (upper < 0)
                {
                    result[frame] = (float[])channel.Values[channel.Values.Length - (1)].Clone();
                    continue;
                }
                if (upper == 0 || channel.Interpolation.Equals("STEP", StringComparison.OrdinalIgnoreCase))
                {
                    result[frame] = (float[])channel.Values[upper == 0 ? 0 : Math.Max(0, upper - 1)].Clone();
                    continue;
                }
                float span = channel.Times[upper] - channel.Times[upper - 1];
                float amount = span > 0 ? Clamp((time - channel.Times[upper - 1]) / span, 0, 1) : 0;
                if (channel.Path == "rotation")
                {
                    var first = new Quaternion(channel.Values[upper - 1][0], channel.Values[upper - 1][1], channel.Values[upper - 1][2], channel.Values[upper - 1][3]);
                    var second = new Quaternion(channel.Values[upper][0], channel.Values[upper][1], channel.Values[upper][2], channel.Values[upper][3]);
                    var q = Normalize(Quaternion.Slerp(Normalize(first), Normalize(second), amount));
                    result[frame] = Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new float[] { q.X }, new float[] { q.Y })), new float[] { q.Z })), new float[] { q.W }));
                }
                else
                {
                    var first = channel.Values[upper - 1];
                    var second = channel.Values[upper];
                    result[frame] = Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new float[] { first[0] + (second[0] - first[0]) * amount }, new float[] { first[1] + (second[1] - first[1]) * amount })), new float[] { first[2] + (second[2] - first[2]) * amount }));
                }
            }
            return result;
        }

        static float[][] SampleNativeTrack(string name, string path, int[] indices, float[][] values, int frameCount, int frameRate)
        {
            var channel = new AnimationChannel(0, name, path,
                indices.Select(index => index / (float)frameRate).ToArray(), values, "LINEAR");
            return SampleChannel(channel, frameCount, frameRate);
        }

        static void WriteFullQuaternionTrack(BinaryWriter writer, float[][] values)
        {
            if (values.Length == 0 || values.Length > ushort.MaxValue)
                throw new InvalidDataException(EnglishText.Text("Unsupported rotation key count."));
            writer.Write((ushort)values.Length);
            foreach (var components in values)
            {
                var q = Normalize(new Quaternion(components[0], components[1], components[2], components[3]));
                // XAnim stores only x/y/z; the positive w is reconstructed by the game.
                // q and -q describe the same rotation, so canonicalize to positive w first.
                if (q.W < 0)
                    q = Negate(q);
                if (q.X * q.X + q.Y * q.Y + q.Z * q.Z > 1.0001f)
                    throw new InvalidDataException("GLB rotation quaternion is not normalized.");
                writer.Write(QuantizeQuaternion(q.X));
                writer.Write(QuantizeQuaternion(q.Y));
                writer.Write(QuantizeQuaternion(q.Z));
            }
        }

        static short QuantizeQuaternion(float value) => (short)Clamp((int)Math.Round(Clamp(value, -1, 1) * QuaternionScale), short.MinValue, short.MaxValue);

        static void WriteTranslationTrack(BinaryWriter writer, float[][] values)
        {
            if (values.Length == 0 || values.Length > ushort.MaxValue)
                throw new InvalidDataException(EnglishText.Text("Unsupported translation key count."));
            writer.Write((ushort)values.Length);
            if (values.Length == 1)
            {
                for (int axis = 0; axis < 3; axis++)
                    writer.Write(values[0][axis]);
                return;
            }
            var mins = new float[3];
            var maxs = new float[3];
            for (int axis = 0; axis < 3; axis++)
            {
                mins[axis] = values.Min(value => value[axis]);
                maxs[axis] = values.Max(value => value[axis]);
            }
            writer.Write((byte)0); // Full 16-bit translation data.
            for (int axis = 0; axis < 3; axis++)
                writer.Write(mins[axis]);
            var sizes = Enumerable.Range(0, 3).Select(axis => maxs[axis] - mins[axis]).ToArray();
            foreach (float size in sizes)
                writer.Write(size);
            foreach (var value in values)
                for (int axis = 0; axis < 3; axis++)
                {
                    ushort encoded = sizes[axis] <= 1e-8f ? (ushort)0 : (ushort)Clamp((int)Math.Round((value[axis] - mins[axis]) / sizes[axis] * ushort.MaxValue), 0, ushort.MaxValue);
                    writer.Write(encoded);
                }
        }

        static Quaternion ToGlbQuaternion(Quaternion value, bool rootJoint)
        {
            if (!rootJoint)
                return value;
            var basis = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -(float)Math.PI / 2);
            return Normalize(basis * value);
        }

        static Vector3 ToGlbTranslation(Vector3 value, bool rootJoint) => rootJoint ? new Vector3(value.X, value.Z, -value.Y) : value;
        static Quaternion ToNativeQuaternion(Quaternion value, bool rootJoint)
        {
            if (!rootJoint)
                return value;
            var basis = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)Math.PI / 2);
            return Normalize(basis * value);
        }
        static Vector3 ToNativeTranslation(Vector3 value, bool rootJoint) => rootJoint ? new Vector3(value.X, -value.Z, value.Y) : value;

        static int AddFloatAccessor(JsonObject root, byte[] binary, float[][] values, string type, bool includeBounds)
        {
            int width;
            switch (type)
            {
                case "SCALAR":
                    width = 1;
                    break;
                case "VEC3":
                    width = 3;
                    break;
                case "VEC4":
                    width = 4;
                    break;
                default:
                    throw new InvalidDataException("Unsupported GLB animation accessor type.");
            }
            if (values.Any(value => value.Length != width))
                throw new InvalidDataException("GLB animation values have an inconsistent width.");
            byte[] currentBinary = GetBinary(root, binary);
            int alignedOffset = Align4(currentBinary.Length);
            var payload = new byte[checked(values.Length * width * 4)];
            int cursor = 0;
            foreach (var value in values)
                foreach (float component in value)
                {
                    if (float.IsNaN(component) || float.IsInfinity(component))
                        throw new InvalidDataException("GLB animation has a non-finite value.");
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(cursor, 4), Helpers.SingleToInt32Bits(component));
                    cursor += 4;
                }
            byte[] newBinary = new byte[checked(alignedOffset + payload.Length)];
            Buffer.BlockCopy(currentBinary, 0, newBinary, 0, currentBinary.Length);
            Buffer.BlockCopy(payload, 0, newBinary, alignedOffset, payload.Length);
            SetBinary(root, newBinary);

            var views = RequireArray(root, "bufferViews");
            int viewIndex = views.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = alignedOffset, ["byteLength"] = payload.Length });
            var accessors = RequireArray(root, "accessors");
            int accessorIndex = accessors.Count;
            var accessor = new JsonObject { ["bufferView"] = viewIndex, ["componentType"] = 5126, ["count"] = values.Length, ["type"] = type };
            if (includeBounds && values.Length > 0)
            {
                accessor["min"] = new JsonArray(Enumerable.Range(0, width).Select(axis => (JsonNode)JsonValue.Create(values.Min(value => value[axis]))).ToArray());
                accessor["max"] = new JsonArray(Enumerable.Range(0, width).Select(axis => (JsonNode)JsonValue.Create(values.Max(value => value[axis]))).ToArray());
            }
            accessors.Add(accessor);
            return accessorIndex;
        }

        // JsonObject instances cannot carry an extra managed field; this operation-local map keeps the resized BIN.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, BinaryHolder> BinaryOverrides = new System.Runtime.CompilerServices.ConditionalWeakTable<JsonObject, XAnimHelpers.BinaryHolder>();
        sealed class BinaryHolder
        {
            internal byte[] Value = new byte[0];
        }
        static byte[] GetBinary(JsonObject root, byte[] original) => BinaryOverrides.TryGetValue(root, out var holder) ? holder.Value : original;
        static void SetBinary(JsonObject root, byte[] value)
        {
            BinaryOverrides.Remove(root);
            BinaryOverrides.Add(root, new BinaryHolder { Value = value });
        }

        static bool FrameIndicesMatch(JsonObject root, byte[] binary, int accessorIndex, int[] indices, int frameRate)
        {
            if (indices.Length == 0)
                return false;
            var values = ReadAccessorFloats(root, GetBinary(root, binary), accessorIndex, "SCALAR");
            if (values.Length != indices.Length)
                return false;
            for (int i = 0; i < indices.Length; i++)
                if (Math.Abs(values[i][0] - indices[i] / (float)frameRate) > 0.00001f)
                    return false;
            return true;
        }

        static float[][] ReadAccessorFloats(JsonObject root, byte[] binary, int accessorIndex, string expectedType)
        {
            var accessors = RequireArray(root, "accessors");
            var views = RequireArray(root, "bufferViews");
            if ((uint)accessorIndex >= (uint)accessors.Count)
                throw new InvalidDataException("GLB accessor index is out of range.");
            var accessor = accessors[accessorIndex].AsObject();
            if ((string)accessor["type"] != expectedType || GetInt(accessor, "componentType") != 5126 || accessor.ContainsKey("sparse"))
                throw new InvalidDataException("XAnim import supports only non-sparse float32 animation accessors.");
            int viewIndex = GetInt(accessor, "bufferView");
            if ((uint)viewIndex >= (uint)views.Count)
                throw new InvalidDataException("GLB bufferView index is out of range.");
            var view = views[viewIndex].AsObject();
            int width;
            switch (expectedType)
            {
                case "SCALAR":
                    width = 1;
                    break;
                case "VEC3":
                    width = 3;
                    break;
                case "VEC4":
                    width = 4;
                    break;
                default:
                    throw new InvalidDataException("Unsupported accessor type.");
            }
            int count = GetInt(accessor, "count");
            int start = (int?)view["byteOffset"] ?? 0;
            start += (int?)accessor["byteOffset"] ?? 0;
            int stride = (int?)view["byteStride"] ?? width * 4;
            if (count < 0 || stride < width * 4 || start < 0 || (long)start + Math.Max(0, count - 1) * stride + width * 4 > binary.Length)
                throw new InvalidDataException("GLB animation accessor points outside the BIN chunk.");
            var result = new float[count][];
            for (int i = 0; i < count; i++)
            {
                result[i] = new float[width];
                for (int axis = 0; axis < width; axis++)
                    result[i][axis] = Helpers.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(binary.AsSpan(start + i * stride + axis * 4, 4)));
            }
            return result;
        }

        static HashSet<string> ReadJointNames(JsonObject root)
        {
            var nodes = RequireArray(root, "nodes");
            var skins = RequireArray(root, "skins");
            if (skins.Count == 0)
                throw new InvalidDataException(EnglishText.Text("GLB model does not contain skin / skeleton."));
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skin in skins.OfType<JsonObject>())
                foreach (var joint in RequireArray(skin, "joints"))
                {
                    int index = joint.GetValue<int>();
                    if ((uint)index >= (uint)nodes.Count)
                        throw new InvalidDataException("GLB skin contains an invalid joint index.");
                    string name = (string)nodes[index]?["name"];
                    if (!string.IsNullOrWhiteSpace(name))
                        result.Add(name);
                }
            return result;
        }

        static Dictionary<string, int> FindJointNodes(JsonObject root)
        {
            var nodes = RequireArray(root, "nodes");
            var joints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var skin in RequireArray(root, "skins").OfType<JsonObject>())
                foreach (var item in RequireArray(skin, "joints"))
                {
                    int index = item.GetValue<int>();
                    string name = (string)nodes[index]?["name"] ?? "";
                    if (name.Length > 0 && !joints.TryAdd(name, index))
                        throw new InvalidDataException("GLB skeleton has duplicate joint names: " + name);
                }
            if (joints.Count == 0)
                throw new InvalidDataException(EnglishText.Text("GLB model does not contain skin / joints."));
            return joints;
        }

        // A preview may contain another skin for the weapon. Pick the skin with the
        // greatest overlap with the source XAnim and use only its joint hierarchy.
        internal static IReadOnlyList<XAnimRigChoice> FindImportRigs(WorkbenchProject project, string assetName, string file)
        {
            var clip = ReadNative(FindNative(project, assetName));
            var root = ReadGlb(file).Root;
            var nodes = RequireArray(root, "nodes");
            var names = clip.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skins = RequireArray(root, "skins");
            var candidates = new List<(int Index, HashSet<int> Joints, int Score)>();
            for (int index = 0; index < skins.Count; index++)
            {
                var joints = RequireArray(skins[index].AsObject(), "joints").Select(item => item.GetValue<int>()).ToHashSet();
                if (joints.Any(joint => joint < 0 || joint >= nodes.Count)) throw new InvalidDataException("GLB skin contains an invalid joint index.");
                int score = joints.Count(joint => names.Contains((string)nodes[joint]?["name"] ?? ""));
                if (score > 0 && !candidates.Any(candidate => candidate.Joints.SetEquals(joints))) candidates.Add((index, joints, score));
            }
            if (candidates.Count == 0) throw new InvalidDataException(EnglishText.Text("GLB does not contain a suitable XAnim skeleton."));
            int best = candidates.Max(candidate => candidate.Score);
            var result = new List<XAnimRigChoice>();
            foreach (var candidate in candidates.Where(candidate => candidate.Score == best))
            {
                var childJoints = new HashSet<int>();
                foreach (int joint in candidate.Joints)
                    if (nodes[joint]?["children"] is JsonArray children)
                        foreach (var child in children)
                            if (candidate.Joints.Contains(child.GetValue<int>())) childJoints.Add(child.GetValue<int>());
                int rootJoint = candidate.Joints.First(joint => !childJoints.Contains(joint));
                int parent = -1;
                for (int index = 0; index < nodes.Count; index++)
                    if (nodes[index]?["children"] is JsonArray children && children.Any(child => child.GetValue<int>() == rootJoint)) { parent = index; break; }
                string rigName = parent >= 0 ? (string)nodes[parent]?["name"] ?? "" : (string)nodes[rootJoint]?["name"] ?? "";
                result.Add(new XAnimRigChoice(candidate.Index, rigName + " · skin " + candidate.Index + EnglishText.Text(" · bones matched: ") + candidate.Score));
            }
            return result;
        }

        static HashSet<int> FindSelectedRigNodes(JsonObject root, HashSet<int> roots)
        {
            var nodes = RequireArray(root, "nodes");
            var selected = new HashSet<int>();
            var pending = new Stack<int>(roots);
            while (pending.Count > 0)
            {
                int index = pending.Pop();
                if (!selected.Add(index)) continue;
                if (nodes[index]?["children"] is JsonArray children)
                    foreach (var child in children) pending.Push(child.GetValue<int>());
            }
            return selected;
        }

        static HashSet<int> FindClipRootJointNodes(JsonObject root, NativeClip clip, int? selectedSkin = null)
        {
            var nodes = RequireArray(root, "nodes");
            var names = clip.Bones.Select(bone => bone.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<(HashSet<int> Joints, int Score)>();
            var skins = RequireArray(root, "skins");
            if (selectedSkin.HasValue && (selectedSkin.Value < 0 || selectedSkin.Value >= skins.Count)) throw new InvalidDataException(EnglishText.Text("Selected skeleton is missing from GLB."));
            foreach (var skin in skins.OfType<JsonObject>().Where((skin, index) => !selectedSkin.HasValue || index == selectedSkin.Value))
            {
                var joints = new HashSet<int>(RequireArray(skin, "joints").Select(item => item.GetValue<int>()));
                if (joints.Any(index => index < 0 || index >= nodes.Count))
                    throw new InvalidDataException("GLB skin contains an invalid joint index.");
                int score = joints.Count(index => names.Contains((string)nodes[index]?["name"] ?? ""));
                if (score > 0)
                    if (!candidates.Any(candidate => candidate.Joints.SetEquals(joints))) candidates.Add((joints, score));
            }
            if (candidates.Count == 0)
                throw new InvalidDataException(EnglishText.Text("GLB does not contain a suitable XAnim skeleton."));
            int best = candidates.Max(candidate => candidate.Score);
            if (candidates.Count(candidate => candidate.Score == best) != 1)
                throw new InvalidDataException(EnglishText.Text("GLB contains multiple equally suitable XAnim skeletons."));
            var selected = candidates.First(candidate => candidate.Score == best).Joints;
            var children = new HashSet<int>();
            foreach (int joint in selected)
                if (nodes[joint]?["children"] is JsonArray childArray)
                    foreach (var child in childArray)
                        if (selected.Contains(child.GetValue<int>()))
                            children.Add(child.GetValue<int>());
            return selected.Where(index => !children.Contains(index)).ToHashSet();
        }
        static HashSet<int> FindRootJointNodes(JsonObject root)
        {
            var joints = FindJointNodes(root);
            var nodes = RequireArray(root, "nodes");
            var children = new HashSet<int>();
            foreach (int nodeIndex in joints.Values)
                if (nodes[nodeIndex]?["children"] is JsonArray childArray)
                    foreach (var child in childArray)
                        if (joints.Values.Contains(child.GetValue<int>()))
                            children.Add(child.GetValue<int>());
            return joints.Values.Where(index => !children.Contains(index)).ToHashSet();
        }

        static GlbFile ReadGlb(string file) => ReadGlb(File.ReadAllBytes(file));
        static GlbFile ReadGlb(byte[] data)
        {
            if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) != 0x46546c67 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != 2)
                throw new InvalidDataException(EnglishText.Text("GLB 2.0 with a model / animation is required."));
            int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)));
            if (total != data.Length)
                throw new InvalidDataException(EnglishText.Text("GLB header length does not match file size."));
            int offset = 12;
            byte[] jsonBytes = null;
            byte[] binary = null;
            while (offset + 8 <= data.Length)
            {
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
                offset += 8;
                if (length < 0 || offset + (long)length > data.Length)
                    throw new InvalidDataException("GLB chunk is truncated.");
                if (type == 0x4e4f534a)
                    jsonBytes = data.AsSpan(offset, length).ToArray();
                else if (type == 0x004e4942)
                    binary = data.AsSpan(offset, length).ToArray();
                offset += length;
            }
            if (jsonBytes == null || binary == null)
                throw new InvalidDataException(EnglishText.Text("GLB must contain JSON and BIN chunks."));
            var root = JsonNode.Parse(Encoding.UTF8.GetString(jsonBytes).TrimEnd(' ', '\0')) as JsonObject ?? throw new InvalidDataException(EnglishText.Text("GLB JSON root must be an object."));
            if (RequireArray(root, "buffers").Count != 1)
                throw new InvalidDataException(EnglishText.Text("Only GLB with a single BIN buffer is supported."));
            return new GlbFile(root, binary);
        }

        static void WriteGlb(GlbFile glb, string output)
        {
            byte[] binary = GetBinary(glb.Root, glb.Binary);
            glb.Root["buffers"][0]["byteLength"] = binary.Length;
            byte[] json = Encoding.UTF8.GetBytes(glb.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            int jsonLength = Align4(json.Length);
            int binLength = Align4(binary.Length);
            int totalLength = checked(12 + 8 + jsonLength + 8 + binLength);
            using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(0x46546c67u);
                    writer.Write(2u);
                    writer.Write(totalLength);
                    writer.Write(jsonLength);
                    writer.Write(0x4e4f534au);
                    writer.Write(json);
                    for (int i = json.Length; i < jsonLength; i++)
                        writer.Write((byte)0x20);
                    writer.Write(binLength);
                    writer.Write(0x004e4942u);
                    writer.Write(binary);
                    for (int i = binary.Length; i < binLength; i++)
                        writer.Write((byte)0);
                }
            }
        }

        static void WriteCString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Contains((byte)0))
                throw new InvalidDataException("XAnim bone name contains a null character.");
            writer.Write(bytes);
            writer.Write((byte)0);
        }

        static string ReadCString(BinaryReader reader, int limit)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < limit; i++)
            {
                byte value = reader.ReadByte();
                if (value == 0)
                    return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
            throw new InvalidDataException("XAnim has an unterminated or oversized string.");
        }

        static byte[] ReadExactly(BinaryReader reader, int count)
        {
            byte[] data = reader.ReadBytes(count);
            if (data.Length != count)
                throw new EndOfStreamException("XAnim is truncated.");
            return data;
        }

        static void SkipBytes(BinaryReader reader, int count)
        {
            if (count < 0 || reader.BaseStream.Position + (long)count > reader.BaseStream.Length)
                throw new EndOfStreamException("XAnim data is truncated.");
            reader.BaseStream.Seek(count, SeekOrigin.Current);
        }

        static bool GetBit(byte[] bytes, int index) => (bytes[index / 8] & (1 << (index % 8))) != 0;
        static void SetBit(byte[] bytes, int index, bool value)
        {
            byte mask = (byte)(1 << (index % 8));
            if (value)
                bytes[index / 8] |= mask;
            else
                bytes[index / 8] &= (byte)~mask;
        }

        static JsonArray RequireArray(JsonObject parent, string key) => parent[key] as JsonArray ?? throw new InvalidDataException("GLB is missing array '" + key + "'.");
        static int GetInt(JsonObject parent, string key) => parent[key]?.GetValue<int>() ?? throw new InvalidDataException("GLB is missing integer '" + key + "'.");
        static int Align4(int value) => checked((value + 3) & ~3);
        static float Clamp(float value, float minimum, float maximum) => Math.Max(minimum, Math.Min(maximum, value));
        static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));
        static Quaternion Normalize(Quaternion value) => value.LengthSquared() > 1e-12f ? Quaternion.Normalize(value) : Quaternion.Identity;
        static Quaternion Negate(Quaternion value) => new Quaternion(-value.X, -value.Y, -value.Z, -value.W);
    }
}
