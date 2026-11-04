using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Imports raw assets, never substitutes them from a loaded base zone.
    /// </summary>
    internal sealed class RoundTrips
    {
        private readonly Action<string> _log;
        private readonly ToolRunner _runner;

        internal RoundTrips(Action<string> log = null)
        {
            _log = log;
            _runner = new ToolRunner(log);
        }
        /// <summary>
        /// Creates isolated serialization fixtures and compares OAT build/export results.
        /// </summary>
        public async Task<OperationResult> RunAsync(RoundTripRequest request, CancellationToken cancellation = default)
        {
            string original = Path.GetFullPath(request.ExportDirectory);
            if (!Directory.Exists(original))
                throw new DirectoryNotFoundException(original);
            var run = Workspace.NewRun(request.OutputRoot, "roundtrip");
            var outcomes = new List<TestOutcome>();
            var definitions = Directory.GetFiles(Path.Combine(original, "zone_source"), "*.zone");
            if (definitions.Length == 0)
                throw new InvalidDataException(EnglishText.Text("The export does not contain zone_source/*.zone."));
            var entries = File.ReadAllLines(definitions[0]).Where(s => !s.StartsWith('>') && !s.StartsWith("//") && s.Contains(',')) .Select(s => s.Trim()).Distinct().ToArray();
            var cases = new List<(string Name, string Type, string Asset, bool Edit)> { ("rawfile", "rawfile", "roundtrip_note.txt", false) };
            foreach (var type in new[] { "localize", "stringtable", "image", "xanim", "xmodel" })
            {
                var entry = entries.FirstOrDefault(s => s.StartsWith(type + ",", StringComparison.Ordinal));
                if (entry != null)
                    cases.Add((type, type, entry.Substring((type.Length + 1)), false));
            }
            var world = entries.FirstOrDefault(s => s.StartsWith("xmodel,", StringComparison.Ordinal) && s.Contains("weapon_"));
            if (world != null)
                cases.Add(("world-model", "xmodel", world.Substring(7), false));
            var image = cases.FirstOrDefault(c => c.Type == "image");
            if (image != default)
                cases.Add(("edited-texture", "image", image.Asset, true));
            var model = cases.FirstOrDefault(c => c.Type == "xmodel");
            if (model != default)
                cases.Add(("edited-model", "xmodel", model.Asset, true));

            foreach (var item in cases)
            {
                cancellation.ThrowIfCancellationRequested();
                var testDir = Path.Combine(run, item.Name);
                Directory.CreateDirectory(testDir);
                var logs = new StringBuilder();
                try
                {
                    _log?.Invoke(EnglishText.Format("TEST {0}: {1},{2}", item.Name, item.Type, item.Asset));
                    string assets = Path.Combine(testDir, "assets"), source = Path.Combine(testDir, "source");
                    Directory.CreateDirectory(assets);
                    Directory.CreateDirectory(source);
                    // All other assets are references. This tests import of the selected asset, not material compatibility in game.
                    Directory.CreateDirectory(Path.Combine(source, "assetlist"));
                    File.WriteAllLines(Path.Combine(source, "assetlist", "dependencies.csv"), entries.Where(s => s != item.Type + "," + item.Asset));
                    string name = "rt_" + item.Name.Replace('-', '_') + "_mp";
                    string asset = item.Asset;
                    var compareFiles = new List<(string Input, string RelativeOutput)>();
                    void CopyAsset(string relative)
                    {
                        string input = Path.Combine(original, relative), target = Path.Combine(assets, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(input, target);
                        compareFiles.Add((target, relative));
                    }
                    if (item.Type == "rawfile")
                    {
                        string file = Path.Combine(assets, asset);
                        File.WriteAllText(file, "Redacted Workbench — UTF-8 roundtrip\nline 2\n", new UTF8Encoding(false));
                        compareFiles.Add((file, asset));
                    }
                    else if (item.Type == "localize")
                        CopyAsset($"english/localizedstrings/{asset}.str");
                    else if (item.Type == "stringtable")
                        CopyAsset(asset);
                    else if (item.Type == "image")
                    {
                        CopyAsset($"images/{asset.Replace('*', '_')}.dds");
                        if (item.Edit)
                        {
                            var file = compareFiles[0].Input;
                            var bytes = File.ReadAllBytes(file);
                            // A one-byte edit in the first compressed texel block; dimensions/format/mips remain intact.
                            int pixelStart = Encoding.ASCII.GetString(bytes, 84, 4) == "DX10" ? 148 : 128;
                            if (bytes.Length <= pixelStart)
                                throw new InvalidDataException(EnglishText.Text("DDS without pixels."));
                            bytes[pixelStart] ^= 0x1;
                            File.WriteAllBytes(file, bytes);
                        }
                    }
                    else if (item.Type == "xanim")
                        CopyAsset("xanim/" + asset);
                    else if (item.Type == "xmodel")
                    {
                        CopyAsset($"xmodel/{asset}.json");
                        using (var metadata = JsonDocument.Parse(File.ReadAllText(compareFiles[0].Input)))
                        {
                            foreach (var lod in metadata.RootElement.GetProperty("lods").EnumerateArray())
                                CopyAsset(lod.GetProperty("file").GetString());
                            if (item.Edit)
                                GlbModule.EditFirstPosition(compareFiles[1].Input, 0.125f);
                        }
                    }
                    File.WriteAllText(Path.Combine(source, name + ".zone"), $">game,T6\nignore,dependencies\n" + (item.Type == "image" ? $">ipak,{name}\n" : "") + $"{item.Type},{asset}\n");
                    string zoneOut = Path.Combine(testDir, "zone"), dump = Path.Combine(testDir, "dump");
                    if (item.Type == "image")
                    {
                        var converted = await _runner.RunAsync(Path.Combine(request.BinDirectory, "ImageConverter.exe"),
                            Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { compareFiles[0].Input })), testDir, null, cancellation);
                        logs.AppendLine(converted.Output);
                        converted.RequireSuccess();
                    }
                    var linked = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Linker.exe"), 
                        Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(
                            Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(
                            new string[] { "--no-color" }, new string[] { "--source-search-path" })), new string[] { source })), new string[] { "--asset-search-path" })), 
                            new string[] { assets + ";" + Path.Combine(request.BinDirectory, "raw/t6") })), new string[] { "--output-folder" })), new string[] { zoneOut })), new string[] { name })), 
                            testDir, null, cancellation);
                    logs.AppendLine(linked.Output);
                    linked.RequireSuccess();
                    var ff = Path.Combine(zoneOut, name + ".ff");
                    if (!File.Exists(ff))
                        throw new IOException(EnglishText.Text("No built FF."));
                    var dumped = await _runner.RunAsync(Path.Combine(request.BinDirectory, "Unlinker.exe"), Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(
                        Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(
                            Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(
                                new string[] { "--no-color" }, new string[] { "--search-path" })), new string[] { zoneOut })), new string[] { "--model-format" })), new string[] { "GLB" })), 
                            new string[] { "--image-format" })), new string[] { "DDS" })), new string[] { "--include-assets" })), new string[] { item.Type })), new string[] { "--output-folder" })), 
                            new string[] { dump })), new string[] { ff })), testDir, null, cancellation);
                    logs.AppendLine(dumped.Output);
                    dumped.RequireSuccess();
                    var details = new List<string>();
                    foreach (var pair in compareFiles)
                    {
                        string output = Path.Combine(dump, pair.RelativeOutput);
                        if (item.Type == "localize")
                            output = Path.Combine(dump, $"english/localizedstrings/{name}.str");
                        if (!File.Exists(output))
                            throw new IOException(EnglishText.Text("Not exported ") + pair.RelativeOutput);
                        if (Path.GetExtension(output) == ".glb")
                            details.Add(GlbModule.Compare(pair.Input, output));
                        else if (item.Type == "localize")
                        {
                            // Pool order is not stable. Compare reference/language pairs, rejecting duplicates.
                            var first = LocalizeStrings(pair.Input);
                            var second = LocalizeStrings(output);
                            if (first.Count != second.Count || first.Any(p => !second.TryGetValue(p.Key, out var text) || text != p.Value))
                                throw new InvalidDataException(EnglishText.Text("Localization strings changed."));
                            details.Add(EnglishText.Text("All localization directives match."));
                        }
                        else if (item.Type == "xmodel")
                        {
                            var first = JsonNode.Parse(File.ReadAllText(pair.Input));
                            var second = JsonNode.Parse(File.ReadAllText(output));
                            if (!JsonNode.DeepEquals(first, second))
                                throw new InvalidDataException(EnglishText.Text("Model metadata changed."));
                            details.Add(EnglishText.Text("Model metadata matches."));
                        }
                        else if (item.Type == "stringtable")
                        {
                            var first = CsvRows(pair.Input);
                            var second = CsvRows(output);
                            if (first.Length != second.Length || first.Zip(second).Any(r => !r.First.SequenceEqual(r.Second)))
                                throw new InvalidDataException(EnglishText.Text("Table cells changed."));
                            details.Add(EnglishText.Format("All {0} CSV rows match cell for cell (line endings may differ).", first.Length));
                        }
                        else
                        {
                            if (Workspace.Hash(pair.Input) != Workspace.Hash(output))
                                throw new InvalidDataException(EnglishText.Text("Files differ: ") + pair.RelativeOutput);
                            details.Add(pair.RelativeOutput + EnglishText.Text(": SHA-256 matched."));
                        }
                    }
                    Workspace.WriteJson(Path.Combine(testDir, "files.json"), compareFiles.Select(p => new
                    {
                        input = p.Input,
                        inputSha256 = Workspace.Hash(p.Input),
                        output = item.Type == "localize" ? Path.Combine(dump, $"english/localizedstrings/{name}.str") : Path.Combine(dump, p.RelativeOutput)
                    }));
                    outcomes.Add(new TestOutcome(item.Name, "PASS", string.Join(" ", details), testDir));
                    _log?.Invoke(EnglishText.Format("VERIFIED: {0} — {1}", item.Name, string.Join(" ", details)));
                }
                catch (OperationCanceledException) 
                { 
                    throw; 
                }
                catch (Exception ex)
                {
                    outcomes.Add(new TestOutcome(item.Name, "FAIL", ex.Message, testDir));
                    _log?.Invoke(EnglishText.Format("FAILED: {0} — {1}", item.Name, ex.Message));
                }
                finally 
                { 
                    await Helpers.WriteAllTextAsync(Path.Combine(testDir, "oat.log"), logs.ToString(), CancellationToken.None); 
                }
            }
            Workspace.WriteJson(Path.Combine(run, "report.json"), new
            {
                source = original,
                tests = outcomes,
                scope = "OAT raw import -> standard T6 FF -> OAT export. Live Redacted NOT VERIFIED",
                linkerSha256 = Workspace.Hash(Path.Combine(request.BinDirectory, "Linker.exe")),
                unlinkerSha256 = Workspace.Hash(Path.Combine(request.BinDirectory, "Unlinker.exe")),
                status = outcomes.All(o => o.Status == "PASS") ? "PASS" : "FAIL"
            });
            int passed = outcomes.Count(o => o.Status == "PASS");
            return new OperationResult(run, EnglishText.Format("Tests passed: {0}/{1}.", passed, outcomes.Count), outcomes.Where(o => o.Status != "PASS").Select(o => o.Name + ": " + o.Details).ToArray());
        }

        internal static string[][] CsvRows(string file)
        {
            using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(file) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false })
            {
                parser.SetDelimiters(",");
                var rows = new List<string[]>();
                while (!parser.EndOfData)
                    rows.Add(parser.ReadFields());
                return rows.ToArray();
            }
        }

        internal static Dictionary<string, string> LocalizeStrings(string file)
        {
            var entries = new Dictionary<string, string>();
            string reference = null;
            foreach (var line in File.ReadAllLines(file))
            {
                var match = Regex.Match(line, @"^\s*(REFERENCE|LANG_\w+)\s+(.*)$");
                if (!match.Success)
                    continue;
                if (match.Groups[1].Value == "REFERENCE")
                    reference = match.Groups[2].Value.Trim();
                else
                    entries.Add((reference ?? throw new InvalidDataException("Language before reference")) + "/" + match.Groups[1].Value, match.Groups[2].Value);
            }
            if (entries.Count == 0)
                throw new InvalidDataException("No localize strings.");
            return entries;
        }
    }
}
