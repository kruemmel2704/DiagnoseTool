using System;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DiagnoseTool
{
    /// <summary>
    /// Auto-Update: Hinweisleiste in der Oberfläche, Befehle und REST-Anbindung.
    /// </summary>
    public partial class MainViewModel
    {
        private bool _isUpdateBannerVisible;
        private string _updateBannerTitle = "";
        private string _updateBannerText = "";
        private bool _isUpdateBusy;
        private string _updateCheckStatus = "";

        public RelayCommand InstallUpdateCommand { get; private set; }
        public RelayCommand DismissUpdateCommand { get; private set; }
        public RelayCommand OpenReleaseNotesCommand { get; private set; }
        public RelayCommand CheckForUpdatesCommand { get; private set; }

        public bool IsUpdateBannerVisible
        {
            get => _isUpdateBannerVisible;
            set => SetProperty(ref _isUpdateBannerVisible, value);
        }

        public string UpdateBannerTitle
        {
            get => _updateBannerTitle;
            set => SetProperty(ref _updateBannerTitle, value);
        }

        public string UpdateBannerText
        {
            get => _updateBannerText;
            set => SetProperty(ref _updateBannerText, value);
        }

        public bool IsUpdateBusy
        {
            get => _isUpdateBusy;
            set
            {
                if (SetProperty(ref _isUpdateBusy, value))
                {
                    OnPropertyChanged(nameof(IsUpdateIdle));
                    InstallUpdateCommand?.RaiseCanExecuteChanged();
                    CheckForUpdatesCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsUpdateIdle => !IsUpdateBusy;

        public string UpdateCheckStatus
        {
            get => _updateCheckStatus;
            set => SetProperty(ref _updateCheckStatus, value);
        }

        private bool AnyTestRunning => IsCpuStressRunning || IsRamStressRunning || IsBatteryTestRunning || IsRecording
                                       || _launcherService.IsFurMarkRunning();

        private void InitializeUpdater()
        {
            InstallUpdateCommand = new RelayCommand(() => _ = InstallUpdateAsync(), () => !IsUpdateBusy);
            DismissUpdateCommand = new RelayCommand(() =>
            {
                UpdateService.Dismiss(UpdateService.LastResult?.LatestVersion);
                IsUpdateBannerVisible = false;
            });
            OpenReleaseNotesCommand = new RelayCommand(() =>
            {
                var url = UpdateService.LastResult?.ReleaseUrl;
                if (!string.IsNullOrEmpty(url)) OpenUrl(url);
            });
            CheckForUpdatesCommand = new RelayCommand(() => _ = CheckForUpdatesManuallyAsync(), () => !IsUpdateBusy);

            UpdateService.StateChanged += () => Dispatcher.UIThread.Post(RefreshUpdateBanner);
            UpdateService.RequestShutdownForUpdate = msg => Dispatcher.UIThread.Post(() =>
            {
                ShowStatus(msg, false);
                SessionLifecycleService.MarkCleanExit("update");
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                else
                    Environment.Exit(0);
            });

            if (!string.IsNullOrEmpty(Program.UpdatedFromVersion))
            {
                ShowStatus($"Update erfolgreich: v{Program.UpdatedFromVersion} → v{AppInfo.Version}", false);
            }

            UpdateCheckStatus = UpdateService.IsDisabled ? "Update-Prüfung deaktiviert" : $"v{AppInfo.Version}";
            UpdateService.Start();
        }

        private void RefreshUpdateBanner()
        {
            var info = UpdateService.LastResult;
            IsUpdateBusy = UpdateService.IsBusy;

            switch (UpdateService.State)
            {
                case UpdateService.StateDownloading:
                case UpdateService.StateInstalling:
                    IsUpdateBannerVisible = true;
                    UpdateBannerTitle = $"Update auf v{info?.LatestVersion}";
                    UpdateBannerText = UpdateService.StatusMessage;
                    return;
                case UpdateService.StateFailed when info != null && info.IsUpdateAvailable:
                    IsUpdateBannerVisible = true;
                    UpdateBannerTitle = $"Update auf v{info.LatestVersion} fehlgeschlagen";
                    UpdateBannerText = UpdateService.StatusMessage;
                    return;
            }

            if (info != null && info.IsUpdateAvailable && !UpdateService.IsDismissed(info.LatestVersion))
            {
                IsUpdateBannerVisible = true;
                UpdateBannerTitle = $"Neue Version v{info.LatestVersion} verfügbar (installiert: v{AppInfo.Version})";
                UpdateBannerText = info.CanInstallAutomatically
                    ? (AnyTestRunning
                        ? "Achtung: Laufende Tests und die Aufzeichnung werden beim Update beendet."
                        : "Das Update wird heruntergeladen, geprüft und das DiagnoseTool automatisch neu gestartet.")
                    : "Für dieses System ist keine automatische Installation möglich – bitte über die Versionshinweise herunterladen.";
            }
            else if (UpdateService.State != UpdateService.StateFailed)
            {
                IsUpdateBannerVisible = false;
            }

            if (!UpdateService.IsDisabled)
            {
                UpdateCheckStatus = info?.Error != null
                    ? "Update-Prüfung fehlgeschlagen"
                    : info != null && info.IsUpdateAvailable
                        ? $"v{AppInfo.Version} • Update v{info.LatestVersion} verfügbar"
                        : $"v{AppInfo.Version} • aktuell";
            }
        }

        private async Task CheckForUpdatesManuallyAsync()
        {
            var info = await UpdateService.CheckAsync();
            Dispatcher.UIThread.Post(() =>
            {
                if (info?.Error != null) ShowStatus(info.Error, true);
                else if (info != null && info.IsUpdateAvailable)
                {
                    UpdateService.Dismiss(null); // manuell geprüft -> Hinweis wieder anzeigen
                    RefreshUpdateBanner();
                    ShowStatus($"Update auf v{info.LatestVersion} verfügbar.", false);
                }
                else ShowStatus($"DiagnoseTool ist aktuell (v{AppInfo.Version}).", false);
            });
        }

        private async Task<(bool, string)> InstallUpdateAsync()
        {
            var result = await UpdateService.DownloadAndInstallAsync();
            Dispatcher.UIThread.Post(() =>
            {
                if (!result.Item1) ShowStatus(result.Item2, true);
                RefreshUpdateBanner();
            });
            return result;
        }

        // ------------------------------------------------------------------
        // REST-Fernsteuerung
        // ------------------------------------------------------------------

        public object GetUpdateStatus() => UpdateService.GetStatus();

        public RemoteActionResult CheckForUpdates()
        {
            var info = UpdateService.CheckAsync().GetAwaiter().GetResult();
            Dispatcher.UIThread.Post(RefreshUpdateBanner);
            if (info?.Error != null) return RemoteActionResult.Fail("update_check", info.Error, info);
            return RemoteActionResult.Ok("update_check",
                info.IsUpdateAvailable ? $"Update auf v{info.LatestVersion} verfügbar." : $"DiagnoseTool ist aktuell (v{AppInfo.Version}).",
                info);
        }

        public RemoteActionResult InstallUpdate()
        {
            var info = UpdateService.LastResult ?? UpdateService.CheckAsync().GetAwaiter().GetResult();
            if (info == null || !info.IsUpdateAvailable)
                return RemoteActionResult.Fail("update_install", $"Kein Update verfügbar (installiert: v{AppInfo.Version}).");
            if (!info.CanInstallAutomatically)
                return RemoteActionResult.Fail("update_install", "Für dieses System ist keine automatische Installation möglich.", info);
            if (UpdateService.IsBusy)
                return RemoteActionResult.Fail("update_install", "Es läuft bereits eine Update-Aktion.");

            // Download im Hintergrund, die Antwort kommt sofort zurück (danach startet die App neu)
            _ = Task.Run(InstallUpdateAsync);
            return RemoteActionResult.Ok("update_install",
                $"Update auf v{info.LatestVersion} wird heruntergeladen und installiert. Das DiagnoseTool startet danach neu.",
                new { from = AppInfo.Version, to = info.LatestVersion });
        }
    }
}
