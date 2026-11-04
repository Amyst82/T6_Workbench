using AMUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace T6_Workbench
{
    public static class MainViewModel
    {
        public static WorkbenchViewModel WorkbenchViewModel = new WorkbenchViewModel();
    }
    public class WorkbenchViewModel : BaseViewModel
    {
        WorkbenchSettingsViewModel settings = new WorkbenchSettingsViewModel();
        ProjectSessionViewModel currentProject;
        WorkbenchOperationStatus status = new WorkbenchOperationStatus(WorkbenchOperationState.Ready, "Ready");
        bool canOpenResult;
        int selectedLodIndex;
        bool previewUsesShaders = true;
        PreviewCameraState previewCamera = new PreviewCameraState();
        OperationResult lastResult;
        Exception lastError;

        public ObservableCollection<ProjectSessionViewModel> RecentProjects { get; } = new ObservableCollection<ProjectSessionViewModel>();
        public ObservableCollection<ModelLodOptionViewModel> AvailableLods { get; } = new ObservableCollection<ModelLodOptionViewModel>();
        public WorkbenchSettingsViewModel Settings
        {
            get => settings;
            private set => SetProperty(ref settings, value);
        }
        public ProjectSessionViewModel CurrentProject
        {
            get => currentProject;
            private set => SetProperty(ref currentProject, value);
        }
        public WorkbenchOperationStatus Status
        {
            get => status;
            private set => SetProperty(ref status, value);
        }
        public bool CanOpenResult
        {
            get => canOpenResult;
            set => SetProperty(ref canOpenResult, value);
        }
        public int SelectedLodIndex
        {
            get => selectedLodIndex;
            set
            {
                int normalized = Math.Max(0, value);
                if (!SetProperty(ref selectedLodIndex, normalized)) return;
                if (CurrentProject != null) CurrentProject.SelectedLodIndex = normalized;
            }
        }
        public bool PreviewUsesShaders
        {
            get => previewUsesShaders;
            set
            {
                if (!SetProperty(ref previewUsesShaders, value)) 
                    return;
                NotifyProperty(nameof(PreviewShadingIndex));
                if (CurrentProject != null) 
                    CurrentProject.PreviewUsesShaders = value;
            }
        }
        public PreviewCameraState PreviewCamera
        {
            get => previewCamera;
            private set => SetProperty(ref previewCamera, value);
        }
        public int PreviewShadingIndex
        {
            get => PreviewUsesShaders ? 1 : 0;
            set => PreviewUsesShaders = value != 0;
        }
        internal OperationResult LastResult
        {
            get => lastResult;
            private set => SetProperty(ref lastResult, value);
        }
        internal Exception LastError
        {
            get => lastError;
            private set => SetProperty(ref lastError, value);
        }

        public ICommand OpenRecentProjectCommand { get; }
        internal event Action<ProjectSessionViewModel> OpenProjectRequested;

        internal WorkbenchViewModel()
        {
            OpenRecentProjectCommand = new RelayCommand(parameter =>
            {
                if (parameter is ProjectSessionViewModel project && project.IsAvailable)
                    OpenProjectRequested?.Invoke(project);
            });
        }

        internal void LoadRecentProjects(IEnumerable<ProjectSessionViewModel> projects)
        {
            RecentProjects.Clear();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectSessionViewModel project in projects)
            {
                if (string.IsNullOrWhiteSpace(project.ManifestPath)) 
                    continue;
                string manifest;
                try 
                { 
                    manifest = Path.GetFullPath(project.ManifestPath); 
                }
                catch 
                { 
                    continue; 
                }
                if (!paths.Add(manifest)) 
                    continue;
                project.ManifestPath = manifest;
                project.ProjectPath = Path.GetDirectoryName(manifest) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(project.Name))
                    project.Name = Path.GetFileName(project.ProjectPath);
                if (project.Settings == null) 
                    project.Settings = new WorkbenchSettingsViewModel();
                project.OpenCommand = OpenRecentProjectCommand;
                RecentProjects.Add(project);
                if (RecentProjects.Count == 10) 
                    break;
            }
        }

        internal ProjectSessionViewModel ActivateProject(WorkbenchProject project)
        {
            string manifest = Path.GetFullPath(project.Manifest);
            ProjectSessionViewModel session = RecentProjects.FirstOrDefault(item => item.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                session = new ProjectSessionViewModel
                {
                    Settings = Settings.Clone(),
                    OpenCommand = OpenRecentProjectCommand
                };
                RecentProjects.Insert(0, session);
            }
            else if (!session.Settings.HasAnyValue)
            {
                session.Settings.CopyFrom(Settings);
            }
            session.OpenCommand = OpenRecentProjectCommand;

            session.Name = project.Name;
            session.ProjectPath = project.Root;
            session.ManifestPath = manifest;
            session.LastOpenedAt = DateTime.Now;
            if (RecentProjects.IndexOf(session) > 0)
                RecentProjects.Move(RecentProjects.IndexOf(session), 0);
            while (RecentProjects.Count > 10)
                RecentProjects.RemoveAt(RecentProjects.Count - 1);

            CurrentProject = session;
            Settings = session.Settings;
            PreviewCameraState camera = (session.PreviewCamera = session.PreviewCamera ?? new PreviewCameraState());
            PreviewCamera = camera;
            SelectedLodIndex = session.SelectedLodIndex;
            PreviewUsesShaders = session.PreviewUsesShaders;
            return session;
        }

        internal void SetStatus(WorkbenchOperationState state, string message)
        {
            Status = new WorkbenchOperationStatus(state, message);
            if (state == WorkbenchOperationState.Built || state == WorkbenchOperationState.BuiltWithWarnings)
                CanOpenResult = true;
            else if (state == WorkbenchOperationState.Busy || state == WorkbenchOperationState.Building)
                CanOpenResult = false;
        }

        internal void BeginOperation(WorkbenchOperationState state, string message)
        {
            LastResult = null;
            LastError = null;
            CanOpenResult = false;
            SetStatus(state, message);
        }

        internal void CompleteOperation(OperationResult result, string message, bool built)
        {
            LastResult = result;
            SetStatus(built ? result.Warnings.Length > 0 ? WorkbenchOperationState.BuiltWithWarnings : WorkbenchOperationState.Built : WorkbenchOperationState.Ready, message);
            CanOpenResult = true;
        }

        internal void SetError(Exception error, string message)
        {
            LastError = error;
            SetStatus(WorkbenchOperationState.Error, message);
        }

        internal void ChangeProjectChangeCount(int delta)
        {
            if (CurrentProject != null)
                CurrentProject.ProjectChangeCount = Math.Max(0, CurrentProject.ProjectChangeCount + delta);
        }

        internal void RememberPreview(string assetType, string assetName, int lodIndex, bool usesShaders)
        {
            SelectedLodIndex = Math.Max(0, lodIndex);
            PreviewUsesShaders = usesShaders;
            if (CurrentProject != null && !string.IsNullOrWhiteSpace(assetType) && !string.IsNullOrWhiteSpace(assetName))
            {
                CurrentProject.LastActiveAssetType = assetType;
                CurrentProject.LastActiveAssetName = assetName;
            }
        }
    }
}
