using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace T6_Workbench
{
    /// <summary>Remembers the last folder used by each file or folder picker.</summary>
    internal static class DialogPathMemory
    {
        static readonly string storagePath = Path.Combine(AppContext.BaseDirectory, "workbench.dialog-paths.json");

        static readonly Dictionary<string, string> directories = Load();

        internal static bool? ShowDialog(FileDialog dialog, Window owner, string key)
        {
            dialog.InitialDirectory = InitialDirectory(key);
            bool? accepted = dialog.ShowDialog(owner);
            if (accepted == true)
            {
                RememberFile(key, dialog.FileName);
            }
            return accepted;
        }

        internal static bool? ShowDialog(OpenFolderDialog dialog, Window owner, string key)
        {
            dialog.InitialDirectory = InitialDirectory(key);
            bool? accepted = dialog.ShowDialog(owner);
            if (accepted == true)
            {
                RememberFolder(key, dialog.FolderName);
            }
            return accepted;
        }

        static string InitialDirectory(string key)
        {
            if (directories.TryGetValue(key, out string previous) && Directory.Exists(previous))
                return previous;
            return AppContext.BaseDirectory;
        }

        static void RememberFile(string key, string fileName)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(fileName));
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    RememberFolder(key, folder);
                }
            }
            catch (Exception ex) when (((ex is System.ArgumentException) || (ex is System.IO.IOException)) || (ex is System.NotSupportedException))
            {
            }
        }

        static void RememberFolder(string key, string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                directories[key] = Path.GetFullPath(folder);
                string temporary = storagePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(directories), new UTF8Encoding(false));
                if (File.Exists(storagePath))
                {
                    try
                    {
                        File.Replace(temporary, storagePath, null);
                    }
                    catch (IOException)
                    {
                        File.Copy(temporary, storagePath, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, storagePath);
                }
            }
            catch (Exception ex) when ((((ex is System.ArgumentException) || (ex is System.IO.IOException)) || (ex is System.NotSupportedException)) || (ex is System.UnauthorizedAccessException))
            {
            }
        }

        static Dictionary<string, string> Load()
        {
            try
            {
                return File.Exists(storagePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(storagePath)) ?? new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when ((((ex is System.IO.IOException) || (ex is System.Text.Json.JsonException)) || (ex is System.NotSupportedException)) || (ex is System.UnauthorizedAccessException))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
