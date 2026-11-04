using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace T6_Workbench
{
    /// <summary>
    /// DdsPreviewResult. Decode supported DDS pixels into frozen WPF preview images.
    /// </summary>
    public sealed class DdsPreviewResult
    { 
        public BitmapSource Image { get; set; } 
        public int Width { get; set; } 
        public int Height { get; set; } 
        public string Format { get; set; } 
        public DdsPreviewResult(BitmapSource Image, int Width, int Height, string Format) 
        { 
            this.Image = Image; 
            this.Width = Width; 
            this.Height = Height; 
            this.Format = Format; 
        } 
    }

    /// <summary>Decodes the common T6 DDS formats for display inside the Workbench.</summary>
    internal static class DdsPreviewLoader
    {
        /// <summary>
        /// Decode and validate the supported preview format into WPF data.
        /// </summary>
        public static DdsPreviewResult Load(string path) => Load(File.ReadAllBytes(path));

        /// <summary>Decode DDS bytes embedded in a GLB without writing an intermediate file.</summary>
        public static DdsPreviewResult Load(byte[] data)
        {
            if (data.Length < 128 || !data.AsSpan(0, 4).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("DDS ")))
                throw new InvalidDataException(EnglishText.Text("File is not a DDS texture."));
            int height = ReadInt32(data, 12), width = ReadInt32(data, 16);
            if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > 128_000_000)
                throw new InvalidDataException(EnglishText.Text("DDS texture size is outside the valid range."));

            uint pfFlags = ReadUInt32(data, 80);
            string fourCc = Encoding.ASCII.GetString(data, 84, 4);
            uint rMask = ReadUInt32(data, 92), gMask = ReadUInt32(data, 96), bMask = ReadUInt32(data, 100), aMask = ReadUInt32(data, 104);
            int bitsPerPixel = ReadInt32(data, 88);
            int offset = 128;
            if (fourCc == "DX10")
            {
                if (data.Length < 148)
                    throw new InvalidDataException(EnglishText.Text("DDS is missing the DX10 header."));
                int dxgi = ReadInt32(data, 128);
                switch (dxgi)
                {
                    case 71:
                    case 72:
                        fourCc = "DXT1";
                        break;
                    case 74:
                    case 75:
                        fourCc = "DXT3";
                        break;
                    case 77:
                    case 78:
                        fourCc = "DXT5";
                        break;
                    case 83:
                    case 84:
                        fourCc = "ATI2";
                        break;
                    case 80:
                    case 81:
                        fourCc = "ATI1";
                        break;
                    case 28:
                    case 29:
                    case 87:
                    case 91:
                        fourCc = "";
                        break;
                    default:
                        throw new NotSupportedException(EnglishText.Format("DDS DXGI format {0} is not yet supported.", dxgi));
                }
                if (fourCc.Length == 0)
                {
                    pfFlags = 0x41;
                    bitsPerPixel = 32;
                    (rMask, gMask, bMask, aMask) = (dxgi == 28) || (dxgi == 29
)
                        ? (0x000000FFu, 0x0000FF00u, 0x00FF0000u, 0xFF000000u)
                        : (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
                }
                offset = 148;
            }

            var pixels = new byte[checked(width * height * 4)];
            string format;
            switch (fourCc)
            {
                case "DXT1":
                    DecodeColorBlocks(data, offset, width, height, pixels, true);
                    format = "BC1 / DXT1";
                    break;
                case "DXT3":
                    DecodeDxt3(data, offset, width, height, pixels);
                    format = "BC2 / DXT3";
                    break;
                case "DXT5":
                    DecodeDxt5(data, offset, width, height, pixels);
                    format = "BC3 / DXT5";
                    break;
                case "ATI2":
                    DecodeBc5(data, offset, width, height, pixels);
                    format = "BC5 / ATI2";
                    break;
                case "ATI1":
                    DecodeBc4Blocks(data, offset, width, height, pixels);
                    format = "BC4 / ATI1";
                    break;
                default:
                    if ((pfFlags & (0x40 | 0x2 | 0x20000)) != 0 && (((bitsPerPixel == 8) || (bitsPerPixel == 16)) || (bitsPerPixel == 24)) || (bitsPerPixel == 32))
                    {
                        DecodeUncompressed(data, offset, width, height, pixels, bitsPerPixel, pfFlags, rMask, gMask, bMask, aMask);
                        format = $"{bitsPerPixel}-bit uncompressed";
                    }
                    else
                        throw new NotSupportedException(EnglishText.Format("DDS format {0} is not yet supported.", fourCc.Trim('\0')));
                    break;
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return new DdsPreviewResult(bitmap, width, height, format);
        }

        /// <summary>
        /// Resolves or reads the Dxt3 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeDxt3(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Require(data, offset, (long)blocksX * blocksY * 16);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * 16;
                    ulong alphaBits = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at, 8));
                    DecodeColorBlock(data, at + 8, bx, by, width, height, pixels, false);
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                        {
                            int pixel = py * 4 + px;
                            byte alpha = (byte)(((alphaBits >> (pixel * 4)) & 0xf) * 17);
                            SetAlpha(pixels, width, height, bx * 4 + px, by * 4 + py, alpha);
                        }
                }
        }

        /// <summary>
        /// Resolves or reads the Dxt5 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeDxt5(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Require(data, offset, (long)blocksX * blocksY * 16);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * 16;
                    var alpha = DecodeBc4(data.AsSpan(at, 8));
                    DecodeColorBlock(data, at + 8, bx, by, width, height, pixels, false);
                    for (int py = 0; py < 4; py++)
                        for (int px = 0; px < 4; px++)
                            SetAlpha(pixels, width, height, bx * 4 + px, by * 4 + py, alpha[py * 4 + px]);
                }
        }

        /// <summary>
        /// Resolves or reads the ColorBlocks value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeColorBlocks(byte[] data, int offset, int width, int height, byte[] pixels, bool allowTransparent)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Require(data, offset, (long)blocksX * blocksY * 8);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                    DecodeColorBlock(data, offset + (by * blocksX + bx) * 8, bx, by, width, height, pixels, allowTransparent);
        }

        /// <summary>
        /// Resolves or reads the Bc4Blocks value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeBc4Blocks(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Require(data, offset, (long)blocksX * blocksY * 8);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    var values = DecodeBc4(data.AsSpan(offset + (by * blocksX + bx) * 8, 8));
                    for (int i = 0; i < 16; i++)
                        SetPixel(pixels, width, height, bx * 4 + i % 4, by * 4 + i / 4,
                        values[i], values[i], values[i], 255);
                }
        }

        /// <summary>
        /// Resolves or reads the ColorBlock value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeColorBlock(byte[] data, int at, int bx, int by, int width, int height, byte[] pixels, bool allowTransparent)
        {
            ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));
            ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2, 2));
            var colors = new (byte R, byte G, byte B, byte A)[4];
            colors[0] = From565(c0);
            colors[1] = From565(c1);
            if (!allowTransparent || c0 > c1)
            {
                colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
            }
            else
            {
                colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                colors[3] = (0, 0, 0, 0);
            }
            uint selectors = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4, 4));
            for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    var c = colors[(int)((selectors >> ((py * 4 + px) * 2)) & 3)];
                    SetPixel(pixels, width, height, bx * 4 + px, by * 4 + py, c.R, c.G, c.B, c.A);
                }
        }

        /// <summary>
        /// Resolves or reads the Bc5 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeBc5(byte[] data, int offset, int width, int height, byte[] pixels)
        {
            int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
            Require(data, offset, (long)blocksX * blocksY * 16);
            for (int by = 0; by < blocksY; by++)
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int at = offset + (by * blocksX + bx) * 16;
                    var red = DecodeBc4(data.AsSpan(at, 8));
                    var green = DecodeBc4(data.AsSpan(at + 8, 8));
                    for (int i = 0; i < 16; i++)
                    {
                        float nx = red[i] / 127.5f - 1, ny = green[i] / 127.5f - 1;
                        byte blue = (byte)Helpers.Clamp((int)Math.Round(Helpers.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny)) * 127.5f + 127.5f), 0, 255);
                        SetPixel(pixels, width, height, bx * 4 + i % 4, by * 4 + i / 4, red[i], green[i], blue, 255);
                    }
                }
        }

        /// <summary>
        /// Resolves or reads the Bc4 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static byte[] DecodeBc4(ReadOnlySpan<byte> block)
        {
            var palette = new byte[8];
            palette[0] = block[0];
            palette[1] = block[1];
            if (palette[0] > palette[1])
                for (int i = 1; i <= 6; i++)
                    palette[i + 1] = (byte)(((7 - i) * palette[0] + i * palette[1]) / 7);
            else
            {
                for (int i = 1; i <= 4; i++)
                    palette[i + 1] = (byte)(((5 - i) * palette[0] + i * palette[1]) / 5);
                palette[6] = 0;
                palette[7] = 255;
            }
            ulong selectors = 0;
            for (int i = 0; i < 6; i++)
                selectors |= (ulong)block[2 + i] << (i * 8);
            var result = new byte[16];
            for (int i = 0; i < 16; i++)
                result[i] = palette[(int)((selectors >> (i * 3)) & 7)];
            return result;
        }

        /// <summary>
        /// Resolves or reads the Uncompressed value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void DecodeUncompressed(byte[] data, int offset, int width, int height, byte[] pixels, int bitsPerPixel,
            uint flags, uint rMask, uint gMask, uint bMask, uint aMask)
        {
            int bytesPerPixel = bitsPerPixel / 8;
            Require(data, offset, (long)width * height * bytesPerPixel);
            bool alphaOnly = (flags & 0x2) != 0 && (flags & 0x40) == 0;
            bool luminance = (flags & 0x20000) != 0;
            for (int i = 0; i < width * height; i++)
            {
                ReadOnlySpan<byte> packedBytes = data.AsSpan(offset + i * bytesPerPixel, bytesPerPixel);
                uint packed;
                switch (bytesPerPixel)
                {
                    case 1:
                        packed = packedBytes[0];
                        break;
                    case 2:
                        packed = BinaryPrimitives.ReadUInt16LittleEndian(packedBytes);
                        break;
                    case 3:
                        packed = (uint)(packedBytes[0] | packedBytes[1] << 8 | packedBytes[2] << 16);
                        break;
                    case 4:
                        packed = BinaryPrimitives.ReadUInt32LittleEndian(packedBytes);
                        break;
                    default:
                        packed = 0;
                        break;
                }
                byte r = Extract(packed, rMask), g = Extract(packed, gMask), b = Extract(packed, bMask);
                byte a = aMask == 0 ? (byte)255 : Extract(packed, aMask);
                if (luminance)
                {
                    byte value = rMask != 0 ? r : (byte)packed;
                    r = g = b = value;
                }
                if (alphaOnly)
                {
                    r = g = b = 255;
                    a = aMask == 0 ? (byte)packed : Extract(packed, aMask);
                }
                int dst = i * 4;
                pixels[dst] = b;
                pixels[dst + 1] = g;
                pixels[dst + 2] = r;
                pixels[dst + 3] = a;
            }
        }

        static (byte R, byte G, byte B, byte A) From565(ushort value) =>
            ((byte)(((value >> 11) & 31) * 255 / 31), (byte)(((value >> 5) & 63) * 255 / 63), (byte)((value & 31) * 255 / 31), 255);

        static (byte R, byte G, byte B, byte A) Mix((byte R, byte G, byte B, byte A) a, (byte R, byte G, byte B, byte A) b,
            int weightA, int weightB, int divisor) =>
            ((byte)((a.R * weightA + b.R * weightB) / divisor), (byte)((a.G * weightA + b.G * weightB) / divisor),
             (byte)((a.B * weightA + b.B * weightB) / divisor), 255);

        static byte Extract(uint value, uint mask)
        {
            if (mask == 0)
                return 0;
            int shift = Helpers.TrailingZeroCount(mask);
            uint max = mask >> shift;
            return (byte)(((value & mask) >> shift) * 255 / max);
        }

        /// <summary>
        /// Updates, serializes or converts the Alpha value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void SetAlpha(byte[] pixels, int width, int height, int x, int y, byte alpha)
        {
            if ((uint)x < (uint)width && (uint)y < (uint)height)
                pixels[(y * width + x) * 4 + 3] = alpha;
        }

        /// <summary>
        /// Updates, serializes or converts the Pixel value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static void SetPixel(byte[] pixels, int width, int height, int x, int y, byte r, byte g, byte b, byte a)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
                return;
            int at = (y * width + x) * 4;
            pixels[at] = b;
            pixels[at + 1] = g;
            pixels[at + 2] = r;
            pixels[at + 3] = a;
        }

        /// <summary>
        /// Resolves or reads the Int32 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static int ReadInt32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        /// <summary>
        /// Resolves or reads the UInt32 value. Decode supported DDS pixels into frozen WPF preview images.
        /// </summary>
        static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        static void Require(byte[] data, int offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > data.Length)
                throw new InvalidDataException(EnglishText.Text("DDS data is truncated."));
        }
    }
}
