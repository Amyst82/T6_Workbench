using Microsoft.Win32;
using System;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

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
        #region Project operations
        private void OpenProjButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Title = EnglishText.Text("Open project"), Filter = EnglishText.Text("Workbench project (project.json)|project.json") };
            if (DialogPathMemory.ShowDialog(dialog, this, "project/open-manifest") == true)
            {
                //OpenProject(dialog.FileName);
            }
        }
        

        

        #endregion
    }//
}
