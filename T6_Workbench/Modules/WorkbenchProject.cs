using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    public sealed class WorkbenchProject
    {
        public string Root { get; set; }
        public string Name { get; set; }
        public string FastfileName { get; set; }
        public string OriginalSha256 { get; set; }
        public WorkbenchProject(string Root, string Name, string FastfileName, string OriginalSha256)
        {
            this.Root = Root;
            this.Name = Name;
            this.FastfileName = FastfileName;
            this.OriginalSha256 = OriginalSha256;
        }
        internal string[] SourceSearchPaths { get; set; } = new string[0];
        public string Fastfile => Path.Combine(Root, "source", FastfileName);
        public string Assets => Path.Combine(Root, "assets");
        public string Edits => Path.Combine(Root, "edits");
        public string Dependencies => Path.Combine(Root, "dependencies");
        public string Capture => Path.Combine(Root, "source", "CaptureValidator.bin");
        public string Zone => Path.Combine(Assets, "zone_source", Path.GetFileNameWithoutExtension(FastfileName) + ".zone");
        public string Builds => Path.Combine(Root, "builds");
        public string Manifest => Path.Combine(Root, "project.json");
        public string Inventory => Path.Combine(Root, "inventory.json");

        /// <summary>
        /// Load and validate project files and the original FF hash.
        /// </summary>
        public static WorkbenchProject Open(string path)
        {
            string manifest = Directory.Exists(path) ? Path.Combine(path, "project.json") : path;
            using (var json = JsonDocument.Parse(File.ReadAllText(manifest)))
            {
                var root = json.RootElement;
                var project = new WorkbenchProject(Path.GetDirectoryName(Path.GetFullPath(manifest)), root.GetProperty("name").GetString(), root.GetProperty("fastfileName").GetString(), root.GetProperty("originalSha256").GetString())
                {
                    SourceSearchPaths = root.TryGetProperty("sourceSearchPaths", out var storedPaths) && storedPaths.ValueKind == JsonValueKind.Array ? storedPaths.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray() : new string[0]
                };
                if (Path.GetFileName(project.FastfileName) != project.FastfileName ||
                    !File.Exists(project.Fastfile) || !Workspace.Hash(project.Fastfile).Equals(project.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(project.Assets) || !File.Exists(project.Zone))
                {
                    throw new InvalidDataException(EnglishText.Text("The project is incomplete or its source FF has changed."));
                }
                return project;
            }
        }

        /// <summary>
        /// Creates the project tree only after validating source and tools; copies the fastfile, exports its assets, collects dependencies and writes the manifest last.
        /// </summary>
        public static async Task<WorkbenchProject> CreateAsync(string root, string ff, ExportRequest options, Action<string> log = null, CancellationToken cancellation = default)
        {
            root = Path.GetFullPath(root);
            Workspace.RejectGameOutput(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException(EnglishText.Text("Select an empty folder for a new project."));
            // Validate external dependencies before leaving any files in the destination.
            // This specifically avoids an orphaned source/ folder after a stale capture setting.
            if (!File.Exists(ff))
                throw new FileNotFoundException("The selected fastfile does not exist.", ff);
            options = new ExportRequest(options) { SearchPaths = ResolveSourceSearchPaths(ff, options.SearchPaths) };
            string unlinker = Path.Combine(options.BinDirectory, "Unlinker.exe");
            if (!File.Exists(unlinker))
                throw new FileNotFoundException("OAT Unlinker not found. Select the OAT tools folder in Settings.", unlinker);
            if (Workspace.IsRedacted(ff))
                CaptureValidator.Validate(options.CaptureFile);
            Directory.CreateDirectory(Path.Combine(root, "source"));
            var name = Path.GetExtension(ff).Equals(".ff2", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(ff) + ".ff" : Path.GetFileName(ff);
            var source = Path.Combine(root, "source", name);
            File.Copy(ff, source);
            string hash = Workspace.Hash(ff);
            if (Workspace.Hash(source) != hash)
                throw new IOException(EnglishText.Text("The FF copy did not match the original."));
            var project = new WorkbenchProject(root, Path.GetFileNameWithoutExtension(name), name, hash)
            {
                SourceSearchPaths = options.SearchPaths
            };
            if (Workspace.IsRedacted(source))
            {
                CaptureValidator.Validate(options.CaptureFile);
                File.Copy(options.CaptureFile, project.Capture);
            }
            var exported = await new AssetPipelineModule(log).ExportAsync(new ExportRequest(options)
            {
                Fastfile = source,
                CaptureFile = File.Exists(project.Capture) ? project.Capture : options.CaptureFile,
                OutputRoot = Path.Combine(root, "work"),
                ModelFormat = "GLB",
                ImageFormat = "DDS",
                IncludeAssets = "",
                SpecificAssetName = "",
                SpecificAssetType = "",
                AllowMissingIdentityNormal = true
            }, cancellation: cancellation);
            var listed = await new AssetPipelineModule(log).ExportAsync(new ExportRequest(options)
            {
                Fastfile = source,
                CaptureFile = File.Exists(project.Capture) ? project.Capture : options.CaptureFile,
                OutputRoot = Path.Combine(root, "work"),
                ModelFormat = "GLB",
                ImageFormat = "DDS",
                IncludeAssets = "",
                SpecificAssetName = "",
                SpecificAssetType = ""
            }, listOnly: true, cancellation: cancellation);
            var inventory = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool inContent = false;
            foreach (var raw in File.ReadLines(Path.Combine(listed.Directory, "oat.log")))
            {
                string line = raw.Trim();
                if (line.Equals("Content:", StringComparison.OrdinalIgnoreCase))
                {
                    inContent = true;
                    continue;
                }
                if (!inContent)
                    continue;
                if (line.StartsWith("Unloaded zone", StringComparison.OrdinalIgnoreCase))
                    break;
                int comma = line.IndexOf(',');
                if (comma > 0 && comma < line.Length - 1)
                {
                    var type = line.Substring(0, (comma) - (0)).Trim().ToLowerInvariant();
                    var assetName = AssetNameNormalizer.NormalizeName(line.Substring((comma + 1)));
                    if (seen.Add(type + "\0" + assetName))
                        inventory.Add(new
                        {
                            type,
                            name = assetName
                        });
                }
            }
            if (inventory.Count > 0)
                Workspace.WriteJson(project.Inventory, inventory);
            Directory.Move(Path.Combine(exported.Directory, "dump"), project.Assets);
            Directory.CreateDirectory(project.Dependencies);
            var imageFolder = Path.Combine(project.Assets, "images");
            if (Directory.Exists(imageFolder))
                foreach (var dds in Directory.EnumerateFiles(imageFolder, "*.dds", SearchOption.AllDirectories))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var relative = Helpers.GetRelativePath(imageFolder, dds);
                    var candidate = options.SearchPaths.Select(path => Path.Combine(path, "images", Path.ChangeExtension(relative, ".iwi")))
                        .FirstOrDefault(File.Exists);
                    if (candidate != null)
                    {
                        var target = Path.Combine(project.Dependencies, "images", Path.ChangeExtension(relative, ".iwi"));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(candidate, target);
                    }
                }
            await ReferencedTechniqueSets.ResolveAsync(ReferencedTechniqueSets.ReadReferences(Path.Combine(listed.Directory, "oat.log"), referencesOnly: true),
                project.Assets, project.Dependencies, options.SearchPaths, options.BinDirectory,
                Path.Combine(root, "work", "referenced-techsets"), log, cancellation);
            Directory.CreateDirectory(project.Edits);
            Directory.CreateDirectory(project.Builds);
            if (!File.Exists(project.Zone))
                throw new InvalidDataException(EnglishText.Text("OAT did not export zone_source/*.zone."));
            // OAT can emit a second delimiter before material names; clean only the project-owned export.
            AssetNameNormalizer.NormalizeZoneFile(project.Zone);
            Workspace.WriteJson(project.Manifest, new
            {
                version = 1,
                name = project.Name,
                fastfileName = name,
                originalSha256 = hash,
                sourceSearchPaths = project.SourceSearchPaths,
                createdUtc = DateTime.UtcNow
            });
            // The full export now lives in assets; remove temporary input copies from this new project.
            var work = Path.Combine(root, "work");
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            return project;
        }

        /// <summary>Prefer the original FF's installation when locating IPAK and loose dependencies.</summary>
        internal static string[] ResolveSourceSearchPaths(string fastfile, IEnumerable<string> fallbackPaths)
        {
            string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(fastfile));
            var directories = new List<string> { sourceDirectory };
            DirectoryInfo zone = Directory.GetParent(sourceDirectory);
            if (zone != null && zone.Name.Equals("zone", StringComparison.OrdinalIgnoreCase) && zone.Parent != null)
            {
                string game = zone.Parent.FullName;
                directories.Add(Path.Combine(game, "data"));
                directories.Add(Path.Combine(game, "zone", "all"));
                directories.Add(Path.Combine(game, "zone", "english"));
                directories.Add(Path.Combine(game, "main"));
                directories.Add(Path.Combine(game, "sound"));
            }
            directories.AddRange(fallbackPaths.Where(path => !string.IsNullOrWhiteSpace(path)));
            return directories.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Copy an asset tree while retaining relative paths and checking cancellation.
        /// </summary>
        public static void CopyTree(string source, string destination, CancellationToken cancellation = default)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                string target = Path.Combine(destination, Helpers.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        public string AssetPath(string relative) => ChildPath(Assets, relative);
        public string EditPath(string relative) => ChildPath(Edits, relative);
        /// <summary>
        /// Resolve a child path and reject traversal beyond the project root.
        /// </summary>
        static string ChildPath(string root, string relative)
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(EnglishText.Text("Invalid asset path."));
            return result;
        }
    }

    /// <summary>Stable, UI-independent entry point for project and package workflows.</summary>
    public sealed class WorkbenchSession : INotifyPropertyChanged
    {
        readonly Action<string> log;
        readonly SynchronizationContext ownerContext;
        readonly ObservableCollection<AssetRef> mutableAssets = new System.Collections.ObjectModel.ObservableCollection<AssetRef>(new AssetRef[0]);
        readonly ReadOnlyObservableCollection<AssetRef> assets;
        WorkbenchProject project;

        /// <summary>Capture the owning UI context so collection notifications remain safe for WPF bindings.</summary>
        public WorkbenchSession(Action<string> log = null)
        {
            this.log = log;
            ownerContext = SynchronizationContext.Current;
            assets = new ReadOnlyObservableCollection<AssetRef>(mutableAssets);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Immutable project metadata; PropertyChanged announces project switches.</summary>
        public WorkbenchProject Project => project;
        /// <summary>Read-only observable inventory for TreeView/ListView bindings.</summary>
        public ReadOnlyObservableCollection<AssetRef> Assets => assets;
        public bool HasProject => project != null;
        public bool CanBuildRedacted => project != null &&
            project.FastfileName.Equals("patch_redacted.ff", StringComparison.OrdinalIgnoreCase) &&
            Workspace.IsRedacted(project.Fastfile);

        /// <summary>Refresh the bound inventory after an asset edit or a change made by another tool.</summary>
        public async Task RefreshAsync(CancellationToken cancellation = default)
        {
            var current = project ?? throw new InvalidOperationException(EnglishText.Text("Open a project first."));
            var inventory = await Task.Run(() => ReadInventory(current), cancellation);
            await PublishInventoryAsync(inventory);
        }

        /// <summary>
        /// Load project metadata and its typed inventory into the session.
        /// </summary>
        public async Task OpenAsync(string projectPath, CancellationToken cancellation = default)
        {
            var opened = await Task.Run(() => WorkbenchProject.Open(projectPath), cancellation);
            var assets = await Task.Run(() => ReadInventory(opened), cancellation);
            await PublishProjectAsync(opened, assets);
        }

        /// <summary>Raise binding state changes on the context that created the session.</summary>
        Task PublishProjectAsync(WorkbenchProject opened, IReadOnlyList<AssetRef> inventory)
        {
            void Publish()
            {
                project = opened;
                ReplaceInventory(inventory);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Project)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasProject)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanBuildRedacted)));
            }
            if (ownerContext == null || ReferenceEquals(SynchronizationContext.Current, ownerContext))
            {
                Publish();
                return Task.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>();
            ownerContext.Post(state =>
            {
                try
                {
                    Publish();
                    completion.SetResult(true);
                }
                catch (Exception error) 
                { 
                    completion.SetException(error); 
                }
            }, null);
            return completion.Task;
        }

        Task PublishInventoryAsync(IReadOnlyList<AssetRef> inventory)
        {
            void Publish()
            {
                ReplaceInventory(inventory);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Assets)));
            }
            if (ownerContext == null || ReferenceEquals(SynchronizationContext.Current, ownerContext))
            {
                Publish();
                return Task.CompletedTask;
            }
            var completion = new TaskCompletionSource<bool>();
            ownerContext.Post(state =>
            {
                try
                {
                    Publish();
                    completion.SetResult(true);
                }
                catch (Exception error) 
                { 
                    completion.SetException(error); 
                }
            }, null);
            return completion.Task;
        }

        /// <summary>Diff the existing collection so bound views can preserve selection and expansion state.</summary>
        void ReplaceInventory(IReadOnlyList<AssetRef> inventory)
        {
            var desired = inventory.ToHashSet();
            for (int i = mutableAssets.Count - 1; i >= 0; i--)
            {
                if (!desired.Contains(mutableAssets[i]))
                {
                    mutableAssets.RemoveAt(i);
                }
            }
            var existing = mutableAssets.ToHashSet();
            foreach (var asset in inventory)
            {
                if (existing.Add(asset))
                {
                    mutableAssets.Add(asset);
                }
            }
        }

        /// <summary>
        /// Validate source/dependencies before creating a project; export assets and write a manifest.
        /// </summary>
        public async Task CreateAsync(string projectRoot, string fastfile, WorkbenchPaths paths, CancellationToken cancellation = default)
        {
            var options = Request(fastfile, paths, projectRoot);
            var created = await WorkbenchProject.CreateAsync(projectRoot, fastfile, options, log, cancellation);
            await OpenAsync(created.Manifest, cancellation);
        }

        /// <summary>
        /// Selects the matching project-build workflow, then returns its asynchronous operation to the caller.
        /// </summary>
        public Task<OperationResult> BuildAsync(BuildFlavor flavor, WorkbenchPaths paths, CancellationToken cancellation = default)
        {
            var current = project ?? throw new InvalidOperationException(EnglishText.Text("Open a project first."));
            switch (flavor)
            {
                case BuildFlavor.StandardT6:
                    return BuildT6Async(current, paths, cancellation);
                case BuildFlavor.RedactedV43:
                    return BuildRedactedAsync(current, paths, cancellation);
                case BuildFlavor.StandaloneRedacted:
                    return new AssetPipelineModule(log).BuildStandaloneAsync(current, paths, cancellation);
                default:
                    throw new ArgumentOutOfRangeException(nameof(flavor));
            }
        }

        static ExportRequest Request(string fastfile, WorkbenchPaths paths, string outputRoot)
        {
            return new ExportRequest(paths.BinDirectory, fastfile, paths.CaptureFile, outputRoot,
            Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] 
            { 
                Path.Combine(paths.GameDirectory, "data") 
            }, 
            new string[] 
            { 
                Path.Combine(paths.GameDirectory, "zone", "all") 
            })), 
            new string[] 
            { 
                Path.Combine(paths.GameDirectory, "zone", "english") 
            })));
        }

        /// <summary>
        /// Read/deduplicate asset identities from manifest inventory or zone source.
        /// </summary>
        internal static IReadOnlyList<AssetRef> ReadInventory(WorkbenchProject current)
        {
            var found = new List<AssetRef>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(current.Inventory))
            {
                using (var document = JsonDocument.Parse(File.ReadAllText(current.Inventory)))
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        Add(item.GetProperty("type").GetString(), AssetNameNormalizer.NormalizeName(item.GetProperty("name").GetString()));
                    }
                }
            }
            else
            {
                foreach (var line in File.ReadLines(current.Zone))
                {
                    var value = line.Trim();
                    if (value.Length == 0 || value.StartsWith('>') || value.StartsWith("//"))
                        continue;
                    int comma = value.IndexOf(',');
                    if (comma > 0 && comma < value.Length - 1)
                    {
                        string normalized = AssetNameNormalizer.NormalizeZoneLine(value);
                        int normalizedComma = normalized.IndexOf(',');
                        Add(normalized.Substring(0, (normalizedComma) - (0)), normalized.Substring((normalizedComma + 1)));
                    }
                }
            }
            // New material/image assets live under edits and therefore are not part of the source FF inventory.
            // Merge them into this session view so a binding-based UI sees the same project state after reopening.
            foreach (var item in new[] 
            { 
                (Folder: "materials", Pattern: "*.json", Type: "material"),
                                     (Folder: "images", Pattern: "*.dds", Type: "image"),
                                     (Folder: "images", Pattern: "*.iwi", Type: "image"),
                                     (Folder: "xmodel", Pattern: "*.json", Type: "xmodel"),
                                     (Folder: "xanim", Pattern: "*", Type: "xanim"),
                                     (Folder: "weapons", Pattern: "*", Type: "weapon") 
            })
            {
                var folder = current.EditPath(item.Folder);
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.EnumerateFiles(folder, item.Pattern, SearchOption.AllDirectories))
                    Add(item.Type, Path.ChangeExtension(Helpers.GetRelativePath(folder, file), null).Replace('\\', '/'));
            }
            return found.AsReadOnly();

            void Add(string type, string name)
            {
                type = type.Trim().ToLowerInvariant();
                name = name.Trim();
                if (type.Length > 0 && type != "mapents" && name.Length > 0 && seen.Add(type + "\0" + name))
                    found.Add(new AssetRef(type, name));
            }
        }

        /// <summary>
        /// Merge edits, build standard T6, re-export for verification and package the outputs.
        /// </summary>
        async Task<OperationResult> BuildT6Async(WorkbenchProject current, WorkbenchPaths paths, CancellationToken token)
        {
            var stage = Workspace.NewRun(current.Builds, "t6-stage");
            var assets = Path.Combine(stage, "assets");
            WorkbenchProject.CopyTree(current.Assets, assets, token);
            WorkbenchProject.CopyTree(current.Edits, assets, token);
            var knownMaterials = AssetNameNormalizer.ReadMaterialNames(current.Inventory, current.Zone);
            string modelExport = Path.Combine(assets, "model_export");
            if (Directory.Exists(modelExport))
            {
                foreach (var glb in Directory.GetFiles(modelExport, "*.glb", SearchOption.AllDirectories))
                {
                    foreach (var change in GlbModule.NormalizeMaterialNamesInPlace(glb, knownMaterials))
                    {
                        log?.Invoke(change + " File: " + glb);
                    }
                }
            }
            if (Directory.EnumerateFiles(assets, "*.dds", SearchOption.AllDirectories).Any())
            {
                var converted = await new AssetPipelineModule(log).ConvertTexturesAsync(paths.BinDirectory, assets, stage, token);
                WorkbenchProject.CopyTree(Path.Combine(converted.Directory, "assets"), assets, token);
            }
            var built = await new AssetPipelineModule(log).BuildAsync(new BuildRequest(paths.BinDirectory, current.Zone, assets, paths.OutputDirectory, Capture(current, paths), new string[0]), token);
            var zone = Path.Combine(built.Directory, "zone");
            var ff = Directory.EnumerateFiles(zone, "*.ff", SearchOption.AllDirectories).FirstOrDefault(file => Path.GetFileNameWithoutExtension(file).Equals(current.Name, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException(EnglishText.Text("Linker did not create FF with the project name."));
            var verified = await new AssetPipelineModule(log).ExportAsync(new ExportRequest(paths.BinDirectory, ff,
                Capture(current, paths), stage, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { zone }, new string[] { assets })), new string[] { current.Dependencies })),
                AllowMissingIdentityNormal: true), cancellation: token);
            var package = Workspace.NewRun(paths.OutputDirectory, "t6-package");
            WorkbenchProject.CopyTree(zone, Path.Combine(package, "zone"), token);
            Copy(Path.Combine(verified.Directory, "report.json"), Path.Combine(package, "verification", "export-report.json"));
            Copy(Path.Combine(verified.Directory, "oat.log"), Path.Combine(package, "verification", "oat.log"));
            var images = Path.Combine(assets, "images");
            if (Directory.Exists(images))
            {
                foreach (var iwi in Directory.EnumerateFiles(images, "*.iwi", SearchOption.AllDirectories))
                {
                    Copy(iwi, Path.Combine(package, "data", "images", Path.GetFileName(iwi)));
                }
            }
            return new OperationResult(package, EnglishText.Text("Standard T6 package built and exported again for verification"), new string[0]);
        }

        /// <summary>
        /// Run the Redacted rebuild/comparison and prepare a test package.
        /// </summary>
        async Task<OperationResult> BuildRedactedAsync(WorkbenchProject current, WorkbenchPaths paths, CancellationToken token)
        {
            if (!Workspace.IsRedacted(current.Fastfile) || !current.FastfileName.Equals("patch_redacted.ff", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(EnglishText.Text("Redacted v43 build is only available for patch_redacted.ff."));
            }
            var request = new ExportRequest(Request(current.Fastfile, paths, current.Builds))
            {
                CaptureFile = Capture(current, paths),
                SearchPaths = Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(
                    new string[] 
                    { 
                        current.Dependencies 
                    }, 
                    new string[] 
                    { 
                        current.Assets 
                    })), 
                    Enumerable.ToArray(current.SourceSearchPaths))), 
                    Enumerable.ToArray(Request(current.Fastfile, paths, current.Builds).SearchPaths))),
                ProjectAssetsRoot = current.Assets,
                EditedAssetsRoot = Directory.EnumerateFiles(current.Edits, "*", SearchOption.AllDirectories).Any() ? current.Edits : null
            };
            var rebuilt = await new AssetPipelineModule(log).RunFullRoundTripAsync(request, token);
            token.ThrowIfCancellationRequested();
            using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(rebuilt.Directory, "report.json"))))
            {
                if (report.RootElement.GetProperty("status").GetString() == "FAIL")
                {
                    throw new InvalidDataException(EnglishText.Text("Offline verification of the full zone failed."));
                }
                var packaged = AssetPipelineModule.CreateTestPackage(rebuilt.Directory, paths.GameDirectory, paths.ProbeFile, paths.OutputDirectory, current.Fastfile);
                return new OperationResult(packaged)
                {
                    Warnings = rebuilt.Warnings.Concat(packaged.Warnings).ToArray()
                };
            }
        }

        /// <summary>
        /// Use the capture stored in the project, with a configured fallback.
        /// </summary>
        static string Capture(WorkbenchProject current, WorkbenchPaths paths)
        {
            return File.Exists(current.Capture) ? current.Capture : paths.CaptureFile;
        }

        /// <summary>
        /// Copy an operation-owned file, creating its parent directory when necessary.
        /// </summary>
        static void Copy(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, true);
        }
    }

    /// <summary>
    /// AssetRef. UI-independent project lifecycle, inventory and build dispatch.
    /// </summary>
    public sealed class AssetRef 
    { 
        public string Type { get; set; } 
        public string Name { get; set; } 
        public AssetRef(string Type, string Name) 
        { 
            this.Type = Type; 
            this.Name = Name; 
        } 
    }
    /// <summary>
    /// WorkbenchPaths. UI-independent project lifecycle, inventory and build dispatch.
    /// </summary>
    public sealed class WorkbenchPaths 
    { 
        public string BinDirectory { get; set; } 
        public string CaptureFile { get; set; } 
        public string GameDirectory { get; set; } 
        public string ProbeFile { get; set; } 
        public string OutputDirectory { get; set; } 
        public string RedactedTemplateFile { get; set; } 
        public WorkbenchPaths(string BinDirectory, string CaptureFile, string GameDirectory, string ProbeFile, string OutputDirectory, string RedactedTemplateFile = "") 
        { 
            this.BinDirectory = BinDirectory; 
            this.CaptureFile = CaptureFile; 
            this.GameDirectory = GameDirectory; 
            this.ProbeFile = ProbeFile; 
            this.OutputDirectory = OutputDirectory; 
            this.RedactedTemplateFile = RedactedTemplateFile; 
        } 
    }
    /// <summary>
    /// BuildFlavor. UI-independent project lifecycle, inventory and build dispatch.
    /// </summary>
    public enum BuildFlavor
    {
        StandardT6, RedactedV43, StandaloneRedacted
    }
}
