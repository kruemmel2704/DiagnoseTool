using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
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
        
        // Recording states
        private bool _isRecording = false;
        private DateTime _recordingStartTime;
        private readonly List<LogDataPoint> _recordedData = new List<LogDataPoint>();
        private string _recordingTimeDisplay = "00:00:00";

        // Battery health & stress test states
        private BatteryInfo _batteryInfo;
        private bool _isBatteryTestRunning = false;
        private TimeSpan _batteryTestRemainingTime;
        private string _batteryTestRemainingDisplay = "30:00";
        private double _batteryTestStartPercent;
        private double _batteryTestCurrentPercent;
        private bool _batteryTestCompleted = false;
        private string _batteryTestResults;
        private ObservableCollection<string> _batteryTestLog = new ObservableCollection<string>();

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

            // Fetch initial data
            UpdateDiagnostics();
            _tools = _launcherService.Tools;
            _currentTime = DateTime.Now.ToString("HH:mm:ss dddd, dd.MM.yyyy");
            _batteryInfo = BatteryService.GetBatteryInfo();

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
                }
            }
        }

        public bool IsDashboardSelected => SelectedTab == 0;
        public bool IsToolsSelected => SelectedTab == 1;
        public bool IsRepairSelected => SelectedTab == 2;
        public bool IsInfoSelected => SelectedTab == 3;
        public bool IsBatteryTestSelected => SelectedTab == 4;

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

        private void TimerTick(object sender, EventArgs e)
        {
            UpdateDiagnostics();
            CurrentTime = DateTime.Now.ToString("HH:mm:ss - dd.MM.yyyy");

            // Refresh Battery info in background
            BatteryInfo = BatteryService.GetBatteryInfo();

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
        }

        private void UpdateDiagnostics()
        {
            Diagnostics = _monitorService.GetDiagnostics();
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

        private void ExecuteBrowse(string key)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
                Title = $"Select path for {key}"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                _launcherService.SaveManualPath(key, openFileDialog.FileName);
                // Refresh our tools collection binding
                Tools = null;
                Tools = _launcherService.Tools;
                ShowStatus($"Path updated for {key}!", false);
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

        private void ExecuteStopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;

            if (_recordedData.Count == 0)
            {
                ShowStatus("Keine Datenpunkte erfasst. Aufzeichnung verworfen.", true);
                return;
            }

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "PDF-Dateien (*.pdf)|*.pdf",
                Title = "Prüfbericht speichern",
                FileName = $"Pruefbericht_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    string cpu = Diagnostics != null ? Diagnostics.CpuName : "Unknown CPU";
                    string gpu = Diagnostics != null ? Diagnostics.GpuName : "Unknown GPU";
                    string ram = Diagnostics != null ? Diagnostics.RamUsageDisplay : "Unknown RAM";

                    PdfReportService.GenerateReport(
                        saveFileDialog.FileName,
                        cpu,
                        gpu,
                        ram,
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

        private void ExecuteStartBatteryTest()
        {
            var info = BatteryService.GetBatteryInfo();
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
                // Launch the YouTube URL in default browser
                System.Diagnostics.Process.Start("https://www.youtube.com/watch?v=yq6c8h0Z3-Q");
                ShowStatus("YouTube-Stresstest gestartet. Video wird im Browser abgespielt.", false);
            }
            catch (Exception ex)
            {
                BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Fehler beim Starten des Browsers: {1}", DateTime.Now, ex.Message));
                ShowStatus(string.Format("Fehler beim Starten des Browsers: {0}", ex.Message), true);
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

            var finalInfo = BatteryService.GetBatteryInfo();
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

        private void ExecuteSaveBatteryReport()
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "PDF-Dateien (*.pdf)|*.pdf",
                Title = "Batterie-Prüfbericht speichern",
                FileName = string.Format("Batteriebericht_{0:yyyyMMdd_HHmmss}.pdf", DateTime.Now)
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    string cpu = Diagnostics != null ? Diagnostics.CpuName : "Unknown CPU";
                    string gpu = Diagnostics != null ? Diagnostics.GpuName : "Unknown GPU";
                    string ram = Diagnostics != null ? Diagnostics.RamUsageDisplay : "Unknown RAM";
                    
                    TimeSpan elapsed = TimeSpan.FromMinutes(30).Subtract(_batteryTestRemainingTime);
                    DateTime start = DateTime.Now.Subtract(elapsed);

                    PdfReportService.GenerateBatteryReport(
                        saveFileDialog.FileName,
                        cpu,
                        gpu,
                        ram,
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

        public void Dispose()
        {
            _timer?.Stop();
            _monitorService?.Dispose();
        }
    }

    public class LogDataPoint
    {
        public DateTime Timestamp { get; set; }
        public float CpuCpuLoad { get; set; }
        public float CpuCpuTemp { get; set; }
        public float GpuGpuLoad { get; set; }
        public float GpuGpuTemp { get; set; }
        public float RamPercent { get; set; }
    }
}
