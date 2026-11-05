using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    /// <summary>
    /// Public entry point for GLB preview and import preparation.
    /// </summary>
    public static class GlbModule
    {
        public static GlbPreviewResult LoadPreview(string path, Func<string, BitmapSource> resolveT6Material = null, CancellationToken cancellation = default) =>
            GlbHelpers.LoadPreview(path, resolveT6Material, cancellation);

        public static GlbImportResult NormalizeImport(string file, ISet<string> knownMaterialNames = null) =>
            GlbHelpers.NormalizeImport(file, knownMaterialNames);

        public static string[] NormalizeMaterialNamesInPlace(string file, ISet<string> knownMaterialNames = null) =>
            GlbHelpers.NormalizeMaterialNamesInPlace(file, knownMaterialNames);

        internal static string Compare(string first, string second, Action<string, double> measurement = null, bool measureOnly = false) =>
            GlbHelpers.Compare(first, second, measurement, measureOnly);

        internal static string CompareEditedGeometry(string edited, string rebuilt) =>
            GlbHelpers.CompareEditedGeometry(edited, rebuilt);
    }

    /// <summary>Contains normalized GLB bytes and the import changes made by the normalizer.</summary>
    public sealed class GlbImportResult
    {
        public byte[] Bytes
        {
            get; private set;
        }

        public string[] Changes
        {
            get; private set;
        }

        public GlbImportResult(byte[] bytes, string[] changes)
        {
            Bytes = bytes;
            Changes = changes;
        }
    }
}
