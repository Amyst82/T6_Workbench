using System;
using System.Windows;

namespace T6_Workbench
{
    public partial class MainWindow : Window
    {
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

        private void MainTabGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double availableWidth = Math.Max(0, MainGrid.ActualWidth - 5);
            double minFactor = this.WindowState == WindowState.Maximized ? 0.15d : 0.3d;
            LeftColumn.MinWidth = 300;
            LeftColumn.MaxWidth = availableWidth * 0.5;
            FooterRow.Height = this.WindowState == WindowState.Maximized ? new GridLength(50) : new GridLength(0);
        }
    }
}
