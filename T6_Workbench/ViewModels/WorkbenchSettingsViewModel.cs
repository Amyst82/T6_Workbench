using AMUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace T6_Workbench
{
    public class WorkbenchSettingsViewModel : BaseViewModel
    {
        string oatToolsFolder = "";

        string outputFolder = "";

        string captureFile = "";

        string redactedTemplateFile = "";

        string gameFolder = "";

        string gameFastfile = "";

        string browserFastfile = "";

        string includeAssets = "";

        string zoneFile = "";

        string assetsFolder = "";

        string basesFolder = "";

        string dumpFolder = "";

        string editsFolder = "";

        string runFolder = "";

        string textureOutputFolder = "";

        string blenderExecutable = "";

        public WorkbenchSettingsViewModel()
        {
        }

        public string OatToolsFolder { get => oatToolsFolder; set => SetProperty(ref oatToolsFolder, value ?? ""); }

        public string OutputFolder { get => outputFolder; set => SetProperty(ref outputFolder, value ?? ""); }

        public string CaptureFile { get => captureFile; set => SetProperty(ref captureFile, value ?? ""); }

        public string RedactedTemplateFile { get => redactedTemplateFile; set => SetProperty(ref redactedTemplateFile, value ?? ""); }

        public string GameFolder { get => gameFolder; set => SetProperty(ref gameFolder, value ?? ""); }

        public string GameFastfile { get => gameFastfile; set => SetProperty(ref gameFastfile, value ?? ""); }

        public string BrowserFastfile { get => browserFastfile; set => SetProperty(ref browserFastfile, value ?? ""); }

        public string IncludeAssets { get => includeAssets; set => SetProperty(ref includeAssets, value ?? ""); }

        public string ZoneFile { get => zoneFile; set => SetProperty(ref zoneFile, value ?? ""); }

        public string AssetsFolder { get => assetsFolder; set => SetProperty(ref assetsFolder, value ?? ""); }

        public string BasesFolder { get => basesFolder; set => SetProperty(ref basesFolder, value ?? ""); }

        public string DumpFolder { get => dumpFolder; set => SetProperty(ref dumpFolder, value ?? ""); }

        public string EditsFolder { get => editsFolder; set => SetProperty(ref editsFolder, value ?? ""); }

        public string RunFolder { get => runFolder; set => SetProperty(ref runFolder, value ?? ""); }

        public string TextureOutputFolder { get => textureOutputFolder; set => SetProperty(ref textureOutputFolder, value ?? ""); }

        public string BlenderExecutable { get => blenderExecutable; set => SetProperty(ref blenderExecutable, value ?? ""); }

        [JsonIgnore]
        internal bool HasAnyValue => ToStorageValues().Values.Any(value => !string.IsNullOrWhiteSpace(value));

        internal void SetFromStorage(string key, string value)
        {
            switch (key)
            {
                case "BinBox": OatToolsFolder = value; break;
                case "OutputBox": OutputFolder = value; break;
                case "CaptureBox": CaptureFile = value; break;
                case "TemplateBox": RedactedTemplateFile = value; break;
                case "GameBox": GameFolder = value; break;
                case "FfBox": GameFastfile = value; break;
                case "BrowserFfBox": BrowserFastfile = value; break;
                case "IncludeBox": IncludeAssets = value; break;
                case "ZoneBox": ZoneFile = value; break;
                case "AssetsBox": AssetsFolder = value; break;
                case "BasesBox": BasesFolder = value; break;
                case "DumpBox": DumpFolder = value; break;
                case "EditBox": EditsFolder = value; break;
                case "RunBox": RunFolder = value; break;
                case "TextureOutputPathBox": TextureOutputFolder = value; break;
                case "BlenderBox": BlenderExecutable = value; break;
            }
        }

        internal Dictionary<string, string> ToStorageValues() => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BinBox"] = OatToolsFolder,
            ["OutputBox"] = OutputFolder,
            ["CaptureBox"] = CaptureFile,
            ["TemplateBox"] = RedactedTemplateFile,
            ["GameBox"] = GameFolder,
            ["FfBox"] = GameFastfile,
            ["BrowserFfBox"] = BrowserFastfile,
            ["IncludeBox"] = IncludeAssets,
            ["ZoneBox"] = ZoneFile,
            ["AssetsBox"] = AssetsFolder,
            ["BasesBox"] = BasesFolder,
            ["DumpBox"] = DumpFolder,
            ["EditBox"] = EditsFolder,
            ["RunBox"] = RunFolder,
            ["TextureOutputPathBox"] = TextureOutputFolder,
            ["BlenderBox"] = BlenderExecutable
        };

        internal void CopyFrom(WorkbenchSettingsViewModel other)
        {
            OatToolsFolder = other.OatToolsFolder;
            OutputFolder = other.OutputFolder;
            CaptureFile = other.CaptureFile;
            RedactedTemplateFile = other.RedactedTemplateFile;
            GameFolder = other.GameFolder;
            GameFastfile = other.GameFastfile;
            BrowserFastfile = other.BrowserFastfile;
            IncludeAssets = other.IncludeAssets;
            ZoneFile = other.ZoneFile;
            AssetsFolder = other.AssetsFolder;
            BasesFolder = other.BasesFolder;
            DumpFolder = other.DumpFolder;
            EditsFolder = other.EditsFolder;
            RunFolder = other.RunFolder;
            TextureOutputFolder = other.TextureOutputFolder;
            BlenderExecutable = other.BlenderExecutable;
        }

        internal WorkbenchSettingsViewModel Clone()
        {
            var clone = new WorkbenchSettingsViewModel();
            clone.CopyFrom(this);
            return clone;
        }
    }
}
