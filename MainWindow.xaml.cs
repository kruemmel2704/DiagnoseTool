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
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Allows dragging the borderless window around the screen
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
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

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
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
