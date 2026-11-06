using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace T6_Workbench
{
    public partial class MainWindow : Window
    {
        #region Window UI
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MainGrid_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            Point mousePos = e.GetPosition(ToolBarHead);
            if (mousePos.Y > 50)
                return;
            if(e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void ToolBarHead_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
        }
        #endregion


        private async void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            if (!MainViewModel.WorkbenchViewModel.CanOpenProject)
                return;
            var dialog = new OpenFileDialog { Title = "Open project", Filter = "Workbench project (project.json)|project.json", CheckFileExists = true };
            if (DialogPathMemory.ShowDialog(dialog, this, "project/open-manifest") == true)
            {
                await MainViewModel.WorkbenchViewModel.OpenProjectAsync(dialog.FileName);
            }
        }

        private void ShowRecentProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is ProjectSessionViewModel project)
            {
                MainViewModel.WorkbenchViewModel.ShowRecentProjectFolder(project);
            }
        }

        private void RemoveRecentProject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && item.Tag is ProjectSessionViewModel project)
            {
                MainViewModel.WorkbenchViewModel.RemoveRecentProject(project);
            }
        }

        private void BrowseSetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is string name)
            {
                MainViewModel.WorkbenchViewModel.BrowseSetting(name, this);
            }
        }

        private void ProjSettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            TabsControl.SelectedIndex = 2;
            ProjDropdownMenuCheckBoxButton.IsChecked = false;
        }

        private async void RecentProjStyleBackGroundBorder_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if(e.ClickCount == 2)
            {
                if (sender is FrameworkElement element && element.DataContext is ProjectSessionViewModel project)
                {
                    await MainViewModel.WorkbenchViewModel.OpenProjectAsync(project.ManifestPath);
                }
            }
        }

        private void SwitchThemeButton_Click(object sender, RoutedEventArgs e)
        {
            MainViewModel.WorkbenchViewModel.UIColors.SetTheme(SwitchThemeButton.IsChecked);
        }
    }//
}
