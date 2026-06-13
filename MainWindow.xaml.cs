using System.Windows;
using System.Windows.Input;

namespace DiagnoseTool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            this.Closing += MainWindow_Closing;
            this.StateChanged += MainWindow_StateChanged;

            // Constrain maximized size to prevent taskbar overlap
            this.MaxHeight = SystemParameters.WorkArea.Height;
            this.MaxWidth = SystemParameters.WorkArea.Width;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Allows dragging and double-click maximizing the borderless window
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximize();
                }
                else
                {
                    try
                    {
                        this.DragMove();
                    }
                    catch
                    {
                        // Prevent crash if user double clicks or drags invalid component
                    }
                }
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (this.WindowState == WindowState.Maximized)
            {
                this.WindowState = WindowState.Normal;
            }
            else
            {
                this.WindowState = WindowState.Maximized;
            }
        }

        private void MainWindow_StateChanged(object sender, System.EventArgs e)
        {
            if (this.WindowState == WindowState.Maximized)
            {
                WindowBorder.CornerRadius = new CornerRadius(0);
                WindowBorder.BorderThickness = new Thickness(0);
                MaximizePath.Data = System.Windows.Media.Geometry.Parse("M 6 8 H 14 V 14 H 6 Z M 8 8 V 6 H 16 V 12 H 14");
            }
            else
            {
                WindowBorder.CornerRadius = new CornerRadius(12);
                WindowBorder.BorderThickness = new Thickness(1.5);
                MaximizePath.Data = System.Windows.Media.Geometry.Parse("M 4 4 H 14 V 14 H 4 Z");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Safely dispose the LibreHardwareMonitor computer object and timer
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
        }
    }
}
