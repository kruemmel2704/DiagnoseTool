using System;
using System.Collections.Generic;
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
        private int _selectedTab = 0; // 0 = Dashboard, 1 = Tools, 2 = Repair, 3 = Info
        private string _statusMessage = "Ready";
        private bool _isStatusError = false;
        
        // Recording states
        private bool _isRecording = false;
        private DateTime _recordingStartTime;
        private readonly List<LogDataPoint> _recordedData = new List<LogDataPoint>();
        private string _recordingTimeDisplay = "00:00:00";

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

            // Fetch initial data
            UpdateDiagnostics();
            _tools = _launcherService.Tools;
            _currentTime = DateTime.Now.ToString("HH:mm:ss dddd, dd.MM.yyyy");

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
                }
            }
        }

        public bool IsDashboardSelected => SelectedTab == 0;
        public bool IsToolsSelected => SelectedTab == 1;
        public bool IsRepairSelected => SelectedTab == 2;
        public bool IsInfoSelected => SelectedTab == 3;

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

        // --- Commands ---
        public RelayCommand<string> LaunchCommand { get; }
        public RelayCommand<string> BrowseCommand { get; }
        public RelayCommand<string> ClearPathCommand { get; }
        public RelayCommand RefreshUsbCommand { get; }
        public RelayCommand<string> SelectTabCommand { get; }
        public RelayCommand<string> RunRepairCommand { get; }
        public RelayCommand StartRecordingCommand { get; }
        public RelayCommand StopRecordingCommand { get; }

        private void TimerTick(object sender, EventArgs e)
        {
            UpdateDiagnostics();
            CurrentTime = DateTime.Now.ToString("HH:mm:ss - dd.MM.yyyy");

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
