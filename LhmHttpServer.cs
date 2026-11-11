using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiagnoseTool
{
    public class LhmNode
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("Text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("Min")]
        public string Min { get; set; } = string.Empty;

        [JsonPropertyName("Value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("Max")]
        public string Max { get; set; } = string.Empty;

        [JsonPropertyName("SensorId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string SensorId { get; set; }

        [JsonPropertyName("Type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string Type { get; set; }

        [JsonPropertyName("ImageURL")]
        public string ImageURL { get; set; } = string.Empty;

        [JsonPropertyName("Children")]
        public List<LhmNode> Children { get; set; } = new List<LhmNode>();
    }

    public static class FirewallHelper
    {
        public static bool EnsurePort8085FirewallRule()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;

                // Check if rule already exists
                var checkPsi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall show rule name=\"DiagnoseTool LHM Monitoring Port 8085\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var checkProc = Process.Start(checkPsi);
                if (checkProc != null)
                {
                    string output = checkProc.StandardOutput.ReadToEnd();
                    checkProc.WaitForExit(3000);
                    if (checkProc.ExitCode == 0 && output.IndexOf("DiagnoseTool LHM Monitoring Port 8085", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                // Add inbound rule for port 8085 TCP
                var addPsi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall add rule name=\"DiagnoseTool LHM Monitoring Port 8085\" dir=in action=allow protocol=TCP localport=8085",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var addProc = Process.Start(addPsi);
                if (addProc != null)
                {
                    addProc.WaitForExit(4000);
                    return addProc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FirewallHelper] Failed: {ex.Message}");
            }
            return false;
        }

        public static bool EnsureDiscoveryFirewallRule()
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return false;

                // Check if rule already exists
                var checkPsi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall show rule name=\"DiagnoseTool Discovery Port 5087 UDP\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var checkProc = Process.Start(checkPsi);
                if (checkProc != null)
                {
                    string output = checkProc.StandardOutput.ReadToEnd();
                    checkProc.WaitForExit(3000);
                    if (checkProc.ExitCode == 0 && output.IndexOf("DiagnoseTool Discovery Port 5087 UDP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                // Add inbound rule for port 5087 UDP
                var addPsi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "advfirewall firewall add rule name=\"DiagnoseTool Discovery Port 5087 UDP\" dir=in action=allow protocol=UDP localport=5087",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var addProc = Process.Start(addPsi);
                if (addProc != null)
                {
                    addProc.WaitForExit(4000);
                    return addProc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FirewallHelper UDP] Failed: {ex.Message}");
            }
            return false;
        }
    }

    public class TelemetryStressStatus
    {
        public bool IsCpuStressRunning { get; set; }
        public string CpuStressRemaining { get; set; } = "00:00";
        public int CpuStressThreads { get; set; }
        public bool IsRamStressRunning { get; set; }
        public string RamStressRemaining { get; set; } = "00:00";
        public double RamStressAllocatedGb { get; set; }
        public int RamStressPassCount { get; set; }
        public int RamStressErrorCount { get; set; }
        public bool IsGpuStressRunning { get; set; }
        public bool IsFurMarkFound { get; set; }
        public bool IsRecordingRunning { get; set; }
        public string RecordingDuration { get; set; } = "00:00:00";
        public int RecordedSamplesCount { get; set; }
        public bool IsServerConnected { get; set; }
        public string ServerHost { get; set; } = "";
        public int ServerPort { get; set; } = 5085;
        public string SessionId { get; set; } = SessionLifecycleService.SessionId;
        public string SessionStartedUtc { get; set; } = SessionLifecycleService.SessionStartedUtc.ToString("o");
    }

    public partial class LhmHttpServer : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Task _listenTask;
        private readonly Func<HardwareDiagnostics> _diagnosticsProvider;
        private readonly Func<SystemInfo> _systemInfoProvider;
        private readonly Func<BatteryInfo> _batteryInfoProvider;
        private string _orderNumber = "";

        public Func<TelemetryStressStatus> StressStatusProvider { get; set; }
        public Action<int?, int?> OnStartCpuStress { get; set; }
        public Action OnStopCpuStress { get; set; }
        public Action<int?, int?> OnStartRamStress { get; set; }
        public Action OnStopRamStress { get; set; }
        public Action OnLaunchFurMarkDirect { get; set; }
        public Action<int?> OnLaunchFurMarkDirectWithDuration { get; set; }
        public Action OnLaunchFurMarkGui { get; set; }
        public Action OnStopFurMark { get; set; }
        public Action<int?> OnStartFullStress { get; set; }
        public Action OnStopAllStress { get; set; }
        public Action<string> OnStartRecording { get; set; }
        public Action OnStopRecording { get; set; }
        public Action<string> OnSetOrderNumber { get; set; }
        public Action OnExitApplication { get; set; }
        public IDiagnoseRemoteControl RemoteControl { get; set; }

        public bool IsRunning { get; private set; }
        public int Port { get; } = 8085;
        public string ServerUrl { get; private set; } = "http://localhost:8085/";

        public LhmHttpServer(
            Func<HardwareDiagnostics> diagnosticsProvider,
            Func<SystemInfo> systemInfoProvider = null,
            Func<BatteryInfo> batteryInfoProvider = null)
        {
            _diagnosticsProvider = diagnosticsProvider;
            _systemInfoProvider = systemInfoProvider;
            _batteryInfoProvider = batteryInfoProvider;
        }

        public void UpdateOrderNumber(string orderNumber)
        {
            _orderNumber = orderNumber?.Trim() ?? "";
        }

        public bool Start(string orderNumber = null)
        {
            if (IsRunning)
            {
                if (orderNumber != null) UpdateOrderNumber(orderNumber);
                return true;
            }

            _orderNumber = orderNumber?.Trim() ?? "";

            // Ensure Windows Firewall permits TCP Port 8085
            Task.Run(() => FirewallHelper.EnsurePort8085FirewallRule());

            try
            {
                _listener = new HttpListener();
                
                // Try listening on wildcard for full LAN accessibility
                try
                {
                    _listener.Prefixes.Add($"http://*:{Port}/");
                    _listener.Start();
                }
                catch
                {
                    // If wildcard requires elevated URL reservation, fall back to localhost + any local IP
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                    _listener.Prefixes.Add($"http://localhost:{Port}/");
                    
                    string localIp = GetPrimaryLocalIp();
                    if (!string.IsNullOrEmpty(localIp) && localIp != "127.0.0.1")
                    {
                        try { _listener.Prefixes.Add($"http://{localIp}:{Port}/"); } catch { }
                    }
                    _listener.Start();
                }

                _cts = new CancellationTokenSource();
                _listenTask = Task.Run(() => ListenLoop(_cts.Token));
                IsRunning = true;

                // Auto-register with Hardware-Check
                Task.Run(() => RegisterWithHardwareCheckAsync(_orderNumber));

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LhmHttpServer] Start failed on port {Port}: {ex.Message}");
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;

            try
            {
                _cts?.Cancel();
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LhmHttpServer] Stop error: {ex.Message}");
            }
            finally
            {
                _listener = null;
                _cts?.Dispose();
                _cts = null;
                IsRunning = false;
            }
        }

        private async Task ListenLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context), ct);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[LhmHttpServer] Listener loop error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;

            try
            {
                // Enable CORS for web apps (e.g. Next.js / Hardware-Check)
                resp.AddHeader("Access-Control-Allow-Origin", "*");
                resp.AddHeader("Access-Control-Allow-Headers", "*");
                resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

                if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = (int)HttpStatusCode.NoContent;
                    resp.Close();
                    return;
                }

                string path = req.Url.AbsolutePath.ToLowerInvariant();
                string method = req.HttpMethod.ToUpperInvariant();

                // 1. GET Endpoints
                if (method == "GET")
                {
                    if (path == "/" || path == "/data.json")
                    {
                        var tree = BuildLhmTree();
                        SendResponseJson(resp, tree);
                    }
                    else if (path == "/api/telemetry" || path == "/telemetry")
                    {
                        var telemetry = BuildTelemetryData();
                        SendResponseJson(resp, telemetry);
                    }
                    else if (path == "/api/status" || path == "/status")
                    {
                        var stress = StressStatusProvider?.Invoke();
                        var statusObj = new
                        {
                            status = "online",
                            appName = "CPM DiagnoseTool",
                            version = AppInfo.Version,
                            port = Port,
                            machineName = Environment.MachineName,
                            orderNumber = _orderNumber,
                            serverTime = DateTime.UtcNow.ToString("o"),
                            sessionId = SessionLifecycleService.SessionId,
                            sessionStartedUtc = SessionLifecycleService.SessionStartedUtc.ToString("o"),
                            previousSessionUnclean = SessionLifecycleService.PreviousUncleanSession != null,
                            serverConnection = new
                            {
                                isConnected = stress?.IsServerConnected ?? false,
                                serverHost = stress?.ServerHost ?? "",
                                serverPort = stress?.ServerPort ?? 5085
                            },
                            stressTests = new
                            {
                                isCpuStressRunning = stress?.IsCpuStressRunning ?? false,
                                isRamStressRunning = stress?.IsRamStressRunning ?? false,
                                isGpuStressRunning = stress?.IsGpuStressRunning ?? false,
                                isRecordingRunning = stress?.IsRecordingRunning ?? false
                            }
                        };
                        SendResponseJson(resp, statusObj);
                    }
                    else if (path == "/api/stress" || path == "/api/control/status")
                    {
                        var stress = StressStatusProvider?.Invoke() ?? new TelemetryStressStatus();
                        SendResponseJson(resp, stress);
                    }
                    else if (!TryHandleExtendedGet(req, resp, path))
                    {
                        resp.StatusCode = (int)HttpStatusCode.NotFound;
                        SendResponseJson(resp, new { error = "Endpoint not found", hint = "GET /api listet alle verfügbaren Endpunkte" }, 404);
                    }
                }
                // 2. POST Endpoints (Control Actions)
                else if (method == "POST")
                {
                    string body = "";
                    if (req.HasEntityBody)
                    {
                        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                        body = reader.ReadToEnd();
                    }

                    JsonElement? root = null;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(body);
                            root = doc.RootElement.Clone();
                        }
                        catch { }
                    }

                    if (path == "/api/control/stress/cpu/start" || path == "/api/stress/cpu/start")
                    {
                        int? threads = GetIntProperty(root, "threads");
                        int? duration = GetIntProperty(root, "durationMinutes", "duration");
                        OnStartCpuStress?.Invoke(duration, threads);
                        SendResponseJson(resp, new { success = true, action = "cpu_start", durationMinutes = duration, threads, message = "CPU-Stresstest via API ausgelöst" });
                    }
                    else if (path == "/api/control/stress/cpu/stop" || path == "/api/stress/cpu/stop")
                    {
                        OnStopCpuStress?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "cpu_stop", message = "CPU-Stresstest via API gestoppt" });
                    }
                    else if (path == "/api/control/stress/ram/start" || path == "/api/stress/ram/start")
                    {
                        int? targetPercent = GetIntProperty(root, "targetPercent", "percent");
                        int? duration = GetIntProperty(root, "durationMinutes", "duration");
                        OnStartRamStress?.Invoke(duration, targetPercent);
                        SendResponseJson(resp, new { success = true, action = "ram_start", durationMinutes = duration, targetPercent, message = "RAM-Stresstest via API ausgelöst" });
                    }
                    else if (path == "/api/control/stress/ram/stop" || path == "/api/stress/ram/stop")
                    {
                        OnStopRamStress?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "ram_stop", message = "RAM-Stresstest via API gestoppt" });
                    }
                    else if (path == "/api/control/stress/gpu/donut" || path == "/api/stress/gpu/donut")
                    {
                        int? duration = GetIntProperty(root, "durationMinutes", "duration");
                        if (OnLaunchFurMarkDirectWithDuration != null)
                        {
                            OnLaunchFurMarkDirectWithDuration(duration);
                        }
                        else
                        {
                            OnLaunchFurMarkDirect?.Invoke();
                        }
                        SendResponseJson(resp, new { success = true, action = "gpu_donut", durationMinutes = duration, message = "FurMark 3D Donut Stresstest via API gestartet" });
                    }
                    else if (path == "/api/control/stress/gpu/gui" || path == "/api/stress/gpu/gui")
                    {
                        OnLaunchFurMarkGui?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "gpu_gui", message = "FurMark GUI via API gestartet" });
                    }
                    else if (path == "/api/control/stress/gpu/stop" || path == "/api/stress/gpu/stop")
                    {
                        OnStopFurMark?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "gpu_stop", message = "FurMark via API beendet" });
                    }
                    else if (path == "/api/control/stress/full/start" || path == "/api/stress/full/start")
                    {
                        int duration = GetIntProperty(root, "durationMinutes", "duration") ?? 60;
                        OnStartFullStress?.Invoke(duration);
                        SendResponseJson(resp, new { success = true, action = "full_start", durationMinutes = duration, message = $"Volllast-Test (CPU + GPU) für {duration} Min. gestartet" });
                    }
                    else if (path == "/api/control/stress/full/stop" || path == "/api/stress/full/stop")
                    {
                        OnStopAllStress?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "full_stop", message = "Volllast-Test (CPU + GPU) gestoppt" });
                    }
                    else if (path == "/api/control/stress/stop-all" || path == "/api/stress/stop-all")
                    {
                        OnStopAllStress?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "stop_all", message = "Alle Stresstests via API gestoppt" });
                    }
                    else if (path == "/api/control/recording/start" || path == "/api/recording/start")
                    {
                        string orderNo = root.HasValue && root.Value.TryGetProperty("orderNumber", out var on) ? on.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(orderNo))
                        {
                            UpdateOrderNumber(orderNo);
                        }
                        OnStartRecording?.Invoke(orderNo);
                        SendResponseJson(resp, new { success = true, action = "recording_start", orderNumber = _orderNumber, message = "Werte-Aufzeichnung via API gestartet" });
                    }
                    else if (path == "/api/control/recording/stop" || path == "/api/recording/stop")
                    {
                        OnStopRecording?.Invoke();
                        SendResponseJson(resp, new { success = true, action = "recording_stop", message = "Werte-Aufzeichnung via API gestoppt" });
                    }
                    else if (path == "/api/control/order")
                    {
                        string orderNo = root.HasValue && root.Value.TryGetProperty("orderNumber", out var on) ? on.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(orderNo))
                        {
                            UpdateOrderNumber(orderNo);
                            OnSetOrderNumber?.Invoke(orderNo);
                        }
                        SendResponseJson(resp, new { success = true, orderNumber = _orderNumber });
                    }
                    else if (path == "/api/control/app/exit" || path == "/api/control/close" || path == "/api/control/shutdown")
                    {
                        SendResponseJson(resp, new { success = true, action = "app_exit", message = "Tests abgeschlossen. DiagnoseTool wird beendet." });
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(400);
                            OnExitApplication?.Invoke();
                        });
                    }
                    else if (!TryHandleExtendedPost(req, resp, path, root))
                    {
                        resp.StatusCode = (int)HttpStatusCode.NotFound;
                        SendResponseJson(resp, new { error = "Action endpoint not found", hint = "GET /api listet alle verfügbaren Endpunkte" }, 404);
                    }
                }
                else
                {
                    resp.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    SendResponseJson(resp, new { error = "Method not allowed" }, 405);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LhmHttpServer] Error serving request: {ex.Message}");
                try { SendResponseJson(resp, new { success = false, error = ex.Message }, 500); }
                catch
                {
                    try { resp.StatusCode = (int)HttpStatusCode.InternalServerError; } catch { }
                }
            }
            finally
            {
                try { resp.Close(); } catch { }
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private static void SendResponseJson(HttpListenerResponse resp, object data, int statusCode = 200)
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            resp.StatusCode = statusCode;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = buffer.Length;
            resp.OutputStream.Write(buffer, 0, buffer.Length);
        }

        public object BuildTelemetryData()
        {
            var diag = _diagnosticsProvider?.Invoke();
            var sys = _systemInfoProvider?.Invoke();
            var batt = _batteryInfoProvider?.Invoke();
            var stress = StressStatusProvider?.Invoke() ?? new TelemetryStressStatus();

            return new
            {
                version = AppInfo.Version,
                machineName = Environment.MachineName,
                orderNumber = _orderNumber,
                serverTime = DateTime.UtcNow.ToString("o"),
                system = new
                {
                    manufacturer = sys?.Manufacturer ?? "Unbekannt",
                    model = sys?.Model ?? "Unbekannt",
                    motherboardManufacturer = sys?.MotherboardManufacturer ?? "Unbekannt",
                    motherboardModel = sys?.MotherboardModel ?? diag?.MotherboardName ?? "Unbekannt",
                    serialNumber = sys?.SerialNumber ?? "Unbekannt",
                    fullName = sys?.FullSystemName ?? Environment.MachineName
                },
                cpu = new
                {
                    name = diag?.CpuName ?? "CPU",
                    clockGhz = diag?.CpuClockSpeed ?? 0,
                    loadPercent = diag?.CpuAverageLoad ?? 0,
                    packageTemp = diag?.CpuAverageTemp ?? 0,
                    powerWatts = diag?.CpuPackagePower ?? 0,
                    voltage = diag?.CpuVoltage ?? 0,
                    coreCount = diag?.CpuCores?.Count ?? 0,
                    cores = diag?.CpuCores?.Select(c => new
                    {
                        index = c.CoreIndex,
                        load = c.Load,
                        temp = c.Temperature
                    })
                },
                gpu = new
                {
                    name = diag?.GpuName ?? "GPU",
                    loadPercent = diag?.GpuLoad ?? 0,
                    temp = diag?.GpuTemp ?? 0,
                    hotspotTemp = diag?.GpuHotSpotTemp ?? 0,
                    vramUsedMb = diag?.GpuVramUsedMb ?? 0,
                    vramTotalMb = diag?.GpuVramTotalMb ?? 0,
                    clockSpeed = diag?.GpuClockSpeed ?? 0,
                    power = diag?.GpuPower ?? 0,
                    isDedicated = diag != null && diag.GpuTemp.HasValue && diag.GpuTemp.Value > 0
                },
                ram = new
                {
                    usedGb = diag?.RamUsedGb ?? 0,
                    totalGb = diag?.RamTotalGb ?? 0,
                    loadPercent = diag?.RamPercent ?? 0
                },
                battery = new
                {
                    hasBattery = batt?.IsPresent ?? false,
                    percent = batt?.CurrentChargePercent ?? 0,
                    isCharging = batt?.IsCharging ?? false,
                    isConnectedToAc = batt?.IsConnectedToAc ?? false,
                    healthPercent = batt?.HealthPercent ?? 100,
                    designCapacity = batt?.DesignCapacity ?? 0,
                    fullChargeCapacity = batt?.FullChargeCapacity ?? 0
                },
                storage = diag?.StorageDrives?.Select(d => new
                {
                    name = d.Name,
                    temperature = d.Temperature,
                    usedPercent = d.UsedSpacePercent,
                    healthPercent = d.HealthPercent
                }),
                fans = diag?.Fans?.Select(f => new
                {
                    name = f.Name,
                    rpm = f.Rpm
                }),
                stressTests = new
                {
                    isCpuStressRunning = stress.IsCpuStressRunning,
                    cpuStressRemaining = stress.CpuStressRemaining,
                    cpuStressThreads = stress.CpuStressThreads,
                    isRamStressRunning = stress.IsRamStressRunning,
                    ramStressRemaining = stress.RamStressRemaining,
                    ramStressAllocatedGb = stress.RamStressAllocatedGb,
                    ramStressPassCount = stress.RamStressPassCount,
                    ramStressErrorCount = stress.RamStressErrorCount,
                    isGpuStressRunning = stress.IsGpuStressRunning,
                    isFurMarkFound = stress.IsFurMarkFound,
                    isRecordingRunning = stress.IsRecordingRunning,
                    recordingDuration = stress.RecordingDuration,
                    recordedSamplesCount = stress.RecordedSamplesCount
                },
                serverConnection = new
                {
                    isConnected = stress.IsServerConnected,
                    serverHost = stress.ServerHost,
                    serverPort = stress.ServerPort
                }
            };
        }

        public LhmNode BuildLhmTree()
        {
            var diag = _diagnosticsProvider?.Invoke();
            var sys = _systemInfoProvider?.Invoke();
            var batt = _batteryInfoProvider?.Invoke();

            int id = 1;
            string computerName = !string.IsNullOrEmpty(_orderNumber)
                ? $"[{_orderNumber}] {Environment.MachineName}"
                : Environment.MachineName;

            var root = new LhmNode
            {
                Id = 0,
                Text = "Sensor",
                Min = "Min",
                Value = "Value",
                Max = "Max",
                ImageURL = ""
            };

            var compNode = new LhmNode
            {
                Id = id++,
                Text = computerName,
                ImageURL = "images_icon/computer.png"
            };

            // Order Number badge / sensor if present
            if (!string.IsNullOrEmpty(_orderNumber))
            {
                compNode.Children.Add(new LhmNode
                {
                    Id = id++,
                    Text = $"Auftragsnummer: {_orderNumber}",
                    Value = _orderNumber,
                    SensorId = "/custom/ordernumber",
                    ImageURL = "images_icon/info.png"
                });
            }

            // 1. Mainboard
            string mbName = !string.IsNullOrWhiteSpace(sys?.MotherboardModel) && sys.MotherboardModel != "Unbekannt"
                ? $"{sys.MotherboardManufacturer} {sys.MotherboardModel}".Trim()
                : (diag?.MotherboardName ?? "System Mainboard");

            var mbNode = new LhmNode
            {
                Id = id++,
                Text = mbName,
                SensorId = "/lpc/mainboard/0",
                ImageURL = "images_icon/mainboard.png"
            };

            if (diag != null && diag.MotherboardTemp > 0)
            {
                var mbTemps = new LhmNode { Id = id++, Text = "Temperatures", ImageURL = "images_icon/temperature.png" };
                mbTemps.Children.Add(CreateSensor(ref id, "System Mainboard", "/lpc/mainboard/0/temp/0", "Temperature", diag.MotherboardTemp, "°C"));
                mbNode.Children.Add(mbTemps);
            }

            if (diag != null && diag.Fans != null && diag.Fans.Count > 0)
            {
                var mbFans = new LhmNode { Id = id++, Text = "Fans", ImageURL = "images_icon/fan.png" };
                for (int i = 0; i < diag.Fans.Count; i++)
                {
                    var fan = diag.Fans[i];
                    mbFans.Children.Add(CreateSensor(ref id, fan.Name, $"/lpc/mainboard/0/fan/{i}", "Fan", fan.Rpm, "RPM"));
                }
                mbNode.Children.Add(mbFans);
            }
            compNode.Children.Add(mbNode);

            // 2. CPU
            string cpuName = !string.IsNullOrWhiteSpace(diag?.CpuName) ? diag.CpuName : "CPU Processor";
            var cpuNode = new LhmNode
            {
                Id = id++,
                Text = cpuName,
                SensorId = "/cpu/0",
                ImageURL = "images_icon/cpu.png"
            };

            var cpuTemps = new LhmNode { Id = id++, Text = "Temperatures", ImageURL = "images_icon/temperature.png" };
            var cpuLoads = new LhmNode { Id = id++, Text = "Loads", ImageURL = "images_icon/load.png" };
            var cpuClocks = new LhmNode { Id = id++, Text = "Clocks", ImageURL = "images_icon/clock.png" };
            var cpuPowers = new LhmNode { Id = id++, Text = "Powers", ImageURL = "images_icon/power.png" };

            if (diag?.CpuAverageTemp.HasValue == true)
            {
                cpuTemps.Children.Add(CreateSensor(ref id, "CPU Package", "/cpu/0/temp/0", "Temperature", diag.CpuAverageTemp.Value, "°C"));
            }

            if (diag?.CpuAverageLoad.HasValue == true)
            {
                cpuLoads.Children.Add(CreateSensor(ref id, "CPU Total", "/cpu/0/load/0", "Load", diag.CpuAverageLoad.Value, "%"));
            }

            if (diag != null && diag.CpuPackagePower > 0)
            {
                cpuPowers.Children.Add(CreateSensor(ref id, "CPU Package Power", "/cpu/0/power/0", "Power", diag.CpuPackagePower, "W"));
            }

            if (diag?.CpuCores != null)
            {
                foreach (var core in diag.CpuCores)
                {
                    int cIdx = core.CoreIndex != int.MaxValue ? core.CoreIndex : id;
                    if (core.Temperature.HasValue)
                    {
                        cpuTemps.Children.Add(CreateSensor(ref id, core.Name, $"/cpu/0/temp/{cIdx}", "Temperature", core.Temperature.Value, "°C"));
                    }
                    if (core.Load.HasValue)
                    {
                        cpuLoads.Children.Add(CreateSensor(ref id, core.Name, $"/cpu/0/load/{cIdx}", "Load", core.Load.Value, "%"));
                    }
                    if (core.ClockMhz.HasValue && core.ClockMhz.Value > 0)
                    {
                        cpuClocks.Children.Add(CreateSensor(ref id, core.Name, $"/cpu/0/clock/{cIdx}", "Clock", core.ClockMhz.Value, "MHz"));
                    }
                }
            }

            if (cpuTemps.Children.Count > 0) cpuNode.Children.Add(cpuTemps);
            if (cpuLoads.Children.Count > 0) cpuNode.Children.Add(cpuLoads);
            if (cpuClocks.Children.Count > 0) cpuNode.Children.Add(cpuClocks);
            if (cpuPowers.Children.Count > 0) cpuNode.Children.Add(cpuPowers);
            compNode.Children.Add(cpuNode);

            // 3. RAM
            double totalRam = diag != null && diag.RamTotalGb > 0 ? diag.RamTotalGb : 16.0;
            double usedRam = diag != null && diag.RamUsedGb > 0 ? diag.RamUsedGb : 4.0;
            double freeRam = Math.Max(0.1, totalRam - usedRam);
            double ramPct = diag != null && diag.RamPercent > 0 ? diag.RamPercent : (usedRam / totalRam * 100.0);

            var ramNode = new LhmNode
            {
                Id = id++,
                Text = "Generic Memory",
                SensorId = "/ram",
                ImageURL = "images_icon/ram.png"
            };

            var ramLoads = new LhmNode { Id = id++, Text = "Loads", ImageURL = "images_icon/load.png" };
            ramLoads.Children.Add(CreateSensor(ref id, "Memory", "/ram/load/0", "Load", ramPct, "%"));
            ramNode.Children.Add(ramLoads);

            var ramData = new LhmNode { Id = id++, Text = "Data", ImageURL = "images_icon/data.png" };
            ramData.Children.Add(CreateSensor(ref id, "Memory Used", "/ram/data/0", "Data", usedRam, "GB"));
            ramData.Children.Add(CreateSensor(ref id, "Memory Available", "/ram/data/1", "Data", freeRam, "GB"));
            ramNode.Children.Add(ramData);
            compNode.Children.Add(ramNode);

            // 4. GPU
            if (diag != null && !string.IsNullOrWhiteSpace(diag.GpuName) && diag.GpuName.IndexOf("Keine dedizierte", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string gpuImg = diag.GpuName.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "images_icon/nvidia.png"
                    : (diag.GpuName.IndexOf("amd", StringComparison.OrdinalIgnoreCase) >= 0 || diag.GpuName.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "images_icon/ati.png"
                        : "images_icon/gpu.png");

                var gpuNode = new LhmNode
                {
                    Id = id++,
                    Text = diag.GpuName,
                    SensorId = "/gpu/0",
                    ImageURL = gpuImg
                };

                var gpuTemps = new LhmNode { Id = id++, Text = "Temperatures", ImageURL = "images_icon/temperature.png" };
                if (diag.GpuTemp.HasValue && diag.GpuTemp.Value > 0)
                {
                    gpuTemps.Children.Add(CreateSensor(ref id, "GPU Core", "/gpu/0/temp/0", "Temperature", diag.GpuTemp.Value, "°C"));
                }
                if (diag.GpuHotSpotTemp > 0)
                {
                    gpuTemps.Children.Add(CreateSensor(ref id, "GPU Hot Spot", "/gpu/0/temp/1", "Temperature", diag.GpuHotSpotTemp, "°C"));
                }
                if (gpuTemps.Children.Count > 0) gpuNode.Children.Add(gpuTemps);

                var gpuLoads = new LhmNode { Id = id++, Text = "Loads", ImageURL = "images_icon/load.png" };
                if (diag.GpuLoad.HasValue)
                {
                    gpuLoads.Children.Add(CreateSensor(ref id, "GPU Core", "/gpu/0/load/0", "Load", diag.GpuLoad.Value, "%"));
                }
                if (diag.GpuVramUsage.HasValue)
                {
                    gpuLoads.Children.Add(CreateSensor(ref id, "GPU Memory", "/gpu/0/load/1", "Load", diag.GpuVramUsage.Value, "%"));
                }
                if (gpuLoads.Children.Count > 0) gpuNode.Children.Add(gpuLoads);

                if (diag.GpuClockSpeed > 0)
                {
                    var gpuClocks = new LhmNode { Id = id++, Text = "Clocks", ImageURL = "images_icon/clock.png" };
                    gpuClocks.Children.Add(CreateSensor(ref id, "GPU Core", "/gpu/0/clock/0", "Clock", diag.GpuClockSpeed, "MHz"));
                    gpuNode.Children.Add(gpuClocks);
                }

                if (diag.GpuPower > 0)
                {
                    var gpuPowers = new LhmNode { Id = id++, Text = "Powers", ImageURL = "images_icon/power.png" };
                    gpuPowers.Children.Add(CreateSensor(ref id, "GPU Power", "/gpu/0/power/0", "Power", diag.GpuPower, "W"));
                    gpuNode.Children.Add(gpuPowers);
                }

                if (diag.GpuVramTotalMb > 0)
                {
                    var gpuVram = new LhmNode { Id = id++, Text = "Data", ImageURL = "images_icon/data.png" };
                    gpuVram.Children.Add(CreateSensor(ref id, "GPU Memory Used", "/gpu/0/smalldata/0", "SmallData", diag.GpuVramUsedMb / 1024.0, "GB"));
                    gpuVram.Children.Add(CreateSensor(ref id, "GPU Memory Total", "/gpu/0/smalldata/1", "SmallData", diag.GpuVramTotalMb / 1024.0, "GB"));
                    gpuNode.Children.Add(gpuVram);
                }

                compNode.Children.Add(gpuNode);
            }

            // 5. Storage Drives
            if (diag?.StorageDrives != null)
            {
                for (int i = 0; i < diag.StorageDrives.Count; i++)
                {
                    var drive = diag.StorageDrives[i];
                    var driveNode = new LhmNode
                    {
                        Id = id++,
                        Text = drive.Name,
                        SensorId = $"/hdd/{i}",
                        ImageURL = "images_icon/hdd.png"
                    };

                    if (drive.Temperature.HasValue && drive.Temperature.Value > 0)
                    {
                        var dTemps = new LhmNode { Id = id++, Text = "Temperatures", ImageURL = "images_icon/temperature.png" };
                        dTemps.Children.Add(CreateSensor(ref id, "Temperature", $"/hdd/{i}/temp/0", "Temperature", drive.Temperature.Value, "°C"));
                        driveNode.Children.Add(dTemps);
                    }

                    if (drive.UsedSpacePercent.HasValue && drive.UsedSpacePercent.Value > 0)
                    {
                        var dLoads = new LhmNode { Id = id++, Text = "Loads", ImageURL = "images_icon/load.png" };
                        dLoads.Children.Add(CreateSensor(ref id, "Used Space", $"/hdd/{i}/load/0", "Load", drive.UsedSpacePercent.Value, "%"));
                        driveNode.Children.Add(dLoads);
                    }

                    compNode.Children.Add(driveNode);
                }
            }

            // 6. Battery (if present)
            if (batt != null && batt.IsPresent)
            {
                var battNode = new LhmNode
                {
                    Id = id++,
                    Text = "Battery",
                    SensorId = "/battery/0",
                    ImageURL = "images_icon/battery.png"
                };

                var bLoads = new LhmNode { Id = id++, Text = "Loads", ImageURL = "images_icon/load.png" };
                bLoads.Children.Add(CreateSensor(ref id, "Charge Level", "/battery/0/load/0", "Load", batt.CurrentChargePercent, "%"));
                battNode.Children.Add(bLoads);

                if (batt.Voltage > 0)
                {
                    var bVolts = new LhmNode { Id = id++, Text = "Voltages", ImageURL = "images_icon/voltage.png" };
                    bVolts.Children.Add(CreateSensor(ref id, "Battery Voltage", "/battery/0/voltage/0", "Voltage", batt.Voltage, "V"));
                    battNode.Children.Add(bVolts);
                }

                var bData = new LhmNode { Id = id++, Text = "Data", ImageURL = "images_icon/data.png" };
                if (batt.DesignCapacity > 0) bData.Children.Add(CreateSensor(ref id, "Designed Capacity", "/battery/0/data/0", "Data", batt.DesignCapacity, "mWh"));
                if (batt.FullChargeCapacity > 0) bData.Children.Add(CreateSensor(ref id, "Full Charge Capacity", "/battery/0/data/1", "Data", batt.FullChargeCapacity, "mWh"));
                battNode.Children.Add(bData);

                compNode.Children.Add(battNode);
            }

            root.Children.Add(compNode);
            return root;
        }

        private static LhmNode CreateSensor(ref int id, string text, string sensorId, string type, double value, string unit)
        {
            string formatted = unit == "%"
                ? $"{value:F1} %"
                : (unit == "°C"
                    ? $"{value:F1} °C"
                    : (unit == "MHz"
                        ? $"{value:F0} MHz"
                        : (unit == "W"
                            ? $"{value:F1} W"
                            : (unit == "V"
                                ? $"{value:F2} V"
                                : $"{value:F1} {unit}"))));

            return new LhmNode
            {
                Id = id++,
                Text = text,
                Value = formatted,
                Min = formatted,
                Max = formatted,
                SensorId = sensorId,
                Type = type,
                ImageURL = $"images_icon/{type.ToLowerInvariant()}.png"
            };
        }

        public static string GetPrimaryLocalIp()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                                i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                !i.Description.ToLowerInvariant().Contains("virtual") &&
                                !i.Description.ToLowerInvariant().Contains("pseudo") &&
                                !i.Description.ToLowerInvariant().Contains("vmware") &&
                                !i.Description.ToLowerInvariant().Contains("hyper-v"));

                foreach (var ni in interfaces)
                {
                    var props = ni.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            if (!ip.StartsWith("169.254.") && !ip.StartsWith("127."))
                            {
                                return ip;
                            }
                        }
                    }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        public static async Task RegisterWithHardwareCheckAsync(string orderNumber)
        {
            string localIp = GetPrimaryLocalIp();
            string machineName = Environment.MachineName;
            string displayName = !string.IsNullOrEmpty(orderNumber)
                ? $"[{orderNumber}] {machineName}"
                : machineName;

            // 1. Direct configuration file update in parent directory /Hardware-Check
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] possiblePaths = new[]
                {
                    Path.GetFullPath(Path.Combine(baseDir, "..", "Hardware-Check", "backend", "data", "endpoints.json")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "Hardware-Check", "backend", "data", "endpoints.json")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Hardware-Check", "backend", "data", "endpoints.json")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "Hardware-Check", "backend", "data", "endpoints.json")),
                    @"C:\Users\User\Desktop\Projekte\Hardware-Check\backend\data\endpoints.json"
                };

                foreach (var epFile in possiblePaths)
                {
                    if (File.Exists(epFile) || Directory.Exists(Path.GetDirectoryName(epFile)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(epFile));
                        EndpointsWrapper config = null;

                        if (File.Exists(epFile))
                        {
                            try
                            {
                                string existingJson = File.ReadAllText(epFile);
                                config = JsonSerializer.Deserialize<EndpointsWrapper>(existingJson);
                            }
                            catch { }
                        }

                        if (config == null)
                        {
                            config = new EndpointsWrapper { DemoModeEnabled = false, Endpoints = new List<EndpointEntry>() };
                        }

                        // Remove old entry for this host/machine if present
                        config.Endpoints.RemoveAll(e => e.Host == localIp || e.Host == "127.0.0.1" || e.Name.Contains(machineName));

                        // Add new updated entry
                        config.Endpoints.Insert(0, new EndpointEntry
                        {
                            Id = "dev-" + Guid.NewGuid().ToString("N")[..6],
                            Name = displayName,
                            Host = localIp,
                            Port = 8085,
                            IsEnabled = true,
                            IsAutoDiscovered = false,
                            OrderNumber = orderNumber,
                            TestType = "combined"
                        });

                        string newJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(epFile, newJson);
                        Debug.WriteLine($"[LhmHttpServer] Successfully updated Hardware-Check endpoints at {epFile}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LhmHttpServer] Endpoints file update error: {ex.Message}");
            }

            // 2. HTTP POST to running Hardware-Check backend APIs (ports 5085 & 8085)
            using var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(2);

            string[] apiUrls = new[]
            {
                "http://localhost:5085/api/hardware/devices",
                "http://127.0.0.1:5085/api/hardware/devices",
                $"http://{localIp}:5085/api/hardware/devices"
            };

            var payload = new
            {
                Name = displayName,
                Host = localIp,
                Port = 8085,
                OrderNumber = orderNumber,
                TestType = "combined"
            };
            string payloadJson = JsonSerializer.Serialize(payload);

            foreach (var url in apiUrls)
            {
                try
                {
                    var content = new System.Net.Http.StringContent(payloadJson, Encoding.UTF8, "application/json");
                    var res = await client.PostAsync(url, content);
                    if (res.IsSuccessStatusCode)
                    {
                        Debug.WriteLine($"[LhmHttpServer] Successfully registered via API at {url}");
                    }
                }
                catch { }
            }
        }

        private class EndpointsWrapper
        {
            public bool DemoModeEnabled { get; set; }
            public List<EndpointEntry> Endpoints { get; set; } = new List<EndpointEntry>();
        }

        private class EndpointEntry
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public bool IsEnabled { get; set; }
            public bool IsAutoDiscovered { get; set; }
            public string OrderNumber { get; set; }
            public string TestType { get; set; }
        }

        private static int? GetIntProperty(JsonElement? root, string prop1, string prop2 = null)
        {
            if (!root.HasValue) return null;
            var el = root.Value;
            if (el.TryGetProperty(prop1, out var val) || (prop2 != null && el.TryGetProperty(prop2, out val)))
            {
                if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n)) return n;
                if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out int s)) return s;
            }
            return null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
