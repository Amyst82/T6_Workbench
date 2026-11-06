using AMUI;
using System;
using System.IO;
using System.Text.Json.Serialization;

namespace T6_Workbench
{
    public class ProjectSessionViewModel : BaseViewModel
    {
        string name = "";

        string projectPath = "";

        string manifestPath = "";

        DateTime lastOpenedAt;

        string lastActiveAssetType = "";

        string lastActiveAssetName = "";

        int selectedLodIndex;

        int projectChangeCount = 0;

        bool previewUsesShaders = true;

        WorkbenchSettingsViewModel settings = new WorkbenchSettingsViewModel();

        PreviewCameraState previewCamera = new PreviewCameraState();

        public ProjectSessionViewModel()
        {
        }

        public string Name 
        { 
            get => name; 
            set => SetProperty(ref name, value ?? ""); 
        }

        public string ProjectPath 
        { 
            get => projectPath; 
            set => SetProperty(ref projectPath, value ?? ""); 
        }

        public string ManifestPath 
        { 
            get => manifestPath; 
            set
            {
                if (SetProperty(ref manifestPath, value ?? ""))
                {
                    NotifyProperty(nameof(IsAvailable));
                }
            }
        }

        public DateTime LastOpenedAt 
        { 
            get => lastOpenedAt; 
            set => SetProperty(ref lastOpenedAt, value); 
        }

        public string LastActiveAssetType 
        { 
            get => lastActiveAssetType; 
            set => SetProperty(ref lastActiveAssetType, value ?? ""); 
        }

        public string LastActiveAssetName 
        { 
            get => lastActiveAssetName; 
            set => SetProperty(ref lastActiveAssetName, value ?? ""); 
        }

        public int SelectedLodIndex 
        { 
            get => selectedLodIndex; 
            set => SetProperty(ref selectedLodIndex, Math.Max(0, value)); 
        }

        public int ProjectChangeCount 
        { 
            get => projectChangeCount; 
            set => SetProperty(ref projectChangeCount, Math.Max(0, value)); 
        }

        public bool PreviewUsesShaders
        {
            get => previewUsesShaders;
            set
            {
                if (!SetProperty(ref previewUsesShaders, value)) 
                    return;
                NotifyProperty(nameof(PreviewShadingIndex));
            }
        }

        public WorkbenchSettingsViewModel Settings 
        { 
            get => settings; 
            set => SetProperty(ref settings, value ?? new WorkbenchSettingsViewModel()); 
        }

        public PreviewCameraState PreviewCamera 
        { 
            get => previewCamera; 
            set => SetProperty(ref previewCamera, value ?? new PreviewCameraState());
        }

        [JsonIgnore]
        public int PreviewShadingIndex
        {
            get => PreviewUsesShaders ? 1 : 0;
            set => PreviewUsesShaders = value != 0;
        }

        [JsonIgnore]
        public bool IsAvailable => File.Exists(ManifestPath);
    }
}
