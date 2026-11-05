using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    // Merges a separate weapon GLB into an animated hands GLB for Blender preview.
    // The weapon keeps its own skin; only the hands XAnim is imported back into the game.
    internal static class XAnimWeaponPreview
    {
        internal static int Attach(string animatedPath, string weaponPath, WorkbenchProject project, Action<string> warning, bool attachment = false, float offsetX = 0, float offsetY = 0, float offsetZ = 0)
        {
            var hands = Read(animatedPath);
            var weapon = Read(weaponPath);
            // Clearcoat lives on materials; its texture indices are remapped below along
            // with the core material textures. Other extensions may contain references
            // that require their own remapping and must not be copied blindly.
            var supportedExtensions = new HashSet<string>(StringComparer.Ordinal) { "KHR_materials_clearcoat" };
            var weaponExtensions = new[] { "extensionsUsed", "extensionsRequired" }
                .SelectMany(key => weapon.Json[key] is JsonArray list ? list.Select(item => item?.GetValue<string>() ?? "") : Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray();
            var unsupported = weaponExtensions.Where(name => !supportedExtensions.Contains(name)).ToArray();
            if (unsupported.Length > 0)
                throw new NotSupportedException(EnglishText.Text("Weapon GLB contains glTF extensions that are not yet supported: ") + string.Join(", ", unsupported));
            foreach (string key in new[] { "extensionsUsed", "extensionsRequired" })
            {
                if (weapon.Json[key] is JsonArray declared && declared.Count > 0)
                {
                    var combined = Array(hands.Json, key);
                    var existing = new HashSet<string>(combined.Select(item => item.GetValue<string>()), StringComparer.Ordinal);
                    foreach (var item in declared)
                    {
                        string name = item.GetValue<string>();
                        if (existing.Add(name))
        {
            combined.Add(name);
        }
                    }
                }
            }
            if (weapon.Json["animations"] is JsonArray animations && animations.Count > 0)
                throw new NotSupportedException(EnglishText.Text("Weapon GLB must contain a model without its own animations."));
            JsonArray handsNodes = Array(hands.Json, "nodes");
            JsonArray weaponNodes = Array(weapon.Json, "nodes");
            int mount = -1;
            for (int i = 0; i < handsNodes.Count; i++)
            {
                if (string.Equals((string)handsNodes[i]?["name"], "tag_weapon", StringComparison.OrdinalIgnoreCase))
                {
                    if (mount >= 0)
                    {
                        throw new InvalidDataException(EnglishText.Text("The hands model contains multiple tag_weapon nodes."));
                    }
                    mount = i;
                }
            }
            if (mount < 0)
            {
                throw new InvalidDataException(EnglishText.Text("Hands model is missing tag_weapon for attaching the weapon."));
            }
            int nodeOffset = handsNodes.Count, viewOffset = Array(hands.Json, "bufferViews").Count, accessorOffset = Array(hands.Json, "accessors").Count;
            int meshOffset = Array(hands.Json, "meshes").Count, skinOffset = Array(hands.Json, "skins").Count;
            int materialOffset = Array(hands.Json, "materials").Count, textureOffset = Array(hands.Json, "textures").Count;
            int imageOffset = Array(hands.Json, "images").Count, samplerOffset = Array(hands.Json, "samplers").Count;
            int binaryOffset = (hands.Binary.Length + 3) & ~3;
            var binary = new byte[checked(binaryOffset + weapon.Binary.Length)];
            Buffer.BlockCopy(hands.Binary, 0, binary, 0, hands.Binary.Length);
            Buffer.BlockCopy(weapon.Binary, 0, binary, binaryOffset, weapon.Binary.Length);
            foreach (var item in Array(weapon.Json, "bufferViews"))
            {
                var view = item.DeepClone().AsObject();
                if ((int?)view["buffer"] != 0)
                {
                    throw new InvalidDataException(EnglishText.Text("Weapon: only one embedded buffer is supported."));
                }
                view["byteOffset"] = ((int?)view["byteOffset"] ?? 0) + binaryOffset;
                Array(hands.Json, "bufferViews").Add(view);
            }
            foreach (var item in Array(weapon.Json, "accessors"))
            {
                var accessor = item.DeepClone().AsObject();
                Shift(accessor, "bufferView", viewOffset);
                if (accessor["sparse"] is JsonObject sparse)
                {
                    Shift(sparse["indices"]?.AsObject(), "bufferView", viewOffset);
                    Shift(sparse["values"]?.AsObject(), "bufferView", viewOffset);
                }
                Array(hands.Json, "accessors").Add(accessor);
            }
            var normalImageIndices = FindNormalImageIndices(weapon.Json);
            int imageIndex = 0;
            foreach (var item in Array(weapon.Json, "images"))
            {
                var image = item.DeepClone().AsObject();
                Shift(image, "bufferView", viewOffset);
                try
                {
                    EmbedImage(image, weaponPath, project);
                }
                catch (Exception error) when (((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.NotSupportedException)) || (error is System.ArgumentException)) || (error is System.Runtime.InteropServices.COMException))
                {
                    string imageName = (string)image["uri"] ?? "image " + imageIndex;
                    image.Remove("bufferView");
                    image.Remove("mimeType");
                    image["uri"] = PlaceholderImage(normalImageIndices.Contains(imageIndex));
                    warning?.Invoke(EnglishText.Text("Weapon texture was not loaded: ") + imageName + ". " + error.Message);
                }
                Array(hands.Json, "images").Add(image);
                imageIndex++;
            }
            foreach (var item in Array(weapon.Json, "samplers"))
            {
                Array(hands.Json, "samplers").Add(item.DeepClone());
            }
            foreach (var item in Array(weapon.Json, "textures"))
            {
                var texture = item.DeepClone().AsObject();
                Shift(texture, "sampler", samplerOffset);
                Shift(texture, "source", imageOffset);
                Array(hands.Json, "textures").Add(texture);
            }
            foreach (var item in Array(weapon.Json, "materials"))
            {
                var material = item.DeepClone().AsObject();
                ShiftTextureReferences(material, textureOffset);
                Array(hands.Json, "materials").Add(material);
            }
            foreach (var item in Array(weapon.Json, "meshes"))
            {
                var mesh = item.DeepClone().AsObject();
                foreach (var primitive in Array(mesh, "primitives").OfType<JsonObject>())
                {
                    Shift(primitive, "indices", accessorOffset);
                    Shift(primitive, "material", materialOffset);
                    ShiftAttributeMap(primitive["attributes"] as JsonObject, accessorOffset);
                    if (primitive["targets"] is JsonArray targets)
                    {
                        foreach (var target in targets.OfType<JsonObject>())
                        {
                            ShiftAttributeMap(target, accessorOffset);
                        }
                    }
                }
                Array(hands.Json, "meshes").Add(mesh);
            }
            foreach (var item in Array(weapon.Json, "skins"))
            {
                var skin = item.DeepClone().AsObject();
                Shift(skin, "inverseBindMatrices", accessorOffset);
                Shift(skin, "skeleton", nodeOffset);
                ShiftArray(Array(skin, "joints"), nodeOffset);
                Array(hands.Json, "skins").Add(skin);
            }
            foreach (var item in weaponNodes)
            {
                var node = item.DeepClone().AsObject();
                if (node["camera"] != null || node["extensions"] != null)
                    throw new NotSupportedException(EnglishText.Text("Weapon cameras and node extensions are not yet supported."));
                Shift(node, "mesh", meshOffset);
                Shift(node, "skin", skinOffset);
                if (node["children"] is JsonArray children)
                {
                    ShiftArray(children, nodeOffset);
                }
                handsNodes.Add(node);
            }
            int scene = (int?)weapon.Json["scene"] ?? 0;
            JsonArray scenes = Array(weapon.Json, "scenes");
            if ((uint)scene >= scenes.Count)
                throw new InvalidDataException(EnglishText.Text("Weapon GLB has no active scene."));
            JsonArray weaponSceneRoots = Array(scenes[scene].AsObject(), "nodes");
            WeaponGripAlignment alignment = attachment ? new WeaponGripAlignment(Matrix4x4.CreateTranslation(offsetX, offsetZ, -offsetY), "weapon origin", false) : XAnimWeaponPreviewHelpers.FindGripAlignment(weapon.Json, weaponSceneRoots);
            if (!attachment && alignment.AnchorName == "scene root")
            {
                warning?.Invoke(EnglishText.Text("Weapon GLB has no j_gun pivot or workbench_weapon_grip point; the weapon scene origin was used."));
            }
            // T6 viewmodels attach the weapon pivot to tag_weapon; finger helper bones can move away from the grip.
            if (attachment)
            {
                int[] mounts = Enumerable.Range(0, nodeOffset).Where(i =>
                    string.Equals((string)handsNodes[i]?["name"], "workbench_weapon_grip_alignment", StringComparison.Ordinal)).ToArray();
                if (mounts.Length != 1) 
                    throw new InvalidDataException(EnglishText.Text("Could not find a unique weapon model attachment point."));
                mount = mounts[0];
            }
            var attachmentNode = handsNodes[mount].AsObject();
            if ((!(attachmentNode["children"] is JsonArray attachmentChildren)))
            {
                attachmentChildren = new JsonArray();
                attachmentNode["children"] = attachmentChildren;
            }
            var fittedWeaponNode = new JsonObject
            {
                ["name"] = attachment ? "workbench_weapon_attachment" : "workbench_weapon_grip_alignment",
                ["matrix"] = XAnimWeaponPreviewHelpers.ToGlbMatrix(alignment.Correction),
                ["children"] = new JsonArray(weaponSceneRoots.Select(root => (JsonNode)JsonValue.Create(root.GetValue<int>() + nodeOffset)).ToArray()),
                ["extras"] = new JsonObject
                {
                    ["workbenchWeaponGripAnchor"] = alignment.AnchorName,
                    ["workbenchWeaponGripUsesCustomAnchor"] = alignment.HasCustomGripAnchor,
                    ["workbenchWeaponHandBone"] = "tag_weapon"
                }
            };
            int fittedWeaponNodeIndex = handsNodes.Count;
            handsNodes.Add(fittedWeaponNode);
            attachmentChildren.Add(fittedWeaponNodeIndex);
            hands.Json["buffers"][0]["byteLength"] = binary.Length;
            Write(animatedPath, hands.Json, binary);
            return nodeOffset;
        }

        static void Shift(JsonObject value, string property, int offset)
        {
            if (value?[property] != null)
            {
                value[property] = value[property].GetValue<int>() + offset;
            }
        }

        static void ShiftArray(JsonArray values, int offset)
        {
            for (int i = 0; i < values.Count; i++)
            {
                values[i] = values[i].GetValue<int>() + offset;
            }
        }

        static void ShiftAttributeMap(JsonObject map, int offset)
        {
            if (map == null)
                return;
            foreach (string key in map.Select(pair => pair.Key).ToArray())
            {
                Shift(map, key, offset);
            }
        }

        static void ShiftTextureReferences(JsonNode node, int offset)
        {
            if (node is JsonObject obj)
            {
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.EndsWith("Texture", StringComparison.Ordinal) && pair.Value is JsonObject texture)
                    {
                        Shift(texture, "index", offset);
                    }
                    ShiftTextureReferences(pair.Value, offset);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    ShiftTextureReferences(child, offset);
                }
            }
        }

        static JsonArray Array(JsonObject value, string key)
        {
            if (value[key] is JsonArray result)
                return result;
            var array = new JsonArray();
            value[key] = array;
            return array;
        }

        static HashSet<int> FindNormalImageIndices(JsonObject root)
        {
            var result = new HashSet<int>();
            var textures = Array(root, "textures");
            foreach (var material in Array(root, "materials").OfType<JsonObject>())
            {
                foreach (var normal in new[] { material["normalTexture"], material["extensions"]?["KHR_materials_clearcoat"]?["clearcoatNormalTexture"] }.OfType<JsonObject>())
                {
                    if (normal["index"] == null)
                    {
                        continue;
                    }
                    int textureIndex = normal["index"].GetValue<int>();
                    if ((uint)textureIndex < textures.Count && textures[textureIndex]?["source"] != null)
                    {
                        result.Add(textures[textureIndex]["source"].GetValue<int>());
                    }
                }
            }
            return result;
        }

        static string PlaceholderImage(bool normal)
        {
            byte[] pixel = normal ? new byte[] { 255, 128, 128, 255 } : new byte[] { 255, 255, 255, 255 };
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            }
        }

        static void EmbedImage(JsonObject image, string weaponPath, WorkbenchProject project)
        {
            string uri = (string)image["uri"];
            if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;
            if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute) && !absolute.IsFile)
                throw new NotSupportedException(EnglishText.Text("Network weapon textures are not supported."));
            string path = absolute != null && absolute.IsFile ? absolute.LocalPath : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(weaponPath), Uri.UnescapeDataString(uri.Replace('/', Path.DirectorySeparatorChar))));
            if (!File.Exists(path))
            {
                string imageName = Path.GetFileName(path);
                string[] imageRoots = { project.Edits, project.Assets, project.Dependencies };
                string projectImage = imageRoots.Select(root => Path.Combine(root, "images", imageName)).FirstOrDefault(File.Exists);
                if (projectImage == null) throw new FileNotFoundException(EnglishText.Text("Weapon texture not found: ") + path);
                path = projectImage;
            }
            string extension = Path.GetExtension(path).ToLowerInvariant();
            byte[] bytes;
            string mime;
            if (extension == ".dds")
            {
                var bitmap = TextureModule.LoadDds(path).Image;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    bytes = stream.ToArray();
                    mime = "image/png";
                }
            }
            else if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
            {
                bytes = File.ReadAllBytes(path);
                mime = extension == ".png" ? "image/png" : "image/jpeg";
            }
            else
                throw new NotSupportedException(EnglishText.Text("Weapon texture format is not supported: ") + extension);
            image["uri"] = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }

        sealed class Glb
        {
            public JsonObject Json { get; set; }

            public byte[] Binary { get; set; }

            public Glb(JsonObject Json, byte[] Binary)
            {
                this.Json = Json;
                this.Binary = Binary;
            }
        }

        static Glb Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != 0x46546c67 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)) != bytes.Length)
                throw new InvalidDataException(EnglishText.Text("A valid GLB 2.0 file is required: ") + path);
            int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4));
            if (jsonLength < 0 || 20L + jsonLength + 8 > bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)) != 0x4e4f534a)
                throw new InvalidDataException(EnglishText.Text("Invalid GLB JSON chunk."));
            int pos = 20 + jsonLength;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos, 4));
            if (length < 0 || pos + 8L + length != bytes.Length || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4)) != 0x004e4942)
                throw new InvalidDataException(EnglishText.Text("Invalid GLB BIN chunk."));
            var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes, 20, jsonLength).TrimEnd(' ', '\0')).AsObject();
            if (Array(json, "buffers").Count != 1)
                throw new InvalidDataException(EnglishText.Text("GLB must contain one embedded buffer."));
            return new Glb(json, bytes.AsSpan(pos + 8, length).ToArray());
        }

        static void Write(string path, JsonObject json, byte[] binary)
        {
            byte[] text = Encoding.UTF8.GetBytes(json.ToJsonString());
            int textLength = (text.Length + 3) & ~3, binLength = (binary.Length + 3) & ~3;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(0x46546c67u);
                    writer.Write(2u);
                    writer.Write(checked(12 + 8 + textLength + 8 + binLength));
                    writer.Write(textLength);
                    writer.Write(0x4e4f534au);
                    writer.Write(text);
                    for (int i = text.Length; i < textLength; i++)
                    {
                        writer.Write((byte)32);
                    }
                    writer.Write(binLength);
                    writer.Write(0x004e4942u);
                    writer.Write(binary);
                    for (int i = binary.Length; i < binLength; i++)
                    {
                        writer.Write((byte)0);
                    }
                }
            }
        }
    }
}
