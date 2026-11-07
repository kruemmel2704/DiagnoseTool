using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using Microsoft.Win32;

namespace DiagnoseTool
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly HardwareMonitorService _monitorService;
        private readonly ToolLauncherService _launcherService;
        private readonly DispatcherTimer _timer;
        
        private HardwareDiagnostics _diagnostics;
        private List<TestTool> _tools;
        private string _currentTime;
        private int _selectedTab = 0; // 0 = Dashboard, 1 = Tools, 2 = Repair, 3 = Info, 4 = BatteryTest
        private string _statusMessage = "Ready";
        private bool _isStatusError = false;
        private bool _isAdmin = false;
        private bool _hasDriverError = false;
        private bool _isLhmDriverBlocked = false;
        private bool _isHWiNFOFallbackActive = false;
        
        // Recording states
        private bool _isRecording = false;
        private DateTime _recordingStartTime;
        private readonly List<LogDataPoint> _recordedData = new List<LogDataPoint>();
        private string _recordingTimeDisplay = "00:00:00";

        // Battery health & stress test states
        private BatteryInfo _batteryInfo;
        private SystemInfo _systemInfo;
        private bool _isBatteryTestRunning = false;
        private TimeSpan _batteryTestRemainingTime;
        private string _batteryTestRemainingDisplay = "30:00";
        private double _batteryTestStartPercent;
        private double _batteryTestCurrentPercent;
        private bool _batteryTestCompleted = false;
        private string _batteryTestResults;
        private ObservableCollection<string> _batteryTestLog = new ObservableCollection<string>();

        // CPU Stress Test states
        private bool _isCpuStressRunning = false;
        private TimeSpan _cpuStressRemainingTime;
        private string _cpuStressRemainingDisplay = "00:00";
        private int _cpuStressSelectedThreads = Environment.ProcessorCount;
        private int _cpuStressSelectedDurationIndex = 1; // Default to 1 min (0: 30s, 1: 1m, 2: 5m, 3: 10m, 4: Infinite)
        private bool _cpuStressCompleted = false;
        private string _cpuStressResults = "";
        private ObservableCollection<string> _cpuStressLog = new ObservableCollection<string>();
        private System.Threading.CancellationTokenSource _cpuStressCts;

        public MainViewModel()
        {
            _monitorService = new HardwareMonitorService();
            _launcherService = new ToolLauncherService();

            // Commands
            LaunchCommand = new RelayCommand<string>(ExecuteLaunch);
            BrowseCommand = new RelayCommand<string>(ExecuteBrowse);
            ClearPathCommand = new RelayCommand<string>(ExecuteClearPath);
            RefreshUsbCommand = new RelayCommand(ExecuteRefreshUsb);
            SelectTabCommand = new RelayCommand<string>(ExecuteSelectTab);
            RunRepairCommand = new RelayCommand<string>(ExecuteRunRepair);
            StartRecordingCommand = new RelayCommand(ExecuteStartRecording, () => !IsRecording);
            StopRecordingCommand = new RelayCommand(ExecuteStopRecording, () => IsRecording);
            StartBatteryTestCommand = new RelayCommand(ExecuteStartBatteryTest, () => !IsBatteryTestRunning);
            StopBatteryTestCommand = new RelayCommand(ExecuteStopBatteryTest, () => IsBatteryTestRunning);
            SaveBatteryReportCommand = new RelayCommand(ExecuteSaveBatteryReport, () => BatteryTestCompleted);
            StartHWiNFOCommand = new RelayCommand(ExecuteStartHWiNFO);
            StartCpuStressCommand = new RelayCommand(ExecuteStartCpuStress, () => !IsCpuStressRunning);
            StopCpuStressCommand = new RelayCommand(ExecuteStopCpuStress, () => IsCpuStressRunning);

            // Detect admin privileges
            IsAdmin = IsRunningAsAdmin();

            // Fetch initial data
            UpdateDiagnostics();
            _tools = _launcherService.Tools;
            _currentTime = DateTime.Now.ToString("HH:mm:ss dddd, dd.MM.yyyy");
            _batteryInfo = BatteryService.GetBatteryInfo(_diagnostics?.BatteryData);
            _systemInfo = SystemInfoService.GetSystemInfo();

            // Automate HWiNFO startup if driver is blocked or GPU temperature is missing, HWiNFO is portable, and not already running
            bool isGpuTempMissing = Diagnostics == null || !Diagnostics.GpuTemp.HasValue || Diagnostics.GpuTemp.Value == 0;
            if ((IsLhmDriverBlocked || isGpuTempMissing) && IsHWiNFOPortableFound && !IsHWiNFORunning())
            {
                ExecuteStartHWiNFO();
            }

            // Setup timer to refresh data every second
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += TimerTick;
            _timer.Start();
        }

        public HardwareDiagnostics Diagnostics
        {
            get => _diagnostics;
            set => SetProperty(ref _diagnostics, value);
        }

        public List<TestTool> Tools
        {
            get => _tools;
            set => SetProperty(ref _tools, value);
        }

        public string CurrentTime
        {
            get => _currentTime;
            set => SetProperty(ref _currentTime, value);
        }

        public int SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetProperty(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsDashboardSelected));
                    OnPropertyChanged(nameof(IsToolsSelected));
                    OnPropertyChanged(nameof(IsRepairSelected));
                    OnPropertyChanged(nameof(IsInfoSelected));
                    OnPropertyChanged(nameof(IsBatteryTestSelected));
                    OnPropertyChanged(nameof(IsCpuStressSelected));
                }
            }
        }

        public bool IsDashboardSelected => SelectedTab == 0;
        public bool IsToolsSelected => SelectedTab == 1;
        public bool IsRepairSelected => SelectedTab == 2;
        public bool IsInfoSelected => SelectedTab == 3;
        public bool IsBatteryTestSelected => SelectedTab == 4;
        public bool IsCpuStressSelected => SelectedTab == 5;

        public bool IsCpuStressRunning
        {
            get => _isCpuStressRunning;
            set
            {
                if (SetProperty(ref _isCpuStressRunning, value))
                {
                    StartCpuStressCommand.RaiseCanExecuteChanged();
                    StopCpuStressCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string CpuStressRemainingDisplay
        {
            get => _cpuStressRemainingDisplay;
            set => SetProperty(ref _cpuStressRemainingDisplay, value);
        }

        public int CpuStressSelectedThreads
        {
            get => _cpuStressSelectedThreads;
            set => SetProperty(ref _cpuStressSelectedThreads, value);
        }

        public int CpuStressSelectedDurationIndex
        {
            get => _cpuStressSelectedDurationIndex;
            set => SetProperty(ref _cpuStressSelectedDurationIndex, value);
        }

        public bool CpuStressCompleted
        {
            get => _cpuStressCompleted;
            set => SetProperty(ref _cpuStressCompleted, value);
        }

        public string CpuStressResults
        {
            get => _cpuStressResults;
            set => SetProperty(ref _cpuStressResults, value);
        }

        public ObservableCollection<string> CpuStressLog => _cpuStressLog;

        public int MaxThreads => Environment.ProcessorCount;

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsStatusError
        {
            get => _isStatusError;
            set => SetProperty(ref _isStatusError, value);
        }

        public bool IsAdmin
        {
            get => _isAdmin;
            set
            {
                if (SetProperty(ref _isAdmin, value))
                {
                    OnPropertyChanged(nameof(AdminStatusText));
                }
            }
        }

        public bool HasDriverError
        {
            get => _hasDriverError;
            set => SetProperty(ref _hasDriverError, value);
        }

        public bool IsLhmDriverBlocked
        {
            get => _isLhmDriverBlocked;
            set => SetProperty(ref _isLhmDriverBlocked, value);
        }

        public bool IsHWiNFOFallbackActive
        {
            get => _isHWiNFOFallbackActive;
            set => SetProperty(ref _isHWiNFOFallbackActive, value);
        }

        public bool IsHWiNFOPortableFound => !string.IsNullOrEmpty(GetHWiNFOPath());

        public RelayCommand StartHWiNFOCommand { get; }

        public string AdminStatusText => IsAdmin ? "v1.3.0 | Admin Privileges Active" : "v1.3.0 | No Admin Privileges (Restricted)";

        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(IsNotRecording));
                }
            }
        }

        public bool IsNotRecording => !IsRecording;

        public string RecordingTimeDisplay
        {
            get => _recordingTimeDisplay;
            set => SetProperty(ref _recordingTimeDisplay, value);
        }

        public int RecordedSamplesCount => _recordedData.Count;

        // --- Battery & Stress Test Properties ---
        public BatteryInfo BatteryInfo
        {
            get => _batteryInfo;
            set => SetProperty(ref _batteryInfo, value);
        }

        public SystemInfo SystemInfo
        {
            get => _systemInfo;
            set => SetProperty(ref _systemInfo, value);
        }

        public bool IsBatteryTestRunning
        {
            get => _isBatteryTestRunning;
            set
            {
                if (SetProperty(ref _isBatteryTestRunning, value))
                {
                    OnPropertyChanged(nameof(IsNotBatteryTestRunning));
                }
            }
        }

        public bool IsNotBatteryTestRunning => !IsBatteryTestRunning;

        public string BatteryTestRemainingDisplay
        {
            get => _batteryTestRemainingDisplay;
            set => SetProperty(ref _batteryTestRemainingDisplay, value);
        }

        public double BatteryTestStartPercent
        {
            get => _batteryTestStartPercent;
            set => SetProperty(ref _batteryTestStartPercent, value);
        }

        public double BatteryTestCurrentPercent
        {
            get => _batteryTestCurrentPercent;
            set => SetProperty(ref _batteryTestCurrentPercent, value);
        }

        public bool BatteryTestCompleted
        {
            get => _batteryTestCompleted;
            set => SetProperty(ref _batteryTestCompleted, value);
        }

        public string BatteryTestResults
        {
            get => _batteryTestResults;
            set => SetProperty(ref _batteryTestResults, value);
        }

        public ObservableCollection<string> BatteryTestLog
        {
            get => _batteryTestLog;
            set => SetProperty(ref _batteryTestLog, value);
        }

        // --- Commands ---
        public RelayCommand<string> LaunchCommand { get; }
        public RelayCommand<string> BrowseCommand { get; }
        public RelayCommand<string> ClearPathCommand { get; }
        public RelayCommand RefreshUsbCommand { get; }
        public RelayCommand<string> SelectTabCommand { get; }
        public RelayCommand<string> RunRepairCommand { get; }
        public RelayCommand StartRecordingCommand { get; }
        public RelayCommand StopRecordingCommand { get; }
        public RelayCommand StartBatteryTestCommand { get; }
        public RelayCommand StopBatteryTestCommand { get; }
        public RelayCommand SaveBatteryReportCommand { get; }
        public RelayCommand StartCpuStressCommand { get; }
        public RelayCommand StopCpuStressCommand { get; }

        private void TimerTick(object sender, EventArgs e)
        {
            UpdateDiagnostics();
            CurrentTime = DateTime.Now.ToString("HH:mm:ss - dd.MM.yyyy");

            // Refresh Battery info in background
            BatteryInfo = BatteryService.GetBatteryInfo(Diagnostics?.BatteryData);

            if (IsRecording)
            {
                var duration = DateTime.Now - _recordingStartTime;
                RecordingTimeDisplay = string.Format("{0:D2}:{1:D2}:{2:D2}", duration.Hours, duration.Minutes, duration.Seconds);

                if (Diagnostics != null)
                {
                    _recordedData.Add(new LogDataPoint
                    {
                        Timestamp = DateTime.Now,
                        CpuCpuLoad = Diagnostics.CpuAverageLoad,
                        CpuCpuTemp = Diagnostics.CpuAverageTemp,
                        GpuGpuLoad = Diagnostics.GpuLoad,
                        GpuGpuTemp = Diagnostics.GpuTemp,
                        RamPercent = Diagnostics.RamPercent
                    });
                    OnPropertyChanged(nameof(RecordedSamplesCount));
                }
            }

            if (IsBatteryTestRunning)
            {
                _batteryTestRemainingTime = _batteryTestRemainingTime.Subtract(TimeSpan.FromSeconds(1));
                BatteryTestRemainingDisplay = string.Format("{0:D2}:{1:D2}", _batteryTestRemainingTime.Minutes + _batteryTestRemainingTime.Hours * 60, _batteryTestRemainingTime.Seconds);
                
                BatteryTestCurrentPercent = BatteryInfo.IsPresent ? BatteryInfo.CurrentChargePercent : 0;

                // Log every 60 seconds (when seconds is 0, except at the exact start duration)
                if (_batteryTestRemainingTime.Seconds == 0 && _batteryTestRemainingTime.TotalSeconds < 1800)
                {
                    double currentDrop = BatteryTestStartPercent - BatteryTestCurrentPercent;
                    if (currentDrop < 0) currentDrop = 0;

                    BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Verbleibende Zeit: {1} | Ladestand: {2}% (-{3}%)", 
                        DateTime.Now, BatteryTestRemainingDisplay, BatteryTestCurrentPercent, currentDrop));

                    // Warn if plugged back in
                    if (BatteryInfo.IsConnectedToAc)
                    {
                        BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Warnung: Netzbetrieb aktiv! Bitte das Ladekabel trennen, um die Entladung zu testen.", DateTime.Now));
                    }
                }

                if (_batteryTestRemainingTime.TotalSeconds <= 0)
                {
                    FinishBatteryTest(completed: true);
                }
            }

            if (IsCpuStressRunning)
            {
                if (CpuStressSelectedDurationIndex != 4) // Not Infinite
                {
                    _cpuStressRemainingTime = _cpuStressRemainingTime.Subtract(TimeSpan.FromSeconds(1));
                    CpuStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _cpuStressRemainingTime.Minutes + _cpuStressRemainingTime.Hours * 60, _cpuStressRemainingTime.Seconds);
                    
                    if (_cpuStressRemainingTime.TotalSeconds <= 0)
                    {
                        FinishCpuStress(completed: true);
                    }
                }
                else
                {
                    // Count up for infinite mode
                    _cpuStressRemainingTime = _cpuStressRemainingTime.Add(TimeSpan.FromSeconds(1));
                    CpuStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _cpuStressRemainingTime.Minutes + _cpuStressRemainingTime.Hours * 60, _cpuStressRemainingTime.Seconds);
                }

                // Periodically log diagnostics (every 5 seconds)
                if (DateTime.Now.Second % 5 == 0)
                {
                    string tempStr = Diagnostics != null && Diagnostics.CpuAverageTemp.HasValue ? $"{Diagnostics.CpuAverageTemp.Value:F1} °C" : "-- °C";
                    string loadStr = Diagnostics != null && Diagnostics.CpuAverageLoad.HasValue ? $"{Diagnostics.CpuAverageLoad.Value:F1} %" : "-- %";
                    CpuStressLog.Add(string.Format("[{0:HH:mm:ss}] - CPU-Temp: {1} | CPU-Auslastung: {2}", 
                        DateTime.Now, tempStr, loadStr));
                }
            }
        }

        private void UpdateDiagnostics()
        {
            Diagnostics = _monitorService.GetDiagnostics(IsCpuStressRunning, CpuStressSelectedThreads);

            if (Diagnostics != null)
            {
                IsLhmDriverBlocked = Diagnostics.IsLhmDriverBlocked;
                IsHWiNFOFallbackActive = Diagnostics.IsHWiNFOFallbackActive;
                HasDriverError = IsLhmDriverBlocked && !IsHWiNFOFallbackActive;
            }
            else
            {
                IsLhmDriverBlocked = false;
                IsHWiNFOFallbackActive = false;
                HasDriverError = false;
            }
        }

        private void ExecuteLaunch(string key)
        {
            if (_launcherService.LaunchTool(key, out string error))
            {
                ShowStatus($"Successfully started {key}!", false);
            }
            else
            {
                ShowStatus(error, true);
            }
        }

        private async void ExecuteBrowse(string key)
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow;
                if (window != null)
                {
                    var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
                    {
                        Title = $"Select path for {key}",
                        AllowMultiple = false,
                        FileTypeFilter = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("Executables") { Patterns = new[] { "*.exe" } },
                            new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*" } }
                        }
                    };

                    var result = await window.StorageProvider.OpenFilePickerAsync(options);
                    if (result != null && result.Count > 0)
                    {
                        var filePath = result[0].Path.LocalPath;
                        _launcherService.SaveManualPath(key, filePath);
                        // Refresh our tools collection binding
                        Tools = null;
                        Tools = _launcherService.Tools;
                        ShowStatus($"Path updated for {key}!", false);
                    }
                }
            }
        }

        private void ExecuteClearPath(string key)
        {
            _launcherService.ClearManualPath(key);
            Tools = null;
            Tools = _launcherService.Tools;
            ShowStatus($"Reset path to automatic detection for {key}.", false);
        }

        private void ExecuteRefreshUsb()
        {
            _launcherService.RefreshPaths();
            Tools = null;
            Tools = _launcherService.Tools;
            ShowStatus("Scanned USB sticks for executables.", false);
        }

        private void ExecuteSelectTab(string tabIndexStr)
        {
            if (int.TryParse(tabIndexStr, out int index))
            {
                SelectedTab = index;
            }
        }

        private void ExecuteRunRepair(string key)
        {
            string commandArgs = "";
            string description = "";

            switch (key)
            {
                case "sfc":
                    commandArgs = "/k sfc /scannow";
                    description = "System File Checker (SFC)";
                    break;
                case "dism_restore":
                    commandArgs = "/k dism /online /cleanup-image /restorehealth";
                    description = "DISM Restore Health";
                    break;
                case "dism_check":
                    commandArgs = "/k dism /online /cleanup-image /checkhealth";
                    description = "DISM Check Health";
                    break;
                case "chkdsk":
                    commandArgs = "/k chkdsk C:";
                    description = "Chkdsk C: (Read-only)";
                    break;
                default:
                    ShowStatus("Unknown repair command", true);
                    return;
            }

            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = commandArgs,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                System.Diagnostics.Process.Start(startInfo);
                ShowStatus($"Started {description} in cmd window.", false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to start repair command: {ex.Message}", true);
            }
        }

        private void ShowStatus(string msg, bool isError)
        {
            StatusMessage = msg;
            IsStatusError = isError;

            // Automatically clear status message after 5 seconds if not an error
            if (!isError)
            {
                var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                statusTimer.Tick += (s, e) =>
                {
                    if (StatusMessage == msg)
                    {
                        StatusMessage = "Ready";
                    }
                    statusTimer.Stop();
                };
                statusTimer.Start();
            }
        }

        private void ExecuteStartRecording()
        {
            _recordedData.Clear();
            _recordingStartTime = DateTime.Now;
            IsRecording = true;
            RecordingTimeDisplay = "00:00:00";
            OnPropertyChanged(nameof(RecordedSamplesCount));
            ShowStatus("Werte-Aufzeichnung gestartet.", false);
        }

        private async void ExecuteStopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;

            if (_recordedData.Count == 0)
            {
                ShowStatus("Keine Datenpunkte erfasst. Aufzeichnung verworfen.", true);
                return;
            }

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow;
                if (window != null)
                {
                    var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Prüfbericht speichern",
                        SuggestedFileName = $"Pruefbericht_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                        DefaultExtension = "pdf",
                        FileTypeChoices = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("PDF-Dateien") { Patterns = new[] { "*.pdf" } }
                        }
                    };

                    var result = await window.StorageProvider.SaveFilePickerAsync(options);
                    if (result != null)
                    {
                        try
                        {
                            var filePath = result.Path.LocalPath;
                            string cpu = Diagnostics != null ? Diagnostics.CpuName : "Unknown CPU";
                            string gpu = Diagnostics != null ? Diagnostics.GpuName : "Unknown GPU";
                            string ram = Diagnostics != null ? Diagnostics.RamUsageDisplay : "Unknown RAM";

                            PdfReportService.GenerateReport(
                                filePath,
                                cpu,
                                gpu,
                                ram,
                                SystemInfo,
                                _recordingStartTime,
                                DateTime.Now,
                                _recordedData
                            );

                            ShowStatus("PDF-Prüfbericht erfolgreich gespeichert!", false);
                        }
                        catch (Exception ex)
                        {
                            ShowStatus($"Fehler beim Erstellen des Berichts: {ex.Message}", true);
                        }
                    }
                    else
                    {
                        ShowStatus("Aufzeichnung beendet (nicht gespeichert).", false);
                    }
                }
            }
        }

        private void ExecuteStartBatteryTest()
        {
            var info = BatteryService.GetBatteryInfo(Diagnostics?.BatteryData);
            if (!info.IsPresent)
            {
                ShowStatus("Keine Batterie erkannt. Der Stresstest kann nur auf Laptops durchgeführt werden.", true);
                return;
            }

            BatteryTestLog.Clear();
            BatteryTestStartPercent = info.CurrentChargePercent;
            BatteryTestCurrentPercent = info.CurrentChargePercent;
            _batteryTestRemainingTime = TimeSpan.FromMinutes(30);
            BatteryTestRemainingDisplay = "30:00";
            BatteryTestCompleted = false;
            BatteryTestResults = null;
            IsBatteryTestRunning = true;

            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Batterietest gestartet bei {1}% Ladung.", DateTime.Now, BatteryTestStartPercent));
            if (info.IsConnectedToAc)
            {
                BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Warnung: Ladekabel ist angeschlossen. Für verlässliche Testergebnisse bitte das Kabel trennen!", DateTime.Now));
            }

            try
            {
                // Launch the YouTube URL in default browser using safe helper
                OpenUrl("https://www.youtube.com/watch?v=yq6c8h0Z3-Q");
                ShowStatus("YouTube-Stresstest gestartet. Video wird im Browser abgespielt.", false);
            }
            catch (Exception ex)
            {
                BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Fehler beim Starten des Browsers: {1}", DateTime.Now, ex.Message));
                ShowStatus(string.Format("Fehler beim Starten des Browsers: {0}", ex.Message), true);
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch
            {
                try
                {
                    System.Diagnostics.Process.Start("cmd.exe", $"/c start {url.Replace("&", "^&")}");
                }
                catch
                {
                    try
                    {
                        System.Diagnostics.Process.Start("powershell.exe", $"-Command \"Start-Process '{url}'\"");
                    }
                    catch { }
                }
            }
        }

        private void ExecuteStopBatteryTest()
        {
            if (!IsBatteryTestRunning) return;
            FinishBatteryTest(completed: false);
        }

        private void FinishBatteryTest(bool completed)
        {
            IsBatteryTestRunning = false;
            BatteryTestCompleted = true;

            var finalInfo = BatteryService.GetBatteryInfo(Diagnostics?.BatteryData);
            BatteryTestCurrentPercent = finalInfo.IsPresent ? finalInfo.CurrentChargePercent : 0;
            double drain = BatteryTestStartPercent - BatteryTestCurrentPercent;
            if (drain < 0) drain = 0;

            TimeSpan elapsed = TimeSpan.FromMinutes(30).Subtract(_batteryTestRemainingTime);
            double hours = elapsed.TotalHours;
            double dischargeRate = hours > 0 ? drain / hours : 0;
            double estLife = (dischargeRate > 0 && BatteryTestCurrentPercent > 0) ? BatteryTestCurrentPercent / dischargeRate : 0;

            string statusText = completed ? "vollständig beendet" : "abgebrochen";
            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest {1} nach {2} Min. & {3} Sek.", 
                DateTime.Now, statusText, elapsed.Minutes, elapsed.Seconds));
            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Endladung: {1}% | Gesamtverbrauch: {2}%", 
                DateTime.Now, BatteryTestCurrentPercent, drain));

            if (completed)
            {
                BatteryTestResults = string.Format(
                    "Testergebnis:\n" +
                    "• Batterie-Entladung: {0:F0}% in 30 Minuten\n" +
                    "• Hochgerechnete Entladungsrate: {1:F1}% pro Stunde\n" +
                    "• Geschätzte Restlaufzeit unter Last: {2:F1} Stunden",
                    drain, dischargeRate, estLife);
                ShowStatus("Akkutest erfolgreich beendet!", false);
            }
            else
            {
                BatteryTestResults = string.Format(
                    "Testergebnis (Abgebrochen):\n" +
                    "• Testdauer: {0:D2} Min. {1:D2} Sek.\n" +
                    "• Entladung: {2:F0}%\n" +
                    "• Laufzeitprognose ungenau aufgrund des Abbruchs.",
                    elapsed.Minutes, elapsed.Seconds, drain);
                ShowStatus("Akkutest abgebrochen.", false);
            }
        }

        private async void ExecuteSaveBatteryReport()
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = desktop.MainWindow;
                if (window != null)
                {
                    var options = new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = "Batterie-Prüfbericht speichern",
                        SuggestedFileName = $"Batteriebericht_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
                        DefaultExtension = "pdf",
                        FileTypeChoices = new[]
                        {
                            new Avalonia.Platform.Storage.FilePickerFileType("PDF-Dateien") { Patterns = new[] { "*.pdf" } }
                        }
                    };

                    var result = await window.StorageProvider.SaveFilePickerAsync(options);
                    if (result != null)
                    {
                        try
                        {
                            var filePath = result.Path.LocalPath;
                            string cpu = Diagnostics != null ? Diagnostics.CpuName : "Unknown CPU";
                            string gpu = Diagnostics != null ? Diagnostics.GpuName : "Unknown GPU";
                            string ram = Diagnostics != null ? Diagnostics.RamUsageDisplay : "Unknown RAM";
                            
                            TimeSpan elapsed = TimeSpan.FromMinutes(30).Subtract(_batteryTestRemainingTime);
                            DateTime start = DateTime.Now.Subtract(elapsed);

                            PdfReportService.GenerateBatteryReport(
                                filePath,
                                cpu,
                                gpu,
                                ram,
                                SystemInfo,
                                BatteryInfo,
                                start,
                                DateTime.Now,
                                BatteryTestStartPercent,
                                BatteryTestCurrentPercent,
                                new List<string>(BatteryTestLog)
                            );

                            ShowStatus("PDF-Batteriebericht erfolgreich gespeichert!", false);
                        }
                        catch (Exception ex)
                        {
                            ShowStatus(string.Format("Fehler beim Erstellen des PDF-Berichts: {0}", ex.Message), true);
                        }
                    }
                }
            }
        }

        private static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }        private string GetHWiNFOPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] paths = new[]
            {
                System.IO.Path.Combine(baseDir, "HWiNFO64.exe"),
                System.IO.Path.Combine(baseDir, "Tools", "HWiNFO", "HWiNFO64.exe"),
                System.IO.Path.Combine(baseDir, "Tools", "HWiNFO64.exe")
            };

            foreach (var p in paths)
            {
                if (System.IO.File.Exists(p)) return p;
            }

            return null;
        }

        private bool IsHWiNFORunning()
        {
            try
            {
                return System.Diagnostics.Process.GetProcesses()
                    .Any(p => p.ProcessName.IndexOf("HWiNFO", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch
            {
                return false;
            }
        }

        private void AutoConfigureHWiNFOSensors()
        {
            try
            {
                // 1. Enable Gadget reporting globally
                using (var hkey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\HWiNFO64\Settings"))
                {
                    if (hkey != null)
                    {
                        hkey.SetValue("EnableReporting", 1, RegistryValueKind.DWord);
                        hkey.SetValue("SensorsOnly", 1, RegistryValueKind.DWord);
                        hkey.SetValue("MinimizeSensors", 1, RegistryValueKind.DWord);
                        hkey.SetValue("ShowWelcomeScreen", 0, RegistryValueKind.DWord);
                    }
                }

                // 2. Enable VSB (Virtual Sensor Board) reporting globally under Sensors
                using (var sensorsKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\HWiNFO64\Sensors"))
                {
                    if (sensorsKey != null)
                    {
                        sensorsKey.SetValue("EnableVSB", 1, RegistryValueKind.DWord);

                        // 3. Scan all hardware device subkeys and enable InVSB/Sidebar for all their sensors
                        var hwKeyNames = sensorsKey.GetSubKeyNames();
                        foreach (var hwName in hwKeyNames)
                        {
                            if (hwName.Equals("Settings", StringComparison.OrdinalIgnoreCase) ||
                                hwName.Equals("Custom", StringComparison.OrdinalIgnoreCase) ||
                                hwName.Equals("OSD", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            using (var hwKey = sensorsKey.OpenSubKey(hwName, true))
                            {
                                if (hwKey == null) continue;

                                var metricKeyNames = hwKey.GetSubKeyNames();
                                foreach (var metricName in metricKeyNames)
                                {
                                    using (var metricKey = hwKey.OpenSubKey(metricName, true))
                                    {
                                        if (metricKey == null) continue;
                                        metricKey.SetValue("InVSB", 1, RegistryValueKind.DWord);
                                        metricKey.SetValue("Sidebar", 1, RegistryValueKind.DWord);
                                    }
                                }
                            }
                        }
                    }
                }

                // 4. Backwards compatibility for legacy Settings subkey
                using (var legacySettingsKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\HWiNFO64\Sensors\Settings", true))
                {
                    if (legacySettingsKey != null)
                    {
                        var subKeyNames = legacySettingsKey.GetSubKeyNames();
                        foreach (var subName in subKeyNames)
                        {
                            using (var subKey = legacySettingsKey.OpenSubKey(subName, true))
                            {
                                if (subKey == null) continue;
                                subKey.SetValue("InVSB", 1, RegistryValueKind.DWord);
                                subKey.SetValue("Sidebar", 1, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to auto-configure HWiNFO sensors: {ex.Message}");
            }
        }

        private void ExecuteStartHWiNFO()
        {
            string hwinfoPath = GetHWiNFOPath();
            if (string.IsNullOrEmpty(hwinfoPath))
            {
                ShowStatus("HWiNFO64.exe nicht im Tools-Ordner gefunden.", true);
                return;
            }

            try
            {
                // Automate HWiNFO configuration dynamically
                AutoConfigureHWiNFOSensors();

                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = hwinfoPath,
                    Arguments = "", // No command line flags to avoid free version warnings; registry configurations handle layout
                    WorkingDirectory = System.IO.Path.GetDirectoryName(hwinfoPath),
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
                ShowStatus("HWiNFO64 im Hintergrund gestartet und konfiguriert.", false);
            }
            catch (Exception ex)
            {
                ShowStatus($"Fehler beim Starten von HWiNFO: {ex.Message}", true);
            }
        }

        private void ExecuteStartCpuStress()
        {
            if (IsCpuStressRunning) return;

            IsCpuStressRunning = true;
            CpuStressCompleted = false;
            CpuStressResults = "";
            CpuStressLog.Clear();

            // Resolve duration
            double durationSeconds = 0;
            string durationText = "";
            switch (CpuStressSelectedDurationIndex)
            {
                case 0:
                    durationSeconds = 30;
                    durationText = "30 Sekunden";
                    break;
                case 1:
                    durationSeconds = 60;
                    durationText = "1 Minute";
                    break;
                case 2:
                    durationSeconds = 300;
                    durationText = "5 Minuten";
                    break;
                case 3:
                    durationSeconds = 600;
                    durationText = "10 Minuten";
                    break;
                case 4:
                    durationSeconds = double.MaxValue;
                    durationText = "Unbegrenzt";
                    break;
            }

            if (durationSeconds == double.MaxValue)
            {
                _cpuStressRemainingTime = TimeSpan.Zero;
                CpuStressRemainingDisplay = "Unbegrenzt";
            }
            else
            {
                _cpuStressRemainingTime = TimeSpan.FromSeconds(durationSeconds);
                CpuStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _cpuStressRemainingTime.Minutes + _cpuStressRemainingTime.Hours * 60, _cpuStressRemainingTime.Seconds);
            }

            CpuStressLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest gestartet ({1} Threads, Dauer: {2})", 
                DateTime.Now, CpuStressSelectedThreads, durationText));

            // Start dedicated background threads (avoiding ThreadPool starvation to keep UI responsive)
            _cpuStressCts = new System.Threading.CancellationTokenSource();
            var token = _cpuStressCts.Token;

            for (int i = 0; i < CpuStressSelectedThreads; i++)
            {
                int threadNum = i + 1;
                var worker = new StressWorker();
                var thread = new System.Threading.Thread(() => worker.Run(token))
                {
                    IsBackground = true,
                    Name = $"DiagnoseTool Stress Core {threadNum}",
                    Priority = System.Threading.ThreadPriority.BelowNormal // Give UI thread priority to prevent freezes
                };
                thread.Start();
            }

            // Raise commands changed
            StartCpuStressCommand.RaiseCanExecuteChanged();
            StopCpuStressCommand.RaiseCanExecuteChanged();
        }

        private void ExecuteStopCpuStress()
        {
            if (!IsCpuStressRunning) return;
            CpuStressLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest manuell abgebrochen.", DateTime.Now));
            FinishCpuStress(completed: false);
        }

        private void FinishCpuStress(bool completed)
        {
            if (!IsCpuStressRunning) return;

            // Stop all threads
            try
            {
                _cpuStressCts?.Cancel();
                _cpuStressCts?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cancel CPU stress threads: {ex.Message}");
            }
            _cpuStressCts = null;

            IsCpuStressRunning = false;
            CpuStressCompleted = true;

            if (completed)
            {
                CpuStressResults = $"Test abgeschlossen! {CpuStressSelectedThreads} Threads liefen erfolgreich.";
                CpuStressLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest erfolgreich beendet.", DateTime.Now));
            }
            else
            {
                CpuStressResults = "Test abgebrochen.";
            }

            StartCpuStressCommand.RaiseCanExecuteChanged();
            StopCpuStressCommand.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            _timer?.Stop();
            try
            {
                _cpuStressCts?.Cancel();
                _cpuStressCts?.Dispose();
            }
            catch {}
            _monitorService?.Dispose();
        }
    }

    public class LogDataPoint
    {
        public DateTime Timestamp { get; set; }
        public float? CpuCpuLoad { get; set; }
        public float? CpuCpuTemp { get; set; }
        public float? GpuGpuLoad { get; set; }
        public float? GpuGpuTemp { get; set; }
        public float? RamPercent { get; set; }
    }

    public class StressWorker
    {
        public volatile float Result;

        public void Run(System.Threading.CancellationToken token)
        {
            double val = 12345.67;
            int counter = 0;
            while (!token.IsCancellationRequested)
            {
                // Tight loop of calculations to fully saturate CPU execution pipelines
                for (int j = 0; j < 1000; j++)
                {
                    val = Math.Sqrt(val * 1.0000001 + j);
                    val = Math.Sin(val) * Math.Cos(val);
                }
                if (val > 1000000.0 || double.IsNaN(val) || double.IsInfinity(val))
                {
                    val = 12345.67;
                }
                Result = (float)val; // Force memory write of volatile field to prevent compiler optimization

                // Yield execution briefly every 200 outer iterations (approx. every 10-15ms)
                // This prevents macOS from throttling/penalizing the app process due to App Nap / high thermal load,
                // keeping the UI perfectly responsive while still keeping CPU utilization extremely high.
                counter++;
                if (counter % 200 == 0)
                {
                    System.Threading.Thread.Sleep(1);
                }
            }
        }
    }
}
