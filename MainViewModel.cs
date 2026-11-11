using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;

namespace DiagnoseTool
{
    public partial class MainViewModel : ViewModelBase, IDisposable, IDiagnoseRemoteControl
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
        private TimeSpan _batteryTestTotalDuration = TimeSpan.FromMinutes(30);
        private DateTime _batteryTestStartTime = DateTime.Now;
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
        private int _cpuStressSelectedDurationIndex = 1; // Default to 1 min
        private double? _overrideCpuDurationSeconds = null;
        private bool _isCpuStressInfinite = false;
        private bool _cpuStressCompleted = false;
        private string _cpuStressResults = "";
        private ObservableCollection<string> _cpuStressLog = new ObservableCollection<string>();
        private System.Threading.CancellationTokenSource _cpuStressCts;

        // RAM Stress Test states
        private bool _isRamStressRunning = false;
        private TimeSpan _ramStressRemainingTime;
        private string _ramStressRemainingDisplay = "00:00";
        private int _ramStressTargetPercent = 85; // 40% to 95%
        private int _ramStressSelectedDurationIndex = 1; // Default to 1 min
        private double? _overrideRamDurationSeconds = null;
        private bool _isRamStressInfinite = false;
        private bool _ramStressCompleted = false;
        private string _ramStressResults = "";
        private ObservableCollection<string> _ramStressLog = new ObservableCollection<string>();
        private System.Threading.CancellationTokenSource _ramStressCts;
        private double _ramStressAllocatedGb = 0;
        private double _ramStressSpeedGbps = 0;
        private int _ramStressPassCount = 0;
        private int _ramStressErrorCount = 0;
        private string _ramStressStatusText = "Bereit zum RAM-Stresstest";
        private readonly List<byte[]> _ramBlocks = new List<byte[]>();
        private System.Threading.CancellationTokenSource _furMarkAutoStopCts;

        // Order & Online Monitoring (LHM API on Port 8085)
        private string _orderNumber = "";
        private LhmHttpServer _lhmHttpServer;
        private bool _isOnlineMonitoringActive = false;
        private string _onlineMonitoringStatusText = "Initialisiere Server...";

        // UDP Autodiscovery & Hardware-Check Connection
        private UdpDiscoveryClient _udpDiscoveryClient;
        private bool _isHardwareCheckConnected = false;
        private string _hardwareCheckServerInfo = "";
        private string _hardwareCheckStatusDisplay = "Suche Hardware-Check Server...";

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
            StartCpuStressCommand = new RelayCommand(ExecuteStartCpuStress, () => !IsCpuStressRunning);
            StopCpuStressCommand = new RelayCommand(ExecuteStopCpuStress, () => IsCpuStressRunning);
            StartRamStressCommand = new RelayCommand(ExecuteStartRamStress, () => !IsRamStressRunning);
            StopRamStressCommand = new RelayCommand(ExecuteStopRamStress, () => IsRamStressRunning);
            StartOnlineMonitoringCommand = new RelayCommand(StartOnlineMonitoring);
            StopOnlineMonitoringCommand = new RelayCommand(StopOnlineMonitoring);
            ToggleOnlineMonitoringCommand = new RelayCommand(ToggleOnlineMonitoring);
            LaunchFurMarkDonutCommand = new RelayCommand(ExecuteLaunchFurMarkDirect);
            LaunchFurMarkGuiCommand = new RelayCommand(ExecuteLaunchFurMarkGui);

            // Detect admin privileges
            IsAdmin = IsRunningAsAdmin();

            // Fetch initial data
            UpdateDiagnostics();
            _tools = _launcherService.Tools;
            _currentTime = DateTime.Now.ToString("HH:mm:ss dddd, dd.MM.yyyy");
            _batteryInfo = BatteryService.GetBatteryInfo(_diagnostics?.BatteryData);
            _systemInfo = SystemInfoService.GetSystemInfo();

            // Setup timer to refresh data every second
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += TimerTick;
            _timer.Start();

            // Sitzungsüberwachung mit Kontext versorgen (für Absturzanalyse & Abmeldung bei Hardware-Check)
            InitializeSessionLifecycle();

            // Start API Server and UDP autodiscovery immediately on application start
            StartOnlineMonitoring();
            StartUdpDiscovery();
        }

        public HardwareDiagnostics Diagnostics
        {
            get => _diagnostics;
            set => SetProperty(ref _diagnostics, value);
        }

        public List<TestTool> Tools
        {
            get => _tools;
            set
            {
                if (SetProperty(ref _tools, value))
                {
                    OnPropertyChanged(nameof(FurMarkTool));
                    OnPropertyChanged(nameof(IsFurMarkFound));
                }
            }
        }

        public TestTool FurMarkTool => _tools?.FirstOrDefault(t => t.Key == "FurMark");
        public bool IsFurMarkFound => FurMarkTool?.IsFound ?? false;

        public List<string> StressDurationOptions { get; } = new List<string>
        {
            "30 Sekunden",
            "1 Minute",
            "5 Minuten",
            "10 Minuten",
            "15 Minuten",
            "30 Minuten",
            "60 Minuten",
            "Unbegrenzt"
        };

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
                    OnPropertyChanged(nameof(IsGpuStressSelected));
                    OnPropertyChanged(nameof(IsRepairSelected));
                    OnPropertyChanged(nameof(IsInfoSelected));
                    OnPropertyChanged(nameof(IsBatteryTestSelected));
                    OnPropertyChanged(nameof(IsCpuStressSelected));
                    OnPropertyChanged(nameof(IsRamStressSelected));
                }
            }
        }

        public bool IsDashboardSelected => SelectedTab == 0;
        public bool IsToolsSelected => SelectedTab == 1;
        public bool IsGpuStressSelected => SelectedTab == 1;
        public bool IsRepairSelected => SelectedTab == 2;
        public bool IsInfoSelected => SelectedTab == 3;
        public bool IsBatteryTestSelected => SelectedTab == 4;
        public bool IsCpuStressSelected => SelectedTab == 5;
        public bool IsRamStressSelected => SelectedTab == 6;

        public bool IsRamStressRunning
        {
            get => _isRamStressRunning;
            set
            {
                if (SetProperty(ref _isRamStressRunning, value))
                {
                    StartRamStressCommand.RaiseCanExecuteChanged();
                    StopRamStressCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string RamStressRemainingDisplay
        {
            get => _ramStressRemainingDisplay;
            set => SetProperty(ref _ramStressRemainingDisplay, value);
        }

        public int RamStressTargetPercent
        {
            get => _ramStressTargetPercent;
            set => SetProperty(ref _ramStressTargetPercent, value);
        }

        public int RamStressSelectedDurationIndex
        {
            get => _ramStressSelectedDurationIndex;
            set => SetProperty(ref _ramStressSelectedDurationIndex, value);
        }

        public bool RamStressCompleted
        {
            get => _ramStressCompleted;
            set => SetProperty(ref _ramStressCompleted, value);
        }

        public string RamStressResults
        {
            get => _ramStressResults;
            set => SetProperty(ref _ramStressResults, value);
        }

        public ObservableCollection<string> RamStressLog
        {
            get => _ramStressLog;
            set => SetProperty(ref _ramStressLog, value);
        }

        public double RamStressAllocatedGb
        {
            get => _ramStressAllocatedGb;
            set
            {
                if (SetProperty(ref _ramStressAllocatedGb, value))
                {
                    OnPropertyChanged(nameof(RamStressAllocatedDisplay));
                }
            }
        }

        public string RamStressAllocatedDisplay => $"{RamStressAllocatedGb:F1} GB alloziert";

        public double RamStressSpeedGbps
        {
            get => _ramStressSpeedGbps;
            set
            {
                if (SetProperty(ref _ramStressSpeedGbps, value))
                {
                    OnPropertyChanged(nameof(RamStressSpeedDisplay));
                }
            }
        }

        public string RamStressSpeedDisplay => $"{RamStressSpeedGbps:F1} GB/s";

        public int RamStressPassCount
        {
            get => _ramStressPassCount;
            set => SetProperty(ref _ramStressPassCount, value);
        }

        public int RamStressErrorCount
        {
            get => _ramStressErrorCount;
            set => SetProperty(ref _ramStressErrorCount, value);
        }

        public string RamStressStatusText
        {
            get => _ramStressStatusText;
            set => SetProperty(ref _ramStressStatusText, value);
        }

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

        public string AdminStatusText => IsAdmin ? $"v{AppInfo.ShortVersion} | Admin Privileges Active" : $"v{AppInfo.ShortVersion} | No Admin Privileges (Restricted)";

        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(IsNotRecording));
                    OnPropertyChanged(nameof(ShowOrderNumberInfo));
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
        public RelayCommand StartRamStressCommand { get; }
        public RelayCommand StopRamStressCommand { get; }
        public RelayCommand StartOnlineMonitoringCommand { get; }
        public RelayCommand StopOnlineMonitoringCommand { get; }
        public RelayCommand ToggleOnlineMonitoringCommand { get; }
        public RelayCommand LaunchFurMarkDonutCommand { get; }
        public RelayCommand LaunchFurMarkGuiCommand { get; }

        public bool HasOrderNumber => !string.IsNullOrWhiteSpace(_orderNumber);

        public string OrderNumberDisplay => string.IsNullOrWhiteSpace(_orderNumber) ? "Keine Auftrags-Nr." : _orderNumber;

        public bool ShowOrderNumberInfo => IsRecording || HasOrderNumber;

        public string OrderNumber
        {
            get => _orderNumber;
            set
            {
                if (SetProperty(ref _orderNumber, value))
                {
                    OnPropertyChanged(nameof(HasOrderNumber));
                    OnPropertyChanged(nameof(OrderNumberDisplay));
                    OnPropertyChanged(nameof(ShowOrderNumberInfo));
                    _lhmHttpServer?.UpdateOrderNumber(_orderNumber);
                    if (IsOnlineMonitoringActive)
                    {
                        System.Threading.Tasks.Task.Run(() => LhmHttpServer.RegisterWithHardwareCheckAsync(_orderNumber));
                    }
                    _udpDiscoveryClient?.TriggerImmediateBroadcastAsync();
                }
            }
        }

        public bool IsHardwareCheckConnected
        {
            get => _isHardwareCheckConnected;
            set => SetProperty(ref _isHardwareCheckConnected, value);
        }

        public string HardwareCheckServerInfo
        {
            get => _hardwareCheckServerInfo;
            set => SetProperty(ref _hardwareCheckServerInfo, value);
        }

        public string HardwareCheckStatusDisplay
        {
            get => _hardwareCheckStatusDisplay;
            set => SetProperty(ref _hardwareCheckStatusDisplay, value);
        }

        public bool IsOnlineMonitoringActive
        {
            get => _isOnlineMonitoringActive;
            set => SetProperty(ref _isOnlineMonitoringActive, value);
        }

        public string OnlineMonitoringStatusText
        {
            get => _onlineMonitoringStatusText;
            set => SetProperty(ref _onlineMonitoringStatusText, value);
        }

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
                if (_batteryTestRemainingTime.Seconds == 0 && _batteryTestRemainingTime.TotalSeconds < _batteryTestTotalDuration.TotalSeconds)
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
                if (!_isCpuStressInfinite)
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

            if (IsRamStressRunning)
            {
                if (!_isRamStressInfinite)
                {
                    _ramStressRemainingTime = _ramStressRemainingTime.Subtract(TimeSpan.FromSeconds(1));
                    RamStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _ramStressRemainingTime.Minutes + _ramStressRemainingTime.Hours * 60, _ramStressRemainingTime.Seconds);
                    
                    if (_ramStressRemainingTime.TotalSeconds <= 0)
                    {
                        FinishRamStress(completed: true);
                    }
                }
                else
                {
                    // Count up for infinite mode
                    _ramStressRemainingTime = _ramStressRemainingTime.Add(TimeSpan.FromSeconds(1));
                    RamStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _ramStressRemainingTime.Minutes + _ramStressRemainingTime.Hours * 60, _ramStressRemainingTime.Seconds);
                }

                // Periodically log diagnostics (every 5 seconds)
                if (DateTime.Now.Second % 5 == 0)
                {
                    string ramUsage = Diagnostics?.RamUsageDisplay ?? "--";
                    RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - RAM: {1} ({2:F1}%) | Speed: {3:F1} GB/s | Zyklen: {4} | Fehler: {5}", 
                        DateTime.Now, ramUsage, Diagnostics?.RamPercent ?? 0, RamStressSpeedGbps, RamStressPassCount, RamStressErrorCount));
                }
            }
        }

        private void UpdateDiagnostics()
        {
            Diagnostics = _monitorService.GetDiagnostics(IsCpuStressRunning, CpuStressSelectedThreads);

            if (Diagnostics != null)
            {
                IsLhmDriverBlocked = Diagnostics.IsLhmDriverBlocked;
                HasDriverError = IsLhmDriverBlocked;
            }
            else
            {
                IsLhmDriverBlocked = false;
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
            ShowStatus("Laufwerke & USB-Sticks nach Diagnose-Tools durchsucht.", false);
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
            RunRepairCore(key, out _);
        }

        private bool RunRepairCore(string key, out string message)
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
                    message = "Unknown repair command";
                    ShowStatus(message, true);
                    return false;
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
                message = $"Started {description} in cmd window.";
                ShowStatus(message, false);
                return true;
            }
            catch (Exception ex)
            {
                message = $"Failed to start repair command: {ex.Message}";
                ShowStatus(message, true);
                return false;
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
            if (IsRecording) return;

            _recordedData.Clear();
            _recordingStartTime = DateTime.Now;
            IsRecording = true;
            RecordingTimeDisplay = "00:00:00";
            OnPropertyChanged(nameof(RecordedSamplesCount));

            // Start LHM Online Monitoring Server on port 8085 and register with /Hardware-Check
            StartOnlineMonitoring();

            if (!string.IsNullOrWhiteSpace(OrderNumber))
            {
                ShowStatus($"Werte-Aufzeichnung für Auftrag '{OrderNumber}' gestartet.", false);
            }
            else
            {
                ShowStatus("Werte-Aufzeichnung & LHM Online-Monitoring (Port 8085) gestartet.", false);
            }
        }

        private void ExecuteStopRecording()
        {
            if (!IsRecording) return;
            IsRecording = false;
            ShowStatus("Werte-Aufzeichnung beendet.", false);
        }

        public void StartOnlineMonitoring()
        {
            if (_lhmHttpServer == null)
            {
                _lhmHttpServer = new LhmHttpServer(() => Diagnostics, () => SystemInfo, () => BatteryInfo);
                _lhmHttpServer.StressStatusProvider = GetCurrentStressStatus;
                _lhmHttpServer.OnStartCpuStress = (duration, threads) => RemoteStartCpuStress(duration, threads);
                _lhmHttpServer.OnStopCpuStress = () => RemoteStopCpuStress();
                _lhmHttpServer.OnStartRamStress = (duration, percent) => RemoteStartRamStress(duration, percent);
                _lhmHttpServer.OnStopRamStress = () => RemoteStopRamStress();
                _lhmHttpServer.OnLaunchFurMarkDirect = () => RemoteLaunchFurMarkDirect();
                _lhmHttpServer.OnLaunchFurMarkDirectWithDuration = (duration) => RemoteLaunchFurMarkDirect(duration);
                _lhmHttpServer.OnLaunchFurMarkGui = () => RemoteLaunchFurMarkGui();
                _lhmHttpServer.OnStopFurMark = () => RemoteStopFurMark();
                _lhmHttpServer.OnStartFullStress = (duration) => RemoteStartFullStress(duration);
                _lhmHttpServer.OnStopAllStress = () => RemoteStopAllStress();
                _lhmHttpServer.OnStartRecording = (orderNo) => RemoteStartRecording(orderNo);
                _lhmHttpServer.OnStopRecording = () => RemoteStopRecording();
                _lhmHttpServer.OnSetOrderNumber = (orderNo) => RemoteSetOrderNumber(orderNo);
                _lhmHttpServer.RemoteControl = this;
                _lhmHttpServer.OnExitApplication = () =>
                {
                    SessionLifecycleService.MarkCleanExit("remote_exit");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                        {
                            desktop.Shutdown();
                        }
                        else
                        {
                            Environment.Exit(0);
                        }
                    });
                };
            }

            bool started = _lhmHttpServer.Start(OrderNumber);
            IsOnlineMonitoringActive = _lhmHttpServer.IsRunning;
            if (started)
            {
                string localIp = LhmHttpServer.GetPrimaryLocalIp();
                OnlineMonitoringStatusText = IsHardwareCheckConnected
                    ? $"Port 8085 aktiv • Verbunden mit Hardware-Check ({HardwareCheckServerInfo}:5085)"
                    : $"Port 8085 aktiv (http://{localIp}:8085/data.json)";
            }
            else
            {
                OnlineMonitoringStatusText = "Port 8085 konnte nicht gestartet werden";
            }
        }

        public void StartUdpDiscovery()
        {
            if (_udpDiscoveryClient == null)
            {
                _udpDiscoveryClient = new UdpDiscoveryClient(
                    () => OrderNumber,
                    () => SystemInfo?.FullSystemName ?? Environment.MachineName,
                    state =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            IsHardwareCheckConnected = state.IsConnected;
                            HardwareCheckServerInfo = state.ServerHost;
                            HardwareCheckStatusDisplay = state.StatusDisplay;
                            if (state.IsConnected)
                            {
                                OnlineMonitoringStatusText = $"Port 8085 aktiv • Hardware-Check ({state.ServerHost}:{state.ServerPort})";
                            }
                            else
                            {
                                string localIp = LhmHttpServer.GetPrimaryLocalIp();
                                OnlineMonitoringStatusText = $"Port 8085 aktiv (http://{localIp}:8085/data.json) • Suche Server...";
                            }
                        });
                    });
            }
            _udpDiscoveryClient.Start();
        }

        public TelemetryStressStatus GetCurrentStressStatus()
        {
            return new TelemetryStressStatus
            {
                IsCpuStressRunning = IsCpuStressRunning,
                CpuStressRemaining = CpuStressRemainingDisplay,
                CpuStressThreads = CpuStressSelectedThreads,
                IsRamStressRunning = IsRamStressRunning,
                RamStressRemaining = RamStressRemainingDisplay,
                RamStressAllocatedGb = RamStressAllocatedGb,
                RamStressPassCount = RamStressPassCount,
                RamStressErrorCount = RamStressErrorCount,
                IsGpuStressRunning = _launcherService.IsFurMarkRunning(),
                IsFurMarkFound = IsFurMarkFound,
                IsRecordingRunning = IsRecording,
                RecordingDuration = RecordingTimeDisplay,
                RecordedSamplesCount = RecordedSamplesCount,
                IsServerConnected = IsHardwareCheckConnected,
                ServerHost = HardwareCheckServerInfo,
                ServerPort = 5085,
                SessionId = SessionLifecycleService.SessionId,
                SessionStartedUtc = SessionLifecycleService.SessionStartedUtc.ToString("o")
            };
        }

        public void RemoteStartCpuStress(int? durationMinutes = null, int? threads = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (threads.HasValue && threads.Value > 0)
                {
                    CpuStressSelectedThreads = Math.Min(threads.Value, Environment.ProcessorCount * 2);
                }

                if (durationMinutes.HasValue)
                {
                    if (durationMinutes.Value <= 0)
                    {
                        _overrideCpuDurationSeconds = double.MaxValue;
                        CpuStressSelectedDurationIndex = 7; // Unbegrenzt
                    }
                    else
                    {
                        _overrideCpuDurationSeconds = durationMinutes.Value * 60.0;
                        if (durationMinutes.Value == 1) CpuStressSelectedDurationIndex = 1;
                        else if (durationMinutes.Value == 5) CpuStressSelectedDurationIndex = 2;
                        else if (durationMinutes.Value == 10) CpuStressSelectedDurationIndex = 3;
                        else if (durationMinutes.Value == 15) CpuStressSelectedDurationIndex = 4;
                        else if (durationMinutes.Value == 30) CpuStressSelectedDurationIndex = 5;
                        else if (durationMinutes.Value == 60) CpuStressSelectedDurationIndex = 6;
                    }
                }
                else
                {
                    _overrideCpuDurationSeconds = null;
                }

                if (IsCpuStressRunning)
                {
                    ExecuteStopCpuStress();
                }
                ExecuteStartCpuStress();
                ShowStatus("CPU-Stresstest über Remote gestartet.", false);
            });
        }

        public void RemoteStopCpuStress()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsCpuStressRunning)
                {
                    ExecuteStopCpuStress();
                    ShowStatus("CPU-Stresstest über Remote gestoppt.", false);
                }
            });
        }

        public void RemoteStartRamStress(int? durationMinutes = null, int? targetPercent = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (targetPercent.HasValue && targetPercent.Value >= 30 && targetPercent.Value <= 95)
                {
                    RamStressTargetPercent = targetPercent.Value;
                }

                if (durationMinutes.HasValue)
                {
                    if (durationMinutes.Value <= 0)
                    {
                        _overrideRamDurationSeconds = double.MaxValue;
                        RamStressSelectedDurationIndex = 7; // Unbegrenzt
                    }
                    else
                    {
                        _overrideRamDurationSeconds = durationMinutes.Value * 60.0;
                        if (durationMinutes.Value == 1) RamStressSelectedDurationIndex = 1;
                        else if (durationMinutes.Value == 5) RamStressSelectedDurationIndex = 2;
                        else if (durationMinutes.Value == 10) RamStressSelectedDurationIndex = 3;
                        else if (durationMinutes.Value == 15) RamStressSelectedDurationIndex = 4;
                        else if (durationMinutes.Value == 30) RamStressSelectedDurationIndex = 5;
                        else if (durationMinutes.Value == 60) RamStressSelectedDurationIndex = 6;
                    }
                }
                else
                {
                    _overrideRamDurationSeconds = null;
                }

                if (IsRamStressRunning)
                {
                    ExecuteStopRamStress();
                }
                ExecuteStartRamStress();
                ShowStatus("RAM-Stresstest über Remote gestartet.", false);
            });
        }

        public void RemoteStopRamStress()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsRamStressRunning)
                {
                    ExecuteStopRamStress();
                    ShowStatus("RAM-Stresstest über Remote gestoppt.", false);
                }
            });
        }

        public void RemoteLaunchFurMarkDirect(int? durationMinutes = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _furMarkAutoStopCts?.Cancel();
                    _furMarkAutoStopCts?.Dispose();
                }
                catch { }
                _furMarkAutoStopCts = null;

                ExecuteLaunchFurMarkDirect();

                if (durationMinutes.HasValue && durationMinutes.Value > 0)
                {
                    _furMarkAutoStopCts = new System.Threading.CancellationTokenSource();
                    var token = _furMarkAutoStopCts.Token;
                    int delayMs = durationMinutes.Value * 60 * 1000;
                    System.Threading.Tasks.Task.Delay(delayMs, token).ContinueWith(t =>
                    {
                        if (!t.IsCanceled)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                RemoteStopFurMark();
                                ShowStatus($"FurMark nach {durationMinutes.Value} Min. automatisch beendet.", false);
                            });
                        }
                    }, token);

                    ShowStatus($"FurMark 3D Donut über Remote gestartet (Dauer: {durationMinutes.Value} Min.).", false);
                }
                else
                {
                    ShowStatus("FurMark 3D Donut über Remote gestartet (Unbegrenzt).", false);
                }
            });
        }

        public void RemoteLaunchFurMarkGui()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ExecuteLaunchFurMarkGui();
                ShowStatus("FurMark GUI über Remote geöffnet.", false);
            });
        }

        public void RemoteStopFurMark()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _furMarkAutoStopCts?.Cancel();
                    _furMarkAutoStopCts?.Dispose();
                }
                catch { }
                _furMarkAutoStopCts = null;

                _launcherService.StopFurMark(out _);
                ShowStatus("FurMark über Remote beendet.", false);
            });
        }

        public void RemoteStartFullStress(int? durationMinutes = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RemoteStartCpuStress(durationMinutes);
                RemoteLaunchFurMarkDirect(durationMinutes);
                ShowStatus($"Volllast-Stresstest (CPU + GPU) über Remote gestartet ({(durationMinutes.HasValue && durationMinutes.Value > 0 ? durationMinutes.Value + " Min." : "Unbegrenzt")}).", false);
            });
        }

        public void RemoteStopAllStress()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _furMarkAutoStopCts?.Cancel();
                    _furMarkAutoStopCts?.Dispose();
                }
                catch { }
                _furMarkAutoStopCts = null;

                if (IsCpuStressRunning) ExecuteStopCpuStress();
                if (IsRamStressRunning) ExecuteStopRamStress();
                _launcherService.StopFurMark(out _);
                ShowStatus("Alle Stresstests über Remote beendet.", false);
            });
        }

        public void RemoteStartRecording(string orderNumber = null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsRecording) return;
                if (!string.IsNullOrWhiteSpace(orderNumber))
                {
                    OrderNumber = orderNumber.Trim();
                }
                _recordedData.Clear();
                _recordingStartTime = DateTime.Now;
                IsRecording = true;
                RecordingTimeDisplay = "00:00:00";
                OnPropertyChanged(nameof(RecordedSamplesCount));
                ShowStatus($"Werte-Aufzeichnung über Remote gestartet (Auftrag: {OrderNumber}).", false);
            });
        }

        public void RemoteStopRecording()
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (IsRecording)
                {
                    ExecuteStopRecording();
                    ShowStatus("Werte-Aufzeichnung über Remote beendet.", false);
                }
            });
        }

        public void RemoteSetOrderNumber(string orderNumber)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                OrderNumber = orderNumber?.Trim() ?? "";
                ShowStatus($"Auftragsnummer über Remote gesetzt: {OrderNumber}", false);
                _udpDiscoveryClient?.TriggerImmediateBroadcastAsync();
            });
        }

        public void StopOnlineMonitoring()
        {
            _lhmHttpServer?.Stop();
            IsOnlineMonitoringActive = false;
            OnlineMonitoringStatusText = "Inaktiv";
        }

        public void ToggleOnlineMonitoring()
        {
            if (IsOnlineMonitoringActive)
                StopOnlineMonitoring();
            else
                StartOnlineMonitoring();
        }

        public void ExecuteLaunchFurMarkDirect()
        {
            if (_launcherService.LaunchFurMarkDirect(out string error))
            {
                ShowStatus("FurMark 3D-Donut Stresstest direkt gestartet!", false);
            }
            else
            {
                ShowStatus(error ?? "Fehler beim Starten von FurMark.", true);
            }
        }

        public void ExecuteLaunchFurMarkGui()
        {
            if (_launcherService.LaunchFurMarkGui(out string error))
            {
                ShowStatus("FurMark Einstellungen (GUI) geöffnet.", false);
            }
            else
            {
                ShowStatus(error ?? "Fehler beim Starten von FurMark.", true);
            }
        }

        private void ExecuteStartBatteryTest()
        {
            StartBatteryTestCore(null, true);
        }

        private bool StartBatteryTestCore(int? durationMinutes, bool openVideo)
        {
            if (IsBatteryTestRunning) return true;

            var info = BatteryService.GetBatteryInfo(Diagnostics?.BatteryData);
            if (!info.IsPresent)
            {
                ShowStatus("Keine Batterie erkannt. Der Stresstest kann nur auf Laptops durchgeführt werden.", true);
                return false;
            }

            int minutes = durationMinutes.HasValue && durationMinutes.Value > 0 ? Math.Min(durationMinutes.Value, 600) : 30;
            _batteryTestTotalDuration = TimeSpan.FromMinutes(minutes);
            _batteryTestStartTime = DateTime.Now;

            BatteryTestLog.Clear();
            BatteryTestStartPercent = info.CurrentChargePercent;
            BatteryTestCurrentPercent = info.CurrentChargePercent;
            _batteryTestRemainingTime = _batteryTestTotalDuration;
            BatteryTestRemainingDisplay = string.Format("{0:D2}:00", minutes);
            BatteryTestCompleted = false;
            BatteryTestResults = null;
            IsBatteryTestRunning = true;

            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Batterietest gestartet bei {1}% Ladung (Dauer: {2} Min.).", DateTime.Now, BatteryTestStartPercent, minutes));
            if (info.IsConnectedToAc)
            {
                BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Warnung: Ladekabel ist angeschlossen. Für verlässliche Testergebnisse bitte das Kabel trennen!", DateTime.Now));
            }

            if (!openVideo)
            {
                ShowStatus("Akkutest gestartet (ohne Video-Last).", false);
                return true;
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
            return true;
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

            TimeSpan elapsed = _batteryTestTotalDuration.Subtract(_batteryTestRemainingTime);
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            double hours = elapsed.TotalHours;
            double dischargeRate = hours > 0 ? drain / hours : 0;
            double estLife = (dischargeRate > 0 && BatteryTestCurrentPercent > 0) ? BatteryTestCurrentPercent / dischargeRate : 0;

            string statusText = completed ? "vollständig beendet" : "abgebrochen";
            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest {1} nach {2} Min. & {3} Sek.", 
                DateTime.Now, statusText, (int)elapsed.TotalMinutes, elapsed.Seconds));
            BatteryTestLog.Add(string.Format("[{0:HH:mm:ss}] - Endladung: {1}% | Gesamtverbrauch: {2}%", 
                DateTime.Now, BatteryTestCurrentPercent, drain));

            if (completed)
            {
                BatteryTestResults = string.Format(
                    "Testergebnis:\n" +
                    "• Batterie-Entladung: {0:F0}% in {3:F0} Minuten\n" +
                    "• Hochgerechnete Entladungsrate: {1:F1}% pro Stunde\n" +
                    "• Geschätzte Restlaufzeit unter Last: {2:F1} Stunden",
                    drain, dischargeRate, estLife, _batteryTestTotalDuration.TotalMinutes);
                ShowStatus("Akkutest erfolgreich beendet!", false);
            }
            else
            {
                BatteryTestResults = string.Format(
                    "Testergebnis (Abgebrochen):\n" +
                    "• Testdauer: {0:D2} Min. {1:D2} Sek.\n" +
                    "• Entladung: {2:F0}%\n" +
                    "• Laufzeitprognose ungenau aufgrund des Abbruchs.",
                    (int)elapsed.TotalMinutes, elapsed.Seconds, drain);
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
                            WriteBatteryReport(filePath);

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

        private void WriteBatteryReport(string filePath)
        {
            string cpu = Diagnostics != null ? Diagnostics.CpuName : "Unknown CPU";
            string gpu = Diagnostics != null ? Diagnostics.GpuName : "Unknown GPU";
            string ram = Diagnostics != null ? Diagnostics.RamUsageDisplay : "Unknown RAM";

            TimeSpan elapsed = _batteryTestTotalDuration.Subtract(_batteryTestRemainingTime);
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            DateTime start = IsBatteryTestRunning || BatteryTestCompleted ? _batteryTestStartTime : DateTime.Now.Subtract(elapsed);

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
        }

        private void ExecuteStartCpuStress()
        {
            if (IsCpuStressRunning) return;

            IsCpuStressRunning = true;
            CpuStressCompleted = false;
            CpuStressResults = "";
            CpuStressLog.Clear();

            // Resolve duration
            double durationSeconds = 60;
            string durationText = "1 Minute";

            if (_overrideCpuDurationSeconds.HasValue)
            {
                durationSeconds = _overrideCpuDurationSeconds.Value;
                if (durationSeconds == double.MaxValue)
                {
                    durationText = "Unbegrenzt";
                }
                else
                {
                    int mins = (int)Math.Round(durationSeconds / 60.0);
                    durationText = mins > 0 ? $"{mins} Minuten" : $"{durationSeconds:F0} Sekunden";
                }
            }
            else
            {
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
                        durationSeconds = 900;
                        durationText = "15 Minuten";
                        break;
                    case 5:
                        durationSeconds = 1800;
                        durationText = "30 Minuten";
                        break;
                    case 6:
                        durationSeconds = 3600;
                        durationText = "60 Minuten";
                        break;
                    case 7:
                    default:
                        durationSeconds = double.MaxValue;
                        durationText = "Unbegrenzt";
                        break;
                }
            }

            _isCpuStressInfinite = (durationSeconds == double.MaxValue);
            if (_isCpuStressInfinite)
            {
                _cpuStressRemainingTime = TimeSpan.Zero;
                CpuStressRemainingDisplay = "00:00 (Unbegrenzt)";
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

        private void ExecuteStartRamStress()
        {
            if (IsRamStressRunning) return;

            IsRamStressRunning = true;
            RamStressCompleted = false;
            RamStressResults = "";
            RamStressPassCount = 0;
            RamStressErrorCount = 0;
            RamStressAllocatedGb = 0;
            RamStressSpeedGbps = 0;
            RamStressStatusText = "Reserviere und fülle Arbeitsspeicher...";
            RamStressLog.Clear();

            double durationSeconds = 60;
            string durationText = "1 Minute";

            if (_overrideRamDurationSeconds.HasValue)
            {
                durationSeconds = _overrideRamDurationSeconds.Value;
                if (durationSeconds == double.MaxValue)
                {
                    durationText = "Unbegrenzt";
                }
                else
                {
                    int mins = (int)Math.Round(durationSeconds / 60.0);
                    durationText = mins > 0 ? $"{mins} Minuten" : $"{durationSeconds:F0} Sekunden";
                }
            }
            else
            {
                switch (RamStressSelectedDurationIndex)
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
                        durationSeconds = 900;
                        durationText = "15 Minuten";
                        break;
                    case 5:
                        durationSeconds = 1800;
                        durationText = "30 Minuten";
                        break;
                    case 6:
                        durationSeconds = 3600;
                        durationText = "60 Minuten";
                        break;
                    case 7:
                    default:
                        durationSeconds = double.MaxValue;
                        durationText = "Unbegrenzt";
                        break;
                }
            }

            _isRamStressInfinite = (durationSeconds == double.MaxValue);
            if (_isRamStressInfinite)
            {
                _ramStressRemainingTime = TimeSpan.Zero;
                RamStressRemainingDisplay = "00:00 (Unbegrenzt)";
            }
            else
            {
                _ramStressRemainingTime = TimeSpan.FromSeconds(durationSeconds);
                RamStressRemainingDisplay = string.Format("{0:D2}:{1:D2}", _ramStressRemainingTime.Minutes + _ramStressRemainingTime.Hours * 60, _ramStressRemainingTime.Seconds);
            }

            RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - RAM-Stresstest gestartet (Ziel: {1}% System-RAM, Dauer: {2})", 
                DateTime.Now, RamStressTargetPercent, durationText));

            _ramStressCts = new System.Threading.CancellationTokenSource();
            var token = _ramStressCts.Token;

            System.Threading.Tasks.Task.Run(() => RunRamStressTask(token));

            StartRamStressCommand.RaiseCanExecuteChanged();
            StopRamStressCommand.RaiseCanExecuteChanged();
        }

        private void RunRamStressTask(System.Threading.CancellationToken token)
        {
            const int chunkSize = 64 * 1024 * 1024; // 64 MB per block
            lock (_ramBlocks)
            {
                _ramBlocks.Clear();
            }

            double totalSystemRamGb = (Diagnostics != null && Diagnostics.RamTotalGb > 0) ? Diagnostics.RamTotalGb : 16.0;
            double currentUsedGb = (Diagnostics != null && Diagnostics.RamUsedGb > 0) ? Diagnostics.RamUsedGb : 4.0;
            
            // Calculate target GB to allocate:
            // Desired total RAM usage is RamStressTargetPercent (e.g. 85%) of totalSystemRamGb.
            // Leave at least 1.2 GB headroom for Windows kernel & UI responsiveness.
            double targetUsedGb = totalSystemRamGb * (RamStressTargetPercent / 100.0);
            double maxAllowedUsedGb = Math.Max(0.5, totalSystemRamGb - 1.2);
            if (targetUsedGb > maxAllowedUsedGb)
            {
                targetUsedGb = maxAllowedUsedGb;
            }

            double toAllocateGb = Math.Max(0.5, targetUsedGb - currentUsedGb);
            int targetBlocks = (int)Math.Max(4, (toAllocateGb * 1024.0 * 1024.0 * 1024.0) / chunkSize);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - Allokiere ca. {1:F1} GB ({2} Blöcke à 64 MB)...",
                    DateTime.Now, toAllocateGb, targetBlocks));
            });

            // Allocation phase
            for (int i = 0; i < targetBlocks; i++)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    byte[] block = new byte[chunkSize];
                    // Touch/fill memory so OS commits physical pages
                    Array.Fill<byte>(block, 0x55);
                    lock (_ramBlocks)
                    {
                        _ramBlocks.Add(block);
                    }
                }
                catch (OutOfMemoryException)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - Maximale Speicherallokation erreicht (Sicherheitsgrenze).", DateTime.Now));
                    });
                    break;
                }

                if ((i + 1) % 4 == 0 || i == targetBlocks - 1)
                {
                    double currentAllocGb = ((i + 1) * 64.0) / 1024.0;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        RamStressAllocatedGb = currentAllocGb;
                    });
                }
            }

            double finalAllocGb;
            int blockCount;
            lock (_ramBlocks)
            {
                blockCount = _ramBlocks.Count;
                finalAllocGb = (blockCount * 64.0) / 1024.0;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RamStressAllocatedGb = finalAllocGb;
                RamStressStatusText = $"Aktiv (Gefüllt: {finalAllocGb:F1} GB) - Stresstest läuft";
                RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - {1:F1} GB reserviert ({2} Blöcke). Starte Multi-Thread Prüfzyklen...",
                    DateTime.Now, finalAllocGb, blockCount));
            });

            // Continuous Stress & Integrity testing loop
            byte[] patterns = new byte[] { 0xAA, 0x55, 0xCC, 0x33, 0xFF, 0x00 };
            int patternIndex = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long totalBytesProcessed = 0;

            while (!token.IsCancellationRequested)
            {
                byte writePattern = patterns[patternIndex % patterns.Length];
                byte verifyExpected = writePattern;
                patternIndex++;

                long cycleBytes = 0;
                int cycleErrors = 0;

                // Test blocks in parallel across CPU threads
                System.Threading.Tasks.Parallel.For(0, blockCount, new System.Threading.Tasks.ParallelOptions { CancellationToken = token }, (bIndex, loopState) =>
                {
                    if (token.IsCancellationRequested)
                    {
                        loopState.Stop();
                        return;
                    }

                    byte[] b;
                    lock (_ramBlocks)
                    {
                        if (bIndex < _ramBlocks.Count)
                            b = _ramBlocks[bIndex];
                        else
                            return;
                    }

                    // Write phase
                    for (int j = 0; j < b.Length; j += 64)
                    {
                        b[j] = writePattern;
                        b[j + 1] = writePattern;
                        b[j + 2] = writePattern;
                        b[j + 3] = writePattern;
                    }

                    // Verify phase
                    for (int j = 0; j < b.Length; j += 64)
                    {
                        if (b[j] != verifyExpected || b[j + 1] != verifyExpected)
                        {
                            System.Threading.Interlocked.Increment(ref cycleErrors);
                        }
                    }

                    System.Threading.Interlocked.Add(ref cycleBytes, b.Length);
                });

                if (token.IsCancellationRequested) break;

                totalBytesProcessed += cycleBytes;
                double seconds = sw.Elapsed.TotalSeconds;
                double speedGbps = seconds > 0 ? (totalBytesProcessed / (1024.0 * 1024.0 * 1024.0)) / seconds : 0;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    RamStressPassCount++;
                    if (cycleErrors > 0)
                    {
                        RamStressErrorCount += cycleErrors;
                        RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - WARNUNG: {1} Bitfehler in Zyklus {2} festgestellt!",
                            DateTime.Now, cycleErrors, RamStressPassCount));
                    }
                    RamStressSpeedGbps = speedGbps;
                });

                System.Threading.Thread.Sleep(20);
            }
        }

        private void ExecuteStopRamStress()
        {
            if (!IsRamStressRunning) return;
            RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - Stresstest manuell abgebrochen.", DateTime.Now));
            FinishRamStress(completed: false);
        }

        private void FinishRamStress(bool completed)
        {
            if (!IsRamStressRunning) return;

            try
            {
                _ramStressCts?.Cancel();
                _ramStressCts?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cancel RAM stress: {ex.Message}");
            }
            _ramStressCts = null;

            // Free all memory immediately
            lock (_ramBlocks)
            {
                _ramBlocks.Clear();
            }
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();

            IsRamStressRunning = false;
            RamStressCompleted = true;
            RamStressAllocatedGb = 0;
            RamStressSpeedGbps = 0;

            if (completed)
            {
                RamStressStatusText = "Test erfolgreich abgeschlossen";
                RamStressResults = $"Test abgeschlossen! {RamStressPassCount} Zyklen durchgeführt. Fehler: {RamStressErrorCount}";
                RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - RAM-Stresstest erfolgreich beendet ({1} Zyklen, {2} Fehler). Speicher freigegeben.",
                    DateTime.Now, RamStressPassCount, RamStressErrorCount));
            }
            else
            {
                RamStressStatusText = "Test abgebrochen";
                RamStressResults = "Test abgebrochen. Speicher freigegeben.";
                RamStressLog.Add(string.Format("[{0:HH:mm:ss}] - Speicher freigegeben.", DateTime.Now));
            }

            StartRamStressCommand.RaiseCanExecuteChanged();
            StopRamStressCommand.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            SessionLifecycleService.MarkCleanExit("window_closed");
            _timer?.Stop();
            try
            {
                _cpuStressCts?.Cancel();
                _cpuStressCts?.Dispose();
            }
            catch {}
            try
            {
                _ramStressCts?.Cancel();
                _ramStressCts?.Dispose();
            }
            catch {}
            lock (_ramBlocks)
            {
                _ramBlocks.Clear();
            }
            try
            {
                _furMarkAutoStopCts?.Cancel();
                _furMarkAutoStopCts?.Dispose();
            }
            catch {}
            try
            {
                _udpDiscoveryClient?.Stop();
                _udpDiscoveryClient?.Dispose();
            }
            catch {}
            try
            {
                _lhmHttpServer?.Stop();
                _lhmHttpServer?.Dispose();
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
