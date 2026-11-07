using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;

namespace DiagnoseTool
{
    public partial class MainWindow : Window
    {
        private Border _windowBorder;
        private Avalonia.Controls.Shapes.Path _maximizePath;

        public MainWindow()
        {
            InitializeComponent();
            
            _windowBorder = this.Find<Border>("WindowBorder");
            _maximizePath = this.Find<Avalonia.Controls.Shapes.Path>("MaximizePath");

            this.PropertyChanged += MainWindow_PropertyChanged;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void Window_PointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    ToggleMaximize();
                }
                else
                {
                    this.BeginMoveDrag(e);
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

        private void MainWindow_PropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Window.WindowStateProperty)
            {
                UpdateMaximizeState();
            }
        }

        private void UpdateMaximizeState()
        {
            if (_windowBorder == null || _maximizePath == null) return;

            if (this.WindowState == WindowState.Maximized)
            {
                _windowBorder.CornerRadius = new CornerRadius(0);
                _windowBorder.BorderThickness = new Thickness(0);
                _maximizePath.Data = StreamGeometry.Parse("M 6 8 H 14 V 14 H 6 Z M 8 8 V 6 H 16 V 12 H 14");
            }
            else
            {
                _windowBorder.CornerRadius = new CornerRadius(12);
                _windowBorder.BorderThickness = new Thickness(1.5);
                _maximizePath.Data = StreamGeometry.Parse("M 4 4 H 14 V 14 H 4 Z");
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            // Safely dispose the LibreHardwareMonitor computer object and timer
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
            base.OnClosing(e);
        }
    }
}
