using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DiagnoseTool
{
    public class DiscoveryPacket
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "DIAGNOSE_TOOL_DISCOVER";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "2.3.0";

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = Environment.MachineName;

        [JsonPropertyName("orderNumber")]
        public string OrderNumber { get; set; } = "";

        [JsonPropertyName("host")]
        public string Host { get; set; } = "";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 8085;

        [JsonPropertyName("systemModel")]
        public string SystemModel { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
    }

    public class AckPacket
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("serverHost")]
        public string ServerHost { get; set; }

        [JsonPropertyName("serverPort")]
        public int ServerPort { get; set; } = 5085;

        [JsonPropertyName("clientHost")]
        public string ClientHost { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; }
    }

    public class DiscoveryServerConnectionState
    {
        public bool IsConnected { get; set; }
        public string ServerHost { get; set; } = "";
        public int ServerPort { get; set; } = 5085;
        public DateTime? LastAckTime { get; set; }
        public string StatusDisplay { get; set; } = "Suche Hardware-Check Server...";
    }

    public class UdpDiscoveryClient : IDisposable
    {
        public const int DiscoveryPort = 5087;

        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private Task _broadcastLoopTask;

        private readonly Func<string> _getOrderNumber;
        private readonly Func<string> _getSystemModel;
        private readonly Action<DiscoveryServerConnectionState> _onStateChanged;

        private readonly DiscoveryServerConnectionState _state = new DiscoveryServerConnectionState();
        private readonly object _stateLock = new object();

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        private string _cachedServerHost;
        private bool _isHttpScanning = false;

        public DiscoveryServerConnectionState CurrentState
        {
            get
            {
                lock (_stateLock)
                {
                    return new DiscoveryServerConnectionState
                    {
                        IsConnected = _state.IsConnected,
                        ServerHost = _state.ServerHost,
                        ServerPort = _state.ServerPort,
                        LastAckTime = _state.LastAckTime,
                        StatusDisplay = _state.StatusDisplay
                    };
                }
            }
        }

        public UdpDiscoveryClient(
            Func<string> getOrderNumber,
            Func<string> getSystemModel,
            Action<DiscoveryServerConnectionState> onStateChanged = null)
        {
            _getOrderNumber = getOrderNumber;
            _getSystemModel = getSystemModel;
            _onStateChanged = onStateChanged;
        }

        public void Start()
        {
            if (_cts != null) return;

            // Ensure firewall permits port 5087 UDP
            Task.Run(() => FirewallHelper.EnsureDiscoveryFirewallRule());

            _cts = new CancellationTokenSource();

            // Load previously remembered server host from registry
            _cachedServerHost = LoadServerHostFromRegistry();

            try
            {
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.EnableBroadcast = true;
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UdpDiscoveryClient] Bind on {DiscoveryPort} failed ({ex.Message}), creating ephemeral UDP client.");
                try
                {
                    _udpClient = new UdpClient();
                    _udpClient.EnableBroadcast = true;
                }
                catch (Exception bindEx)
                {
                    Debug.WriteLine($"[UdpDiscoveryClient] Fallback bind failed: {bindEx.Message}");
                    return;
                }
            }

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
            _broadcastLoopTask = Task.Run(() => BroadcastLoopAsync(_cts.Token));

            // Immediate initial broadcast & immediate HTTP probe
            Task.Run(SendBroadcastAsync);
            Task.Run(() => TryHttpDiscoverAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _udpClient?.Close();
            }
            catch { }
            finally
            {
                _udpClient?.Dispose();
                _udpClient = null;
                _cts?.Dispose();
                _cts = null;
            }
        }

        public async Task TriggerImmediateBroadcastAsync()
        {
            await SendBroadcastAsync();
        }

        private async Task BroadcastLoopAsync(CancellationToken ct)
        {
            // Initial fast HTTP discover
            _ = TryHttpDiscoverAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await SendBroadcastAsync();

                    bool connected;
                    lock (_stateLock)
                    {
                        connected = _state.IsConnected;
                        // Check if ACK is stale (> 90 seconds without ACK)
                        if (connected && _state.LastAckTime.HasValue &&
                            DateTime.UtcNow - _state.LastAckTime.Value > TimeSpan.FromSeconds(90))
                        {
                            _state.IsConnected = false;
                            _state.StatusDisplay = "Verbindung zu Hardware-Check unterbrochen (Suche...)";
                            NotifyStateChanged();
                        }
                    }

                    if (!connected)
                    {
                        // Docker-Proof HTTP Discovery & Subnet Probe
                        _ = TryHttpDiscoverAsync(ct);
                    }

                    // Wait interval: 8s if searching, 25s if connected keepalive
                    int delaySeconds = connected ? 25 : 8;
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UdpDiscoveryClient] BroadcastLoop error: {ex.Message}");
                    await Task.Delay(5000, ct);
                }
            }
        }

        public async Task SendBroadcastAsync()
        {
            if (_udpClient == null) return;

            try
            {
                string localIp = LhmHttpServer.GetPrimaryLocalIp();
                var packet = new DiscoveryPacket
                {
                    Type = "DIAGNOSE_TOOL_DISCOVER",
                    Version = "2.3.0",
                    MachineName = Environment.MachineName,
                    OrderNumber = _getOrderNumber?.Invoke() ?? "",
                    Host = localIp,
                    Port = 8085,
                    SystemModel = _getSystemModel?.Invoke() ?? "",
                    Timestamp = DateTime.UtcNow.ToString("o")
                };

                string json = JsonSerializer.Serialize(packet);
                byte[] bytes = Encoding.UTF8.GetBytes(json);

                var destinations = GetBroadcastTargets();
                foreach (var ep in destinations)
                {
                    try
                    {
                        await _udpClient.SendAsync(bytes, bytes.Length, ep);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[UdpDiscoveryClient] Send to {ep} failed: {ex.Message}");
                    }
                }

                Debug.WriteLine($"[UdpDiscoveryClient] Sent DIAGNOSE_TOOL_DISCOVER broadcast to {destinations.Count} targets.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UdpDiscoveryClient] SendBroadcastAsync error: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(ct);
                    string raw = Encoding.UTF8.GetString(result.Buffer);

                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeProp)) continue;

                    string type = typeProp.GetString();

                    if (string.Equals(type, "HARDWARE_CHECK_ACK", StringComparison.OrdinalIgnoreCase))
                    {
                        string sHost = root.TryGetProperty("serverHost", out var sh) ? sh.GetString() : result.RemoteEndPoint.Address.ToString();
                        int sPort = root.TryGetProperty("serverPort", out var sp) ? sp.GetInt32() : 5085;

                        lock (_stateLock)
                        {
                            _state.IsConnected = true;
                            _state.ServerHost = sHost;
                            _state.ServerPort = sPort;
                            _state.LastAckTime = DateTime.UtcNow;
                            _state.StatusDisplay = $"Hardware-Check Server verbunden ({sHost}:{sPort})";
                        }
                        NotifyStateChanged();
                        Debug.WriteLine($"[UdpDiscoveryClient] Received HARDWARE_CHECK_ACK from {sHost}:{sPort}");
                    }
                    else if (string.Equals(type, "HARDWARE_CHECK_PING", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.WriteLine($"[UdpDiscoveryClient] Received HARDWARE_CHECK_PING from {result.RemoteEndPoint}. Responding...");
                        _ = SendBroadcastAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[UdpDiscoveryClient] Receive error: {ex.Message}");
                }
            }
        }

        private void NotifyStateChanged()
        {
            try
            {
                var copy = CurrentState;
                _onStateChanged?.Invoke(copy);
            }
            catch { }
        }

        private List<IPEndPoint> GetBroadcastTargets()
        {
            var targets = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, DiscoveryPort),          // 255.255.255.255
                new IPEndPoint(IPAddress.Loopback, DiscoveryPort)            // 127.0.0.1
            };

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var u in ipProps.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            var ipBytes = u.Address.GetAddressBytes();
                            var maskBytes = u.IPv4Mask?.GetAddressBytes();
                            if (maskBytes != null && maskBytes.Length == 4)
                            {
                                var bcBytes = new byte[4];
                                for (int i = 0; i < 4; i++)
                                {
                                    bcBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                }
                                var bcIp = new IPAddress(bcBytes);
                                if (!targets.Exists(t => t.Address.Equals(bcIp)))
                                {
                                    targets.Add(new IPEndPoint(bcIp, DiscoveryPort));
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Also add cached server host directly (enables UDP unicast through Docker port forward)
            if (!string.IsNullOrWhiteSpace(_cachedServerHost) && IPAddress.TryParse(_cachedServerHost, out var hostIp))
            {
                if (!targets.Exists(t => t.Address.Equals(hostIp)))
                {
                    targets.Add(new IPEndPoint(hostIp, DiscoveryPort));
                }
            }

            return targets;
        }

        public async Task<bool> ProbeAndRegisterHttpAsync(string host, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            try
            {
                string localIp = LhmHttpServer.GetPrimaryLocalIp();
                var reqObj = new
                {
                    host = localIp,
                    port = 8085,
                    machineName = Environment.MachineName,
                    systemModel = _getSystemModel?.Invoke() ?? "",
                    orderNumber = _getOrderNumber?.Invoke() ?? "",
                    version = "2.3.0"
                };

                string json = JsonSerializer.Serialize(reqObj);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(900);

                string url = $"http://{host.Trim()}:5085/api/hardware/devices/discover";
                using var resp = await _http.PostAsync(url, content, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;

                    string sHost = root.TryGetProperty("serverHost", out var sh) && !string.IsNullOrWhiteSpace(sh.GetString())
                        ? sh.GetString()
                        : host.Trim();
                    int sPort = root.TryGetProperty("serverPort", out var sp) ? sp.GetInt32() : 5085;

                    lock (_stateLock)
                    {
                        _state.IsConnected = true;
                        _state.ServerHost = sHost;
                        _state.ServerPort = sPort;
                        _state.LastAckTime = DateTime.UtcNow;
                        _state.StatusDisplay = $"Hardware-Check Server verbunden ({sHost}:{sPort})";
                    }

                    _cachedServerHost = sHost;
                    SaveServerHostToRegistry(sHost);
                    NotifyStateChanged();
                    Debug.WriteLine($"[UdpDiscoveryClient] HTTP Discover success with {sHost}:{sPort}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UdpDiscoveryClient] ProbeAndRegisterHttpAsync failed for {host}: {ex.Message}");
            }

            return false;
        }

        private async Task TryHttpDiscoverAsync(CancellationToken ct)
        {
            if (_isHttpScanning) return;
            _isHttpScanning = true;

            try
            {
                // 1. Try cached server host
                if (!string.IsNullOrWhiteSpace(_cachedServerHost))
                {
                    if (await ProbeAndRegisterHttpAsync(_cachedServerHost, ct))
                        return;
                }

                // 2. Try registry stored server host
                string regHost = LoadServerHostFromRegistry();
                if (!string.IsNullOrWhiteSpace(regHost) && regHost != _cachedServerHost)
                {
                    if (await ProbeAndRegisterHttpAsync(regHost, ct))
                        return;
                }

                // 3. Try Localhost & Default Gateway
                if (await ProbeAndRegisterHttpAsync("127.0.0.1", ct)) return;

                string gateway = GetDefaultGatewayIp();
                if (!string.IsNullOrWhiteSpace(gateway))
                {
                    if (await ProbeAndRegisterHttpAsync(gateway, ct)) return;
                }

                // 4. Subnet probe: sweep port 5085 in parallel on the local subnet (e.g. 192.168.178.1 - 254)
                string subnet = GetLocalSubnetPrefix();
                if (!string.IsNullOrWhiteSpace(subnet))
                {
                    var candidateIps = Enumerable.Range(1, 254).Select(i => $"{subnet}.{i}").ToList();
                    string foundIp = await FindServerInSubnetAsync(candidateIps, ct);
                    if (!string.IsNullOrWhiteSpace(foundIp))
                    {
                        await ProbeAndRegisterHttpAsync(foundIp, ct);
                    }
                }
            }
            finally
            {
                _isHttpScanning = false;
            }
        }

        private async Task<string> FindServerInSubnetAsync(List<string> candidateIps, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string found = null;

            try
            {
                await Parallel.ForEachAsync(candidateIps, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 48,
                    CancellationToken = cts.Token
                }, async (ip, token) =>
                {
                    if (found != null || token.IsCancellationRequested) return;

                    try
                    {
                        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        pingCts.CancelAfter(350);

                        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}:5085/api/hardware/network-info");
                        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, pingCts.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            found = ip;
                            cts.Cancel();
                        }
                    }
                    catch { }
                });
            }
            catch (OperationCanceledException) { }
            catch { }

            return found;
        }

        public void SetManualServerHost(string host)
        {
            string clean = host?.Trim();
            _cachedServerHost = clean;
            SaveServerHostToRegistry(clean);

            if (!string.IsNullOrWhiteSpace(clean))
            {
                Task.Run(() => ProbeAndRegisterHttpAsync(clean, CancellationToken.None));
            }
            else
            {
                lock (_stateLock)
                {
                    _state.IsConnected = false;
                    _state.ServerHost = "";
                    _state.StatusDisplay = "Suche Hardware-Check Server...";
                }
                NotifyStateChanged();
                _ = TryHttpDiscoverAsync(CancellationToken.None);
            }
        }

        public static string LoadServerHostFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\CPM\DiagnoseTool");
                return key?.GetValue("HardwareCheckServerHost") as string;
            }
            catch { return null; }
        }

        public static void SaveServerHostToRegistry(string host)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\CPM\DiagnoseTool");
                if (string.IsNullOrWhiteSpace(host))
                {
                    key?.DeleteValue("HardwareCheckServerHost", false);
                }
                else
                {
                    key?.SetValue("HardwareCheckServerHost", host.Trim());
                }
            }
            catch { }
        }

        private static string GetDefaultGatewayIp()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var gw = ni.GetIPProperties().GatewayAddresses.FirstOrDefault();
                    if (gw != null && gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        return gw.Address.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        private static string GetLocalSubnetPrefix()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = u.Address.ToString();
                            if (ip.StartsWith("192.168.") || ip.StartsWith("10."))
                            {
                                var parts = ip.Split('.');
                                if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.{parts[2]}";
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

