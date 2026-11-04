using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Builds patch_redacted.ff from edited roots and their linker dependencies, without the legacy zone list.
    /// </summary>
    internal sealed class StandaloneRedactedBuild
    {
        private readonly Action<string> _log;
        private readonly ToolRunner _runner;

        internal StandaloneRedactedBuild(Action<string> log = null)
        {
            _log = log;
            _runner = new ToolRunner(log);
        }

        /// <summary>
        /// Builds an isolated compact Redacted package from project edits and their linked dependencies.
        /// </summary>
        public async Task<OperationResult> RunAsync(WorkbenchProject project, WorkbenchPaths paths,
            CancellationToken cancellation = default)
        {
            var edits = Directory.GetFiles(project.Edits, "*", SearchOption.AllDirectories);
            if (edits.Length == 0)
                throw new InvalidDataException(EnglishText.Text("The edits folder is empty. Import at least one asset."));
            var looseMapEnts = LooseMapEntsEdits(project, edits);
            var roots = Roots(project, edits);
            if (roots.Count == 0 && looseMapEnts.Count > 0)
                return BuildLooseMapEntsOnly(project, paths, looseMapEnts);
            if (roots.Count == 0)
                throw new InvalidDataException(EnglishText.Text("No supported FF assets in edits."));
            // The edited project can come from any supported T6 FF. The Redacted auth
            // template is separate and is used only to produce the output container.
            string authTemplate = paths.RedactedTemplateFile.Trim();
            if (authTemplate.Length == 0 && project.FastfileName.Equals("patch_redacted.ff", StringComparison.OrdinalIgnoreCase) && Workspace.IsRedacted(project.Fastfile))
                authTemplate = project.Fastfile;
            if (authTemplate.Length == 0 && !string.IsNullOrWhiteSpace(paths.GameDirectory))
                authTemplate = Path.Combine(paths.GameDirectory, "zone", "redacted", "patch_redacted.ff");
            if (!File.Exists(authTemplate))
                throw new FileNotFoundException(EnglishText.Text("Redacted patch_redacted.ff template not found. Specify it in Settings."), authTemplate);
            if (!Workspace.IsRedacted(authTemplate))
                throw new InvalidDataException(EnglishText.Text("Selected template is not Redacted FF."));
            string captureFile = File.Exists(project.Capture) && authTemplate == project.Fastfile ? project.Capture : paths.CaptureFile;
            CaptureValidator.Validate(captureFile);
            // Keep paths passed to native OAT tools short as well as enabling long paths
            // in Workbench: OAT and Windows configurations may have different limits.
            var run = Workspace.NewRun(paths.OutputDirectory, "redacted");
            var stage = Path.Combine(run, "s");
            var assets = Path.Combine(stage, "a");
            var source = Path.Combine(stage, "source");
            var zone = Path.Combine(stage, "zone");
            Directory.CreateDirectory(source);
            WorkbenchProject.CopyTree(project.Assets, assets, cancellation);
            WorkbenchProject.CopyTree(project.Edits, assets, cancellation);
            var knownMaterials = AssetNameNormalizer.ReadMaterialNames(project.Inventory, project.Zone);
            string editedMaterials = Path.Combine(project.Edits, "materials");
            if (Directory.Exists(editedMaterials))
                foreach (var materialFile in Directory.GetFiles(editedMaterials, "*.json", SearchOption.AllDirectories))
                {
                    string relativeMaterial = Helpers.GetRelativePath(editedMaterials, materialFile).Replace('\\', '/');
                    string materialName = Path.ChangeExtension(relativeMaterial, null).Replace('\\', '/');
                    knownMaterials.Add(materialName);
                }
            string modelExport = Path.Combine(assets, "model_export");
            if (Directory.Exists(modelExport))
                foreach (var glb in Directory.GetFiles(modelExport, "*.glb", SearchOption.AllDirectories))
                    foreach (var change in GlbModule.NormalizeMaterialNamesInPlace(glb, knownMaterials))
                        _log?.Invoke(change + " File: " + glb);
            // A new zone lists only changed roots. OAT adds direct material/image dependencies when loading them.
            File.WriteAllLines(Path.Combine(source, "patch_redacted.zone"),
                new[] { ">game,T6" }.Concat(roots.Select(root => root.Type + "," + root.Name)), Encoding.UTF8);
            var imageFolder = Path.Combine(assets, "images");
            var converterLog = new StringBuilder();
            if (Directory.Exists(imageFolder))
                foreach (var batch in edits.Where(file => file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    .Select(file => Path.Combine(assets, Helpers.GetRelativePath(project.Edits, file)))
                    .Chunk(8))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var conversion = await _runner.RunAsync(Path.Combine(paths.BinDirectory, "ImageConverter.exe"),
                        new[] { "--no-color", "--t6" }.Concat(batch), stage, null, cancellation);
                    converterLog.AppendLine(conversion.Output);
                    conversion.RequireSuccess();
                }
            await Helpers.WriteAllTextAsync(Path.Combine(run, "conversion.log"), converterLog.ToString(), cancellation);
            var baseZones = new List<string>();
            if (roots.Any(root => root.Type.Equals("xmodel", StringComparison.OrdinalIgnoreCase)))
            {
                // Stock XModel materials may be present in common_mp.ff but absent from the raw export.
                // Load that zone only as a build-time dependency; the standalone output zone still lists only edits.
                var commonMp = new[]
                {
                Path.Combine(paths.GameDirectory, "zone", "all", "common_mp.ff"),
                Path.Combine(paths.GameDirectory, "zone", "english", "common_mp.ff")
            }.FirstOrDefault(File.Exists);
                if (commonMp == null)
                    throw new FileNotFoundException(EnglishText.Text("A standalone XModel build requires common_mp.ff in the game folder."));
                baseZones.Add(commonMp);
            }
            if (roots.Any(root => root.Type.Equals("weapon", StringComparison.OrdinalIgnoreCase)))
            {
                // Weapon UI icons can be references in common_mp and absent from
                // patch_mp; their stock definitions are in code_post_gfx_mp.
                // Load it only as a build-time base, not as an output root.
                var codePost = new[] { Path.Combine(paths.GameDirectory, "zone", "all", "code_post_gfx_mp.ff"), Path.Combine(paths.GameDirectory, "zone", "english", "code_post_gfx_mp.ff") }.FirstOrDefault(File.Exists);
                if (codePost != null) baseZones.Add(codePost);
                else _log?.Invoke(EnglishText.Text("Zone code_post_gfx_mp.ff was not found for weapon dependencies; some icon materials may be missing."));
                var patchMp = new[] { Path.Combine(paths.GameDirectory, "zone", "all", "patch_mp.ff"), Path.Combine(paths.GameDirectory, "zone", "english", "patch_mp.ff") }.FirstOrDefault(File.Exists);
                if (patchMp == null)
                    throw new FileNotFoundException(EnglishText.Text("Building weapons requires patch_mp.ff in the game folder."));
                baseZones.Add(patchMp);
            }
            // Native project zones can provide references that OAT exported without payload.
            // The output zone still contains only selected roots and their linker dependencies.
            if (!Workspace.IsRedacted(project.Fastfile) && !baseZones.Any(zoneFile => Path.GetFileName(zoneFile).Equals(project.FastfileName, StringComparison.OrdinalIgnoreCase)))
                baseZones.Add(project.Fastfile);
            // Older projects may have been created before referenced technique sets were collected.
            // Resolve missing sets into this run only; do not modify the original project.
            var referenceAssets = Path.Combine(stage, "referenced-techsets");
            await ReferencedTechniqueSets.ResolveAsync(ReferencedTechniqueSets.ReadReferences(project.Zone, referencesOnly: false), assets, referenceAssets, project.SourceSearchPaths, paths.BinDirectory, Path.Combine(run, "reference-export"), _log, cancellation, project.Dependencies);
            var args = new List<string> { "--no-color", "--source-search-path", source,
            "--asset-search-path", string.Join(";", new[] { assets, project.Dependencies,
                referenceAssets, Path.Combine(paths.GameDirectory, "data"), Path.Combine(paths.BinDirectory, "raw", "t6") }) };
            foreach (var baseZone in baseZones)
            {
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--load" }, new string[] { baseZone })));
            }
            args.AddRange(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--output-folder" }, new string[] { zone })), new string[] { "patch_redacted" })));
            var capture = captureFile;
            var build = await _runner.RunAsync(Path.Combine(paths.BinDirectory, "Linker.exe"), args, stage, capture, cancellation, authTemplate);
            await Helpers.WriteAllTextAsync(Path.Combine(run, "build.log"), build.Output, CancellationToken.None);
            build.RequireSuccess();
            var ff = Path.Combine(zone, "patch_redacted.ff");
            if (!File.Exists(ff) || !Workspace.IsRedacted(ff))
                throw new InvalidDataException(EnglishText.Text("Linker did not create Redacted patch_redacted.ff."));
            var listed = await _runner.RunAsync(Path.Combine(paths.BinDirectory, "Unlinker.exe"),
                Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--list" })), new string[] { ff })), stage, capture, cancellation);
            await Helpers.WriteAllTextAsync(Path.Combine(run, "inventory.log"), listed.Output, CancellationToken.None);
            listed.RequireSuccess();
            var inventory = ReadInventory(listed.Output);
            foreach (var root in roots)
            {
                if (root.Type == "localize" && root.Name == "patch_redacted")
                {
                    // A .str file is a source group; OAT emits one localize asset per REFERENCE.
                    string locFile = Path.Combine(assets, "english", "localizedstrings", "patch_redacted.str");
                    foreach (string line in File.ReadLines(locFile))
                    {
                        var fields = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length == 2 && fields[0].Equals("REFERENCE", StringComparison.OrdinalIgnoreCase) && !inventory.Contains("localize," + fields[1]))
                        {
                            throw new InvalidDataException(EnglishText.Text("The built FF is missing localization ") + fields[1]);
                        }
                    }
                }
                else if (!inventory.Contains(root.Type + "," + root.Name))
                {
                    throw new InvalidDataException(EnglishText.Format("The built FF is missing modified asset {0},{1}.", root.Type, root.Name));
                }
            }
            var package = Path.Combine(run, "package");
            var outputFf = Path.Combine(package, "zone", "redacted", "patch_redacted.ff");
            Copy(ff, outputFf);
            Copy(authTemplate, Path.Combine(package, "backup", "patch_redacted.ff"));
            Copy(paths.ProbeFile, Path.Combine(package, "Plugins", "ZoneAuthProbe.Red32n"));
            var hash = Workspace.Hash(ff);
            var includedImages = new List<string>();
            Directory.CreateDirectory(imageFolder);
            foreach (var image in Directory.GetFiles(imageFolder, "*.iwi", SearchOption.AllDirectories))
            {
                var installed = Path.Combine(paths.GameDirectory, "data", "images", Path.GetFileName(image));
                if (File.Exists(installed) && Workspace.Hash(installed) == Workspace.Hash(image))
                    continue;
                Copy(image, Path.Combine(package, "data", "images", Path.GetFileName(image)));
                includedImages.Add(Path.GetFileName(image));
            }
            var includedMapEnts = new List<object>();
            foreach (var edit in looseMapEnts)
            {
                var relative = Helpers.GetRelativePath(Path.Combine(project.Edits, "mapents"), edit);
                var target = Path.Combine(package, "data", relative);
                var normalized = relative.Replace('\\', '/');
                var original = project.AssetPath(normalized.Substring(0, (normalized.Length - ((".mapents".Length))) - (0)) + ".ents");
                includedMapEnts.Add(CopyMapEntsAndDescribe(edit, target, normalized, original));
            }
            var remarks = new List<string> { EnglishText.Text("Offline build and asset inventory verified; client and match have not been tested.") };
            remarks.AddRange(build.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(line => line.TrimStart().StartsWith("WARN:", StringComparison.OrdinalIgnoreCase)));
            if (roots.Contains(("xmodel", "iw6_viewmodel_honeybadger")) && !roots.Contains(("weapon", "honeybadger_mp")))
                remarks.Add(EnglishText.Text("iw6_viewmodel_honeybadger only changes the first-person weapon model; new weapon asset and world model are not included."));
            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                status = "OFFLINE_ONLY",
                mode = "standalone edited assets",
                sourceName = project.FastfileName,
                sourceSha256 = Workspace.Hash(project.Fastfile),
                templateName = Path.GetFileName(authTemplate),
                templateSha256 = Workspace.Hash(authTemplate),
                warnings = remarks.ToArray(),
                candidateSha256 = hash,
                linkedBaseZones = baseZones,
                editedFiles = edits.Select(file => Helpers.GetRelativePath(project.Edits, file).Replace('\\', '/')).ToArray(),
                requestedRoots = roots.Select(root => root.Type + "," + root.Name).ToArray(),
                actualAssets = inventory.Order(StringComparer.Ordinal).ToArray(),
                includedLooseIwi = includedImages,
                includedLooseMapEnts = includedMapEnts,
                note = "MapEnts edits are packaged under data/maps/mp with the full <map>.d3dbsp.mapents filename and are omitted from the rebuilt FF. Every packaged MapEnts file is checked byte-for-byte against its edit and reports source/output SHA-256. Runtime still needs a manual check."
            });
            File.WriteAllText(Path.Combine(package, "README_TEST.md"),
                EnglishText.Text("# Standalone Redacted v43 mod\n\n") +
                EnglishText.Text("FF named patch_redacted.ff for standard loading and built from edits with the required dependencies. ") +
                (baseZones.Count > 0 ? EnglishText.Text("Standard XModel dependencies are taken from common_mp.ff during build. ") : "") +
                EnglishText.Text("Previous patch_redacted assets are not included automatically. Report and full asset inventory: ../report.json. ") +
                EnglishText.Text("MapEnts edits are placed in data/maps/mp with the full name <map>.d3dbsp.mapents and are not added to the FF. The user has already confirmed that a loose file with this name displays entities; this specific package must be tested manually.\n\n") +
                EnglishText.Text("Close t6mpv43.exe before installing and back up the current game file patch_redacted.ff. The normal ZoneAuthProbe creates no .log/.bin files; a separate debug variant is available for diagnostics. Install only one plugin at a time. ") +
                EnglishText.Text("Copy zone/, Plugins/ and if present data/ into the game. To roll back, restore your own backed-up files. ") +
                EnglishText.Text("Game loading and availability of the modified asset in a match have not yet been tested.\n"), Encoding.UTF8);
            return new OperationResult(package, EnglishText.Format("Standalone FF: {0} modified roots, {1} assets, {2} loose IWI, {3} loose MapEnts.", roots.Count, inventory.Count, includedImages.Count, includedMapEnts.Count),
                remarks.ToArray());
        }

        static List<string> LooseMapEntsEdits(WorkbenchProject project, string[] edits)
        {
            var files = edits.Where(file => Helpers.GetRelativePath(project.Edits, file).Replace('\\', '/') .StartsWith("mapents/", StringComparison.OrdinalIgnoreCase)).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var relative = Helpers.GetRelativePath(Path.Combine(project.Edits, "mapents"), file).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Length != 3 || !parts[0].Equals("maps", StringComparison.OrdinalIgnoreCase) ||
                    !parts[1].Equals("mp", StringComparison.OrdinalIgnoreCase) ||
                    parts.Any(part => ((part == "") || (part == ".")) || (part == "..")) ||
                    !relative.EndsWith(".d3dbsp.mapents", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Text("Loose MapEnts must have the path mapents/maps/mp/<map>.d3dbsp.mapents: ") + relative);
                var sourceEnts = project.AssetPath(relative.Substring(0, (relative.Length - ((".mapents".Length))) - (0)) + ".ents");
                if (!File.Exists(sourceEnts))
                    throw new InvalidDataException(EnglishText.Text("The project has no original MapEnts for ") + relative);
                var originalText = File.ReadAllText(sourceEnts, new UTF8Encoding(false, true));
                var editedText = File.ReadAllText(file, new UTF8Encoding(false, true));
                LooseMapEnts.ValidateAdditive(originalText, editedText);
                if (!seen.Add(relative))
                    throw new InvalidDataException(EnglishText.Text("Duplicate loose MapEnts: ") + relative);
            }
            return files;
        }

        /// <summary>
        /// Performs the LooseMapEntsOnly build operation. Build a compact Redacted test package from staged edits.
        /// </summary>
        static OperationResult BuildLooseMapEntsOnly(WorkbenchProject project, WorkbenchPaths paths, IReadOnlyList<string> edits)
        {
            var run = Workspace.NewRun(paths.OutputDirectory, "mapents-loose");
            var package = Path.Combine(run, "package");
            var included = new List<object>();
            foreach (var edit in edits)
            {
                var relative = Helpers.GetRelativePath(Path.Combine(project.Edits, "mapents"), edit);
                var target = Path.Combine(package, "data", relative);
                var normalized = relative.Replace('\\', '/');
                var original = project.AssetPath(normalized.Substring(0, (normalized.Length - ((".mapents".Length))) - (0)) + ".ents");
                included.Add(CopyMapEntsAndDescribe(edit, target, normalized, original));
            }

            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                status = "LOOSE_MAPENTS_ONLY",
                sourceFastfile = Path.GetFileName(project.Fastfile),
                includedLooseMapEnts = included,
                note = "This package contains only loose Redacted MapEnts entity-string overrides; it contains no FF or ZoneAuth plugin. Each output file is checked byte-for-byte against the edit and reports source/output SHA-256."
            });
            File.WriteAllText(Path.Combine(package, "README_TEST.md"),
                "# Loose MapEnts override for Redacted v43\n\n" +
                "This package contains only data/maps/mp/<map>.d3dbsp.mapents. It does not contain or replace patch_redacted.ff, and it does not add an authorization plugin. Every original entity and key/value pair must remain unchanged and in the original order; new entities can be appended. Whitespace and line-ending changes are allowed. The importer and builder reject removed or modified base entities.\n\n" +
                "Before installing, close the game and back up any existing override. In the parent report.json, verify that each MapEnts entry has matching sourceSha256 and outputSha256 and byteIdentical=true; for additive edits, originalEntitiesPreserved must be true. Copy only the loose file from data/maps/mp/ to the same game path, keeping the full .d3dbsp.mapents filename; do not replace patch_redacted.ff.\n\n" +
                "Reload the local map and check the edited entity. To roll back, restore your backup or remove only the installed .d3dbsp.mapents override. The original MapEnts trigger arrays remain in the map fastfile. Runtime still needs manual verification.\n",
                Encoding.UTF8);
            return new OperationResult(package, EnglishText.Format("Loose MapEnts: {0} files; FF was not rebuilt.", included.Count), new string[] { EnglishText.Text("The package contains only entity-string override; the new build must be tested in a local match.") });
        }

        /// <summary>
        /// Performs the MapEntsAndDescribe copy operation. Build a compact Redacted test package from staged edits.
        /// </summary>
        static object CopyMapEntsAndDescribe(string source, string target, string relative, string original)
        {
            var sourceHash = Workspace.Hash(source);
            var originalHash = Workspace.Hash(original);
            long originalBytes = new FileInfo(original).Length;
            Copy(source, target);
            var outputHash = Workspace.Hash(target);
            bool byteIdentical = FilesEqual(source, target);
            var originalText = File.ReadAllText(original, new UTF8Encoding(false, true));
            var outputText = File.ReadAllText(target, new UTF8Encoding(false, true));
            var validation = LooseMapEnts.ValidateAdditive(originalText, outputText);
            if (!byteIdentical || !sourceHash.Equals(outputHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException(EnglishText.Text("MapEnts package does not match the imported edit byte for byte: ") + relative);
            return new
            {
                path = relative,
                bytes = new FileInfo(target).Length,
                sourceSha256 = sourceHash,
                outputSha256 = outputHash,
                byteIdentical,
                originalEntityStringBytes = originalBytes,
                originalEntityStringSha256 = originalHash,
                originalEntitiesPreserved = validation.OriginalEntitiesPreserved,
                originalEntityCount = validation.OriginalEntityCount,
                outputEntityCount = validation.EditedEntityCount
            };
        }

        static bool FilesEqual(string first, string second)
        {
            var firstInfo = new FileInfo(first);
            var secondInfo = new FileInfo(second);
            if (firstInfo.Length != secondInfo.Length)
                return false;
            using (var a = File.OpenRead(first))
            {
                using (var b = File.OpenRead(second))
                {
                    byte[] left = new byte[81920];
                    byte[] right = new byte[81920];
                    while (true)
                    {
                        int leftRead = a.Read(left, 0, left.Length);
                        int rightRead = b.Read(right, 0, right.Length);
                        if (leftRead != rightRead)
                            return false;
                        if (leftRead == 0)
                            return true;
                        if (!left.AsSpan(0, leftRead).SequenceEqual(right.AsSpan(0, rightRead)))
                            return false;
                    }
                }
            }
        }
        static HashSet<(string Type, string Name)> Roots(WorkbenchProject project, string[] edits)
        {
            var roots = new HashSet<(string, string)>();
            var fileRoots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(project.Inventory))
            {
                using (var inventory = JsonDocument.Parse(File.ReadAllText(project.Inventory)))
                {
                    foreach (var asset in inventory.RootElement.EnumerateArray())
                    {
                        string type = asset.GetProperty("type").GetString() ?? "";
                        string name = AssetNameNormalizer.NormalizeName(asset.GetProperty("name").GetString() ?? "").Replace('\\', '/');
                        if (type != "rawfile" && type != "stringtable") continue;
                        if (!fileRoots.TryGetValue(name, out var matches)) { matches = new List<string>(); fileRoots.Add(name, matches); }
                        if (!matches.Contains(type, StringComparer.OrdinalIgnoreCase)) matches.Add(type);
                    }
                }
            }
            foreach (var file in edits)
            {
                var relative = Helpers.GetRelativePath(project.Edits, file).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Any(part => ((part == "") || (part == ".")) || (part == "..")))
                    throw new InvalidDataException(EnglishText.Text("Invalid edits path: ") + relative);
                string stem = Path.GetFileNameWithoutExtension(relative);
                switch (parts[0].ToLowerInvariant())
                {
                    case "model_export" when relative.EndsWith(".glb", StringComparison.OrdinalIgnoreCase):
                        var owners = Directory.GetFiles(Path.Combine(project.Assets, "xmodel"), "*.json")
                            .Concat(Directory.Exists(Path.Combine(project.Edits, "xmodel")) ? Directory.GetFiles(Path.Combine(project.Edits, "xmodel"), "*.json") : new string[0])
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(model => ModelUses(model, relative)).Select(Path.GetFileNameWithoutExtension)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (owners.Length != 1)
                            throw new InvalidDataException(EnglishText.Text("The GLB requires exactly one xmodel JSON referencing this LOD: ") + relative);
                        roots.Add(("xmodel", owners[0]));
                        break;
                    case "xmodel" when relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("xmodel", stem));
                        break;
                    case "materials" when relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("material", Path.ChangeExtension(relative.Substring("materials/".Length), null).Replace('\\', '/')));
                        break;
                    case "images" when relative.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("image", Path.ChangeExtension(relative.Substring("images/".Length), null).Replace('\\', '/')));
                        break;
                    case "mp" when relative.Equals("mp/statstable.csv", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("stringtable", "mp/statstable.csv"));
                        break;
                    case "english" when relative.Equals("english/localizedstrings/patch_redacted.str", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("localize", "patch_redacted"));
                        break;
                    case "weapons":
                        roots.Add(("weapon", Path.GetFileName(relative)));
                        break;
                    case "xanim":
                        roots.Add(("xanim", Path.GetFileName(relative)));
                        break;
                    case "techsets" when relative.EndsWith(".techset", StringComparison.OrdinalIgnoreCase):
                        roots.Add(("techniqueset", stem));
                        break;
                    case "mapents":
                        if (!relative.EndsWith(".mapents", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(EnglishText.Text("Expected a loose .mapents file: ") + relative);
                        break;
                    default:
                        if (fileRoots.TryGetValue(relative, out var matchingTypes) && matchingTypes.Count == 1)
                        {
                            roots.Add((matchingTypes[0], relative));
                        }
                        else if (matchingTypes != null && matchingTypes.Count > 1)
                            throw new InvalidDataException(EnglishText.Text("Ambiguous edit type ") + relative + ": " + string.Join(", ", matchingTypes));
                        else
                            throw new NotSupportedException(EnglishText.Text("Could not determine a supported edit type ") + relative + EnglishText.Text(" from the source FF inventory."));
                        break;
                }
            }
            return roots;
        }

        static bool ModelUses(string file, string relative)
        {
            using (var json = JsonDocument.Parse(File.ReadAllText(file)))
            {
                return json.RootElement.GetProperty("lods").EnumerateArray()
                .Any(lod => string.Equals(lod.GetProperty("file").GetString()?.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>
        /// Read/deduplicate asset identities from manifest inventory or zone source.
        /// </summary>
        static HashSet<string> ReadInventory(string output)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool content = false;
            foreach (var line in output.Split('\n'))
            {
                var value = line.Trim();
                if (value.Equals("Content:", StringComparison.OrdinalIgnoreCase))
                {
                    content = true;
                    continue;
                }
                if (content && value.StartsWith("Unloaded zone", StringComparison.OrdinalIgnoreCase))
                    break;
                if (!content)
                    continue;
                int comma = value.IndexOf(',');
                if (comma > 0 && comma < value.Length - 1)
                    result.Add(value.Substring(0, (comma) - (0)).Trim() + "," + AssetNameNormalizer.NormalizeName(value.Substring((comma + 1))));
            }
            return result;
        }

        /// <summary>
        /// Copy an operation-owned file, creating its parent directory when necessary.
        /// </summary>
        static void Copy(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            File.Copy(from, to, overwrite: false);
        }
    }
}
