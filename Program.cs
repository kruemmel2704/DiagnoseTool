using Avalonia;
using System;

namespace DiagnoseTool
{
    class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Nach einem Auto-Update: warten, bis die alte Instanz beendet ist (Port 8085 frei)
            UpdateService.WaitForPreviousInstance(args);
            UpdatedFromVersion = UpdateService.UpdatedFromVersion(args);

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        /// <summary>Gesetzt, wenn die App gerade durch ein Auto-Update gestartet wurde.</summary>
        public static string UpdatedFromVersion { get; private set; }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
