using System.Collections.Generic;

namespace T6_Workbench
{
    /// <summary>
    /// Persisted application settings and project-specific UI state.
    /// </summary>
    public sealed class WorkbenchState
    {
        public int Version { get; set; } = 1;

        public WorkbenchSettingsViewModel Settings { get; set; } = new WorkbenchSettingsViewModel();

        public List<ProjectSessionViewModel> RecentProjects { get; set; } = new List<ProjectSessionViewModel>();

        public string LastProjectManifest { get; set; } = "";
    }
}
