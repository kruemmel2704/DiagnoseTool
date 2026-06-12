using System;
using System.Windows;

namespace DiagnoseTool
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            // Clean up resources if necessary
            base.OnExit(e);
        }
    }
}
