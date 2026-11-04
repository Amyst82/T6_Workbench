using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Single UI entry point for project asset edits, export, build and packaging.
    /// </summary>
    public sealed class AssetPipelineModule
    {
        private readonly AssetPipelineHelpers _helpers;

        public AssetPipelineModule(Action<string> log = null)
        {
            _helpers = new AssetPipelineHelpers(log);
        }

        public string Import(WorkbenchProject project, AssetRef asset, string sourceFile, int lodIndex = 0)
        {
            return _helpers.Import(project, asset, sourceFile, lodIndex);
        }

        public string PreviewFile(WorkbenchProject project, AssetRef asset)
        {
            return _helpers.PreviewFile(project, asset);
        }

        public Task<OperationResult> ExportAsync(WorkbenchProject project, IReadOnlyList<AssetRef> assets, string destination, string modelFormat, string imageFormat, WorkbenchPaths paths, CancellationToken cancellation = default)
        {
            return _helpers.ExportAsync(project, assets, destination, modelFormat, imageFormat, paths, cancellation);
        }

        public Task<OperationResult> ExportAsync(ExportRequest request, bool listOnly = false, CancellationToken cancellation = default)
        {
            return _helpers.ExportAsync(request, listOnly, cancellation);
        }

        public Task<OperationResult> BuildAsync(BuildRequest request, CancellationToken cancellation = default)
        {
            return _helpers.BuildAsync(request, cancellation);
        }

        public Task<OperationResult> ConvertTexturesAsync(string binDirectory, string assetDirectory, string outputRoot, CancellationToken cancellation = default)
        {
            return _helpers.ConvertTexturesAsync(binDirectory, assetDirectory, outputRoot, cancellation);
        }

        public Task<OperationResult> RunRoundTripAsync(RoundTripRequest request, CancellationToken cancellation = default)
        {
            return _helpers.RunRoundTripAsync(request, cancellation);
        }

        public Task<OperationResult> RunFullRoundTripAsync(ExportRequest request, CancellationToken cancellation = default)
        {
            return _helpers.RunFullRoundTripAsync(request, cancellation);
        }

        public Task<OperationResult> BuildStandaloneAsync(WorkbenchProject project, WorkbenchPaths paths, CancellationToken cancellation = default)
        {
            return _helpers.BuildStandaloneAsync(project, paths, cancellation);
        }

        public static OperationResult CreateTestPackage(string fullRoundTripDirectory, string gameRoot, string probePlugin, string outputRoot, string projectOriginalFastfile = null)
        {
            return AssetPipelineHelpers.CreateTestPackage(fullRoundTripDirectory, gameRoot, probePlugin, outputRoot, projectOriginalFastfile);
        }
    }
}
