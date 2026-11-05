using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    // Unlinker lists referenced assets, but deliberately does not dump their payload.
    internal static class ReferencedTechniqueSets
    {
        internal static IEnumerable<string> ReadReferences(string file, bool referencesOnly)
        {
            foreach (string line in File.ReadLines(file))
            {
                var match = Regex.Match(line.Trim(), @"^techniqueset\s*,\s*(,?)\s*([^,\s]+)\s*$", RegexOptions.IgnoreCase);
                if (!match.Success || (referencesOnly && match.Groups[1].Value.Length == 0))
                {
                    continue;
                }
                string name = match.Groups[2].Value;
                if (name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..")
                    yield return name;
            }
        }

        internal static async Task ResolveAsync(IEnumerable<string> names, string assets, string destination, IEnumerable<string> searchPaths, string binDirectory, string workDirectory, Action<string> log, CancellationToken cancellation, string existingDependencies = null)
        {
            var missing = new HashSet<string>(names.Where(name =>
                !File.Exists(Path.Combine(assets, "techsets", name + ".techset")) &&
                !File.Exists(Path.Combine(destination, "techsets", name + ".techset")) &&
                (existingDependencies == null || !File.Exists(Path.Combine(existingDependencies, "techsets", name + ".techset")))),
                StringComparer.OrdinalIgnoreCase);
            if (missing.Count == 0) return;
            var directories = searchPaths.Where(Directory.Exists).Concat(SteamZoneDirectories()).Distinct(StringComparer.OrdinalIgnoreCase);
            var candidates = directories.SelectMany(dir => new[] { "common.ff", "common_patch_mp.ff", "common_mp.ff", "patch_mp.ff" }
                .Select(name => Path.Combine(dir, name))).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var runner = new ToolRunner(log);
            string unlinker = Path.Combine(binDirectory, "Unlinker.exe");
            Directory.CreateDirectory(workDirectory);
            int index = 0;
            foreach (string zone in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                if (missing.Count == 0)
                {
                    break;
                }
                // A reference in the candidate is not a definition and cannot be dumped.
                var listing = await new ToolRunner().RunAsync(unlinker, new[] { "--no-color", "--list", zone }, workDirectory, null, cancellation);
                if (listing.ExitCode != 0)
                {
                    log?.Invoke(EnglishText.Text("Could not verify dependencies in ") + zone);
                    continue;
                }
                var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in listing.Output.Split('\n'))
                {
                    var match = Regex.Match(line.Trim(), @"^techniqueset\s*,\s*([^,\s]+)\s*$", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        available.Add(match.Groups[1].Value);
                    }
                }
                var selected = missing.Where(available.Contains).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (selected.Length == 0)
                {
                    continue;
                }
                string dump = Path.Combine(workDirectory, "zone-" + index++);
                var args = new List<string> { "--no-color", "--output-folder", dump };
                foreach (string name in selected)
                {
                    args.AddRange(new[] { "--include-asset", "techniqueset," + name });
                }
                args.Add(zone);
                var exported = await runner.RunAsync(unlinker, args, workDirectory, null, cancellation);
                if (exported.ExitCode != 0 || exported.Errors.Any())
                {
                    log?.Invoke(EnglishText.Text("Could not export techniqueset from ") + zone);
                    continue;
                }
                foreach (string folder in new[] { "techsets", "techniques", "shader_bin" })
                {
                    string src = Path.Combine(dump, folder);
                    if (!Directory.Exists(src))
                    {
                        continue;
                    }
                    foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string target = Path.Combine(destination, folder, Helpers.GetRelativePath(src, file));
                        if (File.Exists(target))
                        {
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(file, target);
                    }
                }
                foreach (string name in selected)
                {
                    if (File.Exists(Path.Combine(destination, "techsets", name + ".techset")))
                    {
                        missing.Remove(name);
                    }
                }
                log?.Invoke(EnglishText.Text("Techniqueset dependencies obtained from ") + zone + ": " + selected.Length);
            }
            if (missing.Count > 0)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "unresolved-techniquesets.txt"), missing.OrderBy(x => x));
                log?.Invoke(EnglishText.Text("Techniqueset source data not found: ") + string.Join(", ", missing.OrderBy(x => x)) + EnglishText.Text(". Full export requires the source zone, for example common.ff from the BO2 installation."));
            }
        }

        private static IEnumerable<string> SteamZoneDirectories()
        {
            string steam = null;
            try
            {
                steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
            }
            if (string.IsNullOrWhiteSpace(steam)) yield break;
            var libraries = new List<string> { steam };
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string text;
                try
                {
                    text = File.ReadAllText(vdf);
                }
                catch (IOException)
                {
                    yield break;
                }
                foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s*\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                {
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string zone = Path.Combine(library, "steamapps", "common", "Call of Duty Black Ops II", "zone", "all");
                if (Directory.Exists(zone)) yield return zone;
            }
        }
    }
}
