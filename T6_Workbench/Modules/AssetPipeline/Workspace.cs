using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace T6_Workbench
{
    public static class Workspace
    {
        /// <summary>
        /// Calculate a streaming SHA-256 file digest.
        /// </summary>
        public static string Hash(string path)
        {
            using (var file = File.OpenRead(path))
            {
                return Helpers.ToHexString(Helpers.Sha256(file)).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Create a unique operation output folder outside the game installation.
        /// </summary>
        public static string NewRun(string outputRoot, string label)
        {
            var root = Path.GetFullPath(outputRoot);
            RejectGameOutput(root);
            Directory.CreateDirectory(root);
            var run = Path.Combine(root, $"{label}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid().ToString("N").Substring(0, (6) - (0))}");
            Directory.CreateDirectory(run);
            return run;
        }

        /// <summary>
        /// Reject output under a detected game-install directory.
        /// </summary>
        public static void RejectGameOutput(string output)
        {
            // Refuse writing under any ancestor identified as a game install.
            for (DirectoryInfo dir = new System.IO.DirectoryInfo(Path.GetFullPath(output)); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "t6mpv43.exe")) || File.Exists(Path.Combine(dir.FullName, "t6mp.exe")))
                {
                    throw new InvalidOperationException(EnglishText.Text("Results must be saved outside the game folder. Installing the mod into the game is a separate step."));
                }
            }
        }

        /// <summary>
        /// Write an indented JSON report or manifest.
        /// </summary>
        public static void WriteJson(string file, object value) =>
            File.WriteAllText(file, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        /// <summary>
        /// Inspect fastfile header markers and validate the filename/zone relationship.
        /// </summary>
        public static bool IsRedacted(string ff)
        {
            using (var stream = File.OpenRead(ff))
            {
                Span<byte> header = stackalloc byte[56];
                stream.ReadExactly(header);
                if (!header.Slice(0, (8) - (0)).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("TAff0100")) || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, (12) - (8))) != 0x1337)
                    return false;
                if (!header.Slice(12, (20) - (12)).SequenceEqual(System.Text.Encoding.UTF8.GetBytes("REDACTED")))
                    throw new InvalidDataException(EnglishText.Text("Corrupt marker REDACTED."));
                int end = header.Slice(24, (56) - (24)).IndexOf((byte)0);
                if (end <= 0)
                    throw new InvalidDataException(EnglishText.Text("Invalid zone name."));
                var name = Encoding.ASCII.GetString(header.Slice(24, end));
                if (!string.Equals(Path.GetFileNameWithoutExtension(ff), name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(EnglishText.Format("The filename must be {0}.ff: it is used for decryption.", name));
                return true;
            }
        }
    }
}
