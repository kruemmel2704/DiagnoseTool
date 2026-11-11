using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiagnoseTool
{
    /// <summary>
    /// Momentaufnahme dessen, was das DiagnoseTool gerade macht (für die Absturzanalyse).
    /// </summary>
    public class SessionActivitySnapshot
    {
        public bool IsCpuStressRunning { get; set; }
        public bool IsRamStressRunning { get; set; }
        public bool IsGpuStressRunning { get; set; }
        public bool IsBatteryTestRunning { get; set; }
        public bool IsRecording { get; set; }
        public float? CpuTemp { get; set; }
        public float? CpuLoad { get; set; }
        public float? CpuPowerWatts { get; set; }
        public float? GpuTemp { get; set; }
        public float? GpuHotSpotTemp { get; set; }
        public float? GpuLoad { get; set; }
        public float? GpuPowerWatts { get; set; }
        public float? RamPercent { get; set; }
        public float? MainboardTemp { get; set; }
        public double? BatteryPercent { get; set; }
        public bool? IsOnAcPower { get; set; }

        public bool AnyStressRunning => IsCpuStressRunning || IsRamStressRunning || IsGpuStressRunning;
    }

    /// <summary>
    /// Sitzungs-Markierung auf der Festplatte. Existiert sie beim Start noch, wurde die letzte
    /// Sitzung NICHT ordnungsgemäß beendet (PC-Absturz, Stromausfall, Programmabsturz, Kill).
    /// </summary>
    public class SessionMarker
    {
        public string SessionId { get; set; }
        public string MachineName { get; set; }
        public string AppVersion { get; set; }
        public int ProcessId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime LastHeartbeatUtc { get; set; }
        public DateTime SystemBootTimeUtc { get; set; }
        public string OrderNumber { get; set; }
        public SessionActivitySnapshot LastActivity { get; set; }

        /// <summary>Gesetzt, wenn das Programm an einer unbehandelten Ausnahme gestorben ist.</summary>
        public string UnhandledException { get; set; }
        public DateTime? UnhandledExceptionUtc { get; set; }
    }

    /// <summary>
    /// Überwacht den Lebenszyklus des DiagnoseTools:
    /// - Ordentliches Beenden: Abmeldung bei Hardware-Check ("Programm geschlossen" statt "Absturz").
    /// - Unsauberes Ende: wird beim nächsten Start erkannt, die Windows-Ereignisanzeige wird ausgewertet
    ///   und ein Absturzbericht an Hardware-Check gesendet.
    /// </summary>
    public static class SessionLifecycleService
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private static readonly object _lock = new object();
        private static Timer _heartbeatTimer;
        private static int _exitHandled;
        private static bool _initialized;
        private static SessionMarker _current;
        private static CrashAnalysisReport _lastCrashReport;
        private static bool _lastCrashReportDelivered;
        private static string _lastCrashReportDeliveredTo;

        public static string SessionId { get; } = Guid.NewGuid().ToString("N");
        public static DateTime SessionStartedUtc { get; } = DateTime.UtcNow;

        /// <summary>Informationen zur letzten, NICHT ordnungsgemäß beendeten Sitzung (null = sauber beendet).</summary>
        public static SessionMarker PreviousUncleanSession { get; private set; }

        public static string ExitReason { get; private set; }

        public static Func<SessionActivitySnapshot> ActivityProvider { get; set; }
        public static Func<string> OrderNumberProvider { get; set; }
        public static Func<(string Host, int Port)?> ServerProvider { get; set; }

        /// <summary>Wird (im Hintergrund-Thread) ausgelöst, sobald ein Absturzbericht erstellt wurde.</summary>
        public static event Action<CrashAnalysisReport> CrashReportCreated;

        public static CrashAnalysisReport LastCrashReport
        {
            get { lock (_lock) return _lastCrashReport; }
        }

        public static bool LastCrashReportDelivered
        {
            get { lock (_lock) return _lastCrashReportDelivered; }
        }

        public static string DataDirectory
        {
            get
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
                string dir = Path.Combine(baseDir, "CPM", "DiagnoseTool");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string MarkerPath => Path.Combine(DataDirectory, "session.json");
        public static string CrashReportDirectory
        {
            get
            {
                string dir = Path.Combine(DataDirectory, "crash_reports");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static DateTime GetSystemBootTimeUtc()
            => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

        /// <summary>
        /// Beim Programmstart aufrufen: prüft die letzte Sitzung und legt die neue Sitzungs-Markierung an.
        /// </summary>
        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
            }

            try
            {
                if (File.Exists(MarkerPath))
                {
                    var prev = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(MarkerPath), JsonOpts);
                    if (prev != null && prev.SessionId != SessionId && !IsProcessStillAlive(prev))
                    {
                        PreviousUncleanSession = prev;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLifecycle] Marker konnte nicht gelesen werden: {ex.Message}");
            }

            _current = new SessionMarker
            {
                SessionId = SessionId,
                MachineName = Environment.MachineName,
                AppVersion = AppInfo.Version,
                ProcessId = Environment.ProcessId,
                StartedUtc = SessionStartedUtc,
                LastHeartbeatUtc = DateTime.UtcNow,
                SystemBootTimeUtc = GetSystemBootTimeUtc()
            };
            WriteMarker();

            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();

            _heartbeatTimer = new Timer(_ => Heartbeat(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

            LoadLastCrashReportFromDisk();

            if (PreviousUncleanSession != null)
            {
                Task.Run(ProcessPreviousUncleanSessionAsync);
            }
        }

        private static bool IsProcessStillAlive(SessionMarker prev)
        {
            // Zweite parallel laufende Instanz -> keine "unsaubere" Sitzung
            try
            {
                if (prev.ProcessId <= 0 || prev.ProcessId == Environment.ProcessId) return false;
                bool sameBoot = Math.Abs((prev.SystemBootTimeUtc - GetSystemBootTimeUtc()).TotalMinutes) < 2;
                if (!sameBoot) return false;
                using var p = Process.GetProcessById(prev.ProcessId);
                return !p.HasExited && p.ProcessName.IndexOf("DiagnoseTool", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Heartbeat()
        {
            if (Volatile.Read(ref _exitHandled) == 1) return;
            try
            {
                lock (_lock)
                {
                    if (_current == null) return;
                    _current.LastHeartbeatUtc = DateTime.UtcNow;
                    try { _current.OrderNumber = OrderNumberProvider?.Invoke(); } catch { }
                    try { _current.LastActivity = ActivityProvider?.Invoke(); } catch { }
                }
                WriteMarker();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLifecycle] Heartbeat Fehler: {ex.Message}");
            }
        }

        private static void WriteMarker()
        {
            try
            {
                string json;
                lock (_lock)
                {
                    json = JsonSerializer.Serialize(_current, JsonOpts);
                }
                string tmp = MarkerPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, MarkerPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLifecycle] Marker schreiben fehlgeschlagen: {ex.Message}");
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                lock (_lock)
                {
                    if (_current == null) return;
                    _current.UnhandledException = e.ExceptionObject?.ToString();
                    _current.UnhandledExceptionUtc = DateTime.UtcNow;
                    _current.LastHeartbeatUtc = DateTime.UtcNow;
                }
                WriteMarker();
            }
            catch { }
        }

        /// <summary>
        /// Ordnungsgemäßes Beenden: Markierung entfernen und Hardware-Check benachrichtigen
        /// ("Programm geschlossen" statt "Absturz"). Mehrfachaufrufe werden ignoriert.
        /// </summary>
        public static void MarkCleanExit(string reason)
        {
            if (Interlocked.Exchange(ref _exitHandled, 1) == 1) return;
            ExitReason = reason;

            try { _heartbeatTimer?.Dispose(); } catch { }

            try
            {
                if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLifecycle] Marker löschen fehlgeschlagen: {ex.Message}");
            }

            try
            {
                // Synchron mit hartem Zeitlimit, damit das Beenden nicht hängt
                var task = Task.Run(() => SendGoodbyeAsync(reason));
                task.Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
        }

        private static IEnumerable<string> GetServerBaseUrls()
        {
            var urls = new List<string>();
            try
            {
                var srv = ServerProvider?.Invoke();
                if (srv.HasValue && !string.IsNullOrWhiteSpace(srv.Value.Host))
                {
                    urls.Add($"http://{srv.Value.Host}:{(srv.Value.Port > 0 ? srv.Value.Port : 5085)}");
                }
            }
            catch { }

            try
            {
                string reg = UdpDiscoveryClient.LoadServerHostFromRegistry();
                if (!string.IsNullOrWhiteSpace(reg)) urls.Add($"http://{reg}:5085");
            }
            catch { }

            urls.Add("http://127.0.0.1:5085");
            return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static object BuildIdentity()
        {
            string order = null;
            try { order = OrderNumberProvider?.Invoke(); } catch { }
            return new
            {
                host = LhmHttpServer.GetPrimaryLocalIp(),
                port = 8085,
                machineName = Environment.MachineName,
                orderNumber = order,
                sessionId = SessionId,
                appVersion = AppInfo.Version
            };
        }

        private static async Task SendGoodbyeAsync(string reason)
        {
            var identity = BuildIdentity();
            string payload = JsonSerializer.Serialize(new
            {
                identity,
                reason,
                closedAtUtc = DateTime.UtcNow.ToString("o"),
                uptimeSeconds = (int)(DateTime.UtcNow - SessionStartedUtc).TotalSeconds
            }, JsonOpts);

            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
            var tasks = GetServerBaseUrls().Select(async baseUrl =>
            {
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync($"{baseUrl}/api/hardware/devices/goodbye", content);
                    Debug.WriteLine($"[SessionLifecycle] Goodbye an {baseUrl}: {(int)resp.StatusCode}");
                }
                catch { }
            });
            await Task.WhenAll(tasks);
        }

        // ------------------------------------------------------------------
        // Absturzbericht der letzten (unsauberen) Sitzung
        // ------------------------------------------------------------------

        private static async Task ProcessPreviousUncleanSessionAsync()
        {
            CrashAnalysisReport report;
            try
            {
                // Windows braucht nach dem Booten etwas, bis alle Ereignisse (z.B. BugCheck 1001) geschrieben sind
                var sinceBoot = TimeSpan.FromMilliseconds(Environment.TickCount64);
                if (sinceBoot < TimeSpan.FromMinutes(2))
                {
                    await Task.Delay(TimeSpan.FromMinutes(2) - sinceBoot);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                report = EventLogAnalyzer.BuildCrashReport(PreviousUncleanSession);
            }
            catch (Exception ex)
            {
                report = CrashAnalysisReport.CreateError(PreviousUncleanSession, $"Absturzanalyse fehlgeschlagen: {ex.Message}");
            }

            SetLastCrashReport(report);
            try { CrashReportCreated?.Invoke(report); } catch { }

            await DeliverCrashReportWithRetryAsync(report);
        }

        private static void SetLastCrashReport(CrashAnalysisReport report)
        {
            lock (_lock)
            {
                _lastCrashReport = report;
                _lastCrashReportDelivered = false;
                _lastCrashReportDeliveredTo = null;
            }

            try
            {
                File.WriteAllText(Path.Combine(CrashReportDirectory, $"{report.ReportId}.json"),
                    JsonSerializer.Serialize(report, JsonOpts), Encoding.UTF8);

                // Nur die letzten 30 Berichte behalten
                foreach (var old in new DirectoryInfo(CrashReportDirectory).GetFiles("*.json")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Skip(30))
                {
                    try { old.Delete(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionLifecycle] Absturzbericht speichern fehlgeschlagen: {ex.Message}");
            }
        }

        private static void LoadLastCrashReportFromDisk()
        {
            try
            {
                var file = new DirectoryInfo(CrashReportDirectory).GetFiles("*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (file == null) return;
                var report = JsonSerializer.Deserialize<CrashAnalysisReport>(File.ReadAllText(file.FullName), JsonOpts);
                if (report != null)
                {
                    lock (_lock)
                    {
                        _lastCrashReport = report;
                        _lastCrashReportDelivered = true; // ältere Berichte wurden bereits zugestellt/angeboten
                    }
                }
            }
            catch { }
        }

        private static async Task DeliverCrashReportWithRetryAsync(CrashAnalysisReport report)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (Volatile.Read(ref _exitHandled) == 1) return;
                if (await TrySendCrashReportAsync(report)) return;
                await Task.Delay(TimeSpan.FromSeconds(attempt < 6 ? 10 : 30));
            }
        }

        /// <summary>Sendet den letzten Absturzbericht (erneut) an Hardware-Check.</summary>
        public static async Task<(bool Success, string Target)> ResendLastCrashReportAsync()
        {
            var report = LastCrashReport;
            if (report == null) return (false, null);
            bool ok = await TrySendCrashReportAsync(report);
            lock (_lock) return (ok, _lastCrashReportDeliveredTo);
        }

        private static async Task<bool> TrySendCrashReportAsync(CrashAnalysisReport report)
        {
            string payload = JsonSerializer.Serialize(new { identity = BuildIdentity(), report }, JsonOpts);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            foreach (var baseUrl in GetServerBaseUrls())
            {
                try
                {
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var resp = await http.PostAsync($"{baseUrl}/api/hardware/devices/crash-report", content);
                    if (resp.IsSuccessStatusCode)
                    {
                        lock (_lock)
                        {
                            _lastCrashReportDelivered = true;
                            _lastCrashReportDeliveredTo = baseUrl;
                        }
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public static object GetSessionInfo() => new
        {
            sessionId = SessionId,
            startedUtc = SessionStartedUtc.ToString("o"),
            uptimeSeconds = (int)(DateTime.UtcNow - SessionStartedUtc).TotalSeconds,
            systemBootTimeUtc = GetSystemBootTimeUtc().ToString("o"),
            markerPath = MarkerPath,
            previousSessionUnclean = PreviousUncleanSession != null,
            previousSession = PreviousUncleanSession,
            lastCrashReport = LastCrashReport == null ? null : new
            {
                reportId = LastCrashReport.ReportId,
                createdUtc = LastCrashReport.CreatedUtc,
                crashType = LastCrashReport.CrashType,
                severity = LastCrashReport.Severity,
                headline = LastCrashReport.Headline,
                delivered = LastCrashReportDelivered
            }
        };
    }
}
