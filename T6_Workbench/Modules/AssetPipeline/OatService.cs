using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Runs OAT texture conversion, export and build operations.
    /// </summary>
    internal sealed class OatService
    {
        private readonly Action<string> _log;
        private readonly ToolRunner _runner;

        internal OatService(Action<string> log = null)
        {
            _log = log;
            _runner = new ToolRunner(log);
        }

        /// <summary>
        /// Batch DDS inputs through OAT ImageConverter and verify each generated IWI.
        /// </summary>
        public async Task<OperationResult> ConvertTexturesAsync(string binDirectory, string assetDirectory, string outputRoot, CancellationToken cancellation = default)
        {
            var source = Path.GetFullPath(assetDirectory);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException(source);
            var files = Directory.GetFiles(source, "*.dds", SearchOption.AllDirectories);
            if (files.Length == 0)
                throw new InvalidDataException(EnglishText.Text("The assets folder does not contain DDS."));
            var run = Workspace.NewRun(outputRoot, "textures");
            var copied = new List<string>();
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var target = Path.Combine(run, "assets", Helpers.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
                copied.Add(target);
            }
            var allLogs = new StringBuilder();
            // Batch limits keep command lines well below the Windows limit for long paths.
            foreach (var batch in copied.Chunk(8))
            {
                var result = await _runner.RunAsync(Path.Combine(binDirectory, "ImageConverter.exe"), new[] { "--no-color", "--t6" }.Concat(batch), run, null, cancellation);
                allLogs.AppendLine(result.Output);
                await Helpers.WriteAllTextAsync(Path.Combine(run, "oat.log"), allLogs.ToString(), CancellationToken.None);
                result.RequireSuccess();
            }
            var outputs = copied.Select(f => Path.ChangeExtension(f, ".iwi")).ToArray();
            if (outputs.Any(f => !File.Exists(f)))
                throw new IOException(EnglishText.Text("ImageConverter did not create all IWI."));
            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                operation = "DDS -> T6 IWI",
                count = outputs.Length,
                files = outputs.Select(f => new { path = f, sha256 = Workspace.Hash(f) }),
                status = "PASS"
            });
            return new OperationResult(run, EnglishText.Format("Created IWI v27: {0}. Original DDS preserved.", outputs.Length), new string[0]);
        }

        /// <summary>
        /// Builds an explicit OAT argument list, exports or lists the requested assets, records evidence, and verifies source FF unchanged.
        /// </summary>
        public async Task<OperationResult> ExportAsync(ExportRequest request, bool listOnly = false, CancellationToken cancellation = default)
        {
            string source = Path.GetFullPath(request.Fastfile);
            string capture = Workspace.IsRedacted(source) ? request.CaptureFile : null;
            if (capture != null)
                CaptureValidator.Validate(capture);
            if (!new[] { "GLB", "GLTF", "OBJ", "XMODEL_EXPORT", "XMODEL_BIN" }.Contains(request.ModelFormat))
                throw new ArgumentException(EnglishText.Text("Unknown model format."));
            if (!new[] { "DDS", "IWI" }.Contains(request.ImageFormat))
                throw new ArgumentException(EnglishText.Text("Unknown texture format."));
            var run = Workspace.NewRun(request.OutputRoot, listOnly ? "list" : "export");
            string input = Path.Combine(run, "input"), dump = Path.Combine(run, "dump");
            Directory.CreateDirectory(input);
            var before = Workspace.Hash(source);
            var copy = Path.Combine(input, Path.GetFileName(source));
            File.Copy(source, copy, overwrite: false);
            if (Workspace.Hash(copy) != before)
                throw new IOException(EnglishText.Text("The FF copy does not match the original."));
            var args = new List<string> { "--no-color", "--model-format", request.ModelFormat, "--image-format", request.ImageFormat };
            if (listOnly)
                args.Add("--list");
            else
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--output-folder" }, new string[] { dump })));
            var searchPaths = request.SearchPaths.Select(Path.GetFullPath).Where(Directory.Exists).ToArray();
            if (searchPaths.Length > 0)
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--search-path" }, new string[] { string.Join(";", searchPaths) })));
            if (request.SpecificAssetType.Length > 0 || request.SpecificAssetName.Length > 0)
            {
                if (request.SpecificAssetType.Length == 0 || request.SpecificAssetName.Length == 0)
                    throw new ArgumentException(EnglishText.Text("Targeted export requires both the asset type and its name."));
                if (request.SpecificAssetName.Contains(','))
                    throw new ArgumentException(EnglishText.Text("OAT selector does not support commas in asset names."));
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--include-asset" }, new string[] { $"{request.SpecificAssetType},{request.SpecificAssetName}" })));
            }
            else if (request.IncludeAssets.Trim().Length > 0)
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--include-assets" }, new string[] { request.IncludeAssets.Trim() })));
            args.Add(copy);
            var result = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Unlinker.exe"), args, run, capture, cancellation);
            await Helpers.WriteAllTextAsync(Path.Combine(run, "oat.log"), result.Output, CancellationToken.None);
            var allowed = result.Errors.Where(e => request.AllowMissingIdentityNormal && e == "ERROR: Could not find data for image \"$identitynormalmap\"").ToArray();
            bool valid = result.ExitCode == 0 && result.Errors.Except(allowed).Any() == false;
            var materialRepairs = new List<string>();
            if (valid && !listOnly && request.ModelFormat.Equals("GLB", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dump))
            {
                foreach (var glb in Directory.GetFiles(dump, "*.glb", SearchOption.AllDirectories))
                {
                    materialRepairs.AddRange(GlbModule.NormalizeMaterialNamesInPlace(glb));
                }
            }
            foreach (var repair in materialRepairs)
                _log?.Invoke(repair);
            bool unchanged = Workspace.Hash(source) == before;
            int files = Directory.Exists(dump) ? Directory.GetFiles(dump, "*", SearchOption.AllDirectories).Length : 0;
            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                operation = listOnly ? "list" : "export",
                source,
                sourceSha256 = before,
                sourceUnchanged = unchanged,
                redacted = capture != null,
                signatureVerified = capture != null ? false : (bool?)null,
                captureSha256 = capture == null ? null : Workspace.Hash(capture),
                toolSha256 = Workspace.Hash(Path.Combine(request.BinDirectory, "Unlinker.exe")),
                request.ModelFormat,
                request.ImageFormat,
                request.SearchPaths,
                exitCode = result.ExitCode,
                errors = result.Errors,
                allowedMissingData = allowed,
                repairedGlbMaterialNames = materialRepairs,
                exportedFiles = files,
                status = valid && unchanged && (listOnly || files > 0) ? (allowed.Length == 0 ? "PASS" : "PARTIAL") : "FAIL"
            });
            if (!unchanged)
                throw new IOException(EnglishText.Text("Source FF has changed."));
            if (!valid)
                throw new InvalidOperationException(EnglishText.Format("Export finished with errors. Report: {0}\n", run) + string.Join("\n", result.Errors));
            if (!listOnly && files == 0)
                throw new InvalidOperationException(EnglishText.Text("OAT did not export any files."));
            return new OperationResult(run, listOnly ? EnglishText.Text("Asset list saved to oat.log") : EnglishText.Format("Files exported: {0}", files), allowed);
        }

        /// <summary>
        /// Invokes OAT Linker with explicit asset/source paths and validates that an FF/IPAK was emitted.
        /// </summary>
        public async Task<OperationResult> BuildAsync(BuildRequest request, CancellationToken cancellation = default)
        {
            var zone = Path.GetFullPath(request.ZoneFile);
            if (!File.Exists(zone) || Path.GetExtension(zone) != ".zone")
                throw new ArgumentException(EnglishText.Text("Select an existing .zone."));
            var run = Workspace.NewRun(request.OutputRoot, "build");
            var sourceDir = Path.Combine(run, "source");
            Directory.CreateDirectory(sourceDir);
            string stagedZone = Path.Combine(sourceDir, Path.GetFileName(zone));
            File.Copy(zone, stagedZone);
            // Repair the generated build copy only; never rewrite the user's project zone file during a build.
            AssetNameNormalizer.NormalizeZoneFile(stagedZone);
            // Source search includes the original directory so includes and adjacent asset lists work.
            var args = new List<string> 
            { 
                "--no-color", "--source-search-path", sourceDir + ";" + Path.GetDirectoryName(zone) + ";" + Path.GetFullPath(request.AssetDirectory),
                "--asset-search-path", Path.GetFullPath(request.AssetDirectory) + ";" + Path.Combine(Path.GetFullPath(request.BinDirectory), "raw/t6"), 
                "--output-folder", Path.Combine(run, "zone") 
            };
            bool needsCapture = false;
            foreach (var baseZone in request.BaseZones)
            {
                needsCapture |= Workspace.IsRedacted(baseZone);
                args.AddRange(Enumerable.ToArray(Enumerable.Concat(new string[] { "--load" }, new string[] { Path.GetFullPath(baseZone) })));
            }
            args.Add(Path.GetFileNameWithoutExtension(zone));
            var result = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Linker.exe"), args, run, needsCapture ? request.CaptureFile : null, cancellation);
            await Helpers.WriteAllTextAsync(Path.Combine(run, "oat.log"), result.Output, CancellationToken.None);
            var outputDirectory = Path.Combine(run, "zone");
            var outputs = Directory.Exists(outputDirectory) ? Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories).Where(f => (Path.GetExtension(f) == ".ff") || (Path.GetExtension(f) == ".ipak")).ToArray() : new string[0];
            Workspace.WriteJson(Path.Combine(run, "report.json"), 
                new {
                operation = "build",
                sourceZone = zone,
                sourceSha256 = Workspace.Hash(zone),
                request.BaseZones,
                format = "standard T6; loading in Redacted NOT VERIFIED",
                exitCode = result.ExitCode,
                errors = result.Errors,
                status = result.ExitCode == 0 && result.Errors.Length == 0 && outputs.Length > 0 ? "PASS" : "FAIL",
                files = outputs.Select(f => new { path = f, sha256 = Workspace.Hash(f) })
            });
            result.RequireSuccess();
            if (outputs.Length == 0)
                throw new IOException(EnglishText.Text("Linker did not create FF / IPAK."));
            return new OperationResult(run, EnglishText.Format("T6 project built: {0} FF / IPAK files.", outputs.Length), new string[0]);
        }
    }
}
