using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Offline reconstruction of the real zone, with every model and LOD compared.
    /// </summary>
    internal sealed class FullRoundTrip
    {
        private readonly Action<string> _log;
        private readonly ToolRunner _runner;

        internal FullRoundTrip(Action<string> log = null)
        {
            _log = log;
            _runner = new ToolRunner(log);
        }

        /// <summary>
        /// Stages edits, rebuilds the zone, re-exports it and writes semantic/content comparison evidence.
        /// </summary>
        public async Task<OperationResult> RunAsync(ExportRequest request, CancellationToken cancellation = default)
        {
            var run = Workspace.NewRun(request.OutputRoot, "full-roundtrip");
            var originalHash = Workspace.Hash(request.Fastfile);
            // Force a full GLB/DDS export: OBJ cannot carry the original rigging.
            OperationResult exported;
            if (!string.IsNullOrWhiteSpace(request.ProjectAssetsRoot))
            {
                var snapshot = Workspace.NewRun(run, "project-source");
                var assetsRoot = Path.GetFullPath(request.ProjectAssetsRoot);
                if (!Directory.Exists(assetsRoot))
                    throw new DirectoryNotFoundException(assetsRoot);
                WorkbenchProject.CopyTree(assetsRoot, Path.Combine(snapshot, "dump"), cancellation);
                Directory.CreateDirectory(Path.Combine(snapshot, "input"));
                File.Copy(request.Fastfile, Path.Combine(snapshot, "input", Path.GetFileName(request.Fastfile)));
                exported = new OperationResult(snapshot, EnglishText.Text("Assets loaded from the project"), new string[0]);
                _log?.Invoke(EnglishText.Text("Build uses the saved project assets."));
            }
            else
            {
                exported = await new AssetPipelineModule(_log).ExportAsync(new ExportRequest(request)
                {
                    OutputRoot = run,
                    ModelFormat = "GLB",
                    ImageFormat = "DDS",
                    IncludeAssets = "",
                    AllowMissingIdentityNormal = true
                }, cancellation: cancellation);
            }
            var original = Path.Combine(exported.Directory, "dump");
            var editedByRelative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var addedModelFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addedModelNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.EditedAssetsRoot))
            {
                var editRoot = Path.GetFullPath(request.EditedAssetsRoot);
                if (!Directory.Exists(editRoot))
                    throw new DirectoryNotFoundException(editRoot);
                var originalModelDirectory = Path.Combine(original, "xmodel");
                var editedModelDirectory = Path.Combine(editRoot, "xmodel");
                if (Directory.Exists(editedModelDirectory))
                {
                    foreach (var modelJson in Directory.GetFiles(editedModelDirectory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        var relativeJson = Helpers.GetRelativePath(editRoot, modelJson).Replace('\\', '/');
                        if (File.Exists(Path.Combine(originalModelDirectory, Path.GetFileName(modelJson))))
                        {
                            continue;
                        }
                        using (var model = JsonDocument.Parse(File.ReadAllText(modelJson)))
                        {
                            var root = model.RootElement;
                            if (!root.TryGetProperty("_type", out var type) || type.GetString() != "xmodel" || !root.TryGetProperty("_game", out var game) || game.GetString() != "t6" ||
                                !root.TryGetProperty("lods", out var lods) || lods.ValueKind != JsonValueKind.Array || lods.GetArrayLength() == 0)
                            {
                                throw new InvalidDataException(EnglishText.Text("The added model must be T6 xmodel JSON with LODs: ") + relativeJson);
                            }
                            if (!Regex.IsMatch(relativeJson, @"^xmodel/[A-Za-z0-9_]+\.json$", RegexOptions.IgnoreCase))
                            {
                                throw new InvalidDataException(EnglishText.Text("Unsupported new model path: ") + relativeJson);
                            }
                            addedModelNames.Add(Path.GetFileNameWithoutExtension(modelJson));
                            addedModelFiles.Add(relativeJson);
                            foreach (var lod in lods.EnumerateArray())
                            {
                                if (!lod.TryGetProperty("file", out var fileProperty) || fileProperty.ValueKind != JsonValueKind.String)
                                {
                                    throw new InvalidDataException(EnglishText.Text("New model LOD has no GLB file: ") + relativeJson);
                                }
                                var lodPath = fileProperty.GetString().Replace('\\', '/');
                                if (!Regex.IsMatch(lodPath, @"^model_export/[A-Za-z0-9_]+\.glb$", RegexOptions.IgnoreCase))
                                {
                                    throw new InvalidDataException(EnglishText.Text("New model must reference a GLB in model_export: ") + lodPath);
                                }
                                if (!File.Exists(Path.Combine(editRoot, lodPath.Replace('/', Path.DirectorySeparatorChar))) && !File.Exists(Path.Combine(original, lodPath.Replace('/', Path.DirectorySeparatorChar))))
                                {
                                    throw new FileNotFoundException(EnglishText.Text("GLB LOD for the new model was not found."), lodPath);
                                }
                                addedModelFiles.Add(lodPath);
                            }
                        }
                    }
                }
                foreach (var edit in Directory.GetFiles(editRoot, "*", SearchOption.AllDirectories))
                {
                    var relative = Helpers.GetRelativePath(editRoot, edit).Replace('\\', '/');
                    var existsInSource = File.Exists(Path.Combine(original, relative));
                    if (relative.StartsWith("zone_source/", StringComparison.OrdinalIgnoreCase) || (!existsInSource && !addedModelFiles.Contains(relative) && !IsAllowedAddedAsset(relative)))
                    {
                        throw new InvalidDataException(EnglishText.Text("You can replace exported assets or add material JSON / image IWI-DDS and validated T6 xmodel GLB: ") + relative);
                    }
                    editedByRelative.Add(relative, edit);
                }
                if (editedByRelative.Count == 0)
                {
                    throw new InvalidDataException(EnglishText.Text("The edits folder contains no files."));
                }
                _log?.Invoke(EnglishText.Format("Asset edits: {0}.", editedByRelative.Count));
            }
            var sourceCopy = Path.Combine(exported.Directory, "input", Path.GetFileName(request.Fastfile));
            var definition = Directory.GetFiles(Path.Combine(original, "zone_source"), "*.zone").Single();
            var entries = File.ReadAllLines(definition).Select(AssetNameNormalizer.NormalizeZoneLine)
                .Where(l => l.Length > 0 && !l.StartsWith('>') && !l.StartsWith("//"))
                .Concat(addedModelNames.Select(name => "xmodel," + name))
                .Distinct(StringComparer.Ordinal).ToArray();
            var modelEntries = entries.Where(l => l.StartsWith("xmodel,", StringComparison.Ordinal)).ToArray();
            if (modelEntries.Length == 0)
            {
                throw new InvalidDataException(EnglishText.Text("FF contains no models."));
            }
            var modelDir = Path.Combine(run, "all-models");
            var modelAssets = Path.Combine(modelDir, "assets");
            var modelSource = Path.Combine(modelDir, "source");
            Directory.CreateDirectory(Path.Combine(modelSource, "assetlist"));
            File.WriteAllLines(Path.Combine(modelSource, "assetlist", "dependencies.csv"), entries.Except(modelEntries));
            var modelsName = "patch_redacted_models_mp";
            File.WriteAllLines(Path.Combine(modelSource, modelsName + ".zone"), new[] { ">game,T6", "ignore,dependencies" }.Concat(modelEntries));
            foreach (var entry in modelEntries)
            {
                var name = entry.Substring(7);
                var relative = $"xmodel/{name}.json";
                if (File.Exists(Path.Combine(original, relative)))
                {
                    CopyFile(original, modelAssets, relative);
                }
                else if (editedByRelative.TryGetValue(relative, out var addedModelJson))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(modelAssets, relative)));
                    File.Copy(addedModelJson, Path.Combine(modelAssets, relative), overwrite: true);
                }
                else
                {
                    throw new FileNotFoundException(EnglishText.Text("Zone model is missing from both the original export and edits folder."), relative);
                }
                using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelAssets, relative))))
                {
                    foreach (var lod in doc.RootElement.GetProperty("lods").EnumerateArray())
                    {
                        var lodFile = lod.GetProperty("file").GetString();
                        var lodInAssets = Path.Combine(modelAssets, lodFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(lodInAssets))
                            continue;
                        if (File.Exists(Path.Combine(original, lodFile.Replace('/', Path.DirectorySeparatorChar))))
                            CopyFile(original, modelAssets, lodFile);
                        else if (File.Exists(Path.Combine(request.EditedAssetsRoot, lodFile.Replace('/', Path.DirectorySeparatorChar))))
                            CopyFile(request.EditedAssetsRoot, modelAssets, lodFile);
                        else
                            throw new FileNotFoundException(EnglishText.Text("GLB LOD not found."), lodFile);
                    }
                }
            }
            foreach (var edit in editedByRelative)
            {
                var target = Path.Combine(modelAssets, edit.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(edit.Value, target, overwrite: true);
            }
            var knownMaterials = entries.Where(l => l.StartsWith("material,", StringComparison.OrdinalIgnoreCase))
                .Select(l => AssetNameNormalizer.NormalizeName(l.Substring(l.IndexOf(',') + 1)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var materialEdit in editedByRelative.Keys.Where(path =>
                         path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                knownMaterials.Add(materialEdit.Substring("materials/".Length, materialEdit.Length - "materials/".Length - ".json".Length));
            await PrepareModelDependencies(modelAssets, original, request, knownMaterials, cancellation);
            // Model comparison rebuilds only xmodel roots. Use T6 game base zones for stock material references.
            // Do not load the source Redacted FF here: this diagnostic zone has a different name and cannot reuse
            // the source FF's Redacted auth template. The actual patch_redacted build below keeps its own template.
            var overlayBases = FindModelBaseZones(request, requireCommon: addedModelNames.Count > 0);
            var modelBuild = await Link(modelDir, modelAssets, modelSource, modelsName, request, overlayBases, cancellation);
            modelBuild.RequireSuccess();
            var modelFf = Path.Combine(modelDir, "zone", modelsName + ".ff");
            var modelDump = await Dump(modelDir, modelFf, request, "xmodel", cancellation,
                useGameSearchPaths: true);
            var modelFiles = Directory.GetFiles(modelAssets, "*", SearchOption.AllDirectories)
                .Select(file => Helpers.GetRelativePath(modelAssets, file).Replace('\\', '/'))
                .Where(relative => relative.StartsWith("xmodel/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("model_export/", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var models = CompareDirectories(modelAssets, modelDump, editedByRelative, modelFiles);
            Workspace.WriteJson(Path.Combine(modelDir, "comparison.json"), new
            {
                status = models.All(c => c.Status == "PASS") ? "PASS" : "FAIL",
                models = modelEntries.Length,
                lodFiles = Directory.GetFiles(modelAssets, "*.glb", SearchOption.AllDirectories).Length,
                materialDependencies = "resolved from staged assets and game base zones",
                files = models,
                binary = BinaryComparison(sourceCopy, modelFf, "Models-only zone: not a reconstruction of the entire source FF")
            });
            _log?.Invoke(EnglishText.Format("All models: {0}/{1} files verified.", models.Count(c => c.Status == "PASS"), models.Count));

            // Preserve source exports. Conversion and build happen only on a new project copy.
            var fullDir = Path.Combine(run, "full-patch");
            var fullAssets = Path.Combine(fullDir, "assets");
            var fullSource = Path.Combine(fullDir, "source");
            Directory.CreateDirectory(fullSource);
            foreach (var file in Directory.GetFiles(original, "*", SearchOption.AllDirectories))
            {
                CopyFile(original, fullAssets, Helpers.GetRelativePath(original, file));
            }
            foreach (var edit in editedByRelative)
            {
                var target = Path.Combine(fullAssets, edit.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(edit.Value, target, overwrite: true);
            }
            NormalizeModelMaterialNames(fullAssets, knownMaterials);
            string nameFull = Path.GetFileNameWithoutExtension(sourceCopy);
            File.WriteAllLines(Path.Combine(fullSource, nameFull + ".zone"), new[] { ">game,T6" }.Concat(entries));
            var converterLog = new System.Text.StringBuilder();
            foreach (var batch in Directory.GetFiles(Path.Combine(fullAssets, "images"), "*.dds", SearchOption.AllDirectories).Chunk(8))
            {
                var converted = await _runner.RunAsync(Path.Combine(request.BinDirectory, "ImageConverter.exe"), new[] { "--no-color", "--t6" }.Concat(batch), fullDir, null, cancellation);
                converterLog.AppendLine(converted.Output);
                converted.RequireSuccess();
            }
            await Helpers.WriteAllTextAsync(Path.Combine(fullDir, "conversion.log"), converterLog.ToString(), cancellation);
            var strict = await Link(Path.Combine(fullDir, "strict"), fullAssets, fullSource, nameFull, request, overlayBases, cancellation, sourceCopy);
            bool strictPassed = strict.ExitCode == 0 && strict.Errors.Length == 0;
            string selectedDir = strictPassed ? Path.Combine(fullDir, "strict") : null;
            string buildMode = strictPassed ? "raw-only" : "failed";
            string[] reuse = new string[0];
            ToolResult assisted = null;
            if (!strictPassed)
            {
                // Separate experiment: preserves unavailable dependencies from the original zone.
                // Every reuse is reported, so this cannot be presented as a complete raw reconstruction.
                var assistedDir = Path.Combine(fullDir, "base-assisted");
                var assistedSource = Path.Combine(assistedDir, "source");
                Directory.CreateDirectory(assistedSource);
                // Its native image descriptor is preserved from the source, but no unavailable pixels are fabricated or packed.
                var unavailable = entries.Where(e => e == "image,$identitynormalmap").ToArray();
                File.WriteAllLines(Path.Combine(assistedSource, nameFull + ".zone"), new[] { ">game,T6" }.Concat(unavailable).Concat(entries.Except(unavailable)));
                assisted = await Link(assistedDir, fullAssets, assistedSource, nameFull, request, overlayBases.Append(sourceCopy).ToArray(), cancellation, sourceCopy);
                reuse = assisted.Output.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("Loaded ") && l.Contains("(src: ") && !l.Contains("(src: disk)")).ToArray();
                if (assisted.ExitCode == 0 && assisted.Errors.Length == 0)
                {
                    selectedDir = assistedDir;
                    buildMode = "base-assisted PARTIAL; original dependencies reused";
                }
            }
            IReadOnlyList<ComparisonEntry> fullComparison = new ComparisonEntry[0];
            object binary = null;
            int exactLooseIwiReused = 0;
            int generatedLooseIwi = 0;
            int sourceLooseIwiUnavailable = 0;
            int changedLooseIwiPayload = 0;
            if (selectedDir != null)
            {
                var rebuilt = Path.Combine(selectedDir, "zone", nameFull + ".ff");
                var fullDump = await Dump(selectedDir, rebuilt, request, "", cancellation,
                    useGameSearchPaths: addedModelNames.Count > 0);
                var comparisonOverrides = editedByRelative.Where(item => !item.Key.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase)) .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
                var comparisonFiles = Directory.GetFiles(original, "*", SearchOption.AllDirectories)
                    .Select(file => Helpers.GetRelativePath(original, file).Replace('\\', '/'))
                    .Concat(comparisonOverrides.Keys)
                    .Where(relative => !relative.StartsWith("zone_source/", StringComparison.OrdinalIgnoreCase))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                fullComparison = CompareDirectories(original, fullDump, comparisonOverrides, comparisonFiles);
                var generatedIpaks = Directory.GetFiles(selectedDir, "*.ipak", SearchOption.AllDirectories);
                if (generatedIpaks.Length != 0)
                {
                    throw new InvalidDataException("Redacted full-build unexpectedly created IPAK: " + string.Join(", ", generatedIpaks));
                }
                var release = Path.Combine(run, "release");
                CopyFile(Path.GetDirectoryName(rebuilt), Path.Combine(release, "zone", "redacted"), Path.GetFileName(rebuilt));
                var sourceIwiRoots = request.SearchPaths.Select(path => Path.Combine(path, "images")).Where(Directory.Exists).ToArray();
                foreach (var iwi in Directory.GetFiles(Path.Combine(fullAssets, "images"), "*.iwi", SearchOption.AllDirectories))
                {
                    var relative = Helpers.GetRelativePath(fullAssets, iwi);
                    var destination = Path.Combine(release, "data", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    var sourceCandidates = sourceIwiRoots.Select(root => Path.Combine(root, Path.GetFileName(iwi)))
                        .Where(File.Exists).ToArray();
                    var exact = sourceCandidates.FirstOrDefault(candidate => SameIwiPixelPayload(candidate, iwi));
                    if (exact != null)
                    {
                        File.Copy(exact, destination);
                        exactLooseIwiReused++;
                    }
                    else
                    {
                        File.Copy(iwi, destination);
                        generatedLooseIwi++;
                        if (sourceCandidates.Length == 0)
                            sourceLooseIwiUnavailable++;
                        else
                            changedLooseIwiPayload++;
                    }
                }
                var looseIwiChecks = editedByRelative.Where(item => item.Key.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase)).Select(item =>
                    {
                        var releaseIwi = Path.Combine(release, "data", item.Key.Replace('/', Path.DirectorySeparatorChar));
                        var passed = File.Exists(releaseIwi) && Workspace.Hash(item.Value) == Workspace.Hash(releaseIwi);
                        return new ComparisonEntry(item.Key, passed ? "PASS" : "FAIL", passed ? "Loose IWI SHA-256 preserved in release." : "Loose IWI is missing or changed in release.");
                    });
                fullComparison = fullComparison.Concat(looseIwiChecks).ToArray();
                Workspace.WriteJson(Path.Combine(selectedDir, "comparison.json"), new
                {
                    files = fullComparison
                });
                binary = BinaryComparison(sourceCopy, rebuilt, "Redacted source versus Redacted reconstruction; opaque signature retained, not verified");
                Workspace.WriteJson(Path.Combine(selectedDir, "binary-comparison.json"), binary);
                var originalList = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Unlinker.exe"),
                    Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, 
                    new string[] { "--list" })), new string[] { sourceCopy })), selectedDir, Workspace.IsRedacted(sourceCopy) ? request.CaptureFile : null, cancellation);
                var rebuiltList = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Unlinker.exe"),
                    Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--list" })), 
                    new string[] { rebuilt })), selectedDir, Workspace.IsRedacted(rebuilt) ? request.CaptureFile : null, cancellation);
                originalList.RequireSuccess();
                rebuiltList.RequireSuccess();
                await Helpers.WriteAllTextAsync(Path.Combine(selectedDir, "original-list.log"), originalList.Output, cancellation);
                await Helpers.WriteAllTextAsync(Path.Combine(selectedDir, "rebuilt-list.log"), rebuiltList.Output, cancellation);
                var a = Inventory(originalList.Output);
                var b = Inventory(rebuiltList.Output);
                Workspace.WriteJson(Path.Combine(selectedDir, "inventory-comparison.json"), new
                {
                    originalAssets = a.Count,
                    rebuiltAssets = b.Count,
                    missing = a.Except(b).ToArray(),
                    added = b.Except(a).ToArray()
                });
            }
            var numericReports = new List<object>();
            foreach (var entry in models.Where(c => c.Status == "FAIL" && c.File.EndsWith(".glb")))
            {
                var deltas = new Dictionary<string, double>();
                string structuralError = null;
                try
                {
                    GlbModule.Compare(Path.Combine(modelAssets, entry.File), Path.Combine(modelDump, entry.File), (semantic, delta) => deltas[semantic] = Math.Max(deltas.GetValueOrDefault(semantic), delta), measureOnly: true);
                }
                catch (Exception ex) 
                { 
                    structuralError = ex.Message; 
                }
                numericReports.Add(new
                {
                    entry.File,
                    maxDeltaBySemantic = deltas,
                    structuralError,
                    status = "MEASURED; original strict failure retained"
                });
            }
            Workspace.WriteJson(Path.Combine(modelDir, "numeric-differences.json"), numericReports);
            bool unchanged = Workspace.Hash(request.Fastfile) == originalHash;
            if (!unchanged)
            {
                throw new IOException(EnglishText.Text("Source FF has changed."));
            }
            string status = models.Any(c => c.Status != "PASS") ? "FAIL" : !strictPassed || fullComparison.Any(c => c.Status != "PASS") ? "PARTIAL" : "PASS";
            var remarks = models.Where(c => c.Status != "PASS").Select(c => c.File + ": " + c.Details).Concat(fullComparison.Where(c => c.Status != "PASS").Select(c => c.File + ": " + c.Details)).ToList();
            remarks.Add("Redacted container / loose IWI: loading in game NOT VERIFIED; opaque source signature reused.");
            if (!strictPassed)
            {
                remarks.Add(EnglishText.Text("Full build using only the raw export failed: ") + string.Join("\n", strict.Errors));
            }
            if (reuse.Length > 0)
            {
                remarks.Add(EnglishText.Text("Dependencies reused from the source zone: ") + string.Join("; ", reuse));
            }
            if (editedByRelative.Count > 0)
            {
                remarks.Add(EnglishText.Format("Edits applied to {0} exported assets; visual results have not been tested in a match.", editedByRelative.Count));
            }
            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                status,
                source = request.Fastfile,
                sourceSha256 = originalHash,
                sourceUnchanged = unchanged,
                exportDirectory = exported.Directory,
                allModelsDirectory = modelDir,
                modelAssetCount = modelEntries.Length,
                editedAssets = editedByRelative.Select(item => new { file = item.Key, sha256 = Workspace.Hash(item.Value) }).ToArray(),
                addedAssets = editedByRelative.Where(item => !File.Exists(Path.Combine(original, item.Key))).Select(item => new { file = item.Key, sha256 = Workspace.Hash(item.Value) }).ToArray(),
                modelComparison = models,
                fullPatchMode = buildMode,
                strictExitCode = strict.ExitCode,
                strictErrors = strict.Errors,
                assistedExitCode = assisted?.ExitCode,
                assistedErrors = assisted?.Errors,
                reusedAssets = reuse,
                rebuiltDirectory = selectedDir,
                binaryComparison = binary,
                fullComparison,
                releaseDirectory = selectedDir == null ? null : Path.Combine(run, "release"),
                looseIwi = new
                {
                    exactSourceReused = exactLooseIwiReused,
                    generated = generatedLooseIwi,
                    sourceUnavailable = sourceLooseIwiUnavailable,
                    pixelPayloadChanged = changedLooseIwiPayload
                },
                container = "Redacted 0x1337; chunks at 0x138; loose IWI; opaque source signature NOT verified",
                limitations = "No live game test; full source binary/internal XModel fields are not guaranteed equal by GLB comparison",
                linkerSha256 = Workspace.Hash(Path.Combine(request.BinDirectory, "Linker.exe")),
                unlinkerSha256 = Workspace.Hash(Path.Combine(request.BinDirectory, "Unlinker.exe"))
            });
            return new OperationResult(run, EnglishText.Format("All models: {0}; comparison {1}/{2}. Full zone: {3}.", modelEntries.Length, models.Count(c => c.Status == "PASS"), models.Count, status), remarks.ToArray());
        }

        async Task PrepareModelDependencies(string modelAssets, string original, ExportRequest request, ISet<string> knownMaterials, CancellationToken cancellation)
        {
            var materialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NormalizeModelMaterialNames(modelAssets, knownMaterials);
            foreach (var glb in Directory.GetFiles(Path.Combine(modelAssets, "model_export"), "*.glb", SearchOption.AllDirectories))
            {
                foreach (var name in ReadGlbMaterialNames(glb))
                {
                    materialNames.Add(name);
                }
            }
            foreach (var name in materialNames)
            {
                var materialRelative = "materials/" + name + ".json";
                var material = CopyDependency(original, modelAssets, materialRelative, request.SearchPaths);
                if (material == null)
                {
                    continue; // A loaded base FF may provide this material.
                }
                using (var document = JsonDocument.Parse(await Helpers.ReadAllTextAsync(material, cancellation)))
                {
                    if (document.RootElement.TryGetProperty("techniqueSet", out var technique) && technique.ValueKind == JsonValueKind.String)
                    {
                        var techniqueSet = CopyDependency(original, modelAssets, "techsets/" + technique.GetString() + ".techset", request.SearchPaths);
                        if (techniqueSet != null)
                        {
                            var setText = await Helpers.ReadAllTextAsync(techniqueSet, cancellation);
                            foreach (Match match in Regex.Matches(setText, @"(?m)^\s*([A-Za-z0-9_]+);"))
                            {
                                var techniqueFile = CopyDependency(original, modelAssets, "techniques/" + match.Groups[1].Value + ".tech", request.SearchPaths);
                                if (techniqueFile == null)
                                {
                                    continue;
                                }
                                var techniqueText = await Helpers.ReadAllTextAsync(techniqueFile, cancellation);
                                foreach (Match shader in Regex.Matches(techniqueText, "\\\"([A-Za-z0-9_]+\\.hlsl)\\\""))
                                {
                                    CopyDependency(original, modelAssets, "shader_bin/" + shader.Groups[1].Value + ".cso", request.SearchPaths);
                                }
                            }
                        }
                    }
                    if (!document.RootElement.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    foreach (var texture in textures.EnumerateArray())
                    {
                        if (!texture.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }
                        var imageName = image.GetString();
                        if (string.IsNullOrWhiteSpace(imageName))
                        {
                            continue;
                        }
                        _ = CopyDependency(original, modelAssets, "images/" + imageName + ".dds", request.SearchPaths) ?? CopyDependency(original, modelAssets, "images/" + imageName + ".iwi", request.SearchPaths);
                    }
                }
            }
            var images = Path.Combine(modelAssets, "images");
            if (!Directory.Exists(images))
                return;
            var dds = Directory.GetFiles(images, "*.dds", SearchOption.AllDirectories);
            foreach (var batch in dds.Chunk(8))
            {
                var converted = await _runner.RunAsync(Path.Combine(request.BinDirectory, "ImageConverter.exe"), new[] { "--no-color", "--t6" }.Concat(batch), modelAssets, null, cancellation);
                converted.RequireSuccess();
            }
        }

        /// <summary>Normalize every GLB LOD in a private build copy before resolving material dependencies.</summary>
        void NormalizeModelMaterialNames(string assets, ISet<string> knownMaterials)
        {
            string modelExport = Path.Combine(assets, "model_export");
            if (!Directory.Exists(modelExport))
                return;
            foreach (var glb in Directory.GetFiles(modelExport, "*.glb", SearchOption.AllDirectories))
            {
                foreach (var change in GlbModule.NormalizeMaterialNamesInPlace(glb, knownMaterials))
                {
                    _log?.Invoke(change + " File: " + glb);
                }
            }
        }

        /// <summary>
        /// Extract material names for model dependency resolution.
        /// </summary>
        static IEnumerable<string> ReadGlbMaterialNames(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 20 || !data.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("glTF")))
            {
                throw new InvalidDataException("Invalid GLB while resolving model materials: " + path);
            }
            int offset = 12;
            while (offset + 8 <= data.Length)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
                var type = data.AsSpan(offset + 4, 4);
                offset += 8;
                if (size < 0 || offset + size > data.Length)
                {
                    throw new InvalidDataException("Invalid GLB chunk: " + path);
                }
                if (type.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("JSON")))
                {
                    using (var document = JsonDocument.Parse(data.AsMemory(offset, size)))
                    {
                        if (!document.RootElement.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
                        {
                            yield break;
                        }
                        foreach (var material in materials.EnumerateArray())
                        {
                            if (!material.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String)
                            {
                                continue;
                            }
                            var name = value.GetString()?.Trim();
                            if (name == null || string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }
                            if (name.StartsWith(','))
                            {
                                throw new InvalidDataException($"GLB material name has a leading comma ('{name}') in {path}; fix the exported source before rebuilding.");
                            }
                            if (name.Split('/').Any(part => ((part == "") || (part == ".")) || (part == "..")))
                            {
                                throw new InvalidDataException("Invalid GLB material path: " + name);
                            }
                            yield return name;
                        }
                        yield break;
                    }
                }
                offset += size;
            }
            throw new InvalidDataException("GLB has no JSON chunk: " + path);
        }

        /// <summary>
        /// Performs the Dependency copy operation. Rebuild, re-export and compare a full staged asset set.
        /// </summary>
        static string CopyDependency(string original, string target, string relative, string[] searchPaths)
        {
            var destination = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination))
            {
                return destination;
            }
            var candidates = new[] { Path.Combine(original, relative.Replace('/', Path.DirectorySeparatorChar)) }
                .Concat(searchPaths.Select(root => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))))
                .Concat(searchPaths.Select(root => Path.Combine(root, Path.GetFileName(relative))))
                .Where(File.Exists);
            var source = candidates.FirstOrDefault();
            if (source == null)
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, overwrite: true);
            return destination;
        }

        async Task<ToolResult> Link(string dir, string assets, string source, string name, ExportRequest request, string[] bases, CancellationToken cancellation, string authTemplate = null)
        {
            Directory.CreateDirectory(dir);
            var args = new List<string> { "--no-color", "--source-search-path", source, "--asset-search-path", assets + ";" + Path.Combine(request.BinDirectory, "raw/t6"),
            "--output-folder", Path.Combine(dir, "zone") };
            foreach (var item in bases)
            {
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--load" }, new string[] { item })));
            }
            args.Add(name);
            var result = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Linker.exe"), args, dir, authTemplate != null || bases.Any(Workspace.IsRedacted) ? request.CaptureFile : null, cancellation, authTemplate);
            await Helpers.WriteAllTextAsync(Path.Combine(dir, "build.log"), result.Output, CancellationToken.None);
            return result;
        }
        async Task<string> Dump(string dir, string ff, ExportRequest request, string filter, CancellationToken cancellation, bool useGameSearchPaths = false)
        {
            var dump = Path.Combine(dir, "dump");
            var looseAssets = Directory.Exists(Path.Combine(dir, "assets")) ? Path.Combine(dir, "assets") : Path.GetFullPath(Path.Combine(dir, "../assets"));
            var args = new List<string> { "--no-color", "--model-format", "GLB", "--image-format", "DDS", "--output-folder", dump,
            "--search-path", string.Join(";", new[] { Path.GetDirectoryName(ff), looseAssets }.Concat(useGameSearchPaths ? request.SearchPaths : new string[0])) };
            if (filter.Length > 0)
            {
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--include-assets" }, new string[] { filter })));
            }
            args.Add(ff);
            var result = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Unlinker.exe"), args, dir, Workspace.IsRedacted(ff) ? request.CaptureFile : null, cancellation);
            await Helpers.WriteAllTextAsync(Path.Combine(dir, "export.log"), result.Output, CancellationToken.None);
            // Keep exactly the same known omission explicit; no source assets are a fallback.
            var unexpected = result.Errors.Where(e => e != "ERROR: Could not find data for image \"$identitynormalmap\"").ToArray();
            if (result.ExitCode != 0 || unexpected.Length > 0)
            {
                throw new InvalidOperationException(string.Join("\n", result.Errors));
            }
            // Re-export may reproduce the OAT delimiter artifact; keep comparisons against canonical material names.
            foreach (var glb in Directory.GetFiles(dump, "*.glb", SearchOption.AllDirectories))
            {
                foreach (var change in GlbModule.NormalizeMaterialNamesInPlace(glb))
                {
                    _log?.Invoke(change + " File: " + glb);
                }
            }
            return dump;
        }
        /// <summary>
        /// Performs the File copy operation. Rebuild, re-export and compare a full staged asset set.
        /// </summary>
        static void CopyFile(string source, string target, string relative)
        {
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (!File.Exists(destination))
            {
                File.Copy(Path.Combine(source, relative), destination);
            }
        }

        /// <summary>
        /// Performs the AllowedAddedAsset is operation. Rebuild, re-export and compare a full staged asset set.
        /// </summary>
        public static bool IsAllowedAddedAsset(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            if (normalized.Split('/').Any(part => ((part == "") || (part == ".")) || (part == "..")))
                return false;
            return (normalized.StartsWith("materials/mc/", StringComparison.OrdinalIgnoreCase) && normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ||
                   (normalized.StartsWith("images/", StringComparison.OrdinalIgnoreCase) && (normalized.EndsWith(".iwi", StringComparison.OrdinalIgnoreCase) ||
                     normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Resolves or reads the ModelBaseZones value. Rebuild, re-export and compare a full staged asset set.
        /// </summary>
        static string[] FindModelBaseZones(ExportRequest request, bool requireCommon = true)
        {
            var common = request.SearchPaths.Select(path => Path.Combine(path, "common_mp.ff")).FirstOrDefault(File.Exists);
            if (common == null && requireCommon)
                throw new FileNotFoundException(EnglishText.Text("A new MP xmodel requires the original common_mp.ff in the game paths."));
            var bases = new List<string>();
            if (common != null)
                bases.Add(common);
            return bases.ToArray();
        }

        /// <summary>
        /// Compare visual payload while allowing unrelated container-header differences.
        /// </summary>
        static bool SameIwiPixelPayload(string source, string rebuilt)
        {
            var a = File.ReadAllBytes(source);
            var b = File.ReadAllBytes(rebuilt);
            return a.Length >= 64 && a.Length == b.Length && a.AsSpan(64).SequenceEqual(b.AsSpan(64));
        }
        /// <summary>
        /// Compare exported trees and record missing, added and changed files.
        /// </summary>
        internal static IReadOnlyList<ComparisonEntry> CompareDirectories(string source, string target, IReadOnlyDictionary<string, string> overrides = null, ISet<string> includeFiles = null)
        {
            var results = new List<ComparisonEntry>();
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(file => Helpers.GetRelativePath(source, file).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (overrides != null)
            {
                files.UnionWith(overrides.Keys);
            }
            foreach (var relative in files)
            {
                if (relative.StartsWith("zone_source/"))
                    continue; // Container metadata compared separately via --list.
                if (includeFiles != null && !includeFiles.Contains(relative))
                    continue;
                var output = Path.Combine(target, relative);
                var sourceFile = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
                var comparisonSource = overrides != null && overrides.TryGetValue(relative, out var replacement) ? replacement : sourceFile;
                try
                {
                    if (!File.Exists(output))
                        throw new FileNotFoundException(EnglishText.Text("File was not exported after build."));
                    string details;
                    if (relative.EndsWith(".glb"))
                    {
                        details = overrides != null && overrides.ContainsKey(relative) ? GlbModule.CompareEditedGeometry(comparisonSource, output) : GlbModule.Compare(comparisonSource, output);
                    }
                    else if (relative.EndsWith(".json"))
                    {
                        if (!JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(comparisonSource)), JsonNode.Parse(File.ReadAllText(output))))
                            throw new InvalidDataException("JSON differs.");
                        details = "JSON structural equality";
                    }
                    else if (relative.EndsWith(".str"))
                    {
                        var a = RoundTrips.LocalizeStrings(comparisonSource);
                        var b = RoundTrips.LocalizeStrings(output);
                        if (a.Count != b.Count || a.Any(p => !b.TryGetValue(p.Key, out var v) || p.Value != v))
                            throw new InvalidDataException("Localize values differ.");
                        details = $"{a.Count} language/reference values equal";
                    }
                    else if (relative.EndsWith(".csv"))
                    {
                        var a = RoundTrips.CsvRows(comparisonSource);
                        var b = RoundTrips.CsvRows(output);
                        if (a.Length != b.Length || a.Zip(b).Any(p => !p.First.SequenceEqual(p.Second)))
                            throw new InvalidDataException("CSV cells differ.");
                        details = $"{a.Length} CSV rows equal, including whitespace";
                    }
                    else
                    {
                        if (Workspace.Hash(comparisonSource) != Workspace.Hash(output))
                            throw new InvalidDataException("SHA-256 differs.");
                        details = "SHA-256 equal";
                    }
                    results.Add(new ComparisonEntry(relative, "PASS", details));
                }
                catch (Exception ex) 
                { 
                    results.Add(new ComparisonEntry(relative, "FAIL", ex.Message)); 
                }
            }
            var expected = files;
            foreach (var file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
            {
                var relative = Helpers.GetRelativePath(target, file).Replace('\\', '/');
                if (includeFiles != null && !includeFiles.Contains(relative))
                    continue;
                if (!expected.Contains(relative) && !relative.StartsWith("zone_source/"))
                    results.Add(new ComparisonEntry(relative, "ADDED", "Additional exported file."));
            }
            return results;
        }
        static HashSet<string> Inventory(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            bool content = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Equals("Content:", StringComparison.OrdinalIgnoreCase))
                {
                    content = true;
                    continue;
                }
                if (!content)
                    continue;
                if (line.StartsWith("Unloaded zone", StringComparison.OrdinalIgnoreCase))
                    break;
                line = AssetNameNormalizer.NormalizeZoneLine(line);
                int comma = line.IndexOf(',');
                if (comma <= 0 || comma == line.Length - 1)
                    continue;
                string type = line.Substring(0, comma).Trim();
                string name = AssetNameNormalizer.NormalizeName(line.Substring(comma + 1));
                if (type.Length > 0 && name.Length > 0)
                    result.Add(type + ", " + name);
            }
            return result;
        }
        static object BinaryComparison(string original, string rebuilt, string scope)
        {
            byte[] a = File.ReadAllBytes(original), b = File.ReadAllBytes(rebuilt);
            int common = Math.Min(a.Length, b.Length), first = -1;
            long different = 0;
            for (int i = 0; i < common; i++)
                if (a[i] != b[i])
                {
                    if (first < 0)
                        first = i;
                    different++;
                }
            return new
            {
                scope,
                original,
                rebuilt,
                originalSize = a.Length,
                rebuiltSize = b.Length,
                originalSha256 = Workspace.Hash(original),
                rebuiltSha256 = Workspace.Hash(rebuilt),
                byteIdentical = a.AsSpan().SequenceEqual(b),
                firstDifferentOffset = first,
                differentBytesInCommonLength = different,
                lengthDifference = b.Length - a.Length,
                originalMagic = System.Text.Encoding.ASCII.GetString(a, 0, 8),
                rebuiltMagic = System.Text.Encoding.ASCII.GetString(b, 0, 8),
                originalVersion = BitConverter.ToUInt32(a, 8),
                rebuiltVersion = BitConverter.ToUInt32(b, 8)
            };
        }
    }
}
