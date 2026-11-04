using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    /// <summary>
    /// Finds each GLB material's T6 colorMap and decodes its project DDS for viewport display.
    /// Roots are ordered from highest priority to lowest priority (edits, exported assets, dependencies).
    /// </summary>
    internal sealed class T6MaterialTextureResolver
    {
        readonly string[] roots;
        readonly int maxTextureDimension;
        readonly Dictionary<string, BitmapSource> textureCache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Initialize the lookup with project or dump roots containing materials/ and images/.</summary>
        public T6MaterialTextureResolver(IEnumerable<string> assetRoots, int maxTextureDimension = 0)
        {
            this.maxTextureDimension = Math.Max(0, maxTextureDimension);
            roots = assetRoots.Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>Resolve a GLB material name such as "mc/mtl_c_usa_mp_fbi_helmet" to its T6 color map.</summary>
        public BitmapSource Resolve(string glbMaterialName)
        {
            string materialName = NormalizeMaterialName(glbMaterialName);
            if (materialName.Length == 0)
                return null;
            if (textureCache.TryGetValue(materialName, out var cached))
                return cached;

            BitmapSource bitmap = null;
            string materialFile = FindMaterialFile(materialName);
            if (materialFile != null)
            {
                try
                {
                    using (var json = JsonDocument.Parse(File.ReadAllBytes(materialFile)))
                    {
                        if (json.RootElement.TryGetProperty("textures", out var textures) && textures.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var texture in textures.EnumerateArray())
                            {
                                if (!texture.TryGetProperty("semantic", out var semantic) ||
                                    !string.Equals(semantic.GetString(), "colorMap", StringComparison.OrdinalIgnoreCase) ||
                                    !texture.TryGetProperty("image", out var imageElement))
                                    continue;
                                string imageName = imageElement.GetString();
                                string imageFile = FindImageFile(imageName);
                                if (imageFile != null)
                                {
                                    bitmap = TextureModule.LoadDds(imageFile).Image;
                                    if (maxTextureDimension > 0)
                                        bitmap = ResizeForPreview(bitmap, maxTextureDimension);
                                }
                                break;
                            }
                        }
                    }
                }
                catch (Exception error) when ((((((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) || (error is System.Text.Json.JsonException)) || (error is System.InvalidOperationException)) || (error is System.NotSupportedException)) || (error is System.ArgumentException))
                {
                    // A preview should remain usable if one optional material or DDS is malformed.
                }
            }

            textureCache[materialName] = bitmap;
            return bitmap;
        }

        static string NormalizeMaterialName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            // Some OAT exports accidentally prefix the GLB slot with a comma. It is not part of the asset key.
            string normalized = value.Trim().TrimStart(',').Trim().Replace('\\', '/').TrimStart('/');
            return normalized;
        }

        string FindMaterialFile(string materialName)
        {
            string relative = materialName + ".json";
            foreach (string root in roots)
            {
                string candidate = SafeChild(root, Path.Combine("materials", relative.Replace('/', Path.DirectorySeparatorChar)));
                if (candidate != null && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        string FindImageFile(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName))
                return null;
            string relative = imageName.Trim().Replace('\\', '/').TrimStart('/');
            string[] variants = relative == relative.Replace('*', '_')
                ? new string[] { relative } : System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new string[] { relative }, new string[] { relative.Replace('*', '_') }));
            foreach (string root in roots)
                foreach (string variant in variants)
                {
                    string candidate = SafeChild(root, Path.Combine("images", variant.Replace('/', Path.DirectorySeparatorChar) + ".dds"));
                    if (candidate != null && File.Exists(candidate))
                        return candidate;
                }

            // Older dumps occasionally flatten or rename image paths. Use a unique filename match as a fallback.
            string leaf = Path.GetFileName(relative.Replace('*', '_')) + ".dds";
            foreach (string root in roots)
            {
                string folder = Path.Combine(root, "images");
                if (!Directory.Exists(folder))
                    continue;
                string[] matches;
                try
                {
                    matches = Directory.EnumerateFiles(folder, leaf, SearchOption.AllDirectories)
                        .Take(2).ToArray();
                }
                catch (Exception error) when ((error is System.IO.IOException) || (error is System.UnauthorizedAccessException)) { continue; }
                if (matches.Length == 1)
                    return matches[0];
            }
            return null;
        }

        /// <summary>Downsample large map textures before caching them so a many-material BSP preview stays memory-safe.</summary>
        static BitmapSource ResizeForPreview(BitmapSource source, int maxDimension)
        {
            int largest = Math.Max(source.PixelWidth, source.PixelHeight);
            if (largest <= maxDimension)
                return source;
            double scale = (double)maxDimension / largest;
            int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            var transform = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth,
                (double)height / source.PixelHeight));
            var converted = new FormatConvertedBitmap(transform, PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            converted.CopyPixels(pixels, stride, 0);
            var resized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            resized.Freeze();
            return resized;
        }
        static string SafeChild(string root, string relative)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(prefix, relative));
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
    }
}
