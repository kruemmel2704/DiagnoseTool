using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace DiagnoseTool
{
    /// <summary>
    /// PE-Modus (DiagnosePE): Netzwerk-Einstellungen fuer LAN & WLAN mit
    /// Rueckmeldung (Verbindung, IP, Ping). Nur sichtbar, wenn IsPeMode true ist.
    /// </summary>
    public partial class MainViewModel
    {
        // -------- Zustand ------------------------------------------------
        private bool _isNetworkBusy;
        private string _networkStatusText = "Netzwerk-Einstellungen (nur PE-Edition).";
        private string _wifiPassword = "";
        private NetworkPeService.WifiNetwork _selectedWifi;
        private bool _isWlanAvailable;

        public ObservableCollection<NetworkPeService.AdapterInfo> NetworkAdapters { get; } = new();
        public ObservableCollection<NetworkPeService.WifiNetwork> WifiNetworks { get; } = new();

        public bool IsNetworkBusy
        {
            get => _isNetworkBusy;
            set
            {
                if (SetProperty(ref _isNetworkBusy, value))
                {
                    RefreshAdaptersCommand?.RaiseCanExecuteChanged();
                    ScanWifiCommand?.RaiseCanExecuteChanged();
                    ConnectWifiCommand?.RaiseCanExecuteChanged();
                    DisconnectWifiCommand?.RaiseCanExecuteChanged();
                    RenewDhcpCommand?.RaiseCanExecuteChanged();
                    PingTestCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public string NetworkStatusText
        {
            get => _networkStatusText;
            set => SetProperty(ref _networkStatusText, value);
        }

        public string WifiPassword
        {
            get => _wifiPassword;
            set => SetProperty(ref _wifiPassword, value);
        }

        public NetworkPeService.WifiNetwork SelectedWifi
        {
            get => _selectedWifi;
            set { if (SetProperty(ref _selectedWifi, value)) ConnectWifiCommand?.RaiseCanExecuteChanged(); }
        }

        public bool IsWlanAvailable
        {
            get => _isWlanAvailable;
            set => SetProperty(ref _isWlanAvailable, value);
        }

        // -------- Commands ----------------------------------------------
        public RelayCommand RefreshAdaptersCommand { get; private set; }
        public RelayCommand ScanWifiCommand { get; private set; }
        public RelayCommand ConnectWifiCommand { get; private set; }
        public RelayCommand DisconnectWifiCommand { get; private set; }
        public RelayCommand RenewDhcpCommand { get; private set; }
        public RelayCommand PingTestCommand { get; private set; }

        private void InitNetworkPe()
        {
            RefreshAdaptersCommand = new RelayCommand(ExecuteRefreshAdapters, () => !IsNetworkBusy);
            ScanWifiCommand        = new RelayCommand(ExecuteScanWifi, () => !IsNetworkBusy);
            ConnectWifiCommand     = new RelayCommand(ExecuteConnectWifi, () => !IsNetworkBusy && SelectedWifi != null);
            DisconnectWifiCommand  = new RelayCommand(ExecuteDisconnectWifi, () => !IsNetworkBusy);
            RenewDhcpCommand       = new RelayCommand(ExecuteRenewDhcp, () => !IsNetworkBusy);
            PingTestCommand        = new RelayCommand(ExecutePingTest, () => !IsNetworkBusy);

            if (!IsPeMode) return;

            // Beim Start im PE einmal Adapter laden und WLAN-Verfuegbarkeit pruefen.
            _ = Task.Run(() =>
            {
                bool wlan = NetworkPeService.IsWlanAvailable();
                var adapters = NetworkPeService.GetAdapters();
                Dispatcher.UIThread.Post(() =>
                {
                    IsWlanAvailable = wlan;
                    ReplaceAdapters(adapters);
                    NetworkStatusText = wlan
                        ? "Bereit. WLAN verfuegbar - 'WLAN suchen' antippen."
                        : "Bereit. Kein WLAN-Adapter erkannt (nur LAN verfuegbar).";
                });
            });
        }

        private void ReplaceAdapters(System.Collections.Generic.List<NetworkPeService.AdapterInfo> items)
        {
            NetworkAdapters.Clear();
            foreach (var a in items) NetworkAdapters.Add(a);
        }

        // -------- Aktionen ----------------------------------------------
        private void ExecuteRefreshAdapters()
        {
            RunNetworkTask("Adapter werden aktualisiert...", () =>
            {
                var adapters = NetworkPeService.GetAdapters();
                var (connected, ssid, ip) = (false, "", "-");
                if (IsWlanAvailable) (connected, ssid, ip) = NetworkPeService.GetWifiState();
                Dispatcher.UIThread.Post(() =>
                {
                    ReplaceAdapters(adapters);
                    NetworkStatusText = connected
                        ? $"Aktualisiert. WLAN verbunden: '{ssid}' (IP {ip})."
                        : $"Aktualisiert. {adapters.Count} Adapter.";
                });
            });
        }

        private void ExecuteScanWifi()
        {
            if (!IsWlanAvailable) { NetworkStatusText = "Kein WLAN-Adapter/Treiber vorhanden."; return; }
            RunNetworkTask("WLAN-Netze werden gesucht...", () =>
            {
                var nets = NetworkPeService.ScanWifi(out string msg);
                Dispatcher.UIThread.Post(() =>
                {
                    WifiNetworks.Clear();
                    foreach (var n in nets) WifiNetworks.Add(n);
                    if (WifiNetworks.Count > 0 && SelectedWifi == null) SelectedWifi = WifiNetworks[0];
                    NetworkStatusText = msg;
                });
            });
        }

        private void ExecuteConnectWifi()
        {
            var target = SelectedWifi;
            if (target == null) { NetworkStatusText = "Bitte zuerst ein WLAN auswaehlen."; return; }
            string pwd = WifiPassword ?? "";
            RunNetworkTask($"Verbinde mit '{target.Ssid}'...", () =>
            {
                bool ok = NetworkPeService.ConnectWifi(target.Ssid, pwd, target.Secured, out string status);
                var adapters = NetworkPeService.GetAdapters();
                Dispatcher.UIThread.Post(() =>
                {
                    ReplaceAdapters(adapters);
                    NetworkStatusText = (ok ? "OK: " : "Fehlgeschlagen: ") + status;
                    if (ok) WifiPassword = "";
                });
            });
        }

        private void ExecuteDisconnectWifi()
        {
            RunNetworkTask("WLAN wird getrennt...", () =>
            {
                NetworkPeService.DisconnectWifi(out string status);
                var adapters = NetworkPeService.GetAdapters();
                Dispatcher.UIThread.Post(() =>
                {
                    ReplaceAdapters(adapters);
                    NetworkStatusText = status;
                });
            });
        }

        private void ExecuteRenewDhcp()
        {
            RunNetworkTask("DHCP (LAN) wird erneuert...", () =>
            {
                NetworkPeService.RenewDhcp(out string status);
                var adapters = NetworkPeService.GetAdapters();
                Dispatcher.UIThread.Post(() =>
                {
                    ReplaceAdapters(adapters);
                    NetworkStatusText = status;
                });
            });
        }

        private void ExecutePingTest()
        {
            RunNetworkTask("Verbindung wird getestet (Ping)...", () =>
            {
                var (ok, msg) = NetworkPeService.PingTest("8.8.8.8");
                Dispatcher.UIThread.Post(() => NetworkStatusText = msg);
            });
        }

        // Hintergrund-Task mit Busy-Flag und Fehlerfang.
        private void RunNetworkTask(string busyText, Action work)
        {
            if (IsNetworkBusy) return;
            IsNetworkBusy = true;
            NetworkStatusText = busyText;
            _ = Task.Run(() =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => NetworkStatusText = "Fehler: " + ex.Message);
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => IsNetworkBusy = false);
                }
            });
        }
    }
}
