using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    /// <summary>
    /// Pure C# bit-exact Redacted container export/rebuild; no Python runtime is required.
    /// </summary>
    public sealed class LosslessContainer
    {
        private readonly Action<string> _log;

        public LosslessContainer(Action<string> log = null)
        {
            _log = log;
        }

        const int HeaderSize = 0x138;

        const int ChunkLimit = 0x8000;

        const int RingSize = 16000;

        const int HashSize = 20;

        const int ZeroTailSize = 64;

        static readonly byte[] CaptureMagic = System.Text.Encoding.UTF8.GetBytes("OATRD1\0\0").ToArray();

        /// <summary>
        /// Exports and reconstructs the container, then checks reconstructed bytes against the original.
        /// </summary>
        public Task<OperationResult> RunAsync(string fastfile, string capture, string outputRoot, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(fastfile);
            var capturePath = Path.GetFullPath(capture);
            if (!Workspace.IsRedacted(sourcePath))
                throw new InvalidDataException(EnglishText.Text("An original Redacted FF is required."));
            CaptureValidator.Validate(capturePath);
            var run = Workspace.NewRun(outputRoot, "lossless-container");
            var exportDir = Path.Combine(run, "export");
            var rebuilt = Path.Combine(run, "rebuilt", Path.GetFileName(sourcePath));
            Export(sourcePath, capturePath, exportDir, cancellation);
            Rebuild(exportDir, capturePath, rebuilt, cancellation);
            if (!Workspace.Hash(sourcePath).Equals(Workspace.Hash(rebuilt), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(EnglishText.Text("Byte-for-byte comparison of the rebuilt FF failed."));
            _log?.Invoke("Bit-exact Redacted container roundtrip completed.");
            return Task.FromResult(new OperationResult(run, EnglishText.Text("Byte-for-byte FF export and rebuild matched."), 
                new string[] { EnglishText.Text("This verifies an unchanged container. Modified assets require in-client validation.") }));
        }

        /// <summary>
        /// Preserve encoded chunks and framing metadata for checked reconstruction.
        /// </summary>
        void Export(string sourcePath, string capturePath, string folder, CancellationToken cancellation)
        {
            if (Directory.Exists(folder))
                throw new InvalidOperationException(EnglishText.Text("Export folder already exists."));
            var source = File.ReadAllBytes(sourcePath);
            var info = Inspect(source);
            var (key, ring) = ReadCapture(capturePath, info.ZoneName);
            var compressed = new MemoryStream();
            var xfile = new MemoryStream();
            var records = new List<ChunkRecord>();
            var ordinals = new int[4];
            var chunks = ReadChunks(source);
            for (int index = 0; index < chunks.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                int stream = index % 4;
                int ordinal = ordinals[stream];
                int ivOffset = ((ordinal % 200) * 4 + stream) * HashSize;
                var plaintext = Salsa20.Xor(chunks[index], key, ring.AsSpan(ivOffset, 8));
                var decoded = InflateRaw(plaintext, index);
                compressed.Write(plaintext);
                xfile.Write(decoded);
                records.Add(new ChunkRecord(plaintext.Length, decoded.Length, Hash(plaintext), Hash(decoded)));
                StepRing(ring, plaintext, stream, ordinal);
                ordinals[stream]++;
            }
            var header = source.AsSpan(0, HeaderSize).ToArray();
            var manifest = new ContainerManifest("redacted-bit-exact-v1", info.ZoneName, Hash(source), source.Length, Hash(header), Hash(compressed.ToArray()), Hash(xfile.ToArray()), xfile.Length, Hash(key), records, false, false);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "header.bin"), header);
            File.WriteAllBytes(Path.Combine(folder, "compressed.bin"), compressed.ToArray());
            File.WriteAllBytes(Path.Combine(folder, "xfile.bin"), xfile.ToArray());
            File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
            _log?.Invoke($"Exported {records.Count} XChunks; XFile bytes={xfile.Length}; sha256={Hash(xfile.ToArray())}");
        }

        /// <summary>
        /// Reconstruct from preserved chunks only when source hashes still match.
        /// </summary>
        void Rebuild(string folder, string capturePath, string outputPath, CancellationToken cancellation)
        {
            if (File.Exists(outputPath))
                throw new InvalidOperationException(EnglishText.Text("Output file already exists."));
            var manifestPath = Path.Combine(folder, "manifest.json");
            var manifest = JsonSerializer.Deserialize<ContainerManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidDataException(EnglishText.Text("Could not read the container manifest."));
            if (manifest.Format != "redacted-bit-exact-v1")
                throw new InvalidDataException(EnglishText.Text("Unsupported manifest format."));
            var header = File.ReadAllBytes(Path.Combine(folder, "header.bin"));
            var compressed = File.ReadAllBytes(Path.Combine(folder, "compressed.bin"));
            var xfile = File.ReadAllBytes(Path.Combine(folder, "xfile.bin"));
            RequireHash(header, manifest.HeaderSha256, EnglishText.Text("Opaque header was modified."));
            RequireHash(compressed, manifest.CompressedSha256, EnglishText.Text("Compressed XChunks were modified."));
            RequireHash(xfile, manifest.XfileSha256, EnglishText.Text("XFile was modified."));
            if (header.Length != HeaderSize || xfile.LongLength != manifest.XfileBytes)
                throw new InvalidDataException(EnglishText.Text("Header or XFile size does not match the manifest."));
            var nameBytes = header.AsSpan(0x18, 32);
            int terminator = nameBytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                terminator = nameBytes.Length;
            }
            var headerZone = Encoding.ASCII.GetString(nameBytes.Slice(0, (terminator) - (0)));
            if (!headerZone.Equals(manifest.ZoneName, StringComparison.Ordinal))
                throw new InvalidDataException(EnglishText.Text("Zone name in header differs from manifest."));
            var (key, ring) = ReadCapture(capturePath, manifest.ZoneName);
            RequireHash(key, manifest.RuntimeKeySha256, EnglishText.Text("Effective key differs from the export."));
            using (var output = new MemoryStream())
            {
                output.Write(header);
                int compressedPosition = 0, rawPosition = 0;
                var ordinals = new int[4];
                for (int index = 0; index < manifest.Xchunks.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var record = manifest.Xchunks[index];
                    if (record.CompressedBytes < 0 || record.DecodedBytes <= 0 || record.DecodedBytes > 32704 ||
                        compressedPosition > compressed.Length - record.CompressedBytes || rawPosition > xfile.Length - record.DecodedBytes)
                        throw new InvalidDataException(EnglishText.Format("XChunk {0} exceeds the exported stream bounds.", index));
                    var packed = compressed.AsSpan(compressedPosition, record.CompressedBytes).ToArray();
                    var decoded = xfile.AsSpan(rawPosition, record.DecodedBytes).ToArray();
                    RequireHash(packed, record.CompressedSha256, $"XChunk {index}: SHA-256 compressed mismatch.");
                    RequireHash(decoded, record.DecodedSha256, $"XChunk {index}: SHA-256 decoded mismatch.");
                    var decodedAgain = InflateRaw(packed, index);
                    if (!decodedAgain.AsSpan().SequenceEqual(decoded))
                        throw new InvalidDataException(EnglishText.Format("XChunk {0} differs from XFile.", index));
                    int stream = index % 4;
                    int ordinal = ordinals[stream];
                    int ivOffset = ((ordinal % 200) * 4 + stream) * HashSize;
                    var cipher = Salsa20.Xor(packed, key, ring.AsSpan(ivOffset, 8));
                    var size = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)cipher.Length));
                    output.Write(size);
                    output.Write(cipher);
                    StepRing(ring, packed, stream, ordinal);
                    ordinals[stream]++;
                    compressedPosition += record.CompressedBytes;
                    rawPosition += record.DecodedBytes;
                }
                if (compressedPosition != compressed.Length || rawPosition != xfile.Length)
                    throw new InvalidDataException(EnglishText.Text("Exported streams contain unused data."));
                output.Write(new byte[4 + ZeroTailSize]);
                var rebuilt = output.ToArray();
                _ = Inspect(rebuilt);
                if (rebuilt.Length != manifest.SourceFfBytes || !Hash(rebuilt).Equals(manifest.SourceFfSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Text("Rebuilt FF does not match the original byte for byte; write cancelled."));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
                using (var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(rebuilt);
                    _log?.Invoke($"Rebuilt {manifest.Xchunks.Count} XChunks; sha256={Hash(rebuilt)}; bit-exact=True");
                }
            }
        }

        /// <summary>
        /// Read container metadata without claiming arbitrary asset edit support.
        /// </summary>
        static FastfileInfo Inspect(byte[] data)
        {
            if (data.Length < HeaderSize + 4 + ZeroTailSize)
                throw new InvalidDataException(EnglishText.Text("Fastfile is shorter than the minimum Redacted header and trailer."));
            if (!data.AsSpan(0, 8).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("TAff0100")))
                throw new InvalidDataException(EnglishText.Text("Unexpected fastfile outer magic."));
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4)) != 0x1337)
                throw new InvalidDataException(EnglishText.Text("Expected Redacted version 0x1337."));
            if (!data.AsSpan(0x0C, 8).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("REDACTED")))
                throw new InvalidDataException(EnglishText.Text("Unexpected auth marker."));
            var nameField = data.AsSpan(0x18, 32);
            int nullAt = nameField.IndexOf((byte)0);
            if (nullAt < 0)
                throw new InvalidDataException(EnglishText.Text("Zone name is not NUL-terminated within the 32-byte field."));
            string zone = Encoding.ASCII.GetString(nameField.Slice(0, (nullAt) - (0)));
            if ((zone.Length < 1) || (zone.Length > 31) || nameField.Slice(0, (nullAt) - (0)).IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
                throw new InvalidDataException(EnglishText.Text("Zone name is empty or contains non-ASCII characters."));
            int pos = HeaderSize, count = 0;
            while (pos + 4 <= data.Length)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                if (size == 0)
                {
                    if (data.Length - (pos + 4) != ZeroTailSize || data.AsSpan(pos + 4).IndexOfAnyExcept((byte)0) >= 0)
                        throw new InvalidDataException(EnglishText.Text("Expected exactly 64 zero bytes after the XChunk end marker."));
                    break;
                }
                if (size > ChunkLimit)
                    throw new InvalidDataException(EnglishText.Format("Invalid XChunk size 0x{0:X} at offset 0x{1:X}.", size, pos));
                if ((long)pos + 4 + size > data.Length)
                    throw new InvalidDataException(EnglishText.Format("XChunk at offset 0x{0:X} extends past end of file.", pos));
                count++;
                pos += 4 + (int)size;
            }
            if (pos + 4 > data.Length || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4)) != 0)
                throw new InvalidDataException(EnglishText.Text("XChunk end marker not found."));
            if (count == 0)
                throw new InvalidDataException(EnglishText.Text("Fastfile does not contain XChunks."));
            return new FastfileInfo(zone, count);
        }

        /// <summary>
        /// Resolves or reads the Chunks value. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        static List<byte[]> ReadChunks(byte[] data)
        {
            var chunks = new List<byte[]>();
            int pos = HeaderSize;
            while (pos + 4 <= data.Length)
            {
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if (size == 0)
                    return chunks;
                if (size > int.MaxValue || pos + (long)size > data.Length)
                    throw new InvalidDataException(EnglishText.Text("XChunk exceeds file size."));
                chunks.Add(data.AsSpan(pos, (int)size).ToArray());
                pos += (int)size;
            }
            throw new InvalidDataException(EnglishText.Text("XChunk end marker not found."));
        }

        /// <summary>
        /// Resolves or reads the Capture value. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        static (byte[] Key, byte[] Ring) ReadCapture(string path, string zone)
        {
            var capture = File.ReadAllBytes(path);
            if (capture.Length != 16040 || !capture.AsSpan(0, 8).SequenceEqual(CaptureMagic))
                throw new InvalidDataException(EnglishText.Text("A seeded OATRD1 capture of exactly 16,040 bytes is required."));
            var ring = capture.AsSpan(40).ToArray();
            var expected = MakeZoneSeed(zone);
            if (!ring.AsSpan().SequenceEqual(expected))
                throw new InvalidDataException(EnglishText.Text("Capture ring does not match the zone name seed."));
            return (capture.AsSpan(8, 32).ToArray(), ring);
        }

        static byte[] MakeZoneSeed(string zone)
        {
            var bytes = Encoding.ASCII.GetBytes(zone);
            if (bytes.Length == 0)
                throw new InvalidDataException(EnglishText.Text("Zone name is empty."));
            var seed = new byte[RingSize];
            for (int offset = 0; offset < seed.Length; offset += 4)
            {
                seed.AsSpan(offset, 4).Fill(bytes[(offset / 4) % bytes.Length]);
            }
            return seed;
        }

        static void StepRing(byte[] ring, byte[] compressed, int stream, int ordinal)
        {
            int next = (((ordinal + 1) % 200) * 4 + stream) * HashSize;
            Helpers.Sha1(compressed).CopyTo(ring, next);
        }

        static byte[] InflateRaw(byte[] data, int index)
        {
            try
            {
                using (var input = new MemoryStream(data, writable: false))
                {
                    using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                    {
                        using (var decoded = new MemoryStream())
                        {
                            inflater.CopyTo(decoded);
                            if ((decoded.Length <= 0) || (decoded.Length > 32704))
                            {
                                throw new InvalidDataException($"XChunk {index} failed strict raw-DEFLATE size validation.");
                            }
                            return decoded.ToArray();
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex) when ((ex is System.IO.IOException) || (ex is System.ArgumentException))
            {
                throw new InvalidDataException($"XChunk {index} failed raw-DEFLATE validation.", ex);
            }
        }

        /// <summary>
        /// Calculate a streaming SHA-256 file digest.
        /// </summary>
        static string Hash(byte[] bytes) => Helpers.ToHexString(Helpers.Sha256(bytes)).ToLowerInvariant();

        static void RequireHash(byte[] bytes, string expected, string message)
        {
            if (!Hash(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(message);
        }

        /// <summary>
        /// FastfileInfo. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        sealed class FastfileInfo
        {
            public string ZoneName { get; set; }

            public int ChunkCount { get; set; }

            public FastfileInfo(string ZoneName, int ChunkCount)
            {
                this.ZoneName = ZoneName;
                this.ChunkCount = ChunkCount;
            }
        }
        /// <summary>
        /// ChunkRecord. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        sealed class ChunkRecord
        {
            public int CompressedBytes { get; set; }

            public int DecodedBytes { get; set; }

            public string CompressedSha256 { get; set; }

            public string DecodedSha256 { get; set; }

            public ChunkRecord(int CompressedBytes, int DecodedBytes, string CompressedSha256, string DecodedSha256)
            {
                this.CompressedBytes = CompressedBytes;
                this.DecodedBytes = DecodedBytes;
                this.CompressedSha256 = CompressedSha256;
                this.DecodedSha256 = DecodedSha256;
            }
        }
        /// <summary>
        /// ContainerManifest. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        sealed class ContainerManifest
        {
            public string Format { get; set; }

            public string ZoneName { get; set; }

            public string SourceFfSha256 { get; set; }

            public long SourceFfBytes { get; set; }

            public string HeaderSha256 { get; set; }

            public string CompressedSha256 { get; set; }

            public string XfileSha256 { get; set; }

            public long XfileBytes { get; set; }

            public string RuntimeKeySha256 { get; set; }

            public List<ChunkRecord> Xchunks { get; set; }

            public bool ExternalLooseAssetsIncluded { get; set; }

            public bool EditedZoneInstallable { get; set; }

            public ContainerManifest(string Format, string ZoneName, string SourceFfSha256, long SourceFfBytes, string HeaderSha256, string CompressedSha256,  string XfileSha256, long XfileBytes, string RuntimeKeySha256, List<ChunkRecord> Xchunks, bool ExternalLooseAssetsIncluded, bool EditedZoneInstallable)
            {
                this.Format = Format;
                this.ZoneName = ZoneName;
                this.SourceFfSha256 = SourceFfSha256;
                this.SourceFfBytes = SourceFfBytes;
                this.HeaderSha256 = HeaderSha256;
                this.CompressedSha256 = CompressedSha256;
                this.XfileSha256 = XfileSha256;
                this.XfileBytes = XfileBytes;
                this.RuntimeKeySha256 = RuntimeKeySha256;
                this.Xchunks = Xchunks;
                this.ExternalLooseAssetsIncluded = ExternalLooseAssetsIncluded;
                this.EditedZoneInstallable = EditedZoneInstallable;
            }
        }

        /// <summary>
        /// Salsa20. Checked reconstruction from preserved Redacted container chunks.
        /// </summary>
        static class Salsa20
        {
            static ReadOnlySpan<byte> Sigma => System.Text.Encoding.UTF8.GetBytes("expand 32-byte k");

            static uint Rotate(uint value, int bits) => (value << bits) | (value >> (32 - bits));
            /// <summary>
            /// Resolves or reads the Word value. Checked reconstruction from preserved Redacted container chunks.
            /// </summary>
            static uint ReadWord(ReadOnlySpan<byte> source, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, 4));

            public static byte[] Xor(byte[] data, byte[] key, ReadOnlySpan<byte> nonce)
            {
                if (key.Length != 32 || nonce.Length != 8)
                    throw new InvalidDataException("Salsa20 requires a 32-byte key and 8-byte nonce.");
                var output = new byte[data.Length];
                for (int block = 0; block * 64 < data.Length; block++)
                {
                    var stream = Block(key, nonce, (ulong)block);
                    int offset = block * 64, count = Math.Min(64, data.Length - offset);
                    for (int i = 0; i < count; i++)
                    {
                        output[offset + i] = (byte)(data[offset + i] ^ stream[i]);
                    }
                }
                return output;
            }

            static byte[] Block(byte[] key, ReadOnlySpan<byte> nonce, ulong counter)
            {
                Span<uint> state = stackalloc uint[16];
                state[0] = ReadWord(Sigma, 0);
                state[5] = ReadWord(Sigma, 4);
                state[10] = ReadWord(Sigma, 8);
                state[15] = ReadWord(Sigma, 12);
                for (int i = 0; i < 4; i++)
                {
                    state[1 + i] = ReadWord(key, i * 4);
                }
                for (int i = 0; i < 4; i++)
                {
                    state[11 + i] = ReadWord(key, (i + 4) * 4);
                }
                state[6] = ReadWord(nonce, 0);
                state[7] = ReadWord(nonce, 4);
                state[8] = (uint)counter;
                state[9] = (uint)(counter >> 32);
                Span<uint> x = stackalloc uint[16];
                state.CopyTo(x);
                unchecked
                {
                    for (int round = 0; round < 10; round++)
                    {
                        x[4] ^= Rotate(x[0] + x[12], 7);
                        x[8] ^= Rotate(x[4] + x[0], 9);
                        x[12] ^= Rotate(x[8] + x[4], 13);
                        x[0] ^= Rotate(x[12] + x[8], 18);
                        x[9] ^= Rotate(x[5] + x[1], 7);
                        x[13] ^= Rotate(x[9] + x[5], 9);
                        x[1] ^= Rotate(x[13] + x[9], 13);
                        x[5] ^= Rotate(x[1] + x[13], 18);
                        x[14] ^= Rotate(x[10] + x[6], 7);
                        x[2] ^= Rotate(x[14] + x[10], 9);
                        x[6] ^= Rotate(x[2] + x[14], 13);
                        x[10] ^= Rotate(x[6] + x[2], 18);
                        x[3] ^= Rotate(x[15] + x[11], 7);
                        x[7] ^= Rotate(x[3] + x[15], 9);
                        x[11] ^= Rotate(x[7] + x[3], 13);
                        x[15] ^= Rotate(x[11] + x[7], 18);
                        x[1] ^= Rotate(x[0] + x[3], 7);
                        x[2] ^= Rotate(x[1] + x[0], 9);
                        x[3] ^= Rotate(x[2] + x[1], 13);
                        x[0] ^= Rotate(x[3] + x[2], 18);
                        x[6] ^= Rotate(x[5] + x[4], 7);
                        x[7] ^= Rotate(x[6] + x[5], 9);
                        x[4] ^= Rotate(x[7] + x[6], 13);
                        x[5] ^= Rotate(x[4] + x[7], 18);
                        x[11] ^= Rotate(x[10] + x[9], 7);
                        x[8] ^= Rotate(x[11] + x[10], 9);
                        x[9] ^= Rotate(x[8] + x[11], 13);
                        x[10] ^= Rotate(x[9] + x[8], 18);
                        x[12] ^= Rotate(x[15] + x[14], 7);
                        x[13] ^= Rotate(x[12] + x[15], 9);
                        x[14] ^= Rotate(x[13] + x[12], 13);
                        x[15] ^= Rotate(x[14] + x[13], 18);
                    }
                    Span<byte> result = stackalloc byte[64];
                    for (int i = 0; i < 16; i++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(result.Slice(i * 4, 4), x[i] + state[i]);
                    }
                    return result.ToArray();
                }
            }
        }
    }
}
