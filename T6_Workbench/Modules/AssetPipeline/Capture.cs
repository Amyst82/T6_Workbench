using System;
using System.IO;
using System.Linq;

namespace T6_Workbench
{
    /// <summary>
    /// Validates the Redacted OAT capture file before it is passed to a child process.
    /// </summary>
    public static class CaptureValidator
    {
        /// <summary>
        /// Validates the capture header and expected file size.
        /// </summary>
        public static void Validate(string path)
        {
            // A capture is required only for Redacted containers, never for ordinary T6 maps.
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Redacted capture not found. Select a valid OATRD1 capture in Settings.", path);
            using (var stream = File.OpenRead(path))
            {
                Span<byte> magic = stackalloc byte[8];
                stream.ReadExactly(magic);
                if (stream.Length != 16040 || !magic.SequenceEqual(System.Text.Encoding.UTF8.GetBytes("OATRD1\0\0")))
                    throw new InvalidDataException(EnglishText.Text("An initial OATRD1 capture (16,040 bytes) is required. Key contents are not displayed."));
            }
        }
    }
}
