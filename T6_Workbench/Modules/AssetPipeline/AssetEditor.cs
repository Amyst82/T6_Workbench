using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    // File formats and project layout live here, so a replacement window does not need to know them.
    // Stages asset edits inside a project and delegates format-specific work to modules.
    internal sealed class AssetEditor
    {
        private readonly Action<string> _log;
        internal AssetEditor(Action<string> log = null)
        {
            this._log = log;
        }
        internal string Import(WorkbenchProject project, AssetRef asset, string sourceFile, int lodIndex = 0)
        {
            if (!File.Exists(sourceFile))
                throw new FileNotFoundException(EnglishText.Text("Edit file not found."), sourceFile);
            string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
            string target;
            if (asset.Type == "mapents")
            {
                if (extension != ".mapents")
                    throw new InvalidDataException(EnglishText.Text("A loose .mapents file is required."));
                var text = File.ReadAllText(sourceFile, new UTF8Encoding(false, true));
                var originalPath = project.AssetPath(asset.Name + ".ents");
                if (!File.Exists(originalPath))
                    throw new FileNotFoundException(EnglishText.Text("The project has no original MapEnts entity string."), originalPath);
                var original = File.ReadAllText(originalPath, new UTF8Encoding(false, true));
                LooseMapEnts.ValidateAdditive(original, text);
                target = project.EditPath("mapents/" + (asset.Name + ".mapents").Replace('/', Path.DirectorySeparatorChar));
                if (Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    return target;
                if (File.Exists(target))
                {
                    var relative = Helpers.GetRelativePath(project.Edits, target);
                    var backup = Path.Combine(project.Builds, "backups", relative + ".bak-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff"));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(target, backup);
                    _log?.Invoke(EnglishText.Text("Previous MapEnts edit saved: ") + backup);
                }
            }
            else if (asset.Type == "image")
            {
                target = project.EditPath(Helpers.GetRelativePath(project.Assets, FindImage(project, asset.Name)));
                if (extension == ".png")
                {
                    WriteDds(sourceFile, target);
                    return target;
                }
                if (extension != ".dds")
                    throw new InvalidDataException(EnglishText.Text("PNG or DDS is required."));
                _ = TextureModule.LoadDds(sourceFile);
            }
            else if (asset.Type == "xmodel" && extension == ".glb")
            {
                var lods = FindLods(project, asset.Name);
                if (lodIndex < 0 || lodIndex >= lods.Count)
                    throw new ArgumentOutOfRangeException(nameof(lodIndex));
                _ = GlbModule.LoadPreview(sourceFile);
                var materialNames = AssetNameNormalizer.ReadMaterialNames(project.Inventory, project.Zone);
                var normalized = GlbModule.NormalizeImport(sourceFile, materialNames);
                target = project.EditPath(Helpers.GetRelativePath(project.Assets, lods[lodIndex]));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, normalized.Bytes);
                foreach (var change in normalized.Changes)
                    _log?.Invoke(change);
                return target;
            }
            else if ((asset.Type == "rawfile") || (asset.Type == "stringtable"))
            {
                var original = project.AssetPath(asset.Name);
                if (!File.Exists(original))
                    throw new FileNotFoundException(EnglishText.Text("OAT did not export the original file."), original);
                if (!extension.Equals(Path.GetExtension(original), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Text("The edit file extension must match the original."));
                target = project.EditPath(Helpers.GetRelativePath(project.Assets, original));
            }
            else
            {
                var original = FindGeneric(project, asset);
                if (!extension.Equals(Path.GetExtension(original), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Text("The edit file extension must match the original."));
                if (extension == ".json")
                    using (var parsedDocument = JsonDocument.Parse(File.ReadAllText(sourceFile)))
                    {
                    }
                if (!File.Exists(Effective(project, original)))
                    throw new FileNotFoundException(EnglishText.Text("OAT did not export asset data."), original);
                target = project.EditPath(Helpers.GetRelativePath(project.Assets, original));
            }
            Copy(sourceFile, target);
            return target;
        }
        internal string PreviewFile(WorkbenchProject project, AssetRef asset)
        {
            if (asset.Type == "image")
                return Effective(project, FindImage(project, asset.Name));
            if (asset.Type == "xmodel")
                return FindLods(project, asset.Name).Select(file => Effective(project, file)).FirstOrDefault();
            if (asset.Type == "material")
            {
                var material = project.AssetPath("materials/" + asset.Name + ".json");
                var effective = Effective(project, material);
                return File.Exists(effective) ? effective : null;
            }
            return null;
        }
        internal async Task<OperationResult> ExportAsync(WorkbenchProject project, IReadOnlyList<AssetRef> assets, string destination, string modelFormat, string imageFormat, WorkbenchPaths paths, CancellationToken cancellation = default)
        {
            Workspace.RejectGameOutput(destination);
            if (assets.Count == 0)
                throw new ArgumentException(EnglishText.Text("No assets selected."), nameof(assets));
            if ((!(((((modelFormat == "GLB") || (modelFormat == "GLTF")) || (modelFormat == "OBJ")) || (modelFormat == "XMODEL_EXPORT")) || (modelFormat == "XMODEL_BIN"))))
                throw new ArgumentException(EnglishText.Text("Unsupported model format."));
            if ((!(((imageFormat == "DDS") || (imageFormat == "PNG")) || (imageFormat == "IWI"))))
                throw new ArgumentException(EnglishText.Text("Unsupported image format."));
            Directory.CreateDirectory(destination);
            int copied = 0;
            var warnings = new List<string>();
            foreach (var asset in assets.Distinct())
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    copied += await ExportOneAsync(project, asset, destination, modelFormat, imageFormat, paths, cancellation);
                }
                catch (FileNotFoundException error) 
                { 
                    warnings.Add(asset.Type + "," + asset.Name + ": " + error.Message); 
                }
                catch (DirectoryNotFoundException error) 
                { 
                    warnings.Add(asset.Type + "," + asset.Name + ": " + error.Message); 
                }
            }
            return new OperationResult(destination, EnglishText.Format("Files exported: {0}", copied), warnings.ToArray());
        }
        async Task<int> ExportOneAsync(WorkbenchProject project, AssetRef asset, string destination,
                    string modelFormat, string imageFormat, WorkbenchPaths paths, CancellationToken token)
        {
            if (asset.Type == "mapents")
            {
                var source = project.AssetPath(asset.Name + ".ents");
                if (!File.Exists(source))
                    throw new FileNotFoundException(EnglishText.Text("OAT did not export the MapEnts entity string."), source);
                var target = Path.Combine(destination, (asset.Name + ".mapents").Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                Copy(Effective(project, source), target);
                return 1;
            }
            if (asset.Type == "image")
            {
                var original = FindImage(project, asset.Name);
                var edited = Effective(project, original);
                string relative = Helpers.GetRelativePath(project.Assets, original);
                string output = Path.Combine(destination, Path.ChangeExtension(relative, "." + imageFormat.ToLowerInvariant()));
                if (imageFormat == "PNG")
                {
                    WritePng(edited, output);
                }
                else if (imageFormat == "DDS")
                {
                    Copy(edited, output);
                }
                else
                {
                    var iwi = Path.Combine(project.Dependencies, Path.ChangeExtension(relative, ".iwi"));
                    if (!File.Exists(iwi) || edited != original)
                    {
                        var isolated = Workspace.NewRun(project.Builds, "single-image");
                        Copy(edited, Path.Combine(isolated, Path.GetFileName(edited)));
                        var converted = await new AssetPipelineModule(_log).ConvertTexturesAsync(paths.BinDirectory, isolated, project.Builds, token);
                        iwi = Path.Combine(converted.Directory, "assets", Path.GetFileNameWithoutExtension(edited) + ".iwi");
                    }
                    Copy(iwi, output);
                }
                return 1;
            }
            if (asset.Type == "xmodel")
            {
                if (modelFormat != "GLB")
                {
                    var result = await new AssetPipelineModule(_log).ExportAsync(new ExportRequest(new ExportRequest(paths.BinDirectory,
                        project.Fastfile, File.Exists(project.Capture) ? project.Capture : paths.CaptureFile,
                        project.Builds, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { project.Dependencies }, new string[] { project.Assets })), Enumerable.ToArray(project.SourceSearchPaths))), new string[] { Path.Combine(paths.GameDirectory, "data") })), new string[] { Path.Combine(paths.GameDirectory, "zone", "all") }))))
                    {
                        SpecificAssetType = "xmodel",
                        SpecificAssetName = asset.Name,
                        ModelFormat = modelFormat,
                        AllowMissingIdentityNormal = true
                    }, cancellation: token);
                    var dump = Path.Combine(result.Directory, "dump");
                    WorkbenchProject.CopyTree(dump, destination, token);
                    return Directory.GetFiles(dump, "*", SearchOption.AllDirectories).Length;
                }
                int count = 0;
                var json = project.AssetPath("xmodel/" + asset.Name + ".json");
                if (File.Exists(json))
                {
                    Copy(Effective(project, json), Path.Combine(destination, "xmodel", asset.Name + ".json"));
                    count++;
                }
                foreach (var lod in FindLods(project, asset.Name))
                {
                    Copy(Effective(project, lod), Path.Combine(destination, "model_export", Path.GetFileName(lod)));
                    count++;
                }
                return count;
            }
            string originalFile;
            try
            {
                originalFile = (asset.Type == "rawfile") || (asset.Type == "stringtable") ? project.AssetPath(asset.Name) : FindGeneric(project, asset);
            }
            catch (FileNotFoundException) { _log?.Invoke(EnglishText.Text("No export for ") + asset.Type + "," + asset.Name); return 0; }
            if (!File.Exists(Effective(project, originalFile)))
            {
                _log?.Invoke(EnglishText.Text("No export for ") + asset.Type + "," + asset.Name);
                return 0;
            }
            Copy(Effective(project, originalFile), Path.Combine(destination, Helpers.GetRelativePath(project.Assets, originalFile)));
            return 1;
        }
        static string Folder(string type)
        {
            switch (type)
            {
                case "material":
                    return "materials";
                case "techniqueset":
                    return "techsets";
                case "weapon":
                    return "weapons";
                default:
                    return type;
            }
        }
        static string FindGeneric(WorkbenchProject project, AssetRef asset)
        {
            var folder = Folder(asset.Type);
            var json = project.AssetPath(folder + "/" + asset.Name + ".json");
            if (File.Exists(Effective(project, json)))
                return json;
            var directory = project.AssetPath(folder);
            if (!Directory.Exists(directory))
                throw new FileNotFoundException(EnglishText.Text("OAT did not export the asset: ") + asset.Name);
            var matches = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(file => Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileName(asset.Name), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
                return matches[0];
            throw new FileNotFoundException(EnglishText.Text("No unique OAT file for ") + asset.Type + "," + asset.Name);
        }
        static string Effective(WorkbenchProject project, string original)
        {
            var edited = project.EditPath(Helpers.GetRelativePath(project.Assets, original));
            return File.Exists(edited) ? edited : original;
        }
        static string FindImage(WorkbenchProject project, string name)
        {
            var relative = name.Replace('\\', '/').Replace('*', '_') + ".dds";
            var expected = project.AssetPath("images/" + relative);
            if (File.Exists(Effective(project, expected)))
                return expected;
            var folder = project.AssetPath("images");
            var matches = Directory.EnumerateFiles(folder, "*.dds", SearchOption.AllDirectories).Where(file => Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileNameWithoutExtension(relative), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1)
                return matches[0];
            throw new FileNotFoundException(EnglishText.Text("DDS for image not found or name is ambiguous: ") + name);
        }
        static List<string> FindLods(WorkbenchProject project, string name)
        {
            var folder = project.AssetPath("model_export");
            var normalized = name.Replace('/', '_').Replace('\\', '_');
            var editFolder = project.EditPath("model_export");
            var sourceFiles = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.glb", SearchOption.AllDirectories) : Enumerable.Empty<string>();
            var editFiles = Directory.Exists(editFolder) ? Directory.EnumerateFiles(editFolder, "*.glb", SearchOption.AllDirectories).Select(file => project.AssetPath("model_export/" + Helpers.GetRelativePath(editFolder, file))) : Enumerable.Empty<string>();
            return sourceFiles.Concat(editFiles).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(file =>
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var marker = stem.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
                    if (marker >= 0 && int.TryParse(stem.Substring((marker + 4)), out var lodNumber))
                        stem = stem.Substring(0, (marker) - (0));
                    return stem.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                        stem.Equals(normalized, StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(file =>
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var marker = stem.LastIndexOf("_lod", StringComparison.OrdinalIgnoreCase);
                    return marker >= 0 && int.TryParse(stem.Substring((marker + 4)), out var lod) ? lod : -1;
                }).ToList();
        }
        static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
        static void WritePng(string dds, string png)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(TextureModule.LoadDds(dds).Image));
            Directory.CreateDirectory(Path.GetDirectoryName(png));
            using (var output = File.Create(png))
            {
                encoder.Save(output);
            }
        }
        static void WriteDds(string png, string dds)
        {
            using (var input = File.OpenRead(png))
            {
                var frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                int width = frame.PixelWidth, height = frame.PixelHeight;
                if (width <= 0 || height <= 0 || (long)width * height > 128_000_000)
                    throw new InvalidDataException(EnglishText.Text("PNG size is outside the valid range."));
                var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[checked(width * height * 4)];
                bitmap.CopyPixels(pixels, width * 4, 0);
                // OAT's T6 IWI writer accepts R8_G8_B8_A8, so emit RGBA byte order and masks.
                for (int i = 0; i < pixels.Length; i += 4)
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                var header = new byte[128];
                using (var writer = new BinaryWriter(new MemoryStream(header), Encoding.ASCII, true))
                {
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("DDS "));
                    writer.Write(124);
                    writer.Write(0x100F);
                    writer.Write(height);
                    writer.Write(width);
                    writer.Write(width * 4);
                    writer.BaseStream.Position = 76;
                    writer.Write(32);
                    writer.Write(0x41);
                    writer.Write(0);
                    writer.Write(32);
                    writer.Write(0x000000FFu);
                    writer.Write(0x0000FF00u);
                    writer.Write(0x00FF0000u);
                    writer.Write(0xFF000000u);
                    writer.Write(0x1000);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dds));
                using (var output = File.Create(dds))
                {
                    output.Write(header);
                    output.Write(pixels);
                }
            }
        }
    }
}
