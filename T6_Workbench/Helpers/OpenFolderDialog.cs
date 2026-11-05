using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;

namespace T6_Workbench
{
    /// <summary>
    /// Folder picker adapter for the WPF dialog API absent from .NET Framework.
    /// </summary>
    internal sealed class OpenFolderDialog
    {
        public string Title { get; set; } = "Select folder";

        public string InitialDirectory { get; set; } = "";

        public string FolderName { get; private set; } = "";

        /// <summary>Wrap the framework folder dialog and keep ownership with the calling WPF window.</summary>
        public bool? ShowDialog(Window owner)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = Title,
                SelectedPath = InitialDirectory,
                ShowNewFolderButton = true
            })
            {
                var result = dialog.ShowDialog(new WindowOwner(new WindowInteropHelper(owner).Handle));
                if (result != System.Windows.Forms.DialogResult.OK)
                    return false;
                FolderName = dialog.SelectedPath;
                return true;
            }
        }
        /// <summary>
        /// WindowOwner. Folder picker adapter owned by the WPF window.
        /// </summary>
        private sealed class WindowOwner : System.Windows.Forms.IWin32Window
        {
            private readonly IntPtr _handle;

            public WindowOwner(IntPtr handle)
            {
                _handle = handle;
            }

            public IntPtr Handle => _handle;
        }
    }
}
