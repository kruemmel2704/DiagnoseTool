using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DiagnoseTool
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Verhindert Ruhezustand & Standby während die Diagnose-App läuft
                SleepManagementService.PreventSleepAndHibernate();

                // Sitzungsüberwachung: erkennt unsaubere Beendigung (Absturz) der letzten Sitzung
                SessionLifecycleService.Initialize();

                desktop.Exit += (s, e) =>
                {
                    // Ordentliches Beenden -> Hardware-Check zeigt "Programm geschlossen" statt "Absturz"
                    SessionLifecycleService.MarkCleanExit("app_exit");
                    SleepManagementService.RestoreNormalSleep();
                };

                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel()
                };
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
