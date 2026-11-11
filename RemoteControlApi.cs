using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DiagnoseTool
{
    /// <summary>
    /// Zentrale Versionsinformation (aus der Assembly, gepflegt in DiagnoseTool.csproj).
    /// </summary>
    public static class AppInfo
    {
        public static string Version { get; } = ResolveVersion();

        public static string ShortVersion
        {
            get
            {
                var parts = Version.Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : Version;
            }
        }

        private static string ResolveVersion()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(info))
                {
                    // "+<commit-hash>" Suffix (SourceLink) entfernen
                    int plus = info.IndexOf('+');
                    return plus > 0 ? info.Substring(0, plus) : info;
                }
                var v = asm.GetName().Version;
                if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { }
            return "0.0.0";
        }
    }

    /// <summary>
    /// Ergebnis einer per REST-API ausgelösten Aktion.
    /// </summary>
    public class RemoteActionResult
    {
        public bool Success { get; set; }
        public string Action { get; set; }
        public string Message { get; set; }
        public object Data { get; set; }

        public static RemoteActionResult Ok(string action, string message, object data = null)
            => new RemoteActionResult { Success = true, Action = action, Message = message, Data = data };

        public static RemoteActionResult Fail(string action, string message, object data = null)
            => new RemoteActionResult { Success = false, Action = action, Message = message, Data = data };
    }

    /// <summary>
    /// Binäre Datei (PDF/CSV), die über die REST-API ausgeliefert wird.
    /// </summary>
    public class RemoteFileResult
    {
        public byte[] Content { get; set; }
        public string FileName { get; set; }
        public string ContentType { get; set; } = "application/octet-stream";
        public string Error { get; set; }
        public bool Success => Content != null && Content.Length > 0 && string.IsNullOrEmpty(Error);
    }

    /// <summary>
    /// Vollständige Fernsteuer-Schnittstelle des DiagnoseTools. Wird von <see cref="MainViewModel"/>
    /// implementiert und vom <see cref="LhmHttpServer"/> (Port 8085) über REST angesprochen, damit
    /// Hardware-Check (ASP.NET) den kompletten Funktionsumfang der Desktop-App steuern kann.
    /// </summary>
    public interface IDiagnoseRemoteControl
    {
        // --- Status / Informationen ---
        object GetAppState();
        object GetDiagnosticsSnapshot();
        object GetTools();
        object GetRepairCommands();
        object GetHWiNFOState();
        object GetCpuStressDetails();
        object GetRamStressDetails();
        object GetGpuStressDetails();
        object GetBatteryTestDetails();
        object GetRecordingDetails(bool includeSamples, int? lastSamples);

        // --- Tools (OCCT, FurMark, Prime95, Victoria) ---
        RemoteActionResult LaunchTool(string key);
        RemoteActionResult SetToolPath(string key, string path);
        RemoteActionResult ClearToolPath(string key);
        RemoteActionResult RefreshTools();

        // --- Reparatur (SFC, DISM, CHKDSK) ---
        RemoteActionResult RunRepair(string key);

        // --- HWiNFO Fallback-Sensorik ---
        RemoteActionResult StartHWiNFO();

        // --- CPU/RAM Stresstest Voreinstellungen ---
        RemoteActionResult ConfigureCpuStress(int? threads, int? durationIndex);
        RemoteActionResult ConfigureRamStress(int? targetPercent, int? durationIndex);

        // --- Akkutest ---
        RemoteActionResult StartBatteryTest(int? durationMinutes, bool openVideo);
        RemoteActionResult StopBatteryTest();

        // --- Aufzeichnung ---
        RemoteActionResult ClearRecording();
        RemoteFileResult ExportRecordingCsv();

        // --- PDF-Berichte ---
        RemoteFileResult GenerateSystemReportPdf();
        RemoteFileResult GenerateBatteryReportPdf();

        // --- Oberfläche / System ---
        RemoteActionResult SelectTab(string tab);
        RemoteActionResult ShowStatusMessage(string message, bool isError);
        RemoteActionResult WindowAction(string action);
        RemoteActionResult SetSleepPrevention(bool enabled);
        RemoteActionResult AnnounceToHardwareCheck();
    }

    /// <summary>
    /// Selbstbeschreibung aller REST-Endpunkte des DiagnoseTools (GET /api bzw. /api/capabilities).
    /// </summary>
    public static class RemoteApiCatalog
    {
        public static object Build(int port)
        {
            var get = new List<object>
            {
                Ep("/data.json", "LibreHardwareMonitor-kompatibler Sensorbaum"),
                Ep("/api", "Diese Übersicht aller Endpunkte (Alias: /api/capabilities)"),
                Ep("/api/status", "Online-Status, Version, Auftragsnummer, Serververbindung"),
                Ep("/api/telemetry", "Kompakte Live-Telemetrie (CPU/GPU/RAM/Akku/Laufwerke/Lüfter/Stresstests)"),
                Ep("/api/diagnostics", "Vollständiger Rohdaten-Snapshot aller Sensoren"),
                Ep("/api/system", "System-/Mainboard-Informationen inkl. Seriennummern"),
                Ep("/api/battery", "Akku-Informationen (Kapazität, Gesundheit, Ladezustand)"),
                Ep("/api/app/state", "Zustand der Oberfläche (Tab, Statusmeldung, Admin, Treiber, HWiNFO, Energiesparmodus)"),
                Ep("/api/tools", "Diagnose-Tools (OCCT, FurMark, Prime95, Victoria) mit Pfad & Verfügbarkeit"),
                Ep("/api/repair", "Verfügbare Reparaturbefehle (sfc, dism_check, dism_restore, chkdsk)"),
                Ep("/api/hwinfo", "HWiNFO Status (gefunden, läuft, Fallback aktiv)"),
                Ep("/api/stress", "Kompakter Stresstest-Status (Alias: /api/control/status)"),
                Ep("/api/stress/cpu", "CPU-Stresstest inkl. Log, Ergebnis und Einstellungen"),
                Ep("/api/stress/ram", "RAM-Stresstest inkl. Log, Zyklen, Fehler und Einstellungen"),
                Ep("/api/stress/gpu", "FurMark GPU-Stresstest Status"),
                Ep("/api/battery-test", "Akkutest inkl. Restzeit, Log und Ergebnis"),
                Ep("/api/recording", "Werte-Aufzeichnung (Query: samples=true, last=N)"),
                Ep("/api/recording/samples", "Alle aufgezeichneten Messpunkte (Query: last=N)"),
                Ep("/api/recording/export", "Aufzeichnung als CSV-Datei"),
                Ep("/api/report/system", "System-Prüfbericht als PDF (aus der Werte-Aufzeichnung)"),
                Ep("/api/report/battery", "Batterie-Prüfbericht als PDF"),
            };

            var post = new List<object>
            {
                Ep("/api/control/stress/cpu/start", "CPU-Stresstest starten", "{ durationMinutes?, threads? }"),
                Ep("/api/control/stress/cpu/stop", "CPU-Stresstest stoppen"),
                Ep("/api/control/stress/cpu/config", "CPU-Stresstest Voreinstellung", "{ threads?, durationIndex? (0-7) }"),
                Ep("/api/control/stress/ram/start", "RAM-Stresstest starten", "{ durationMinutes?, targetPercent? (30-95) }"),
                Ep("/api/control/stress/ram/stop", "RAM-Stresstest stoppen"),
                Ep("/api/control/stress/ram/config", "RAM-Stresstest Voreinstellung", "{ targetPercent?, durationIndex? (0-7) }"),
                Ep("/api/control/stress/gpu/donut", "FurMark 3D-Donut starten", "{ durationMinutes? }"),
                Ep("/api/control/stress/gpu/gui", "FurMark GUI öffnen"),
                Ep("/api/control/stress/gpu/stop", "FurMark beenden"),
                Ep("/api/control/stress/full/start", "Volllast CPU + GPU", "{ durationMinutes? }"),
                Ep("/api/control/stress/full/stop", "Volllast stoppen"),
                Ep("/api/control/stress/stop-all", "Alle Stresstests stoppen"),
                Ep("/api/control/battery-test/start", "Akkutest starten (Alias: stress/battery/start)", "{ durationMinutes? (Std. 30), openVideo? (Std. true) }"),
                Ep("/api/control/battery-test/stop", "Akkutest abbrechen (Alias: stress/battery/stop)"),
                Ep("/api/control/recording/start", "Werte-Aufzeichnung starten", "{ orderNumber? }"),
                Ep("/api/control/recording/stop", "Werte-Aufzeichnung stoppen"),
                Ep("/api/control/recording/clear", "Aufgezeichnete Messpunkte verwerfen"),
                Ep("/api/control/order", "Auftragsnummer setzen", "{ orderNumber }"),
                Ep("/api/control/tools/refresh", "Laufwerke & USB-Sticks nach Tools durchsuchen"),
                Ep("/api/control/tools/{key}/launch", "Tool starten (OCCT, FurMark, Prime95, Victoria)"),
                Ep("/api/control/tools/{key}/path", "Manuellen Tool-Pfad setzen", "{ path }"),
                Ep("/api/control/tools/{key}/clear-path", "Manuellen Tool-Pfad zurücksetzen"),
                Ep("/api/control/repair/{key}", "Reparaturbefehl starten (sfc, dism_check, dism_restore, chkdsk)"),
                Ep("/api/control/hwinfo/start", "HWiNFO64 starten und Sensoren konfigurieren"),
                Ep("/api/control/ui/tab", "Tab in der Oberfläche wählen", "{ tab: 0-6 | dashboard|tools|gpu|repair|info|battery|cpu|ram }"),
                Ep("/api/control/ui/status", "Statusmeldung in der Oberfläche anzeigen", "{ message, isError? }"),
                Ep("/api/control/ui/window", "Fenster steuern", "{ action: minimize|maximize|restore|focus }"),
                Ep("/api/control/power/sleep", "Standby/Ruhezustand blockieren oder freigeben", "{ preventSleep: bool }"),
                Ep("/api/control/hardware-check/announce", "Erneut bei Hardware-Check anmelden (HTTP + UDP)"),
                Ep("/api/control/app/exit", "DiagnoseTool beenden (Aliase: close, shutdown)"),
            };

            return new
            {
                appName = "CPM DiagnoseTool",
                version = AppInfo.Version,
                port,
                machineName = Environment.MachineName,
                get,
                post
            };
        }

        private static object Ep(string path, string description, string body = null)
            => new { path, description, body };
    }

    internal static class TabResolver
    {
        private static readonly Dictionary<string, int> Names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "dashboard", 0 },
            { "tools", 1 },
            { "gpu", 1 },
            { "gpustress", 1 },
            { "repair", 2 },
            { "info", 3 },
            { "battery", 4 },
            { "batterytest", 4 },
            { "cpu", 5 },
            { "cpustress", 5 },
            { "ram", 6 },
            { "ramstress", 6 }
        };

        public static string[] TabNames { get; } = { "dashboard", "tools", "repair", "info", "battery", "cpu", "ram" };

        public static bool TryResolve(string value, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (int.TryParse(value, out int n) && n >= 0 && n <= 6)
            {
                index = n;
                return true;
            }
            return Names.TryGetValue(value.Replace("-", "").Replace("_", ""), out index);
        }

        public static string NameOf(int index)
            => index >= 0 && index < TabNames.Length ? TabNames[index] : index.ToString();
    }
}
