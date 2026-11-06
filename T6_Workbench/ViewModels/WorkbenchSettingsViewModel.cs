using AMUI;
using System.Linq;

namespace T6_Workbench
{
    public class WorkbenchSettingsViewModel : BaseViewModel
    {
        string oatToolsFolder = "";

        string redactedTemplateFile = "";

        string blenderExecutable = "";

        string captureFile = "";

        string gameFolder = "";

        string outputFolder = "";

        string textureOutputFolder = "";

        public string OatToolsFolder { get => oatToolsFolder; set => SetProperty(ref oatToolsFolder, value ?? ""); }

        public string RedactedTemplateFile { get => redactedTemplateFile; set => SetProperty(ref redactedTemplateFile, value ?? ""); }

        public string BlenderExecutable { get => blenderExecutable; set => SetProperty(ref blenderExecutable, value ?? ""); }

        public string CaptureFile { get => captureFile; set => SetProperty(ref captureFile, value ?? ""); }

        public string GameFolder { get => gameFolder; set => SetProperty(ref gameFolder, value ?? ""); }

        public string OutputFolder { get => outputFolder; set => SetProperty(ref outputFolder, value ?? ""); }

        public string TextureOutputFolder { get => textureOutputFolder; set => SetProperty(ref textureOutputFolder, value ?? ""); }

        internal bool HasAnyValue => new[] { OatToolsFolder, RedactedTemplateFile, BlenderExecutable, CaptureFile, GameFolder, OutputFolder, TextureOutputFolder }.Any(value => !string.IsNullOrWhiteSpace(value));

        internal void CopyFrom(WorkbenchSettingsViewModel other)
        {
            OatToolsFolder = other.OatToolsFolder;
            RedactedTemplateFile = other.RedactedTemplateFile;
            BlenderExecutable = other.BlenderExecutable;
            CaptureFile = other.CaptureFile;
            GameFolder = other.GameFolder;
            OutputFolder = other.OutputFolder;
            TextureOutputFolder = other.TextureOutputFolder;
        }

        internal WorkbenchSettingsViewModel Clone()
        {
            var clone = new WorkbenchSettingsViewModel();
            clone.CopyFrom(this);
            return clone;
        }
    }
}
