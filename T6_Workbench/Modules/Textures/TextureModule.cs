using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    /// <summary>
    /// Public entry point for texture conversion, DDS preview and material creation.
    /// </summary>
    public static class TextureModule
    {
        public static Task<OperationResult> ConvertAsync(IReadOnlyCollection<string> files, string binDirectory, string outputDirectory, string outputFormat, string compression, Action<string> log, CancellationToken cancellation)
        {
            return TextureHelpers.ConvertAsync(files, binDirectory, outputDirectory, outputFormat, compression, log, cancellation);
        }

        public static DdsPreviewResult LoadDds(string path) => TextureHelpers.LoadDds(path);

        public static DdsPreviewResult LoadDds(byte[] data) => TextureHelpers.LoadDds(data);

        public static Func<string, BitmapSource> CreateMaterialResolver(IEnumerable<string> assetRoots, int maxTextureDimension = 0)
        {
            return TextureHelpers.CreateMaterialResolver(assetRoots, maxTextureDimension);
        }

        public static void ValidateMaterialName(string name) => TextureHelpers.ValidateMaterialName(name);

        public static string CreateMaterial(string template, IReadOnlyDictionary<string, string> images) => TextureHelpers.CreateMaterial(template, images);
    }
}
