using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace T6_Workbench
{
    // Creates a new weapon and merges its menu registration with the active project table.
    internal static class WeaponCreationModule
    {
        internal sealed class Request
        {
            internal string Id = "33", Name = "", Display = "", Description = "", View = "", World = "", Template = "", Table = "", Localization = "", TemplateRow = "", Icon = "", AnimationMappings = "";
        }

        internal static string Create(WorkbenchProject project, Request request)
        {
            string name = request.Name.Trim().ToLowerInvariant();
            if (name.Length == 0 || name.Any(c => !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) || name.EndsWith("_mp", StringComparison.Ordinal))
                throw new InvalidDataException(EnglishText.Text("Weapon names must contain Latin letters, digits and _, without the suffix _mp."));
            if (string.IsNullOrWhiteSpace(request.Display) || request.Display.IndexOfAny(new[] { '\r', '\n', '"', '\\' }) >= 0 || request.Description.IndexOfAny(new[] { '\r', '\n', '"', '\\' }) >= 0)
                throw new InvalidDataException(EnglishText.Text("Names and descriptions must not contain quotation marks, backslashes or line breaks."));
            foreach (var model in new[] { request.View, request.World })
            {
                if (string.IsNullOrWhiteSpace(model) || !File.Exists(project.EditPath("xmodel/" + model + ".json")) && !File.Exists(project.AssetPath("xmodel/" + model + ".json")))
                    throw new FileNotFoundException(EnglishText.Text("XModel is missing from the project: ") + model);
            }
            if (!File.Exists(request.Template))
                throw new FileNotFoundException(EnglishText.Text("A T6 weapon template file is required."), request.Template);
            string weaponName = name + "_mp", target = project.EditPath("weapons/" + weaponName);
            if (File.Exists(target) || File.Exists(project.AssetPath("weapons/" + weaponName)))
                throw new IOException(EnglishText.Text("This weapon already exists: ") + weaponName);
            var parts = File.ReadAllText(request.Template).TrimEnd('\0', '\r', '\n').Split('\\');
            if (parts.Length < 3 || parts[0] != "WEAPONFILE" || parts.Length % 2 != 1)
                throw new InvalidDataException(EnglishText.Text("The template is not a valid T6 WEAPONFILE."));
            var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < parts.Length; i += 2)
            {
                if (lookup.ContainsKey(parts[i]))
                    throw new InvalidDataException(EnglishText.Text("Duplicate weapon field: ") + parts[i]);
                lookup.Add(parts[i], i + 1);
            }
            void Set(string key, string value)
            {
                if (!lookup.TryGetValue(key, out int i))
                    throw new InvalidDataException(EnglishText.Text("Weapon template is missing field ") + key);
                if (value.Contains('\\'))
                    throw new InvalidDataException(EnglishText.Text("Invalid field value ") + key);
                parts[i] = value;
            }
            string token = "WEAPON_" + name.ToUpperInvariant();
            Set("displayName", token);
            Set("gunModel", request.View);
            Set("worldModel", request.World);
            Set("parentWeaponName", name);
            foreach (var line in request.AnimationMappings.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0 || equals == line.Length - 1)
                    throw new InvalidDataException(EnglishText.Text("Each animation line must have the format slot=name_xanim."));
                string slot = line.Substring(0, equals).Trim(), anim = line.Substring(equals + 1).Trim();
                if (!slot.EndsWith("Anim", StringComparison.OrdinalIgnoreCase) && slot.IndexOf("Anim", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException(EnglishText.Text("Does not look like an animation slot: ") + slot);
                if (!File.Exists(project.EditPath("xanim/" + anim)) && !File.Exists(project.AssetPath("xanim/" + anim)))
                    throw new FileNotFoundException(EnglishText.Text("XAnim is missing from the project: ") + anim);
                Set(slot, anim);
            }
            string tableTarget = project.EditPath("mp/statstable.csv");
            string tableSource = File.Exists(tableTarget) ? tableTarget : request.Table;
            if (!File.Exists(tableSource))
                throw new FileNotFoundException(EnglishText.Text("Select the current mp/statstable.csv."), tableSource);
            var lines = File.ReadAllLines(tableSource).ToList();
            if (lines.Count < 2)
                throw new InvalidDataException(EnglishText.Text("Weapon table is empty."));
            var rows = lines.Select(ParseCsv).ToArray();
            if (rows.Any(r => r.Count > 4 && r[4].Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(EnglishText.Text("Weapon is already registered: ") + name);
            var templateRows = rows.Where(r => r.Count > 7 && r[4].Equals(request.TemplateRow, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (templateRows.Length != 1)
                throw new InvalidDataException(EnglishText.Text("Could not find a unique template row in statstable: ") + request.TemplateRow);
            var ids = new HashSet<int>();
            foreach (var row in rows.Skip(1))
            {
                if (row.Count > 0 && int.TryParse(row[0], out int n) && !ids.Add(n))
                    throw new InvalidDataException(EnglishText.Text("Duplicate entry in table ID ") + n);
            }
            if (!int.TryParse(request.Id, out int chosenId) || chosenId < 1)
                throw new InvalidDataException(EnglishText.Text("Specify a positive weapon ID."));
            int index = -1;
            for (int i = 1; i < rows.Length; i++)
            {
                if (rows[i].Count > 7 && int.TryParse(rows[i][0], out int n) && n == chosenId && rows[i].Skip(1).All(string.IsNullOrEmpty))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
                throw new InvalidDataException(EnglishText.Text("The selected ID is missing or occupied in statstable. Specify the ID of an existing empty row."));
            var rowNew = new List<string>(templateRows[0]);
            rowNew[0] = rows[index][0];
            rowNew[3] = token;
            rowNew[4] = name;
            rowNew[7] = token + "_DESC";
            if (request.TemplateRow.Equals("knife_held", StringComparison.OrdinalIgnoreCase))
            {
                rowNew[9] = "2";
                rowNew[10] = "48";
                rowNew[12] = "1";
                rowNew[13] = "secondary";
            }
            if (string.IsNullOrWhiteSpace(request.Icon))
                throw new InvalidDataException(EnglishText.Text("Specify the name of an icon material prepared in the project."));
            string icon = request.Icon.Trim().Replace('\\', '/');
            if (!File.Exists(project.EditPath("materials/" + icon + ".json")) && !File.Exists(project.AssetPath("materials/" + icon + ".json")))
                throw new FileNotFoundException(EnglishText.Text("Icon material not found in the project: ") + icon);
            rowNew[6] = icon;
            foreach (string key in new[] { "hudIcon", "killIcon", "ammoCounterIcon" })
            {
                Set(key, rowNew[6]);
            }
            lines[index] = string.Join(",", rowNew.Select(Csv));
            string locTarget = project.EditPath("english/localizedstrings/patch_redacted.str");
            string locSource = File.Exists(locTarget) ? locTarget : request.Localization;
            if (!File.Exists(locSource))
                throw new FileNotFoundException(EnglishText.Text("Select the current patch_redacted.str."), locSource);
            string loc = File.ReadAllText(locSource);
            if (loc.IndexOf("REFERENCE " + token, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException(EnglishText.Text("Localization key already exists: ") + token);
            int marker = loc.LastIndexOf("ENDMARKER", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || loc.Substring(marker + 9).Trim().Length != 0)
                throw new InvalidDataException(EnglishText.Text("Localization file is missing the final ENDMARKER."));
            string addition = "REFERENCE " + token + "\nLANG_ENGLISH \"" + request.Display.Trim() + "\"\n\nREFERENCE " + token + "_DESC\nLANG_ENGLISH \"" + request.Description.Trim() + "\"\n\n";
            string locResult = loc.Insert(marker, addition);
            // Keep both existing edits recoverable before updating shared registration files.
            foreach (string file in new[] { tableTarget, locTarget })
            {
                if (File.Exists(file))
                {
                    string backup = Path.Combine(project.Builds, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Path.GetFileName(file));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(file, backup);
                }
            }
            foreach (string file in new[] { target, tableTarget, locTarget })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
            }
            File.WriteAllText(target, string.Join("\\", parts), new UTF8Encoding(false));
            File.WriteAllLines(tableTarget, lines, new UTF8Encoding(false));
            File.WriteAllText(locTarget, locResult, new UTF8Encoding(false));
            return weaponName + " · ID " + rowNew[0];
        }

        static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

        static List<string> ParseCsv(string line)
        {
            var fields = new List<string>();
            var value = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (c == ',' && !quoted)
                {
                    fields.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(c);
                }
            }
            if (quoted)
                throw new InvalidDataException(EnglishText.Text("Invalid CSV line (unterminated quote)."));
            fields.Add(value.ToString());
            return fields;
        }
    }
}
