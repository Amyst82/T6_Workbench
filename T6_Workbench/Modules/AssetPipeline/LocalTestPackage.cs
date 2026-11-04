using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace T6_Workbench
{
    /// <summary>
    /// Prepare a reviewable local-only test package without modifying the game.
    /// </summary>
    internal static class LocalTestPackage
    {
        /// <summary>
        /// Assemble a local test package and its installation metadata.
        /// </summary>
        public static OperationResult Create(string fullRoundTripDirectory, string gameRoot, string probePlugin, string outputRoot, string projectOriginalFastfile = null)
        {
            var reportPath = Path.Combine(Path.GetFullPath(fullRoundTripDirectory), "report.json");
            using (var report = JsonDocument.Parse(File.ReadAllText(reportPath)))
            {
                var root = report.RootElement;
                var offlineStatus = root.GetProperty("status").GetString();
                if (offlineStatus == "FAIL")
                    throw new InvalidDataException(EnglishText.Text("Cannot package an FF that failed offline verification."));
                var release = root.GetProperty("releaseDirectory").GetString();
                if (string.IsNullOrWhiteSpace(release) || !Directory.Exists(release))
                    throw new InvalidDataException(EnglishText.Text("Full build did not create a release folder."));
                var expectedOriginal = root.GetProperty("sourceSha256").GetString();
                var game = Path.GetFullPath(gameRoot);
                var installed = projectOriginalFastfile == null ? Path.Combine(game, "zone", "redacted", "patch_redacted.ff") : Path.GetFullPath(projectOriginalFastfile);
                if (!File.Exists(installed) || !Workspace.Hash(installed).Equals(expectedOriginal, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(EnglishText.Text("The project's source FF does not match the build's source FF."));
                }
                var candidate = Path.Combine(release, "zone", "redacted", "patch_redacted.ff");
                if (!Workspace.IsRedacted(candidate))
                {
                    throw new InvalidDataException(EnglishText.Text("The release folder has no Redacted FF."));
                }
                var plugin = Path.GetFullPath(probePlugin);
                if (!File.Exists(plugin))
                {
                    throw new FileNotFoundException(EnglishText.Text("Diagnostic Red32n not found."), plugin);
                }
                var ak47VertexEdit = root.GetProperty("editedAssets").EnumerateArray().Any(edit => string.Equals(edit.GetProperty("file").GetString(), "model_export/iw3_weapon_ak47_lod0.glb", StringComparison.OrdinalIgnoreCase));

                var package = Workspace.NewRun(outputRoot, "test-package");
                Copy(candidate, Path.Combine(package, "zone", "redacted", "patch_redacted.ff"));
                Copy(installed, Path.Combine(package, "backup", "patch_redacted.ff"));
                Copy(plugin, Path.Combine(package, "Plugins", "ZoneAuthProbe.Red32n"));
                var installedProbe = Path.Combine(game, "Plugins", "ZoneAuthProbe.Red32n");
                if (File.Exists(installedProbe))
                {
                    Copy(installedProbe, Path.Combine(package, "backup", "Plugins", "ZoneAuthProbe.Red32n"));
                }
                var mapEntsPlugin = Path.Combine(game, "Plugins", "MapEntsRuntimeOverride.Red32n");
                if (File.Exists(mapEntsPlugin))
                {
                    Copy(mapEntsPlugin, Path.Combine(package, "Plugins", "MapEntsRuntimeOverride.Red32n"));
                    Copy(mapEntsPlugin, Path.Combine(package, "backup", "Plugins", "MapEntsRuntimeOverride.Red32n"));
                }
                var candidateHash = Workspace.Hash(candidate);

                var sourceImages = Path.Combine(release, "data", "images");
                var included = new List<string>();
                int unchanged = 0;
                if (Directory.Exists(sourceImages))
                {
                    foreach (var image in Directory.GetFiles(sourceImages, "*.iwi", SearchOption.AllDirectories))
                    {
                        var relative = Helpers.GetRelativePath(Path.Combine(release, "data"), image);
                        var original = Path.Combine(game, "data", relative);
                        if (File.Exists(original) && Workspace.Hash(image) == Workspace.Hash(original))
                        {
                            unchanged++;
                            continue;
                        }
                        Copy(image, Path.Combine(package, "data", relative));
                        included.Add(relative.Replace('\\', '/'));
                    }
                }

                var packagedMapEnts = new List<string>();
                if (root.TryGetProperty("looseMapEnts", out var mapEnts) && mapEnts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in mapEnts.EnumerateArray())
                    {
                        var releasePath = item.GetProperty("releasePath").GetString() ?? throw new InvalidDataException(EnglishText.Text("The MapEnts report is missing the release path."));
                        var expectedHash = item.GetProperty("sha256").GetString() ?? throw new InvalidDataException(EnglishText.Text("The MapEnts report is missing SHA-256."));
                        var normalized = releasePath.Replace('\\', '/');
                        if (!normalized.StartsWith("data/maps/", StringComparison.OrdinalIgnoreCase) || normalized.Split('/').Any(part => (part == ".") || (part == "..")))
                        {
                            throw new InvalidDataException(EnglishText.Text("Unsafe loose MapEnts path: ") + releasePath);
                        }
                        var source = Path.GetFullPath(Path.Combine(release, normalized.Replace('/', Path.DirectorySeparatorChar)));
                        var releaseRoot = Path.GetFullPath(release) + Path.DirectorySeparatorChar;
                        if (!source.StartsWith(releaseRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                        {
                            throw new FileNotFoundException(EnglishText.Text("The release folder has no loose MapEnts."), source);
                        }
                        if (!Workspace.Hash(source).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(EnglishText.Text("Loose MapEnts SHA-256 does not match the report: ") + releasePath);
                        }
                        var entityText = File.ReadAllText(source);
                        if (Regex.IsMatch(entityText, "\"targetname\"\\s+\"(?:_oat_mapents_workflow_probe|codex_mapents_native_test)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        {
                            throw new InvalidDataException(EnglishText.Text("Loose MapEnts contains the old diagnostic box. Remove it before packaging: ") + releasePath);
                        }

                        var dataRelative = normalized.Substring("data/".Length);
                        var dataDestination = Path.Combine(package, "data", dataRelative.Replace('/', Path.DirectorySeparatorChar));
                        Copy(source, dataDestination);

                        // The current hook reads data/maps directly. Do not ship a second
                        // active .ents source: stale copies can resurrect removed entities.
                        var runtimeName = Path.GetFileNameWithoutExtension(dataRelative) + ".ents";
                        packagedMapEnts.Add(dataRelative.Replace('\\', '/'));

                        var installedData = Path.Combine(game, "data", dataRelative.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(installedData))
                        {
                            Copy(installedData, Path.Combine(package, "backup", "data", dataRelative.Replace('/', Path.DirectorySeparatorChar)));
                        }
                        var installedRuntime = Path.Combine(game, "Plugins", "MapEnts", runtimeName);
                        if (File.Exists(installedRuntime))
                        {
                            Copy(installedRuntime, Path.Combine(package, "backup", "Plugins", "MapEnts", runtimeName));
                        }
                    }
                }

                var testSteps = new List<string>();
                if (ak47VertexEdit)
                    testSteps.Add(EnglishText.Text("Start a local match and select ak47_mp. Its worldModel is iw3_weapon_ak47. Inspect another player's weapon or a dropped weapon: the first LOD0 vertex is shifted 3 units along X. Test firing and restarting the map."));
                if (packagedMapEnts.Any(item => item.Contains("mp_raid.d3dbsp.mapents", StringComparison.OrdinalIgnoreCase)))
                {
                    var raidMapEnts = Path.Combine(package, "data", "maps", "mp", "mp_raid.d3dbsp.mapents");
                    var hasRaidText = File.Exists(raidMapEnts) && File.ReadAllText(raidMapEnts).Contains("_oat_is_this_raid", StringComparison.Ordinal);
                    if (hasRaidText)
                        testSteps.Add(EnglishText.Text("Load a local match on mp_raid. Check that IS THIS RAID reads horizontally, the text plane is vertical, the probe box is absent, original entities remain, and the text is only visible from the front."));
                }
                if (testSteps.Count == 0)
                    testSteps.Add(EnglishText.Text("Start a local match, inspect the modified models and test restarting the map."));

                Workspace.WriteJson(Path.Combine(package, "report.json"), new
                {
                    status = "EXPERIMENTAL",
                    sourceRun = Path.GetFullPath(fullRoundTripDirectory),
                    offlineStatus,
                    candidateSha256 = candidateHash,
                    originalSha256 = expectedOriginal,
                    probeSha256 = Workspace.Hash(plugin),
                    unchangedLooseIwiOmitted = unchanged,
                    includedLooseIwi = included,
                    looseMapEnts = packagedMapEnts,
                    mapEntsRuntimePluginSha256 = File.Exists(Path.Combine(package, "Plugins", "MapEntsRuntimeOverride.Red32n")) ? Workspace.Hash(Path.Combine(package, "Plugins", "MapEntsRuntimeOverride.Red32n")) : null,
                    validation = "Offline asset roundtrip only for this candidate; game startup and live match not verified"
                });
                var numberedSteps = new List<string>
                {
                    EnglishText.Text("Completely close t6mpv43.exe. Steam does not need to be started."),
                    EnglishText.Text("Hook 0.3.1 only reads data/maps/mp/mp_raid.d3dbsp.mapents. The installer backs up and removes the old Plugins/MapEnts/mp_raid.d3dbsp.ents, to prevent it from restoring the test box."),
                    EnglishText.Text("Run Install-TestPackage.ps1 from the package folder. It will install zone/, data/ and plugins; first creating a backup in install-backups/.")
                };
                numberedSteps.AddRange(testSteps);
                numberedSteps.Add(EnglishText.Text("To roll back, completely close t6mpv43.exe and restore files from install-backups/<time>, using the path printed by the script. If this backup does not contain the old Plugins/MapEnts/mp_raid.d3dbsp.ents, do not restore that file. Do not copy the package's backup/ folder into the game. The normal version does not create logs or dumps."));
                string installScript = EnglishText.Text("param([string]$GameRoot = \"D:\\Games\\Redacted\")\r\n$ErrorActionPreference = 'Stop'\r\n$gamePrefix = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\\') + '\\'\r\n$runningGame = Get-Process -Name t6mpv43 -ErrorAction SilentlyContinue | Where-Object {\r\n  try { ([IO.Path]::GetFullPath($_.Path)).StartsWith($gamePrefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }\r\n}\r\nif ($runningGame) { throw 'Completely close t6mpv43.exe from this folder before installing.' }\r\n$packageRoot = $PSScriptRoot\r\n$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'\r\n$backupRoot = Join-Path $packageRoot (\"install-backups\\\" + $stamp)\r\n$relativeFiles = @(\r\n  'zone\\redacted\\patch_redacted.ff',\r\n  'Plugins\\ZoneAuthProbe.Red32n',\r\n  'Plugins\\MapEntsRuntimeOverride.Red32n',\r\n  'Plugins\\MapEnts\\mp_raid.d3dbsp.ents',\r\n  'data\\maps\\mp\\mp_raid.d3dbsp.mapents'\r\n)\r\nforeach ($relative in $relativeFiles) {\r\n  $installed = Join-Path $GameRoot $relative\r\n  if (Test-Path -LiteralPath $installed -PathType Leaf) {\r\n    $saved = Join-Path $backupRoot $relative\r\n    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $saved) | Out-Null\r\n    Copy-Item -LiteralPath $installed -Destination $saved\r\n  }\r\n}\r\n$stale = Join-Path $GameRoot 'Plugins\\MapEnts\\mp_raid.d3dbsp.ents'\r\nif (Test-Path -LiteralPath $stale -PathType Leaf) { Remove-Item -LiteralPath $stale -Force }\r\nforeach ($folder in @('zone', 'data')) {\r\n  $source = Join-Path $packageRoot $folder\r\n  if (Test-Path -LiteralPath $source -PathType Container) {\r\n    Copy-Item -Path (Join-Path $source '*') -Destination (Join-Path $GameRoot $folder) -Recurse -Force\r\n  }\r\n}\r\n$sourcePlugins = Join-Path $packageRoot 'Plugins'\r\nif (Test-Path -LiteralPath $sourcePlugins -PathType Container) {\r\n  Get-ChildItem -LiteralPath $sourcePlugins -File | ForEach-Object {\r\n    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path (Join-Path $GameRoot 'Plugins') $_.Name) -Force\r\n  }\r\n}\r\nWrite-Output \"Installed package. Backup: $backupRoot\"");
                File.WriteAllText(Path.Combine(package, "Install-TestPackage.ps1"), installScript, Encoding.UTF8);
                File.WriteAllText(Path.Combine(package, "README_TEST.md"),
                    EnglishText.Text("# Redacted v43 local test\n\n") +
                    EnglishText.Text("The build passed offline verification; the client and a match have not been tested with this package.\n\n") +
                    string.Join("\n", numberedSteps.Select((step, index) => $"{index + 1}. {step}")) + "\n\n" +
                    EnglishText.Text("The normal ZoneAuthProbe computes SHA-256 in memory, creates no .log/.bin files, and limits interception to the standard validation of a valid zone/redacted/patch_redacted.ff in Redacted v43. Without an allow file, it trusts any FF at this exact path. For diagnostics, use ZoneAuthProbe.debug.Red32n; install only one variant. The debug version creates separate log/bin files next to the DLL.\n"),
                    Encoding.UTF8);
                return new OperationResult(package, EnglishText.Format("Test package ready: FF + plugin + {0} MapEnts + {1} IWI; {2} matching IWI were not copied.", packagedMapEnts.Count, included.Count, unchanged),
                    new string[] { EnglishText.Text("A live match and the model's visual results have not been tested.") });
            }
        }

        /// <summary>
        /// Copy an operation-owned file, creating its parent directory when necessary.
        /// </summary>
        static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, overwrite: false);
        }
    }
}
