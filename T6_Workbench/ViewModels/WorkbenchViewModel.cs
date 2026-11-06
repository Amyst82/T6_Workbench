using AMUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace T6_Workbench
{
    public static class MainViewModel
    {
        public static WorkbenchViewModel WorkbenchViewModel = new WorkbenchViewModel();
        
    }

    public class WorkbenchViewModel : BaseViewModel
    {
        readonly WorkbenchStateStore stateStore = new WorkbenchStateStore(Path.Combine(AppContext.BaseDirectory, "workbench.state.json"));

        WorkbenchSettingsViewModel globalSettings;

        bool stateInitialized;

        bool restoringState;

        bool stateLoadFailed;

        string stateStorageError = "";

        string lastProjectManifest = "";

        private UIColors _uiColors = new UIColors();

        WorkbenchSettingsViewModel settings = new WorkbenchSettingsViewModel();

        ProjectSessionViewModel currentProject;

        WorkbenchOperationStatus status = new WorkbenchOperationStatus(WorkbenchOperationState.Ready, "Ready");

        bool canOpenResult;

        int selectedLodIndex;

        bool previewUsesShaders = true;

        PreviewCameraState previewCamera = new PreviewCameraState();

        OperationResult lastResult;

        Exception lastError;

        public WorkbenchSession Session { get; } = new WorkbenchSession();

        public ObservableCollection<ProjectSessionViewModel> RecentProjects { get; } = new ObservableCollection<ProjectSessionViewModel>();

        public ObservableCollection<ModelLodOptionViewModel> AvailableLods { get; } = new ObservableCollection<ModelLodOptionViewModel>();

        public UIColors UIColors
        {
            get => _uiColors;
            private set => SetProperty(ref _uiColors, value);
        }

        public WorkbenchSettingsViewModel Settings
        {
            get => settings;
            private set
            {
                if (ReferenceEquals(settings, value))
                    return;
                settings.PropertyChanged -= SettingsChanged;
                SetProperty(ref settings, value);
                settings.PropertyChanged += SettingsChanged;
            }
        }

        public ProjectSessionViewModel CurrentProject
        {
            get => currentProject;
            private set
            {
                if (ReferenceEquals(currentProject, value))
                    return;
                if (currentProject != null)
                {
                    currentProject.PropertyChanged -= ProjectChanged;
                }
                SetProperty(ref currentProject, value);
                if (currentProject != null)
                {
                    currentProject.PropertyChanged += ProjectChanged;
                }
            }
        }

        public WorkbenchOperationStatus Status
        {
            get => status;
            private set
            {
                if (!SetProperty(ref status, value))
                    return;
                NotifyProperty(nameof(CanOpenProject));
            }
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
                if (!SetProperty(ref selectedLodIndex, normalized))
                    return;
                if (CurrentProject != null)
                {
                    CurrentProject.SelectedLodIndex = normalized;
                }
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
                {
                    CurrentProject.PreviewUsesShaders = value;
                }
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

        public OperationResult LastResult
        {
            get => lastResult;
            private set => SetProperty(ref lastResult, value);
        }

        public Exception LastError
        {
            get => lastError;
            private set => SetProperty(ref lastError, value);
        }

        public string StateStorageError
        {
            get => stateStorageError;
            private set => SetProperty(ref stateStorageError, value);
        }

        public bool CanOpenProject => !Status.IsRunning;

        internal string LastProjectManifest => lastProjectManifest;

        internal void RemoveRecentProject(ProjectSessionViewModel project)
        {
            if (project != null && project != CurrentProject)
            {
                RecentProjects.Remove(project);
            }
        }

        internal void ShowRecentProjectFolder(ProjectSessionViewModel project)
        {
            if (project == null || !Directory.Exists(project.ProjectPath))
                return;
            try
            {
                Process.Start(new ProcessStartInfo(project.ProjectPath) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                StateStorageError = "Could not open the project folder: " + ex.Message;
            }
        }

        internal async Task OpenProjectAsync(string manifestPath)
        {
            if (Status.IsRunning)
                return;
            SaveState();
            SetStatus(WorkbenchOperationState.Busy, "Opening project...");
            try
            {
                await Session.OpenAsync(manifestPath);
                ActivateProject(Session.Project);
                AvailableLods.Clear();
                LastResult = null;
                LastError = null;
                SetStatus(WorkbenchOperationState.Ready, "Project opened: " + CurrentProject.Name);
            }
            catch (Exception ex)
            {
                SetError(ex, "Could not open project: " + ex.Message);
                MessageBox.Show(Application.Current.MainWindow, Status.Message, "Open project", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        internal void InitializeState()
        {
            if (stateInitialized)
                return;
            restoringState = true;
            globalSettings = new WorkbenchSettingsViewModel
            {
                OatToolsFolder = Path.Combine(AppContext.BaseDirectory, "OAT"),
                CaptureFile = Path.Combine(AppContext.BaseDirectory, "captures", "patch_redacted.bin"),
                OutputFolder = Path.Combine(AppContext.BaseDirectory, "workspace"),
                TextureOutputFolder = Path.Combine(AppContext.BaseDirectory, "workspace", "textures")
            };
            try
            {
                WorkbenchState state = stateStore.Load();
                if (state != null)
                {
                    globalSettings = state.Settings ?? globalSettings;
                    lastProjectManifest = state.LastProjectManifest ?? "";
                    LoadRecentProjects(state.RecentProjects ?? new List<ProjectSessionViewModel>());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException || ex is NotSupportedException)
            {
                stateLoadFailed = true;
                StateStorageError = "Could not load settings: " + ex.Message;
            }
            Settings = globalSettings;
            restoringState = false;
            stateInitialized = true;
            RecentProjects.CollectionChanged += RecentProjectsChanged;
        }

        void SettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (restoringState)
                return;
            stateLoadFailed = false;
            SaveState();
        }

        void RecentProjectsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (restoringState)
                return;
            stateLoadFailed = false;
            SaveState();
        }

        void ProjectChanged(object sender, PropertyChangedEventArgs e)
        {
            if (restoringState)
                return;
            restoringState = true;
            try
            {
                Settings = CurrentProject.Settings;
                PreviewCamera = CurrentProject.PreviewCamera;
                SelectedLodIndex = CurrentProject.SelectedLodIndex;
                PreviewUsesShaders = CurrentProject.PreviewUsesShaders;
            }
            finally
            {
                restoringState = false;
            }
            SaveState();
        }

        internal void SaveState()
        {
            if (!stateInitialized || restoringState || stateLoadFailed)
                return;
            try
            {
                if (CurrentProject != null)
                {
                    lastProjectManifest = CurrentProject.ManifestPath;
                }
                stateStore.Save(new WorkbenchState
                {
                    Settings = globalSettings,
                    RecentProjects = RecentProjects.Where(project => project != null).Take(10).ToList(),
                    LastProjectManifest = lastProjectManifest
                });
                StateStorageError = "";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Text.Json.JsonException || ex is NotSupportedException)
            {
                StateStorageError = "Could not save settings: " + ex.Message;
            }
        }

        internal void BrowseSetting(string name, Window owner)
        {
            var property = typeof(WorkbenchSettingsViewModel).GetProperty(name ?? "");
            if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
                return;
            string path = SettingsPathPicker.Select(name, owner);
            if (path != null)
            {
                property.SetValue(Settings, path);
            }
        }

        internal void LoadRecentProjects(IEnumerable<ProjectSessionViewModel> projects)
        {
            RecentProjects.Clear();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectSessionViewModel project in projects)
            {
                if (project == null || string.IsNullOrWhiteSpace(project.ManifestPath))
                {
                    continue;
                }
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
                {
                    continue;
                }
                project.ManifestPath = manifest;
                project.ProjectPath = Path.GetDirectoryName(manifest) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(project.Name))
                {
                    project.Name = Path.GetFileName(project.ProjectPath);
                }
                if (project.Settings == null)
                {
                    project.Settings = new WorkbenchSettingsViewModel();
                }
                project.PreviewCamera = project.PreviewCamera ?? new PreviewCameraState();
                RecentProjects.Add(project);
                if (RecentProjects.Count == 10)
                {
                    break;
                }
            }
        }

        internal ProjectSessionViewModel ActivateProject(WorkbenchProject project)
        {
            SaveState();
            restoringState = true;
            ProjectSessionViewModel session;
            try
            {
                string manifest = Path.GetFullPath(project.Manifest);
                session = RecentProjects.FirstOrDefault(item => item.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase));
                if (session == null)
                {
                    session = new ProjectSessionViewModel { Settings = Settings.Clone()};
                    RecentProjects.Insert(0, session);
                }
                else if (!session.Settings.HasAnyValue)
                {
                    session.Settings.CopyFrom(Settings);
                }
                session.Name = project.Name;
                session.ProjectPath = project.Root;
                session.ManifestPath = manifest;
                session.LastOpenedAt = DateTime.Now;
                if (RecentProjects.IndexOf(session) > 0)
                {
                    RecentProjects.Move(RecentProjects.IndexOf(session), 0);
                }
                while (RecentProjects.Count > 10)
                {
                    RecentProjects.RemoveAt(RecentProjects.Count - 1);
                }
                CurrentProject = session;
                Settings = session.Settings;
                PreviewCameraState camera = (session.PreviewCamera = session.PreviewCamera ?? new PreviewCameraState());
                PreviewCamera = camera;
                SelectedLodIndex = session.SelectedLodIndex;
                PreviewUsesShaders = session.PreviewUsesShaders;
            }
            finally
            {
                restoringState = false;
            }
            stateLoadFailed = false;
            SaveState();
            return session;
        }

        internal void SetStatus(WorkbenchOperationState state, string message)
        {
            Status = new WorkbenchOperationStatus(state, message);
            CanOpenResult = false;
            if (state == WorkbenchOperationState.Built || state == WorkbenchOperationState.BuiltWithWarnings)
            {
                CanOpenResult = true;
            }
            else if (state == WorkbenchOperationState.Busy || state == WorkbenchOperationState.Building)
            {
                CanOpenResult = false;
            }
        }

        internal void BeginOperation(WorkbenchOperationState state, string message)
        {
            CanOpenResult = false;
            SetStatus(state, message);
        }

        internal void CompleteOperation(OperationResult result, string message, bool built)
        {
            LastResult = result;
            LastError = null;
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
            {
                CurrentProject.ProjectChangeCount = Math.Max(0, CurrentProject.ProjectChangeCount + delta);
            }
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
