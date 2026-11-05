using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    #region MaterialTemplateDocument
    /// <summary>Clones an exported T6 material. Shader state and sampler identifiers remain native.</summary>
    internal static class MaterialTemplateDocument
    {
        /// <summary>Validate an asset name, never a filesystem path supplied by a caller.</summary>
        public static void ValidateName(string name)
        {
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_~.-]+(?:/[A-Za-z0-9_~.-]+)*$") || name.Split('/').Any(part => (part == ".") || (part == "..") || part.EndsWith(".", StringComparison.Ordinal)))
                throw new InvalidDataException(EnglishText.Text("Material name: Latin letters, digits, _ - ~ and /; without . and .. in the path."));
        }

        /// <summary>Replace explicitly selected texture semantics; empty values retain the template maps.</summary>
        public static string Create(string template, IReadOnlyDictionary<string, string> images)
        {
            var root = JsonNode.Parse(File.ReadAllText(template))?.AsObject() ?? throw new InvalidDataException(EnglishText.Text("Material template is empty."));
            if (string.IsNullOrWhiteSpace(root["techniqueSet"]?.GetValue<string>()))
                throw new InvalidDataException(EnglishText.Text("Template does not contain techniqueSet."));
            var textures = root["textures"] as JsonArray ?? throw new InvalidDataException(EnglishText.Text("Template is missing textures."));
            foreach (var pair in images.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)))
            {
                var slots = textures.OfType<JsonObject>().Where(item =>
                    string.Equals(item["semantic"]?.GetValue<string>(), pair.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (slots.Length == 0)
                    throw new InvalidDataException(EnglishText.Text("Template has no slot ") + pair.Key);
                foreach (var slot in slots)
                {
                    slot["image"] = pair.Value;
                }
            }
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
    #endregion

    #region TextureFileConverter
    /// <summary>Converts PNG, DDS and T6 IWI files without changing the supplied inputs.</summary>
    internal static class TextureFileConverter
    {
        /// <summary>
        /// Updates, serializes or converts the Async value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        public static async Task<OperationResult> ConvertAsync(IReadOnlyCollection<string> files, string binDirectory, string outputDirectory, string outputFormat, string compression, Action<string> log, CancellationToken cancellation)
        {
            if (files.Count == 0)
                throw new InvalidDataException(EnglishText.Text("Add PNG, DDS or IWI files first."));
            string outputRoot = Path.GetFullPath(outputDirectory);
            Workspace.RejectGameOutput(outputRoot);
            Directory.CreateDirectory(outputRoot);
            string extension;
            switch (outputFormat)
            {
                case "PNG":
                    extension = ".png";
                    break;
                case "DDS":
                    extension = ".dds";
                    break;
                case "IWI v27 (T6)":
                    extension = ".iwi";
                    break;
                default:
                    throw new ArgumentException(EnglishText.Text("Unknown output format."));
            }
            string[] sources = files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string source in sources)
            {
                if (!File.Exists(source))
                    throw new FileNotFoundException(EnglishText.Text("Input file not found."), source);
                if ((!(((Path.GetExtension(source).ToLowerInvariant() == ".png") || (Path.GetExtension(source).ToLowerInvariant() == ".dds")) || (Path.GetExtension(source).ToLowerInvariant() == ".iwi"))))
                    throw new InvalidDataException(EnglishText.Text("Only PNG, DDS and IWI are supported."));
            }
            var targets = sources.Select(source => Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(source) + extension)).ToArray();
            if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
                throw new IOException(EnglishText.Text("Multiple input files have the same name. Use separate operations or folders."));
            foreach (var pair in sources.Zip(targets))
            {
                if (Path.GetFullPath(pair.First).Equals(Path.GetFullPath(pair.Second), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(EnglishText.Text("Output folder matches the source folder; choose a separate folder."));
                if (File.Exists(pair.Second))
                    throw new IOException(EnglishText.Text("Output file already exists; choose another folder: ") + pair.Second);
            }
            string converter = Path.Combine(Path.GetFullPath(binDirectory), "ImageConverter.exe");
            if (sources.Any(file => Path.GetExtension(file).Equals(".iwi", StringComparison.OrdinalIgnoreCase)) ||
                outputFormat == "IWI v27 (T6)")
            {
                if (!File.Exists(converter))
                    throw new FileNotFoundException(EnglishText.Text("OAT's standard ImageConverter.exe was not found."), converter);
            }
            string tempRoot = Path.Combine(Path.GetTempPath(), "RedactedWorkbench-Texture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var runner = new ToolRunner(log);
                for (int i = 0; i < sources.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string source = sources[i], target = targets[i];
                    string stem = i.ToString("D4");
                    if (await TryConvertPreservingFormatAsync(source, target, outputFormat, compression, stem, tempRoot, converter, runner, cancellation))
                    {
                        log?.Invoke(EnglishText.Format("Texture: {0} → {1} (source format).", Path.GetFileName(source), Path.GetFileName(target)));
                        continue;
                    }
                    var bitmap = await LoadAsync(source, stem, tempRoot, converter, runner, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (outputFormat == "PNG")
                    {
                        SavePng(bitmap, target);
                    }
                    else
                    {
                        string dds = Path.Combine(tempRoot, stem + "-encoded.dds");
                        WriteDds(bitmap, dds, compression);
                        if (outputFormat == "DDS")
                        {
                            File.Move(dds, target);
                        }
                        else
                        {
                            var result = await runner.RunAsync(converter, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { dds })), tempRoot, null, cancellation);
                            result.RequireSuccess();
                            string iwi = Path.ChangeExtension(dds, ".iwi");
                            if (!File.Exists(iwi))
                                throw new IOException(EnglishText.Text("ImageConverter did not create IWI for ") + Path.GetFileName(source));
                            File.Move(iwi, target);
                        }
                    }
                    log?.Invoke(EnglishText.Format("Texture: {0} → {1} ({2}).", Path.GetFileName(source), Path.GetFileName(target), compression));
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return new OperationResult(outputRoot, EnglishText.Format("Textures converted: {0} → {1}.", sources.Length, extension.ToUpperInvariant()), new string[0]);
        }

        static async Task<bool> TryConvertPreservingFormatAsync(string source, string target, string outputFormat, string compression, string stem, string tempRoot, string converter, ToolRunner runner, CancellationToken cancellation)
        {
            if (compression != "AUTO" || outputFormat == "PNG")
                return false;
            string sourceExtension = Path.GetExtension(source).ToLowerInvariant();
            if (sourceExtension == ".dds" && outputFormat == "DDS")
            {
                File.Copy(source, target);
                return true;
            }
            if (sourceExtension == ".iwi" && outputFormat == "DDS")
            {
                string isolated = Path.Combine(tempRoot, stem + "-preserve.iwi");
                File.Copy(source, isolated);
                var result = await runner.RunAsync(converter, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { isolated })), tempRoot, null, cancellation);
                result.RequireSuccess();
                string dds = Path.ChangeExtension(isolated, ".dds");
                if (!File.Exists(dds))
                    throw new IOException(EnglishText.Text("ImageConverter did not extract DDS from ") + Path.GetFileName(source));
                File.Move(dds, target);
                return true;
            }
            if (outputFormat != "IWI v27 (T6)" || (!((sourceExtension == ".dds") || (sourceExtension == ".iwi"))))
                return false;
            if (sourceExtension == ".iwi" && IsT6Iwi27(source))
            {
                File.Copy(source, target);
                return true;
            }
            string ddsInput;
            if (sourceExtension == ".dds")
            {
                ddsInput = Path.Combine(tempRoot, stem + "-preserve.dds");
                File.Copy(source, ddsInput);
            }
            else
            {
                string iwiInput = Path.Combine(tempRoot, stem + "-preserve.iwi");
                File.Copy(source, iwiInput);
                var decoded = await runner.RunAsync(converter, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { iwiInput })), tempRoot, null, cancellation);
                decoded.RequireSuccess();
                ddsInput = Path.ChangeExtension(iwiInput, ".dds");
                if (!File.Exists(ddsInput))
                    throw new IOException(EnglishText.Text("ImageConverter did not extract DDS from ") + Path.GetFileName(source));
            }
            var encoded = await runner.RunAsync(converter, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { ddsInput })), tempRoot, null, cancellation);
            encoded.RequireSuccess();
            string iwi = Path.ChangeExtension(ddsInput, ".iwi");
            if (!File.Exists(iwi))
                throw new IOException(EnglishText.Text("ImageConverter did not create IWI for ") + Path.GetFileName(source));
            File.Move(iwi, target);
            return true;
        }

        /// <summary>
        /// Performs the T6Iwi27 is operation. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static bool IsT6Iwi27(string path)
        {
            using (var input = File.OpenRead(path))
            {
                Span<byte> header = stackalloc byte[4];
                return input.Read(header) == header.Length && header[0] == (byte)'I' && header[1] == (byte)'W' && header[2] == (byte)'i' && header[3] == 27;
            }
        }

        /// <summary>
        /// Resolves or reads the Async value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static async Task<BitmapSource> LoadAsync(string path, string stem, string tempRoot, string converter, ToolRunner runner, CancellationToken cancellation)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".png":
                    using (var input = File.OpenRead(path))
                    {
                        var frame = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                        return ToBgra(frame);
                    }
                case ".dds":
                    return TextureModule.LoadDds(path).Image;
                case ".iwi":
                    var isolatedIwi = Path.Combine(tempRoot, stem + "-input.iwi");
                    File.Copy(path, isolatedIwi);
                    var result = await runner.RunAsync(converter, Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "--no-color" }, new string[] { "--t6" })), new string[] { isolatedIwi })), tempRoot, null, cancellation);
                    result.RequireSuccess();
                    var dds = Path.ChangeExtension(isolatedIwi, ".dds");
                    if (!File.Exists(dds))
                        throw new IOException(EnglishText.Text("ImageConverter did not extract DDS from ") + Path.GetFileName(path));
                    return TextureModule.LoadDds(dds).Image;
                default:
                    throw new InvalidDataException(EnglishText.Text("Only PNG, DDS and IWI are supported."));
            }
        }

        static BitmapSource ToBgra(BitmapSource source)
        {
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0 || source.PixelWidth > 16384 || source.PixelHeight > 16384 || (long)source.PixelWidth * source.PixelHeight > 64_000_000)
                throw new InvalidDataException(EnglishText.Text("Texture size exceeds the 16K / 64 megapixel limit."));
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }

        /// <summary>
        /// Updates, serializes or converts the Png value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static void SavePng(BitmapSource bitmap, string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path))
            {
                encoder.Save(output);
            }
        }

        /// <summary>
        /// Write the DDS header, mips and selected uncompressed or BC payload.
        /// </summary>
        static void WriteDds(BitmapSource source, string path, string compression)
        {
            var bitmap = ToBgra(source);
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            byte[] pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(pixels, width * 4, 0);
            bool raw = (compression == "RGBA8") || (compression == "AUTO");
            bool bc1 = compression == "BC1", bc2 = compression == "BC2", bc3 = compression == "BC3", bc5 = compression == "BC5";
            if (!raw && !bc1 && !bc2 && !bc3 && !bc5)
            {
                throw new ArgumentException(EnglishText.Text("Unknown compression format."));
            }
            if (raw)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }
            }
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            int blockBytes = bc1 ? 8 : 16;
            int linearSize = checked(blocksX * blocksY * blockBytes);
            var levels = BuildMipChain(pixels, width, height);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var output = File.Create(path))
            {
                using (var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: false))
                {
                    writer.Write(System.Text.Encoding.UTF8.GetBytes("DDS "));
                    writer.Write(124);
                    writer.Write(raw ? 0x2100F : 0xA1007);
                    writer.Write(height);
                    writer.Write(width);
                    writer.Write(raw ? width * 4 : linearSize);
                    writer.Write(0);
                    writer.Write(levels.Count);
                    for (int i = 0; i < 11; i++)
                    {
                        writer.Write(0);
                    }
                    writer.Write(32);
                    if (raw)
                    {
                        writer.Write(0x41);
                        writer.Write(0);
                        writer.Write(32);
                        writer.Write(0x000000FFu);
                        writer.Write(0x0000FF00u);
                        writer.Write(0x00FF0000u);
                        writer.Write(0xFF000000u);
                    }
                    else
                    {
                        writer.Write(4);
                        writer.Write(bc1 ? FourCc("DXT1") : bc2 ? FourCc("DXT3") : bc3 ? FourCc("DXT5") : FourCc("ATI2"));
                        writer.Write(0);
                        writer.Write(0u);
                        writer.Write(0u);
                        writer.Write(0u);
                        writer.Write(0u);
                    }
                    writer.Write(levels.Count > 1 ? 0x401008 : 0x1000);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    writer.Write(0);
                    var block = new Rgba[16];
                    var channel = new byte[16];
                    foreach (var level in levels)
                    {
                        if (raw)
                        {
                            writer.Write(level.Pixels);
                            continue;
                        }
                        int levelBlocksX = (level.Width + 3) / 4, levelBlocksY = (level.Height + 3) / 4;
                        for (int by = 0; by < levelBlocksY; by++)
                        {
                            for (int bx = 0; bx < levelBlocksX; bx++)
                            {
                                ReadBlock(level.Pixels, level.Width, level.Height, bx, by, block);
                                if (bc1)
                                {
                                    EncodeColorBlock(block, true, writer);
                                }
                                else if (bc2)
                                {
                                    ulong alpha = 0;
                                    for (int i = 0; i < 16; i++)
                                    {
                                        alpha |= (ulong)((block[i].A + 8) / 17) << (i * 4);
                                    }
                                    writer.Write(alpha);
                                    EncodeColorBlock(block, false, writer);
                                }
                                else if (bc3)
                                {
                                    for (int i = 0; i < 16; i++)
                                    {
                                        channel[i] = block[i].A;
                                    }
                                    EncodeBc4(channel, writer);
                                    EncodeColorBlock(block, false, writer);
                                }
                                else
                                {
                                    for (int i = 0; i < 16; i++)
                                    {
                                        channel[i] = block[i].R;
                                    }
                                    EncodeBc4(channel, writer);
                                    for (int i = 0; i < 16; i++)
                                    {
                                        channel[i] = block[i].G;
                                    }
                                    EncodeBc4(channel, writer);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Generate lower-resolution mip levels until reaching 1 by 1.
        /// </summary>
        static List<MipLevel> BuildMipChain(byte[] basePixels, int baseWidth, int baseHeight)
        {
            var levels = new List<MipLevel> { new TextureFileConverter.MipLevel(basePixels, baseWidth, baseHeight) };
            while (baseWidth > 1 || baseHeight > 1)
            {
                var previous = levels[levels.Count - 1];
                int width = Math.Max(1, previous.Width / 2), height = Math.Max(1, previous.Height / 2);
                var pixels = new byte[checked(width * height * 4)];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int x0 = x * previous.Width / width, x1 = Math.Max(x0 + 1, (x + 1) * previous.Width / width);
                        int y0 = y * previous.Height / height, y1 = Math.Max(y0 + 1, (y + 1) * previous.Height / height);
                        int count = (x1 - x0) * (y1 - y0), output = (y * width + x) * 4;
                        for (int channel = 0; channel < 4; channel++)
                        {
                            int sum = 0;
                            for (int sy = y0; sy < y1; sy++)
                            {
                                for (int sx = x0; sx < x1; sx++)
                                {
                                    sum += previous.Pixels[(sy * previous.Width + sx) * 4 + channel];
                                }
                            }
                            pixels[output + channel] = (byte)((sum + count / 2) / count);
                        }
                    }
                }
                levels.Add(new MipLevel(pixels, width, height));
                baseWidth = width;
                baseHeight = height;
            }
            return levels;
        }

        /// <summary>
        /// Resolves or reads the Block value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static void ReadBlock(byte[] pixels, int width, int height, int bx, int by, Rgba[] block)
        {
            for (int py = 0; py < 4; py++)
            {
                for (int px = 0; px < 4; px++)
                {
                    int x = Math.Min(width - 1, bx * 4 + px), y = Math.Min(height - 1, by * 4 + py);
                    int at = (y * width + x) * 4, index = py * 4 + px;
                    block[index] = new TextureFileConverter.Rgba(pixels[at + 2], pixels[at + 1], pixels[at], pixels[at + 3]);
                }
            }
        }

        /// <summary>
        /// Updates, serializes or converts the ColorBlock value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static void EncodeColorBlock(Rgba[] pixels, bool allowTransparent, BinaryWriter writer)
        {
            bool transparent = allowTransparent && pixels.Any(pixel => pixel.A < 128);
            var eligible = transparent ? pixels.Where(pixel => pixel.A >= 128).ToArray() : pixels;
            if (eligible.Length == 0)
            {
                eligible = new TextureFileConverter.Rgba[] { new Rgba(0, 0, 0, 255) };
            }
            var low = eligible[0];
            var high = eligible[0];
            long farthest = -1;
            for (int i = 0; i < eligible.Length; i++)
            {
                for (int j = i + 1; j < eligible.Length; j++)
                {
                    long distance = ColorDistance(eligible[i], eligible[j]);
                    if (distance > farthest)
                    {
                        farthest = distance;
                        low = eligible[i];
                        high = eligible[j];
                    }
                }
            }
            ushort c0 = Pack565(high), c1 = Pack565(low);
            if (transparent)
            {
                if (c0 > c1)
                {
                    (c0, c1) = (c1, c0);
                }
            }
            else
            {
                if (c0 < c1)
                {
                    (c0, c1) = (c1, c0);
                }
                if (c0 == c1)
                {
                    if (c0 < ushort.MaxValue)
                    {
                        c0++;
                    }
                    else
                    {
                        c1--;
                    }
                }
            }
            var palette = ColorPalette(c0, c1, transparent);
            uint selectors = 0;
            for (int i = 0; i < 16; i++)
            {
                int chosen = 0;
                if (transparent && pixels[i].A < 128)
                {
                    chosen = 3;
                }
                else
                {
                    long best = long.MaxValue;
                    int limit = transparent ? 3 : 4;
                    for (int p = 0; p < limit; p++)
                    {
                        long distance = ColorDistance(pixels[i], palette[p]);
                        if (distance < best)
        {
            best = distance;
            chosen = p;
        }
                    }
                }
                selectors |= (uint)chosen << (i * 2);
            }
            writer.Write(c0);
            writer.Write(c1);
            writer.Write(selectors);
        }

        /// <summary>
        /// Updates, serializes or converts the Bc4 value. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        static void EncodeBc4(byte[] values, BinaryWriter writer)
        {
            byte maximum = values.Max(), minimum = values.Min();
            writer.Write(maximum);
            writer.Write(minimum);
            Span<byte> palette = stackalloc byte[8];
            palette[0] = maximum;
            palette[1] = minimum;
            if (maximum > minimum)
            {
                for (int i = 1; i <= 6; i++)
                {
                    palette[i + 1] = (byte)(((7 - i) * maximum + i * minimum) / 7);
                }
            }
            else
            {
                for (int i = 1; i <= 4; i++)
                {
                    palette[i + 1] = (byte)(((5 - i) * maximum + i * minimum) / 5);
                }
                palette[6] = 0;
                palette[7] = 255;
            }
            ulong selectors = 0;
            for (int i = 0; i < 16; i++)
            {
                int bestIndex = 0, bestDistance = int.MaxValue;
                for (int p = 0; p < 8; p++)
                {
                    int distance = Math.Abs(values[i] - palette[p]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestIndex = p;
                    }
                }
                selectors |= (ulong)bestIndex << (i * 3);
            }
            for (int i = 0; i < 6; i++)
            {
                writer.Write((byte)(selectors >> (i * 8)));
            }
        }

        static ushort Pack565(Rgba c) => (ushort)(((c.R * 31 + 127) / 255 << 11) | ((c.G * 63 + 127) / 255 << 5) | ((c.B * 31 + 127) / 255));

        static long ColorDistance(Rgba a, Rgba b)
        {
            long r = a.R - b.R, g = a.G - b.G, bl = a.B - b.B;
            return r * r + g * g + bl * bl;
        }

        static Rgba[] ColorPalette(ushort c0, ushort c1, bool transparent)
        {
            Rgba a = Unpack565(c0), b = Unpack565(c1);
            if (transparent)
            {
                return Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new TextureFileConverter.Rgba[] { a }, 
                    new TextureFileConverter.Rgba[] { b })), new TextureFileConverter.Rgba[] { Mix(a, b, 1, 1, 2) })), new TextureFileConverter.Rgba[] { new Rgba(0, 0, 0, 0) }));
            }
            return Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new TextureFileConverter.Rgba[] { a }, new TextureFileConverter.Rgba[] { b })), new TextureFileConverter.Rgba[] { Mix(a, b, 2, 1, 3) })), new TextureFileConverter.Rgba[] { Mix(a, b, 1, 2, 3) }));
        }

        static Rgba Unpack565(ushort value) => new TextureFileConverter.Rgba((byte)(((value >> 11) & 31) * 255 / 31), (byte)(((value >> 5) & 63) * 255 / 63), (byte)((value & 31) * 255 / 31), 255);

        static Rgba Mix(Rgba a, Rgba b, int wa, int wb, int d) => new TextureFileConverter.Rgba((byte)((a.R * wa + b.R * wb) / d), (byte)((a.G * wa + b.G * wb) / d), (byte)((a.B * wa + b.B * wb) / d), 255);

        static uint FourCc(string text) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(text));
        /// <summary>
        /// MipLevel. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        sealed class MipLevel
        {
            public byte[] Pixels { get; set; }

            public int Width { get; set; }

            public int Height { get; set; }

            public MipLevel(byte[] Pixels, int Width, int Height)
            {
                this.Pixels = Pixels;
                this.Width = Width;
                this.Height = Height;
            }
        }
        /// <summary>
        /// Rgba. PNG/DDS/IWI conversion, mip creation and managed block-compression encoding.
        /// </summary>
        struct Rgba
        {
            public byte R { get; set; }

            public byte G { get; set; }

            public byte B { get; set; }

            public byte A { get; set; }

            public Rgba(byte R, byte G, byte B, byte A)
            {
                this.R = R;
                this.G = G;
                this.B = B;
                this.A = A;
            }
        }
    }
    #endregion

    #region TextureHelpers
    /// <summary>Texture and material helpers used by the public TextureModule facade.</summary>
    internal static class TextureHelpers
    {
        internal static Task<OperationResult> ConvertAsync(IReadOnlyCollection<string> files, string binDirectory, string outputDirectory, string outputFormat, string compression, Action<string> log, CancellationToken cancellation)
        {
            return TextureFileConverter.ConvertAsync(files, binDirectory, outputDirectory, outputFormat, compression, log, cancellation);
        }

        internal static DdsPreviewResult LoadDds(string path) => DdsPreviewLoader.Load(path);

        internal static DdsPreviewResult LoadDds(byte[] data) => DdsPreviewLoader.Load(data);

        internal static Func<string, BitmapSource> CreateMaterialResolver(IEnumerable<string> assetRoots, int maxTextureDimension)
        {
            var resolver = new T6MaterialTextureResolver(assetRoots, maxTextureDimension);
            return resolver.Resolve;
        }

        internal static void ValidateMaterialName(string name) => MaterialTemplateDocument.ValidateName(name);

        internal static string CreateMaterial(string template, IReadOnlyDictionary<string, string> images) => MaterialTemplateDocument.Create(template, images);
    }
    #endregion
}
