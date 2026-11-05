using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T6_Workbench
{
    internal static class Helpers
    {
        /// <summary>
        /// Build a Windows relative path without URI escaping or current-directory assumptions.
        /// </summary>
        internal static string GetRelativePath(string directory, string file)
        {
            string from = Path.GetFullPath(directory), to = Path.GetFullPath(file);
            if (!string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase))
                return to;
            var a = from.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var b = to.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            int common = 0;
            while (common < a.Length && common < b.Length && a[common].Equals(b[common], StringComparison.OrdinalIgnoreCase))
            {
                common++;
            }
            var parts = Enumerable.Repeat("..", a.Length - common).Concat(b.Skip(common)).ToArray();
            return parts.Length == 0 ? "." : string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        /// <summary>
        /// Performs the PathFullyQualified is operation. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static bool IsPathFullyQualified(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && (path.StartsWith("\\\\", StringComparison.Ordinal) || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')));
        }
        /// <summary>
        /// Performs the Finite is operation. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        /// <summary>
        /// Performs the Finite is operation. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

        internal static float Clamp(float value, float min, float max) => Math.Min(max, Math.Max(min, value));

        internal static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

        internal static float Sqrt(float value) => (float)Math.Sqrt(value);

        internal static int TrailingZeroCount(uint value)
        {
            if (value == 0)
                return 32;
            int result = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                result++;
            }
            return result;
        }

        internal static float Int32BitsToSingle(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

        internal static int SingleToInt32Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        internal static byte[] Sha256(byte[] data)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(data);
            }
        }

        internal static byte[] Sha256(Stream stream)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(stream);
            }
        }

        internal static byte[] Sha1(byte[] data)
        {
            using (var hash = SHA1.Create())
            {
                return hash.ComputeHash(data);
            }
        }

        internal static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        internal static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));
            var batch = new List<T>(size);
            foreach (T value in source)
            {
                batch.Add(value);
                if (batch.Count == size)
                {
                    yield return batch.ToArray();
                    batch.Clear();
                }
            }
            if (batch.Count != 0)
                yield return batch.ToArray();
        }

        internal static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> source, IComparer<T> comparer = null) => source.OrderBy(value => value, comparer);

        internal static void Clear<T>(this System.Collections.Concurrent.ConcurrentQueue<T> queue)
        {
            while (queue.TryDequeue(out var removedItem))
            {
            }
        }

        internal static int IndexOfAnyInRange(this Span<byte> bytes, byte low, byte high)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] >= low && bytes[i] <= high)
                    return i;
            }
            return -1;
        }
        /// <summary>
        /// Fill a buffer across short stream reads or throw on premature EOF.
        /// </summary>
        internal static void ReadExactly(this Stream stream, Span<byte> destination)
        {
            byte[] buffer = new byte[destination.Length];
            int offset = 0;
            while (offset < buffer.Length)
            {
                int count = stream.Read(buffer, offset, buffer.Length - offset);
                if (count == 0)
                    throw new EndOfStreamException("Unexpected end of binary data.");
                offset += count;
            }
            buffer.AsSpan().CopyTo(destination);
        }

        internal static int Read(this Stream stream, Span<byte> destination)
        {
            byte[] buffer = new byte[destination.Length];
            int count = stream.Read(buffer, 0, buffer.Length);
            buffer.AsSpan(0, count).CopyTo(destination);
            return count;
        }
        /// <summary>
        /// Updates, serializes or converts the  value. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static void Write(this Stream stream, ReadOnlySpan<byte> bytes)
        {
            byte[] buffer = bytes.ToArray();
            stream.Write(buffer, 0, buffer.Length);
        }
        /// <summary>
        /// Updates, serializes or converts the  value. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static void Write(this BinaryWriter writer, ReadOnlySpan<byte> bytes) => writer.Write(bytes.ToArray());
        /// <summary>
        /// Resolves or reads the String value. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static string GetString(this Encoding encoding, ReadOnlySpan<byte> bytes) => encoding.GetString(bytes.ToArray());
        /// <summary>
        /// Resolves or reads the String value. Small .NET Framework API compatibility implementations used at call sites.
        /// </summary>
        internal static string GetString(this Encoding encoding, Span<byte> bytes) => encoding.GetString(bytes.ToArray());

        internal static int IndexOfAnyExcept(this Span<byte> bytes, byte value)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != value)
                    return i;
            }
            return -1;
        }

        internal static bool Contains(this string value, string text, StringComparison comparison) => value.IndexOf(text, comparison) >= 0;

        internal static bool Contains(this string value, char character) => value.IndexOf(character) >= 0;

        internal static bool StartsWith(this string value, char character) => value.Length > 0 && value[0] == character;

        internal static bool EndsWith(this string value, char character) => value.Length > 0 && value[value.Length - 1] == character;

        internal static string[] Split(this string value, char separator, StringSplitOptions options) => value.Split(new[] { separator }, options);

        internal static string[] Split(this string value, char separator, int count, StringSplitOptions options) => value.Split(new[] { separator }, count, options);

        internal static TValue GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> source, TKey key, TValue defaultValue = default)
        {
            return source.TryGetValue(key, out var value) ? value : defaultValue;
        }

        internal static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> source, TKey key, TValue value)
        {
            if (source.ContainsKey(key))
                return false;
            source.Add(key, value);
            return true;
        }

        internal static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(this IEnumerable<TFirst> first, IEnumerable<TSecond> second)
        {
            return Enumerable.Zip(first, second, (a, b) => (a, b));
        }

        internal static async Task<string> ReadAllTextAsync(string file, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    string result = await reader.ReadToEndAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return result;
                }
            }
        }

        internal static async Task WriteAllTextAsync(string file, string text, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            }
        }

        internal static void MoveFile(string source, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination))
            {
                File.Replace(source, destination, null);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        /// <summary>
        /// Quote one argv value using the Windows CRT backslash/quote rules.
        /// </summary>
        /// <remarks>
        /// No command shell is used. A trailing backslash must be doubled before the closing quote.
        /// </remarks>
        internal static string QuoteArgument(string value)
        {
            if (value.IndexOf('\0') >= 0)
                throw new ArgumentException("Process arguments cannot contain NUL.", nameof(value));
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    slashes++;
                    continue;
                }
                if (character == '"')
                {
                    result.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes).Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        /// <summary>
        /// Observe process exit without blocking the UI dispatcher or polling.
        /// </summary>
        internal static async Task WaitForExitAsync(Process process)
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler handler = (eventSender, eventArgs) => completed.TrySetResult(true);
            process.Exited += handler;
            try
            {
                process.EnableRaisingEvents = true;
                // Close the race where the process exits just before the event subscription.
                if (process.HasExited)
                {
                    completed.TrySetResult(true);
                }
                await completed.Task.ConfigureAwait(false);
            }
            finally
            {
                process.Exited -= handler;
            }
        }
    }
}
