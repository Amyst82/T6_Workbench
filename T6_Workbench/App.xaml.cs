using System.Windows;

namespace T6_Workbench
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            MainViewModel.WorkbenchViewModel.InitializeState();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            MainViewModel.WorkbenchViewModel.SaveState();
            base.OnExit(e);
        }
    }
}
