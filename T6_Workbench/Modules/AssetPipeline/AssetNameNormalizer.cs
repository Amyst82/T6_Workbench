using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace T6_Workbench
{
    /// <summary>
    /// Repairs delimiter artifacts in asset names without changing valid asset identities.
    /// </summary>
    internal static class AssetNameNormalizer
    {
        /// <summary>
        /// Remove the extra leading comma emitted for some material entries by the current OAT export.
        /// </summary>
        internal static string NormalizeName(string name)
        {
            string result = name.Trim();
            while (result.StartsWith(",", StringComparison.Ordinal))
            {
                result = result.Substring(1).TrimStart();
            }
            return result.Trim();
        }

        /// <summary>
        /// Normalize one type,name line while preserving zone directives and comments.
        /// </summary>
        internal static string NormalizeZoneLine(string line)
        {
            string value = line.Trim();
            if (value.Length == 0 || value.StartsWith(">", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
                return value;
            int comma = value.IndexOf(',');
            if (comma <= 0 || comma >= value.Length - 1)
                return value;
            string type = value.Substring(0, comma).Trim();
            string name = NormalizeName(value.Substring(comma + 1));
            return type.Length == 0 || name.Length == 0 ? value : type + "," + name;
        }

        /// <summary>
        /// Read known material names from either the saved inventory or the project's zone definition.
        /// </summary>
        internal static HashSet<string> ReadMaterialNames(string inventoryPath, string zonePath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(inventoryPath))
            {
                using (var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath)))
                {
                    if (inventory.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in inventory.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString().Equals("material", StringComparison.OrdinalIgnoreCase) && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                            {
                                string canonical = NormalizeName(name.GetString());
                                if (canonical.Length > 0)
                                {
                                    result.Add(canonical);
                                }
                            }
                        }
                    }
                }
            }
            // Read the zone as a fallback and also as a supplement: older inventories may omit dependencies.
            if (File.Exists(zonePath))
            {
                foreach (string raw in File.ReadLines(zonePath))
                {
                    string line = NormalizeZoneLine(raw);
                    int comma = line.IndexOf(',');
                    if (comma <= 0 || comma == line.Length - 1 || !line.Substring(0, comma).Trim().Equals("material", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string name = NormalizeName(line.Substring(comma + 1));
                    if (name.Length > 0)
                    {
                        result.Add(name);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Canonicalize material names in an OAT-generated zone file in the build-owned copy.
        /// </summary>
        internal static void NormalizeZoneFile(string path)
        {
            if (!File.Exists(path))
                return;
            string[] lines = File.ReadAllLines(path);
            string[] normalized = Array.ConvertAll(lines, NormalizeZoneLine);
            if (lines.SequenceEqual(normalized, StringComparer.Ordinal))
                return;
            File.WriteAllLines(path, normalized);
        }
    }
}
