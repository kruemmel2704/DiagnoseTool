using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DiagnoseTool
{
    public static class CrashSeverity
    {
        public const string Info = "INFO";
        public const string Low = "NIEDRIG";
        public const string Medium = "MITTEL";
        public const string High = "HOCH";
        public const string Critical = "KRITISCH";

        public static int Rank(string severity) => severity switch
        {
            Critical => 4,
            High => 3,
            Medium => 2,
            Low => 1,
            _ => 0
        };

        public static string Max(string a, string b) => Rank(a) >= Rank(b) ? a : b;

        public static string FromRank(int rank) => rank switch
        {
            >= 4 => Critical,
            3 => High,
            2 => Medium,
            1 => Low,
            _ => Info
        };

        public static string Explain(string severity) => severity switch
        {
            Critical => "Kritisch: Gerät nicht ausliefern. Ursache muss vor Freigabe behoben und mit erneutem Volllast-Test verifiziert werden.",
            High => "Hoch: Deutliches Stabilitätsproblem. Ursache eingrenzen (Treiber/Hardware) und Test wiederholen.",
            Medium => "Mittel: Auffälligkeit mit Risiko für die Stabilität. Beobachten und nach Möglichkeit beheben.",
            Low => "Niedrig: Kein Hardware-Defekt erkennbar. Hinweis dokumentieren.",
            _ => "Info: Kein Absturz bzw. geordneter Vorgang."
        };
    }

    public class EventLogEntryInfo
    {
        public DateTime TimeUtc { get; set; }
        public string TimeLocal { get; set; }
        public string Log { get; set; }
        public string Provider { get; set; }
        public int EventId { get; set; }
        public int Level { get; set; }
        public string LevelName { get; set; }
        public string Task { get; set; }
        public long? RecordId { get; set; }
        public string Message { get; set; }
        public Dictionary<string, string> Data { get; set; } = new Dictionary<string, string>();

        public string Get(string key)
            => Data != null && Data.TryGetValue(key, out var v) ? v : null;
    }

    public class CrashFinding
    {
        public DateTime? TimeUtc { get; set; }
        public string TimeLocal { get; set; }
        public string Log { get; set; }
        public string Provider { get; set; }
        public int? EventId { get; set; }
        public string Severity { get; set; } = CrashSeverity.Info;
        public string Category { get; set; }
        public string Title { get; set; }
        public string Interpretation { get; set; }
        public string Evidence { get; set; }
    }

    public class BugCheckInfo
    {
        public uint Code { get; set; }
        public string CodeHex { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public List<string> Parameters { get; set; } = new List<string>();
        public string DumpPath { get; set; }
        public string Source { get; set; }
    }

    public class CrashAnalysisReport
    {
        public string ReportId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 12);
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string MachineName { get; set; } = Environment.MachineName;
        public string AppVersion { get; set; } = AppInfo.Version;
        public string OsDescription { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;

        /// <summary>"crash" = Bericht zur unsauber beendeten Sitzung, "adhoc" = manuelle Analyse eines Zeitraums.</summary>
        public string Scope { get; set; } = "crash";

        /// <summary>
        /// system_bugcheck | system_hardware_error | system_power_loss | system_forced_reset |
        /// system_unexpected_shutdown | system_unknown_reboot | system_planned_shutdown |
        /// app_crash | app_terminated | no_crash | unknown
        /// </summary>
        public string CrashType { get; set; } = "unknown";
        public string CrashTypeDisplay { get; set; }
        public bool IsSystemCrash { get; set; }
        public bool IsRealCrash { get; set; }
        public bool SystemRebooted { get; set; }

        public string Severity { get; set; } = CrashSeverity.Info;
        public int SeverityScore { get; set; }
        public string SeverityExplanation { get; set; }
        public string Category { get; set; }
        public string Headline { get; set; }
        public string Summary { get; set; }

        public DateTime? CrashTimeUtc { get; set; }
        public string CrashTimeLocal { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndUtc { get; set; }
        public DateTime SystemBootTimeUtc { get; set; }
        public string SystemBootTimeLocal { get; set; }

        public SessionMarker PreviousSession { get; set; }
        public BugCheckInfo BugCheck { get; set; }

        public List<string> LikelyCauses { get; set; } = new List<string>();
        public List<string> Recommendations { get; set; } = new List<string>();
        public List<string> TechnicalDetails { get; set; } = new List<string>();
        public List<CrashFinding> Findings { get; set; } = new List<CrashFinding>();

        public Dictionary<string, int> EventCounts { get; set; } = new Dictionary<string, int>();
        public int WheaCorrectedLast30Days { get; set; }
        public int WheaFatalLast30Days { get; set; }
        public int UnexpectedShutdownsLast30Days { get; set; }

        /// <summary>Vollständiger Auszug aus der Ereignisanzeige (System + Application) im Analysezeitraum.</summary>
        public List<EventLogEntryInfo> Events { get; set; } = new List<EventLogEntryInfo>();
        public bool EventsTruncated { get; set; }

        public string Error { get; set; }

        public static CrashAnalysisReport CreateError(SessionMarker prev, string error)
        {
            var now = DateTime.UtcNow;
            return new CrashAnalysisReport
            {
                PreviousSession = prev,
                CrashType = "unknown",
                CrashTypeDisplay = "Unbekannt",
                Severity = CrashSeverity.High,
                SeverityScore = 60,
                SeverityExplanation = CrashSeverity.Explain(CrashSeverity.High),
                Category = "Unbekannt",
                Headline = "Programm wurde nicht ordnungsgemäß beendet – Analyse nicht möglich",
                Summary = error,
                Error = error,
                IsRealCrash = true,
                WindowStartUtc = prev?.LastHeartbeatUtc ?? now,
                WindowEndUtc = now,
                SystemBootTimeUtc = SessionLifecycleService.GetSystemBootTimeUtc()
            };
        }

        public string ToText()
        {
            var de = new CultureInfo("de-DE");
            string L(DateTime? utc) => utc.HasValue ? utc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", de) : "-";

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("  CPM DIAGNOSETOOL - ABSTURZANALYSE (WINDOWS-EREIGNISANZEIGE)");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Bericht-ID:        {ReportId}");
            sb.AppendLine($"Erstellt:          {L(CreatedUtc)}");
            sb.AppendLine($"Rechner:           {MachineName}  ({OsDescription})");
            sb.AppendLine($"DiagnoseTool:      v{AppVersion}");
            if (PreviousSession != null)
            {
                sb.AppendLine($"Auftrags-Nr.:      {(string.IsNullOrWhiteSpace(PreviousSession.OrderNumber) ? "-" : PreviousSession.OrderNumber)}");
                sb.AppendLine($"Sitzung gestartet: {L(PreviousSession.StartedUtc)}");
                sb.AppendLine($"Letztes Lebenszeichen: {L(PreviousSession.LastHeartbeatUtc)}");
            }
            sb.AppendLine($"Systemstart:       {L(SystemBootTimeUtc)}");
            sb.AppendLine($"Analysezeitraum:   {L(WindowStartUtc)} - {L(WindowEndUtc)}");
            sb.AppendLine();
            sb.AppendLine($"ERGEBNIS:          {Headline}");
            sb.AppendLine($"Art:               {CrashTypeDisplay}");
            sb.AppendLine($"Kritikalität:      {Severity} ({SeverityScore}/100)");
            sb.AppendLine($"                   {SeverityExplanation}");
            sb.AppendLine($"Kategorie:         {Category}");
            if (CrashTimeUtc.HasValue) sb.AppendLine($"Absturzzeitpunkt:  {L(CrashTimeUtc)}");
            sb.AppendLine();
            sb.AppendLine("Zusammenfassung:");
            sb.AppendLine("  " + Summary);

            if (BugCheck != null)
            {
                sb.AppendLine();
                sb.AppendLine("Bluescreen (BugCheck):");
                sb.AppendLine($"  Code:        {BugCheck.CodeHex} {BugCheck.Name}");
                sb.AppendLine($"  Bedeutung:   {BugCheck.Description}");
                if (BugCheck.Parameters.Count > 0) sb.AppendLine($"  Parameter:   {string.Join(", ", BugCheck.Parameters)}");
                if (!string.IsNullOrWhiteSpace(BugCheck.DumpPath)) sb.AppendLine($"  Speicherabbild: {BugCheck.DumpPath}");
            }

            if (PreviousSession?.LastActivity != null)
            {
                var a = PreviousSession.LastActivity;
                sb.AppendLine();
                sb.AppendLine("Zustand kurz vor dem Ausfall:");
                sb.AppendLine($"  Stresstests: CPU={(a.IsCpuStressRunning ? "läuft" : "aus")}, RAM={(a.IsRamStressRunning ? "läuft" : "aus")}, GPU={(a.IsGpuStressRunning ? "läuft" : "aus")}, Akkutest={(a.IsBatteryTestRunning ? "läuft" : "aus")}");
                sb.AppendLine($"  CPU: {F(a.CpuTemp, "°C")} / {F(a.CpuLoad, "%")} / {F(a.CpuPowerWatts, "W")}   GPU: {F(a.GpuTemp, "°C")} (Hotspot {F(a.GpuHotSpotTemp, "°C")}) / {F(a.GpuLoad, "%")} / {F(a.GpuPowerWatts, "W")}   RAM: {F(a.RamPercent, "%")}");
                if (a.BatteryPercent.HasValue) sb.AppendLine($"  Akku: {a.BatteryPercent:F0} % ({(a.IsOnAcPower == true ? "Netzbetrieb" : "Akkubetrieb")})");
            }

            if (PreviousSession?.UnhandledException != null)
            {
                sb.AppendLine();
                sb.AppendLine("Unbehandelte Ausnahme im DiagnoseTool:");
                sb.AppendLine("  " + PreviousSession.UnhandledException.Replace("\n", "\n  "));
            }

            AppendList(sb, "Wahrscheinliche Ursachen", LikelyCauses);
            AppendList(sb, "Empfohlene Maßnahmen", Recommendations);
            AppendList(sb, "Technische Details", TechnicalDetails);

            sb.AppendLine();
            sb.AppendLine($"Hardware-Fehler (WHEA) letzte 30 Tage: {WheaFatalLast30Days} schwerwiegend, {WheaCorrectedLast30Days} korrigiert");
            sb.AppendLine($"Unerwartete Herunterfahren letzte 30 Tage: {UnexpectedShutdownsLast30Days}");

            sb.AppendLine();
            sb.AppendLine("--- BEFUNDE ---");
            foreach (var f in Findings)
            {
                sb.AppendLine($"[{f.TimeLocal ?? "-"}] [{f.Severity}] [{f.Category}] {f.Title}  ({f.Log}/{f.Provider} #{f.EventId})");
                sb.AppendLine($"    -> {f.Interpretation}");
                if (!string.IsNullOrWhiteSpace(f.Evidence)) sb.AppendLine($"    -> Beleg: {f.Evidence}");
            }

            sb.AppendLine();
            sb.AppendLine($"--- VOLLSTÄNDIGER AUSZUG EREIGNISANZEIGE ({Events.Count} Ereignisse{(EventsTruncated ? ", gekürzt" : "")}) ---");
            foreach (var e in Events.OrderBy(e => e.TimeUtc))
            {
                sb.AppendLine($"{e.TimeLocal}\t{e.Log}\t{e.LevelName}\t{e.Provider}\t{e.EventId}\t{(e.Message ?? "").Replace("\r", " ").Replace("\n", " ")}");
            }

            if (!string.IsNullOrWhiteSpace(Error))
            {
                sb.AppendLine();
                sb.AppendLine("FEHLER: " + Error);
            }
            return sb.ToString();
        }

        private static string F(float? v, string unit) => v.HasValue ? $"{v.Value:F1} {unit}" : "-";

        private static void AppendList(StringBuilder sb, string title, List<string> items)
        {
            if (items == null || items.Count == 0) return;
            sb.AppendLine();
            sb.AppendLine(title + ":");
            foreach (var i in items) sb.AppendLine("  - " + i);
        }
    }
}
