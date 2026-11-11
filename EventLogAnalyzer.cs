using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DiagnoseTool
{
    /// <summary>
    /// Liest die Windows-Ereignisanzeige (System + Application) aus und erstellt eine
    /// tiefentechnische Absturzanalyse inkl. Kritikalitätsbewertung.
    /// </summary>
    public static class EventLogAnalyzer
    {
        public static readonly string[] DefaultLogs = { "System", "Application" };
        private const int MaxEventsPerLog = 4000;
        private static readonly Regex HexCodeRegex = new Regex(@"0x([0-9A-Fa-f]{1,8})\b", RegexOptions.Compiled);

        private static readonly HashSet<string> GpuProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nvlddmkm", "amdkmdag", "amdkmdap", "amdwddmg", "igfx", "igfxn", "igfxnd", "Display", "dxgkrnl", "Microsoft-Windows-DxgKrnl"
        };

        // ------------------------------------------------------------------
        // Rohdaten aus der Ereignisanzeige
        // ------------------------------------------------------------------

        public static List<EventLogEntryInfo> ReadEvents(string log, DateTime fromUtc, DateTime toUtc, int[] levels, int max, out string error, string provider = null)
        {
            error = null;
            if (!OperatingSystem.IsWindows())
            {
                error = "Die Windows-Ereignisanzeige ist nur unter Windows verfügbar.";
                return new List<EventLogEntryInfo>();
            }
            return ReadEventsWindows(log, fromUtc, toUtc, levels, max, provider, out error);
        }

        [SupportedOSPlatform("windows")]
        private static List<EventLogEntryInfo> ReadEventsWindows(string log, DateTime fromUtc, DateTime toUtc, int[] levels, int max, string provider, out string error)
        {
            error = null;
            var list = new List<EventLogEntryInfo>();
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(provider))
            {
                conditions.Add($"Provider[@Name='{provider.Replace("'", "")}']");
            }
            if (levels != null && levels.Length > 0)
            {
                var lv = new List<int>(levels);
                if (lv.Contains(4) && !lv.Contains(0)) lv.Add(0); // "LogAlways" gehört zu Informationen
                conditions.Add("(" + string.Join(" or ", lv.Distinct().Select(l => $"Level={l}")) + ")");
            }
            conditions.Add($"TimeCreated[@SystemTime>='{fromUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}' and @SystemTime<='{toUtc.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']");

            string xpath = $"*[System[{string.Join(" and ", conditions)}]]";

            try
            {
                var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                EventRecord rec;
                while (list.Count < max && (rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        list.Add(ToEntry(rec, log));
                    }
                }
            }
            catch (EventLogNotFoundException)
            {
                error = $"Protokoll '{log}' nicht gefunden.";
            }
            catch (UnauthorizedAccessException)
            {
                error = $"Kein Zugriff auf das Protokoll '{log}' (Administratorrechte erforderlich).";
            }
            catch (Exception ex)
            {
                error = $"Fehler beim Lesen von '{log}': {ex.Message}";
            }

            return list;
        }

        [SupportedOSPlatform("windows")]
        private static EventLogEntryInfo ToEntry(EventRecord rec, string log)
        {
            var timeUtc = (rec.TimeCreated ?? DateTime.Now).ToUniversalTime();
            var entry = new EventLogEntryInfo
            {
                TimeUtc = timeUtc,
                TimeLocal = timeUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("de-DE")),
                Log = rec.LogName ?? log,
                Provider = rec.ProviderName,
                EventId = rec.Id,
                Level = rec.Level ?? 4,
                RecordId = rec.RecordId
            };

            entry.LevelName = LevelName(entry.Level);
            try { entry.Task = rec.TaskDisplayName; } catch { }

            try
            {
                var xml = XDocument.Parse(rec.ToXml());
                XNamespace ns = xml.Root?.Name.Namespace ?? XNamespace.None;
                var eventData = xml.Root?.Element(ns + "EventData");
                if (eventData != null)
                {
                    int idx = 1;
                    foreach (var d in eventData.Elements(ns + "Data"))
                    {
                        string name = (string)d.Attribute("Name");
                        if (string.IsNullOrWhiteSpace(name)) name = $"param{idx}";
                        entry.Data[name] = d.Value;
                        idx++;
                    }
                }

                var userData = xml.Root?.Element(ns + "UserData");
                if (userData != null)
                {
                    foreach (var leaf in userData.Descendants().Where(e => !e.HasElements))
                    {
                        entry.Data[leaf.Name.LocalName] = leaf.Value;
                    }
                }
            }
            catch { }

            try
            {
                entry.Message = rec.FormatDescription();
            }
            catch { }

            if (string.IsNullOrWhiteSpace(entry.Message))
            {
                entry.Message = entry.Data.Count > 0
                    ? string.Join("; ", entry.Data.Select(kv => $"{kv.Key}={kv.Value}"))
                    : $"Ereignis {entry.EventId} von {entry.Provider}";
            }

            return entry;
        }

        private static string LevelName(int level) => level switch
        {
            1 => "Kritisch",
            2 => "Fehler",
            3 => "Warnung",
            5 => "Ausführlich",
            _ => "Information"
        };

        /// <summary>Liest mehrere Protokolle und liefert die Ereignisse absteigend nach Zeit.</summary>
        public static List<EventLogEntryInfo> ReadAll(IEnumerable<string> logs, DateTime fromUtc, DateTime toUtc, int[] levels, int maxPerLog, List<string> errors, out bool truncated)
        {
            truncated = false;
            var all = new List<EventLogEntryInfo>();
            foreach (var log in logs)
            {
                var events = ReadEvents(log, fromUtc, toUtc, levels, maxPerLog, out string err);
                if (err != null) errors?.Add(err);
                if (events.Count >= maxPerLog) truncated = true;
                all.AddRange(events);
            }
            return all.OrderByDescending(e => e.TimeUtc).ToList();
        }

        // ------------------------------------------------------------------
        // Absturzbericht für eine unsauber beendete Sitzung
        // ------------------------------------------------------------------

        public static CrashAnalysisReport BuildCrashReport(SessionMarker prev)
        {
            var now = DateTime.UtcNow;
            var bootUtc = SessionLifecycleService.GetSystemBootTimeUtc();
            var report = new CrashAnalysisReport
            {
                Scope = "crash",
                PreviousSession = prev,
                SystemBootTimeUtc = bootUtc,
                SystemBootTimeLocal = Local(bootUtc),
                WindowEndUtc = now
            };

            DateTime lastAlive = prev?.LastHeartbeatUtc ?? now.AddHours(-1);
            report.SystemRebooted = prev != null && bootUtc > lastAlive.AddSeconds(-30)
                                    && Math.Abs((prev.SystemBootTimeUtc - bootUtc).TotalMinutes) > 1;
            report.CrashTimeUtc = lastAlive;
            report.CrashTimeLocal = Local(lastAlive) + " (letztes Lebenszeichen, Ausfall innerhalb von ca. 10 Sek. danach)";

            var start = lastAlive.AddMinutes(-30);
            if (prev != null && prev.StartedUtc < start && !report.SystemRebooted) start = prev.StartedUtc;
            if (now - start > TimeSpan.FromDays(7)) start = now.AddDays(-7);
            report.WindowStartUtc = start;

            var errors = new List<string>();
            report.Events = ReadAll(DefaultLogs, start, now, new[] { 1, 2, 3, 4 }, MaxEventsPerLog, errors, out bool truncated);
            report.EventsTruncated = truncated;
            if (errors.Count > 0) report.Error = string.Join(" | ", errors);

            AddHistory(report, now);
            AnalyzeFindings(report);
            DetermineCrashVerdict(report, prev, lastAlive, bootUtc);
            FinalizeReport(report, prev);
            return report;
        }

        /// <summary>Manuelle Analyse der letzten <paramref name="hours"/> Stunden (ohne Sitzungsbezug).</summary>
        public static CrashAnalysisReport BuildAdhocReport(int hours)
        {
            hours = Math.Clamp(hours, 1, 24 * 30);
            var now = DateTime.UtcNow;
            var bootUtc = SessionLifecycleService.GetSystemBootTimeUtc();
            var report = new CrashAnalysisReport
            {
                Scope = "adhoc",
                SystemBootTimeUtc = bootUtc,
                SystemBootTimeLocal = Local(bootUtc),
                WindowStartUtc = now.AddHours(-hours),
                WindowEndUtc = now
            };

            var errors = new List<string>();
            report.Events = ReadAll(DefaultLogs, report.WindowStartUtc, now, new[] { 1, 2, 3, 4 }, MaxEventsPerLog, errors, out bool truncated);
            report.EventsTruncated = truncated;
            if (errors.Count > 0) report.Error = string.Join(" | ", errors);

            AddHistory(report, now);
            AnalyzeFindings(report);
            DetermineAdhocVerdict(report, hours);
            FinalizeReport(report, null);
            return report;
        }

        private static void AddHistory(CrashAnalysisReport report, DateTime now)
        {
            var from = now.AddDays(-30);
            var whea = ReadEvents("System", from, now, null, 1000, out _, "Microsoft-Windows-WHEA-Logger");
            report.WheaFatalLast30Days = whea.Count(IsFatalWhea);
            report.WheaCorrectedLast30Days = whea.Count(e => !IsFatalWhea(e));

            var unexpected = ReadEvents("System", from, now, new[] { 1, 2 }, 500, out _, "EventLog");
            report.UnexpectedShutdownsLast30Days = unexpected.Count(e => e.EventId == 6008);

            report.EventCounts = report.Events
                .GroupBy(e => $"{e.Log}/{e.LevelName}")
                .ToDictionary(g => g.Key, g => g.Count());
        }

        // ------------------------------------------------------------------
        // Einzelne Ereignisse bewerten
        // ------------------------------------------------------------------

        private static bool Is(EventLogEntryInfo e, string provider, params int[] ids)
            => string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase) && (ids.Length == 0 || ids.Contains(e.EventId));

        private static bool IsFatalWhea(EventLogEntryInfo e)
            => string.Equals(e.Provider, "Microsoft-Windows-WHEA-Logger", StringComparison.OrdinalIgnoreCase)
               && (e.EventId == 1 || e.EventId == 18 || e.EventId == 20 || e.EventId == 46 || e.Level <= 2);

        private static string Short(string s, int max = 300)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > max ? s.Substring(0, max) + " …" : s;
        }

        private static CrashFinding Finding(EventLogEntryInfo e, string severity, string category, string title, string interpretation, string evidence = null)
            => new CrashFinding
            {
                TimeUtc = e.TimeUtc,
                TimeLocal = e.TimeLocal,
                Log = e.Log,
                Provider = e.Provider,
                EventId = e.EventId,
                Severity = severity,
                Category = category,
                Title = title,
                Interpretation = interpretation,
                Evidence = evidence ?? Short(e.Message)
            };

        private static string WheaCategory(EventLogEntryInfo e)
        {
            string text = ((e.Message ?? "") + " " + string.Join(" ", e.Data.Values)).ToLowerInvariant();
            if (text.Contains("pci")) return CrashCategories.Hardware;
            if (text.Contains("memory") || text.Contains("speicher")) return CrashCategories.Ram;
            if (text.Contains("processor") || text.Contains("prozessor") || text.Contains("cache") || text.Contains("bus/interconnect") || text.Contains("machine check")) return CrashCategories.Cpu;
            return CrashCategories.Hardware;
        }

        private static bool IsDiagnoseToolApp(EventLogEntryInfo e)
        {
            string app = e.Get("AppName") ?? e.Get("param1") ?? "";
            return app.IndexOf("DiagnoseTool", StringComparison.OrdinalIgnoreCase) >= 0
                   || (e.Message ?? "").IndexOf("DiagnoseTool", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static uint? ParseBugCheckCode(EventLogEntryInfo e)
        {
            if (Is(e, "Microsoft-Windows-Kernel-Power", 41))
            {
                string raw = e.Get("BugcheckCode");
                if (uint.TryParse(raw, out uint dec) && dec != 0) return dec;
                if (!string.IsNullOrEmpty(raw) && raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(raw.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex) && hex != 0) return hex;
                return null;
            }

            string source = e.Get("param1") ?? e.Message ?? "";
            var m = HexCodeRegex.Match(source);
            if (m.Success && uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code) && code != 0)
            {
                return code;
            }
            return null;
        }

        private static void AnalyzeFindings(CrashAnalysisReport report)
        {
            foreach (var e in report.Events.OrderBy(x => x.TimeUtc))
            {
                var f = Classify(e);
                if (f != null) report.Findings.Add(f);
            }

            // Wiederholte, korrigierte Hardwarefehler als Frühwarnung zusammenfassen
            if (report.WheaCorrectedLast30Days >= 10)
            {
                report.Findings.Add(new CrashFinding
                {
                    Severity = report.WheaCorrectedLast30Days >= 50 ? CrashSeverity.High : CrashSeverity.Medium,
                    Category = CrashCategories.Hardware,
                    Title = $"{report.WheaCorrectedLast30Days} korrigierte Hardwarefehler (WHEA) in 30 Tagen",
                    Interpretation = "Die Hardware meldet regelmäßig korrigierte Fehler. Das ist ein Frühwarnzeichen für instabile CPU/RAM/PCIe-Komponenten oder Spannungen."
                });
            }
            if (report.UnexpectedShutdownsLast30Days >= 2)
            {
                report.Findings.Add(new CrashFinding
                {
                    Severity = report.UnexpectedShutdownsLast30Days >= 4 ? CrashSeverity.High : CrashSeverity.Medium,
                    Category = CrashCategories.Hardware,
                    Title = $"{report.UnexpectedShutdownsLast30Days} unerwartete Herunterfahren in 30 Tagen",
                    Interpretation = "Das System ist wiederholt unkontrolliert ausgegangen – ein wiederkehrendes Problem ist wahrscheinlich."
                });
            }
        }

        private static CrashFinding Classify(EventLogEntryInfo e)
        {
            // --- Systemabsturz / Stromverlust ---
            if (Is(e, "Microsoft-Windows-Kernel-Power", 41))
            {
                uint? bc = ParseBugCheckCode(e);
                bool powerButton = (e.Get("PowerButtonTimestamp") ?? "0") != "0" || string.Equals(e.Get("LongPowerButtonPressDetected"), "true", StringComparison.OrdinalIgnoreCase);
                string sleepRaw = e.Get("SleepInProgress");
                bool sleep = !string.IsNullOrEmpty(sleepRaw) && sleepRaw != "0" && !sleepRaw.Equals("false", StringComparison.OrdinalIgnoreCase);
                string details = $"BugcheckCode={e.Get("BugcheckCode") ?? "-"}, PowerButtonTimestamp={e.Get("PowerButtonTimestamp") ?? "-"}, SleepInProgress={e.Get("SleepInProgress") ?? "-"}";

                if (bc.HasValue)
                {
                    var b = BugCheckCatalog.Describe(bc.Value);
                    return Finding(e, CrashSeverity.Max(b.Severity, CrashSeverity.High), b.Category,
                        $"Kernel-Power 41: Neustart nach Bluescreen {b.CodeHex} ({b.Name})", b.Description, details);
                }
                if (powerButton)
                {
                    return Finding(e, CrashSeverity.High, CrashCategories.Hardware,
                        "Kernel-Power 41: System per Power-Taste hart ausgeschaltet",
                        "Das System wurde ohne ordnungsgemäßes Herunterfahren über die Power-Taste ausgeschaltet. Meist eingefroren/hängendes System (Hardware, Treiber oder Überhitzung).", details);
                }
                return Finding(e, CrashSeverity.Critical, CrashCategories.Power,
                    "Kernel-Power 41: Schlagartiger Ausfall ohne Bluescreen",
                    sleep
                        ? "Das System ist während eines Energiesparvorgangs ausgefallen."
                        : "Das System hat schlagartig die Stromversorgung verloren oder wurde hart zurückgesetzt, ohne dass Windows einen Bluescreen schreiben konnte. Typisch für Netzteil-Abschaltung, thermische Notabschaltung, Stromausfall oder massiv instabile Spannungen.",
                    details);
            }

            if (Is(e, "Microsoft-Windows-WER-SystemErrorReporting", 1001) || Is(e, "BugCheck", 1001))
            {
                uint? bc = ParseBugCheckCode(e);
                var b = bc.HasValue ? BugCheckCatalog.Describe(bc.Value) : null;
                return Finding(e, b?.Severity ?? CrashSeverity.High, b?.Category ?? CrashCategories.Driver,
                    b != null ? $"Bluescreen {b.CodeHex} {b.Name}" : "Bluescreen (BugCheck)",
                    b?.Description ?? "Windows wurde nach einem schwerwiegenden Fehler neu gestartet.");
            }

            if (Is(e, "EventLog", 6008))
            {
                return Finding(e, CrashSeverity.High, CrashCategories.Unknown,
                    "Vorheriges Herunterfahren war unerwartet",
                    $"Windows wurde zuvor unerwartet beendet (Zeitpunkt laut Protokoll: {e.Get("param2")} {e.Get("param1")}).");
            }

            // --- Geplante Vorgänge ---
            if (Is(e, "User32", 1074))
            {
                return Finding(e, CrashSeverity.Info, CrashCategories.Planned,
                    $"Geplantes Herunterfahren/Neustart durch {e.Get("param1") ?? "Prozess"}",
                    $"Art: {e.Get("param5") ?? "-"}, Grund: {e.Get("param3") ?? "-"}, Benutzer: {e.Get("param7") ?? "-"}");
            }
            if (Is(e, "Microsoft-Windows-Kernel-General", 13) || Is(e, "EventLog", 6006))
            {
                return Finding(e, CrashSeverity.Info, CrashCategories.Planned, "Betriebssystem wurde ordnungsgemäß beendet", "Geordnetes Herunterfahren von Windows.");
            }
            if (Is(e, "Microsoft-Windows-Kernel-General", 12))
            {
                return Finding(e, CrashSeverity.Info, CrashCategories.Planned, "Systemstart", "Windows wurde gestartet.");
            }

            // --- Hardwarefehler (WHEA) ---
            if (Is(e, "Microsoft-Windows-WHEA-Logger"))
            {
                string cat = WheaCategory(e);
                string src = e.Get("ErrorSource") ?? "-";
                string type = e.Get("ErrorType") ?? "-";
                string details = $"ErrorSource={src}, ErrorType={type}, APIC-ID={e.Get("ApicId") ?? "-"}, MCA-Bank={e.Get("MCABank") ?? "-"}";
                if (IsFatalWhea(e))
                {
                    return Finding(e, CrashSeverity.Critical, cat,
                        $"Schwerwiegender Hardwarefehler (WHEA {e.EventId})",
                        "Die Hardware hat einen NICHT korrigierbaren Fehler gemeldet. Dies ist ein eindeutiger Hardware-/Stabilitätsfehler.", details + " | " + Short(e.Message, 200));
                }
                return Finding(e, CrashSeverity.Medium, cat,
                    $"Korrigierter Hardwarefehler (WHEA {e.EventId})",
                    "Die Hardware hat einen Fehler selbst korrigiert. Einzelne Ereignisse sind unkritisch, gehäuft deuten sie auf instabile Komponenten hin.", details);
            }

            // --- Grafik ---
            if (Is(e, "Display", 4101))
            {
                return Finding(e, CrashSeverity.High, CrashCategories.Gpu, "Grafiktreiber reagierte nicht mehr und wurde wiederhergestellt (TDR)",
                    "Die GPU war für mehrere Sekunden blockiert. Ursachen: Treiber, instabiler GPU-Takt, Überhitzung oder Stromversorgung.");
            }
            if (GpuProviders.Contains(e.Provider ?? "") && e.Level <= 2)
            {
                return Finding(e, CrashSeverity.High, CrashCategories.Gpu, $"Grafiktreiber-Fehler ({e.Provider} #{e.EventId})",
                    "Der Grafiktreiber hat einen Fehler gemeldet (z.B. GPU hängt, Speicherfehler, Ressourcenfehler).");
            }

            // --- Datenträger ---
            if (Is(e, "disk", 7))
                return Finding(e, CrashSeverity.Critical, CrashCategories.Storage, "Fehlerhafter Block auf Datenträger", "Der Datenträger hat einen defekten Sektor gemeldet – Ausfallrisiko/Datenverlust.");
            if (Is(e, "disk", 11) || Is(e, "stornvme", 11))
                return Finding(e, CrashSeverity.High, CrashCategories.Storage, "Controllerfehler am Datenträger", "Der Speichercontroller hat einen Fehler erkannt (Kabel, Steckplatz, SSD-Firmware oder defekter Datenträger).");
            if (Is(e, "disk", 51))
                return Finding(e, CrashSeverity.High, CrashCategories.Storage, "Fehler beim Auslagern (Paging) auf Datenträger", "Lese-/Schreibfehler während des Auslagerns – Datenträger oder Verbindung instabil.");
            if (Is(e, "disk", 153))
                return Finding(e, CrashSeverity.Medium, CrashCategories.Storage, "E/A-Vorgang am Datenträger wiederholt", "Der Datenträger hat verzögert reagiert, die Anfrage musste wiederholt werden.");
            if (Is(e, "disk", 154))
                return Finding(e, CrashSeverity.High, CrashCategories.Storage, "Hardwarefehler des Datenträgers", "E/A-Vorgang wegen Hardwarefehler fehlgeschlagen.");
            if (Is(e, "Ntfs", 55) || Is(e, "Ntfs", 137) || Is(e, "Ntfs", 140))
                return Finding(e, CrashSeverity.High, CrashCategories.Storage, $"Dateisystemproblem (NTFS {e.EventId})", "Das Dateisystem ist beschädigt oder konnte Daten nicht sicher schreiben.");
            if ((Is(e, "storahci", 129) || Is(e, "iaStorAC", 129) || Is(e, "iaStorA", 129) || Is(e, "stornvme", 129)))
                return Finding(e, CrashSeverity.Medium, CrashCategories.Storage, "Reset des Datenträger-Controllers", "Ein Gerät am Speichercontroller musste zurückgesetzt werden (hängende SSD/HDD, Kabel, Energiesparmodus).");
            if (Is(e, "volmgr", 46) || Is(e, "volmgr", 161) || Is(e, "volmgr", 162))
                return Finding(e, CrashSeverity.Medium, CrashCategories.Software, "Speicherabbild (Crash-Dump) konnte nicht erstellt werden", "Für den Absturz liegt evtl. kein Dump vor; Auslagerungsdatei/Dump-Einstellungen prüfen.");

            // --- Thermik / Energie ---
            if (Is(e, "Microsoft-Windows-Kernel-Processor-Power", 37))
                return Finding(e, CrashSeverity.Medium, CrashCategories.Thermal, "CPU-Takt durch Firmware begrenzt", "Die Prozessorgeschwindigkeit wurde von der Firmware gedrosselt – meist Temperatur- oder Leistungsgrenze erreicht.");
            if (Is(e, "Microsoft-Windows-Resource-Exhaustion-Detector", 2004))
                return Finding(e, CrashSeverity.Medium, CrashCategories.Ram, "Arbeitsspeicher wurde knapp", "Windows hat einen Mangel an virtuellem Speicher erkannt (z.B. durch RAM-Stresstest oder Speicherleck).");

            // --- Anwendungen ---
            if (Is(e, "Application Error", 1000))
            {
                bool own = IsDiagnoseToolApp(e);
                string exc = e.Get("ExceptionCode") ?? e.Get("param7") ?? "-";
                string module = e.Get("ModuleName") ?? e.Get("param4") ?? "-";
                return Finding(e, own ? CrashSeverity.Medium : CrashSeverity.Low, own ? CrashCategories.App : CrashCategories.Software,
                    $"Anwendungsabsturz: {e.Get("AppName") ?? e.Get("param1") ?? "?"}",
                    $"Fehlerhaftes Modul: {module}, Ausnahmecode: {exc} ({ExplainExceptionCode(exc)}).");
            }
            if (Is(e, "Application Hang", 1002))
            {
                bool own = IsDiagnoseToolApp(e);
                return Finding(e, own ? CrashSeverity.Medium : CrashSeverity.Low, own ? CrashCategories.App : CrashCategories.Software,
                    $"Anwendung reagierte nicht: {e.Get("param1") ?? "?"}", "Die Anwendung hing und wurde beendet.");
            }
            if (Is(e, ".NET Runtime", 1026) || Is(e, ".NET Runtime", 1025))
            {
                bool own = IsDiagnoseToolApp(e);
                return Finding(e, own ? CrashSeverity.Medium : CrashSeverity.Low, own ? CrashCategories.App : CrashCategories.Software,
                    ".NET-Laufzeitfehler", "Eine .NET-Anwendung wurde wegen einer unbehandelten Ausnahme beendet.");
            }
            if (Is(e, "Service Control Manager", 7031) || Is(e, "Service Control Manager", 7034))
            {
                return Finding(e, CrashSeverity.Low, CrashCategories.Software, "Dienst unerwartet beendet", "Ein Windows-Dienst wurde unerwartet beendet.");
            }
            if (Is(e, "Microsoft-Windows-Kernel-PnP", 219))
            {
                return Finding(e, CrashSeverity.Low, CrashCategories.Driver, "Treiber konnte nicht geladen werden", "Ein Gerätetreiber wurde beim Start nicht geladen.");
            }

            return null;
        }

        private static string ExplainExceptionCode(string code)
        {
            string c = (code ?? "").ToLowerInvariant().Replace("0x", "");
            return c switch
            {
                "c0000005" => "Zugriffsverletzung",
                "e0434352" => ".NET-Ausnahme",
                "c0000409" => "Stack-Buffer-Überlauf / Fail-Fast",
                "c00000fd" => "Stapelüberlauf",
                "c0000374" => "Heap-Beschädigung",
                "80000003" => "Haltepunkt",
                "c000001d" => "Ungültige CPU-Anweisung (Hinweis auf instabile CPU)",
                "c0000006" => "Seitenfehler beim Lesen (Datenträger/RAM)",
                _ => "siehe Microsoft-Dokumentation"
            };
        }

        // ------------------------------------------------------------------
        // Gesamturteil
        // ------------------------------------------------------------------

        private static void SetVerdict(CrashAnalysisReport r, string type, string display, string severity, string category, string headline, string summary, bool system, bool real)
        {
            r.CrashType = type;
            r.CrashTypeDisplay = display;
            r.Severity = severity;
            r.Category = category;
            r.Headline = headline;
            r.Summary = summary;
            r.IsSystemCrash = system;
            r.IsRealCrash = real;
        }

        private static void DetermineCrashVerdict(CrashAnalysisReport r, SessionMarker prev, DateTime lastAlive, DateTime bootUtc)
        {
            var bootWindowStart = bootUtc.AddMinutes(-10);
            var aroundBoot = r.Events.Where(e => e.TimeUtc >= bootWindowStart).ToList();
            var beforeBoot = r.Events.Where(e => e.TimeUtc >= lastAlive.AddMinutes(-2) && e.TimeUtc <= bootUtc.AddMinutes(1)).ToList();

            if (!r.SystemRebooted)
            {
                // PC lief weiter -> nur das Programm ist ausgefallen
                var appCrash = r.Events.FirstOrDefault(e => (Is(e, "Application Error", 1000) || Is(e, ".NET Runtime", 1026) || Is(e, "Application Hang", 1002))
                                                            && IsDiagnoseToolApp(e) && e.TimeUtc >= lastAlive.AddMinutes(-2));
                if (prev?.UnhandledException != null || appCrash != null)
                {
                    string exc = prev?.UnhandledException != null
                        ? Short(prev.UnhandledException.Split('\n').FirstOrDefault(), 200)
                        : Short(appCrash?.Message, 200);
                    SetVerdict(r, "app_crash", "Programmabsturz (DiagnoseTool)", CrashSeverity.Medium, CrashCategories.App,
                        "Das DiagnoseTool ist abgestürzt – der PC lief weiter",
                        $"Windows wurde nicht neu gestartet. Das DiagnoseTool wurde durch einen Programmfehler beendet ({exc}). Kein Hinweis auf einen Hardwaredefekt, sofern keine weiteren Befunde vorliegen.",
                        false, true);
                    if (r.Events.Any(e => Is(e, "Application Error", 1000) && IsDiagnoseToolApp(e) && (e.Get("ExceptionCode") ?? e.Get("param7") ?? "").Contains("c000001d", StringComparison.OrdinalIgnoreCase)))
                    {
                        r.Severity = CrashSeverity.High;
                        r.Category = CrashCategories.Cpu;
                        r.LikelyCauses.Add("Ungültige CPU-Anweisung im Programm – unter Last ein Hinweis auf eine instabile CPU");
                    }
                }
                else
                {
                    SetVerdict(r, "app_terminated", "Programm extern beendet", CrashSeverity.Low, CrashCategories.App,
                        "Das DiagnoseTool wurde beendet, ohne sich abzumelden – kein PC-Absturz",
                        "Windows lief durchgehend weiter und es gibt keinen Absturzeintrag des Programms. Das Programm wurde vermutlich über den Task-Manager, eine Abmeldung oder ein anderes Programm beendet.",
                        false, false);
                }
                return;
            }

            // --- PC wurde neu gestartet ---
            var fatalWhea = beforeBoot.Concat(aroundBoot).FirstOrDefault(IsFatalWhea);
            var bugcheckEvent = aroundBoot.FirstOrDefault(e => Is(e, "Microsoft-Windows-WER-SystemErrorReporting", 1001) || Is(e, "BugCheck", 1001));
            var kp41 = aroundBoot.FirstOrDefault(e => Is(e, "Microsoft-Windows-Kernel-Power", 41));
            uint? bcCode = (bugcheckEvent != null ? ParseBugCheckCode(bugcheckEvent) : null) ?? (kp41 != null ? ParseBugCheckCode(kp41) : null);

            if (bcCode.HasValue)
            {
                var b = BugCheckCatalog.Describe(bcCode.Value);
                b.Source = bugcheckEvent != null ? "WER-SystemErrorReporting 1001" : "Kernel-Power 41";
                if (bugcheckEvent != null)
                {
                    var paramMatch = HexCodeRegex.Matches(bugcheckEvent.Get("param1") ?? bugcheckEvent.Message ?? "");
                    b.Parameters = paramMatch.Cast<Match>().Skip(1).Select(m => m.Value).Take(4).ToList();
                    b.DumpPath = bugcheckEvent.Get("param2");
                }
                else if (kp41 != null)
                {
                    b.Parameters = new[] { "BugcheckParameter1", "BugcheckParameter2", "BugcheckParameter3", "BugcheckParameter4" }
                        .Select(k => kp41.Get(k)).Where(v => !string.IsNullOrEmpty(v)).ToList();
                }
                r.BugCheck = b;

                string severity = fatalWhea != null ? CrashSeverity.Critical : CrashSeverity.Max(b.Severity, CrashSeverity.High);
                SetVerdict(r, "system_bugcheck", "Bluescreen (Systemabsturz)", severity, b.Category,
                    $"PC-Absturz mit Bluescreen {b.CodeHex} ({b.Name})",
                    $"{b.Description} Windows hat den PC daraufhin neu gestartet.",
                    true, true);
                if (fatalWhea != null)
                {
                    r.TechnicalDetails.Add("Zusätzlich wurde ein schwerwiegender WHEA-Hardwarefehler protokolliert – Hardwareursache sehr wahrscheinlich.");
                }
                return;
            }

            if (fatalWhea != null)
            {
                string cat = WheaCategory(fatalWhea);
                SetVerdict(r, "system_hardware_error", "Schwerwiegender Hardwarefehler", CrashSeverity.Critical, cat,
                    $"PC-Absturz durch nicht korrigierbaren Hardwarefehler ({cat})",
                    "Die Hardware hat einen fatalen Fehler (WHEA) gemeldet, anschließend ist das System ausgefallen.",
                    true, true);
                return;
            }

            if (kp41 != null)
            {
                bool powerButton = (kp41.Get("PowerButtonTimestamp") ?? "0") != "0" || string.Equals(kp41.Get("LongPowerButtonPressDetected"), "true", StringComparison.OrdinalIgnoreCase);
                if (powerButton)
                {
                    SetVerdict(r, "system_forced_reset", "Hart ausgeschaltet (System hing)", CrashSeverity.High, CrashCategories.Hardware,
                        "PC hing und wurde per Power-Taste ausgeschaltet",
                        "Windows wurde nicht ordnungsgemäß beendet; die Power-Taste wurde gedrückt. Das System war vermutlich eingefroren (Hardware-Instabilität, Treiber oder Überhitzung).",
                        true, true);
                }
                else
                {
                    SetVerdict(r, "system_power_loss", "Schlagartiger Ausfall / Stromverlust", CrashSeverity.Critical, CrashCategories.Power,
                        "PC ist schlagartig ausgegangen (kein Bluescreen)",
                        "Kernel-Power 41 ohne Bugcheck-Code: Das System hat die Stromversorgung verloren bzw. wurde hart zurückgesetzt, ohne dass Windows reagieren konnte. Unter Last ist dies typisch für Netzteil-Schutzabschaltung, thermische Notabschaltung oder instabile Spannungen.",
                        true, true);
                }
                return;
            }

            bool planned = beforeBoot.Any(e => Is(e, "User32", 1074) || Is(e, "Microsoft-Windows-Kernel-General", 13) || Is(e, "EventLog", 6006));
            if (planned)
            {
                var p = beforeBoot.FirstOrDefault(e => Is(e, "User32", 1074));
                SetVerdict(r, "system_planned_shutdown", "Geplanter Neustart / Herunterfahren", CrashSeverity.Info, CrashCategories.Planned,
                    "Kein Absturz: Windows wurde geordnet heruntergefahren bzw. neu gestartet",
                    p != null
                        ? $"Ausgelöst durch {p.Get("param1") ?? "einen Prozess"} (Grund: {p.Get("param3") ?? "-"}, Benutzer: {p.Get("param7") ?? "-"}). Das DiagnoseTool wurde dabei von Windows beendet."
                        : "Windows hat das System geordnet beendet. Das DiagnoseTool wurde dabei von Windows beendet.",
                    false, false);
                return;
            }

            if (aroundBoot.Any(e => Is(e, "EventLog", 6008)))
            {
                SetVerdict(r, "system_unexpected_shutdown", "Unerwartetes Herunterfahren", CrashSeverity.High, CrashCategories.Unknown,
                    "PC wurde unerwartet beendet",
                    "Windows meldet ein unerwartetes Herunterfahren, aber ohne Bluescreen- oder Kernel-Power-Details.",
                    true, true);
                return;
            }

            SetVerdict(r, "system_unknown_reboot", "Neustart ohne Protokolleintrag", CrashSeverity.High, CrashCategories.Unknown,
                "PC wurde neu gestartet – Ursache nicht protokolliert",
                "Das System wurde seit dem letzten Lebenszeichen neu gestartet, die Ereignisanzeige enthält jedoch keinen eindeutigen Eintrag (evtl. fehlende Rechte oder Protokolle wurden geleert).",
                true, true);
        }

        private static void DetermineAdhocVerdict(CrashAnalysisReport r, int hours)
        {
            var worst = r.Findings.OrderByDescending(f => CrashSeverity.Rank(f.Severity)).FirstOrDefault();
            int crashes = r.Findings.Count(f => f.EventId == 41 || f.Title.StartsWith("Bluescreen"));
            var bc = r.Events.Where(e => Is(e, "Microsoft-Windows-WER-SystemErrorReporting", 1001) || Is(e, "BugCheck", 1001))
                .Select(ParseBugCheckCode).FirstOrDefault(c => c.HasValue);
            if (bc.HasValue) r.BugCheck = BugCheckCatalog.Describe(bc.Value);

            if (worst == null || CrashSeverity.Rank(worst.Severity) <= 1)
            {
                SetVerdict(r, "no_crash", "Keine Auffälligkeiten", CrashSeverity.Info, CrashCategories.Unknown,
                    $"Keine kritischen Ereignisse in den letzten {hours} Std.",
                    "In der Ereignisanzeige wurden keine Abstürze oder Hardwarefehler gefunden.", false, false);
                return;
            }

            var lastCrash = r.Findings.Where(f => f.EventId == 41).OrderByDescending(f => f.TimeUtc).FirstOrDefault();
            if (lastCrash != null) { r.CrashTimeUtc = lastCrash.TimeUtc; r.CrashTimeLocal = lastCrash.TimeLocal + " (Systemstart nach Ausfall)"; }

            SetVerdict(r, crashes > 0 ? (bc.HasValue ? "system_bugcheck" : "system_power_loss") : "unknown",
                crashes > 0 ? "Systemabstürze im Zeitraum" : "Auffälligkeiten im Zeitraum",
                worst.Severity, worst.Category,
                crashes > 0 ? $"{crashes} Systemabsturz/-abstürze in den letzten {hours} Std. – schwerster Befund: {worst.Title}"
                            : $"Schwerster Befund der letzten {hours} Std.: {worst.Title}",
                worst.Interpretation, crashes > 0, crashes > 0);
        }

        private static void FinalizeReport(CrashAnalysisReport r, SessionMarker prev)
        {
            int score = CrashSeverity.Rank(r.Severity) switch { 4 => 90, 3 => 70, 2 => 45, 1 => 20, _ => 5 };
            var act = prev?.LastActivity;

            if (r.IsSystemCrash && act != null)
            {
                var load = new List<string>();
                if (act.IsCpuStressRunning) load.Add("CPU-Stresstest");
                if (act.IsGpuStressRunning) load.Add("GPU-Stresstest (FurMark)");
                if (act.IsRamStressRunning) load.Add("RAM-Stresstest");
                if (act.IsBatteryTestRunning) load.Add("Akkutest");

                if (load.Count > 0)
                {
                    r.TechnicalDetails.Add($"Zum Zeitpunkt des Ausfalls liefen: {string.Join(", ", load)}.");
                    r.LikelyCauses.Add("Ausfall unter Volllast – Stabilität von Netzteil, Kühlung, CPU/GPU/RAM unter Last nicht gegeben");
                    score += 5;
                }

                bool cpuHot = (act.CpuTemp ?? 0) >= 95;
                bool gpuHot = (act.GpuTemp ?? 0) >= 88 || (act.GpuHotSpotTemp ?? 0) >= 105;
                if (cpuHot || gpuHot)
                {
                    string T(float? v) => v.HasValue ? $"{v.Value:F0} °C" : "–";
                    r.LikelyCauses.Insert(0, $"Überhitzung: zuletzt CPU {T(act.CpuTemp)}, GPU {T(act.GpuTemp)} (Hotspot {T(act.GpuHotSpotTemp)}) – thermische Notabschaltung wahrscheinlich");
                    if (r.CrashType == "system_power_loss") r.Category = CrashCategories.Thermal;
                    score += 5;
                }

                float totalPower = (act.CpuPowerWatts ?? 0) + (act.GpuPowerWatts ?? 0);
                if (totalPower > 0)
                {
                    r.TechnicalDetails.Add($"Letzte Leistungsaufnahme: CPU {(act.CpuPowerWatts ?? 0):F0} W + GPU {(act.GpuPowerWatts ?? 0):F0} W = ca. {totalPower:F0} W (ohne Rest-System).");
                }

                if (act.IsOnAcPower == false && act.BatteryPercent.HasValue && act.BatteryPercent.Value <= 5 && r.CrashType == "system_power_loss")
                {
                    r.Severity = CrashSeverity.Low;
                    r.Category = CrashCategories.Power;
                    r.Headline = "PC ist wegen leerem Akku ausgegangen";
                    r.Summary = $"Das Gerät lief im Akkubetrieb mit nur noch {act.BatteryPercent:F0} % Ladung – Ausfall durch leeren Akku, kein Hardwaredefekt.";
                    r.LikelyCauses.Clear();
                    r.LikelyCauses.Add("Akku vollständig entladen");
                    score = 20;
                }
            }

            if (r.IsSystemCrash && r.WheaCorrectedLast30Days >= 10) score += 5;
            if (r.UnexpectedShutdownsLast30Days >= 3) score += 5;

            r.SeverityScore = Math.Clamp(score, 0, 100);
            r.SeverityExplanation = CrashSeverity.Explain(r.Severity);

            if (CrashCategories.Guidance.TryGetValue(r.Category ?? CrashCategories.Unknown, out var g))
            {
                foreach (var c in g.Causes) if (!r.LikelyCauses.Contains(c)) r.LikelyCauses.Add(c);
                foreach (var rec in g.Recommendations) if (!r.Recommendations.Contains(rec)) r.Recommendations.Add(rec);
            }
            if (r.BugCheck != null && !string.IsNullOrWhiteSpace(r.BugCheck.DumpPath))
            {
                r.Recommendations.Insert(0, $"Speicherabbild auswerten: {r.BugCheck.DumpPath} (WinDbg: !analyze -v)");
            }
            if (r.IsRealCrash && r.IsSystemCrash)
            {
                r.Recommendations.Add("Nach Behebung: Volllasttest (CPU + GPU) über mindestens 60 Minuten wiederholen");
            }

            r.TechnicalDetails.Add($"Systemstart (aktuell): {Local(r.SystemBootTimeUtc)}");
            if (prev != null)
            {
                r.TechnicalDetails.Add($"Vorherige Sitzung {prev.SessionId}: gestartet {Local(prev.StartedUtc)}, letztes Lebenszeichen {Local(prev.LastHeartbeatUtc)}, Systemstart damals {Local(prev.SystemBootTimeUtc)}.");
            }
            r.TechnicalDetails.Add($"Ausgewertete Ereignisse: {r.Events.Count}{(r.EventsTruncated ? " (gekürzt)" : "")}, Befunde: {r.Findings.Count}.");

            r.Findings = r.Findings
                .OrderByDescending(f => CrashSeverity.Rank(f.Severity))
                .ThenByDescending(f => f.TimeUtc ?? DateTime.MinValue)
                .ToList();
        }

        private static string Local(DateTime utc)
            => utc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("de-DE"));
    }
}
