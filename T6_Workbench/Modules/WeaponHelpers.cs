using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace T6_Workbench
{
    /// <summary>Private implementation details for reading and editing T6 weapon definitions.</summary>
    internal static class WeaponHelpers
    {
        const string WeaponHeader = "WEAPONFILE";
        const string AttachmentField = "attachments";
        const string UniqueAttachmentField = "attachmentUniques";

        static readonly string[] MainFieldNames =
        Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { "displayName" }, new string[] { "weaponType" })), new string[] { "weaponClass" })), new string[] { "playerAnimType" })), new string[] { "fireType" })), new string[] { "inventoryType" })), new string[] { "ammoName" })), new string[] { "clipSize" })), new string[] { "startAmmo" })), new string[] { "maxAmmo" })), new string[] { "damage" })), new string[] { "minDamage" })), new string[] { "maxDamage" })), new string[] { "range"}));

        /// <summary>Resolve a raw extensionless OAT weapon dump, with common text-file suffixes as fallbacks.</summary>
        internal static string FindAssetFile(string projectRoot, string assetName)
        {
            if (!Directory.Exists(projectRoot) || string.IsNullOrWhiteSpace(assetName))
                return null;

            string weaponDirectory = Path.Combine(projectRoot, "weapons");
            if (!Directory.Exists(weaponDirectory))
                return null;

            string relativeName = assetName.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string[] candidates =
            Enumerable.ToArray(Enumerable.Concat(Enumerable.ToArray(Enumerable.Concat(new string[] { Path.Combine(weaponDirectory, relativeName) }, new string[] { Path.Combine(weaponDirectory, relativeName + ".weapon") })), new string[] {                 Path.Combine(weaponDirectory, relativeName + ".txt")}));

            foreach (string candidate in candidates)
            {
                string fullPath = Path.GetFullPath(candidate);
                string fullRoot = Path.GetFullPath(weaponDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
                    return fullPath;
            }

            string expectedFileName = Path.GetFileName(assetName);
            string[] matches = Directory.EnumerateFiles(weaponDirectory, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetFileNameWithoutExtension(file).Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        /// <summary>Load a T6 WEAPONFILE while preserving its original field order for roundtrip edits.</summary>
        internal static WeaponPreviewDocument LoadPreview(string filePath, string assetName)
        {
            string source = File.ReadAllText(filePath, Encoding.UTF8)
                .TrimStart('\uFEFF')
                .TrimEnd('\0', '\r', '\n');
            string[] tokens = source.Split('\\');

            // Some text dumpers leave one final delimiter; ignore only that empty trailing token.
            if (tokens.Length > 1 && tokens.Length % 2 == 0 && tokens[tokens.Length - 1].Length == 0)
                Array.Resize(ref tokens, tokens.Length - 1);

            if (tokens.Length < 3 || !tokens[0].Equals(WeaponHeader, StringComparison.Ordinal))
                throw new InvalidDataException(EnglishText.Text("The weapon file does not contain the WEAPONFILE header."));
            if (tokens.Length % 2 != 1)
                throw new InvalidDataException(EnglishText.Text("The weapon file contains malformed field/value pairs."));

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var orderedFields = new List<WeaponFieldValue>();
            for (int tokenIndex = 1; tokenIndex < tokens.Length; tokenIndex += 2)
            {
                string fieldName = tokens[tokenIndex].Trim();
                if (fieldName.Length == 0)
                    continue;

                string fieldValue = tokens[tokenIndex + 1];
                fields[fieldName] = fieldValue;
                orderedFields.Add(new WeaponFieldValue(fieldName, fieldValue));
            }

            var modelNames = fields
                .Where(field => IsModelField(field.Key) && !string.IsNullOrWhiteSpace(field.Value))
                .OrderBy(field => ModelFieldPriority(field.Key))
                .ThenBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .Select(field => field.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new WeaponPreviewDocument(
                assetName,
                fields,
                orderedFields,
                modelNames,
                CreateSummary(fields),
                CreateReport(assetName, fields, modelNames));
        }

        /// <summary>Read a newline-separated attachment list from the requested weapon field.</summary>
        internal static IReadOnlyList<string> GetModuleNames(WeaponPreviewDocument document, string fieldName)
        {
            if (!IsAttachmentField(fieldName) || !document.Fields.TryGetValue(fieldName, out string value))
                return Array.Empty<string>();

            return value.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(moduleName => moduleName.Trim())
                .Where(moduleName => moduleName.Length > 0)
                .ToArray();
        }

        /// <summary>Write edited fields and attachment lists only into the project's weapon override.</summary>
        internal static string SaveEdits(WorkbenchProject project, WeaponPreviewDocument document,
            IReadOnlyDictionary<string, string> editedFields, IReadOnlyList<string> attachments,
            IReadOnlyList<string> attachmentUniques)
        {
            if (string.IsNullOrWhiteSpace(document.AssetName) || Path.IsPathRooted(document.AssetName)
                || document.AssetName.IndexOfAny(new[] { ':', '\0' }) >= 0
                || document.AssetName.Split(new[] { '/', '\\' }, StringSplitOptions.None)
                    .Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                throw new InvalidDataException(EnglishText.Text("Invalid weapon asset name."));

            var moduleValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AttachmentField] = SerializeModules(attachments, AttachmentField),
                [UniqueAttachmentField] = SerializeModules(attachmentUniques, UniqueAttachmentField)
            };
            var savedFields = new List<WeaponFieldValue>();

            foreach (WeaponFieldValue field in document.OrderedFields)
            {
                string fieldName = field.Name;
                string fieldValue;
                if (IsAttachmentField(fieldName))
                {
                    continue;
                }
                if (editedFields.TryGetValue(fieldName, out string editedValue))
                {
                    fieldValue = editedValue;
                }
                else
                {
                    fieldValue = field.Value;
                }

                ValidateField(fieldName, fieldValue);
                savedFields.Add(new WeaponFieldValue(fieldName, fieldValue));
            }

            // Keep the module fields at the end of the serialized file for easy manual inspection.
            string attachmentFieldName = document.OrderedFields
                .FirstOrDefault(field => field.Name.Equals(AttachmentField, StringComparison.OrdinalIgnoreCase))?.Name
                ?? AttachmentField;
            string uniqueAttachmentFieldName = document.OrderedFields
                .FirstOrDefault(field => field.Name.Equals(UniqueAttachmentField, StringComparison.OrdinalIgnoreCase))?.Name
                ?? UniqueAttachmentField;
            savedFields.Add(new WeaponFieldValue(attachmentFieldName, moduleValues[AttachmentField]));
            savedFields.Add(new WeaponFieldValue(uniqueAttachmentFieldName, moduleValues[UniqueAttachmentField]));

            var serialized = new StringBuilder(WeaponHeader);
            foreach (WeaponFieldValue field in savedFields)
            {
                ValidateField(field.Name, field.Value);
                serialized.Append('\\').Append(field.Name).Append('\\').Append(field.Value);
            }

            string relativeAssetName = document.AssetName.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string targetPath = project.EditPath(Path.Combine("weapons", relativeAssetName));
            string targetDirectory = Path.GetDirectoryName(targetPath);
            Directory.CreateDirectory(targetDirectory);
            string temporaryPath = targetPath + ".tmp";
            File.WriteAllText(temporaryPath, serialized.ToString(), new UTF8Encoding(false));

            if (File.Exists(targetPath))
                File.Replace(temporaryPath, targetPath, null);
            else
                File.Move(temporaryPath, targetPath);

            return targetPath;
        }

        /// <summary>Build the short summary displayed for the weapon definition.</summary>
        static string CreateSummary(IReadOnlyDictionary<string, string> fields)
        {
            var summary = new List<string>();
            foreach (string fieldName in new[] { "weaponType", "weaponClass", "fireType", "gunModel", "worldModel" })
                if (fields.TryGetValue(fieldName, out string value) && !string.IsNullOrWhiteSpace(value))
                    summary.Add(fieldName + ": " + value);

            int animationCount = fields.Count(field => IsAnimationField(field.Key) && !string.IsNullOrWhiteSpace(field.Value));
            summary.Add("XAnim: " + animationCount);
            return string.Join(" · ", summary);
        }

        /// <summary>Format the weapon identity, model links and animation mappings for readable status output.</summary>
        static string CreateReport(string assetName, IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> modelNames)
        {
            var report = new StringBuilder();
            report.AppendLine(EnglishText.Text("T6 Weapon definition · ") + assetName);
            report.AppendLine(EnglishText.Text("Fields read: ") + fields.Count);
            AppendSection(report, EnglishText.Text("General"), MainFieldNames
                .Where(fields.ContainsKey)
                .Select(fieldName => (fieldName, fields[fieldName])));

            AppendSection(report, EnglishText.Text("Related models"), fields
                .Where(field => IsModelField(field.Key) && !string.IsNullOrWhiteSpace(field.Value))
                .OrderBy(field => ModelFieldPriority(field.Key))
                .ThenBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .Select(field => (field.Key, field.Value)));

            AppendSection(report, EnglishText.Text("XAnim animations"), fields
                .Where(field => IsAnimationField(field.Key) && !string.IsNullOrWhiteSpace(field.Value))
                .OrderBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .Select(field => (field.Key, field.Value)));

            if (modelNames.Count == 0)
                report.AppendLine(EnglishText.Text("The weapon definition has no xmodel references."));
            return report.ToString().TrimEnd();
        }

        /// <summary>Append a readable field group while omitting empty sections.</summary>
        static void AppendSection(StringBuilder report, string title, IEnumerable<(string Name, string Value)> fields)
        {
            var entries = fields.ToArray();
            if (entries.Length == 0)
                return;

            report.AppendLine();
            report.AppendLine(title + ":");
            foreach (var entry in entries)
                report.AppendLine("  " + entry.Name + " = " + entry.Value);
        }

        /// <summary>Validate info-string delimiters while allowing ordinary punctuation and whitespace.</summary>
        static void ValidateField(string fieldName, string fieldValue)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || fieldName.IndexOfAny(new[] { '\\', '\0', '\r', '\n' }) >= 0)
                throw new InvalidDataException(EnglishText.Text("Weapon field name contains invalid characters: ") + fieldName);
            if (fieldValue == null || fieldValue.IndexOfAny(new[] { '\\', '\0' }) >= 0)
                throw new InvalidDataException(EnglishText.Text("Weapon field value contains a backslash or NUL: ") + fieldName);
        }

        /// <summary>Join module rows using the newline representation used by OAT's WEAPONFILE dump.</summary>
        static string SerializeModules(IReadOnlyList<string> moduleNames, string fieldName)
        {
            var cleanedNames = new List<string>();
            foreach (string moduleName in moduleNames)
            {
                string cleanedName = moduleName.Trim();
                if (cleanedName.Length == 0)
                    continue;
                if (cleanedName.IndexOfAny(new[] { '\\', '\0', '\r', '\n' }) >= 0)
                    throw new InvalidDataException(EnglishText.Text("Attachment name contains invalid characters: ") + fieldName);
                cleanedNames.Add(cleanedName);
            }
            return string.Join("\n", cleanedNames);
        }

        /// <summary>Identify the two newline-separated attachment-list fields.</summary>
        static bool IsAttachmentField(string fieldName) =>
            fieldName.Equals(AttachmentField, StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals(UniqueAttachmentField, StringComparison.OrdinalIgnoreCase);

        /// <summary>Identify T6 view, world and hand model fields.</summary>
        static bool IsModelField(string fieldName) =>
            fieldName.Equals("worldModel", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("handModel", StringComparison.OrdinalIgnoreCase)
            || fieldName.StartsWith("gunModel", StringComparison.OrdinalIgnoreCase)
            || fieldName.StartsWith("worldModel", StringComparison.OrdinalIgnoreCase);

        /// <summary>Prefer first-person gun geometry, then world/hand model links.</summary>
        static int ModelFieldPriority(string fieldName)
        {
            if (fieldName.Equals("gunModel", StringComparison.OrdinalIgnoreCase)) return 0;
            if (fieldName.StartsWith("gunModel", StringComparison.OrdinalIgnoreCase)) return 1;
            if (fieldName.StartsWith("worldModel", StringComparison.OrdinalIgnoreCase)) return 2;
            if (fieldName.Equals("handModel", StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }

        /// <summary>Detect regular and special-case XAnim field names in T6 weapon definitions.</summary>
        static bool IsAnimationField(string fieldName) =>
            fieldName.IndexOf("Anim", StringComparison.OrdinalIgnoreCase) >= 0
            || fieldName.StartsWith("dtp_", StringComparison.OrdinalIgnoreCase)
            || fieldName.Equals("slide_in", StringComparison.OrdinalIgnoreCase);
    }
}
