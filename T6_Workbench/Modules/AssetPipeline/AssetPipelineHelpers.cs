using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>Coordinates project edits and the internal OAT, container and package workflows.</summary>
    internal sealed class AssetPipelineHelpers
    {
        private readonly Action<string> _log;

        private readonly AssetEditor _editor;

        internal AssetPipelineHelpers(Action<string> log = null)
        {
            _log = log;
            _editor = new AssetEditor(log);
        }

        internal string Import(WorkbenchProject project, AssetRef asset, string sourceFile, int lodIndex)
        {
            return _editor.Import(project, asset, sourceFile, lodIndex);
        }

        internal string PreviewFile(WorkbenchProject project, AssetRef asset)
        {
            return _editor.PreviewFile(project, asset);
        }

        internal Task<OperationResult> ExportAsync(WorkbenchProject project, IReadOnlyList<AssetRef> assets, string destination, string modelFormat, string imageFormat, WorkbenchPaths paths, CancellationToken cancellation)
        {
            return _editor.ExportAsync(project, assets, destination, modelFormat, imageFormat, paths, cancellation);
        }

        internal Task<OperationResult> ExportAsync(ExportRequest request, bool listOnly, CancellationToken cancellation)
        {
            return new OatService(_log).ExportAsync(request, listOnly, cancellation);
        }

        internal Task<OperationResult> BuildAsync(BuildRequest request, CancellationToken cancellation)
        {
            return new OatService(_log).BuildAsync(request, cancellation);
        }

        internal Task<OperationResult> ConvertTexturesAsync(string binDirectory, string assetDirectory, string outputRoot, CancellationToken cancellation)
        {
            return new OatService(_log).ConvertTexturesAsync(binDirectory, assetDirectory, outputRoot, cancellation);
        }

        internal Task<OperationResult> RunFullRoundTripAsync(ExportRequest request, CancellationToken cancellation)
        {
            return new FullRoundTrip(_log).RunAsync(request, cancellation);
        }

        internal Task<OperationResult> BuildStandaloneAsync(WorkbenchProject project, WorkbenchPaths paths, CancellationToken cancellation)
        {
            return new StandaloneRedactedBuild(_log).RunAsync(project, paths, cancellation);
        }
    }
}
