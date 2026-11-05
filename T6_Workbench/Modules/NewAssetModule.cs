using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace T6_Workbench
{
    // Stages explicitly selected, already T6-compatible assets; it does not retarget rigs.
    internal static class NewAssetModule
    {
        internal sealed class Request
        {
            internal string Name = "", Template = "", Lod0 = "", Lod1 = "", WorldName = "", WorldTemplate = "", WorldLod0 = "", Textures = "", Materials = "", Weapon = "";

            internal string[] Animations = Array.Empty<string>();
        }

        internal static int Stage(WorkbenchProject project, Request request)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            void Add(string relative, byte[] bytes)
            {
                string target = project.EditPath(relative);
                if (files.ContainsKey(target) || File.Exists(target) || File.Exists(project.AssetPath(relative)))
                    throw new IOException(EnglishText.Text("Asset already exists: ") + relative + EnglishText.Text(". Choose another name or import an edit."));
                files.Add(target, bytes);
            }
            void Model(string name, string template, string lod0, string lod1)
            {
                if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                    throw new InvalidDataException(EnglishText.Text("Model names must contain only letters, digits and _."));
                if (!File.Exists(template))
                    throw new FileNotFoundException(EnglishText.Text("Select a compatible T6 XModel JSON as a template."), template);
                var root = JsonNode.Parse(File.ReadAllText(template)) as JsonObject ?? throw new InvalidDataException(EnglishText.Text("Invalid template JSON."));
                if (root["_game"]?.GetValue<string>() != "t6" || root["_type"]?.GetValue<string>() != "xmodel")
                    throw new InvalidDataException(EnglishText.Text("The template must be T6 XModel JSON."));
                var oldLods = root["lods"] as JsonArray ?? throw new InvalidDataException(EnglishText.Text("The template is missing lods."));
                var newLods = new JsonArray();
                var inputs = new[] { lod0, lod1 };
                for (int i = 0; i < inputs.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(inputs[i]))
                    {
                        if (i == 0)
                            throw new InvalidDataException(EnglishText.Text("Select GLB for LOD0."));
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(inputs[i]);
                    if (bytes.Length < 12 || bytes[0] != 0x67 || bytes[1] != 0x6c || bytes[2] != 0x54 || bytes[3] != 0x46)
                        throw new InvalidDataException(EnglishText.Text("Expected a binary GLB: ") + inputs[i]);
                    string relative = "model_export/" + name + "_lod" + i + ".glb";
                    Add(relative, bytes);
                    JsonObject lod = i < oldLods.Count && oldLods[i] is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject { ["distance"] = 500.0 * (i + 1) };
                    lod["file"] = relative;
                    newLods.Add(lod);
                }
                root["lods"] = newLods;
                Add("xmodel/" + name + ".json", System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
            }
            void Folder(string source, string destination, params string[] extensions)
            {
                if (string.IsNullOrWhiteSpace(source))
                    return;
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException(source);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string relative = Helpers.GetRelativePath(source, file).Replace('\\', '/');
                    Add(destination + "/" + relative, File.ReadAllBytes(file));
                }
            }
            Model(request.Name, request.Template, request.Lod0, request.Lod1);
            if (!string.IsNullOrWhiteSpace(request.WorldLod0))
            {
                Model(request.WorldName, request.WorldTemplate, request.WorldLod0, "");
            }
            Folder(request.Textures, "images", ".dds", ".iwi");
            Folder(request.Materials, "materials", ".json");
            foreach (string animation in request.Animations)
            {
                Add("xanim/" + Path.GetFileName(animation), File.ReadAllBytes(animation));
            }
            if (!string.IsNullOrWhiteSpace(request.Weapon))
            {
                Add("weapons/" + Path.GetFileName(request.Weapon), File.ReadAllBytes(request.Weapon));
            }
            // All collisions are checked before any writes; source assets are never altered.
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Key));
                File.WriteAllBytes(file.Key, file.Value);
            }
            return files.Count;
        }
    }
}
