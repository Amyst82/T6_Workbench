using Microsoft.Win32;
using System.Windows;

namespace T6_Workbench
{
    /// <summary>
    /// Selects paths for settings while retaining each dialog's last directory.
    /// </summary>
    internal static class SettingsPathPicker
    {
        internal static string Select(string setting, Window owner)
        {
            switch (setting)
            {
                case nameof(WorkbenchSettingsViewModel.CaptureFile):
                case nameof(WorkbenchSettingsViewModel.RedactedTemplateFile):
                case nameof(WorkbenchSettingsViewModel.BlenderExecutable):
                    var file = new OpenFileDialog { Title = "Select file", Filter = "All files (*.*)|*.*", CheckFileExists = true };
                    return DialogPathMemory.ShowDialog(file, owner, "settings/" + setting) == true ? file.FileName : null;
                default:
                    var folder = new OpenFolderDialog { Title = "Select folder" };
                    return DialogPathMemory.ShowDialog(folder, owner, "settings/" + setting) == true ? folder.FolderName : null;
            }
        }
    }
}
