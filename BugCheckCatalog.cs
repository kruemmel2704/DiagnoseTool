using System.Collections.Generic;

namespace DiagnoseTool
{
    public static class CrashCategories
    {
        public const string Ram = "Arbeitsspeicher (RAM)";
        public const string Cpu = "Prozessor (CPU)";
        public const string Gpu = "Grafikkarte (GPU)";
        public const string Storage = "Datenträger (SSD/HDD)";
        public const string Driver = "Treiber";
        public const string Power = "Stromversorgung / Netzteil";
        public const string Thermal = "Thermik / Kühlung";
        public const string Hardware = "Hardware (allgemein)";
        public const string Software = "Software / Betriebssystem";
        public const string App = "DiagnoseTool (Programm)";
        public const string Planned = "Geplanter Vorgang";
        public const string Unknown = "Unbekannt";

        public static readonly Dictionary<string, (string[] Causes, string[] Recommendations)> Guidance =
            new Dictionary<string, (string[], string[])>
            {
                [Ram] = (
                    new[] { "Defektes oder instabiles RAM-Modul", "Zu aggressives XMP/EXPO-Profil bzw. falsche Timings/Spannung", "Inkompatible Module oder schlecht sitzendes Modul" },
                    new[] { "MemTest86 / Windows-Speicherdiagnose über mehrere Durchläufe ausführen", "XMP/EXPO deaktivieren und auf JEDEC-Standardtakt testen", "Module einzeln testen, Steckplätze tauschen, Kontakte prüfen" }),
                [Cpu] = (
                    new[] { "Instabiler CPU-Takt/Undervolt/Übertaktung", "CPU-Spannungsversorgung (VRM) überlastet oder überhitzt", "Defekter Prozessor (Machine-Check-Fehler)" },
                    new[] { "BIOS auf Standardwerte zurücksetzen (Übertaktung/Undervolt entfernen)", "BIOS/Microcode aktualisieren", "Integrierten CPU-Stresstest mit Temperaturüberwachung wiederholen", "Bei wiederholten Machine-Check-Fehlern CPU tauschen/reklamieren" }),
                [Gpu] = (
                    new[] { "Grafiktreiber abgestürzt oder fehlerhaft", "Instabile GPU-Übertaktung bzw. Werks-OC", "Überhitzung der Grafikkarte oder unzureichende Stromversorgung (PCIe-Stecker/Netzteil)", "Defekter Grafikspeicher (VRAM)" },
                    new[] { "Grafiktreiber mit DDU vollständig entfernen und aktuelle WHQL-Version installieren", "GPU-Takt auf Referenz setzen und FurMark-GPU-Test erneut durchführen", "PCIe-Stromkabel und Netzteilleistung prüfen", "Temperaturen und Lüfter der GPU kontrollieren" }),
                [Storage] = (
                    new[] { "Defekte Sektoren oder ausfallende SSD/HDD", "Fehlerhaftes SATA-/NVMe-Kabel bzw. Steckplatz", "Veraltete SSD-Firmware oder Controllertreiber" },
                    new[] { "SMART-Werte des Datenträgers prüfen", "chkdsk /r ausführen, Datensicherung anlegen", "SSD-Firmware und Storage-Treiber aktualisieren, Kabel/Steckplatz tauschen" }),
                [Driver] = (
                    new[] { "Fehlerhafter Kernel-Treiber (z.B. Netzwerk, Chipsatz, Grafik, Antivirus)", "Veraltete oder inkompatible Treiberversion", "Selten: instabiler RAM, der Treiberdaten beschädigt" },
                    new[] { "Speicherabbild (MEMORY.DMP/Minidump) mit WinDbg/BlueScreenView auswerten, um den Treiber zu identifizieren", "Chipsatz-, Grafik-, Netzwerk- und Storage-Treiber aktualisieren", "Kürzlich installierte Software/Treiber entfernen", "RAM-Test zur Absicherung durchführen" }),
                [Power] = (
                    new[] { "Netzteil liefert unter Last nicht genug Leistung oder schaltet ab (OCP/OPP)", "Lose Strom-/Netzkabel oder Steckdosenleiste", "Stromausfall/Spannungseinbruch", "Mainboard-Spannungsversorgung (VRM) überhitzt/defekt", "Thermische Notabschaltung der CPU/GPU" },
                    new[] { "Netzteil (Leistung, Alter, Kabel) prüfen, ggf. mit Ersatznetzteil gegentesten", "Alle Stromstecker (24-Pin, CPU 8-Pin, PCIe) prüfen", "Volllasttest mit Temperaturüberwachung wiederholen", "USV/andere Steckdose verwenden, um Netzprobleme auszuschließen" }),
                [Thermal] = (
                    new[] { "CPU/GPU erreichen die thermische Grenze (Notabschaltung/Drosselung)", "Wärmeleitpaste ausgetrocknet, Kühler schlecht montiert", "Lüfter defekt oder Gehäuse unzureichend belüftet" },
                    new[] { "Kühler-Montage und Wärmeleitpaste prüfen/erneuern", "Lüfter und Luftstrom kontrollieren, Staub entfernen", "Volllasttest erneut mit Temperaturüberwachung durchführen" }),
                [Hardware] = (
                    new[] { "Nicht korrigierbarer Hardware-Fehler (CPU, RAM, PCIe-Gerät oder Mainboard)", "Instabile Übertaktung/Spannungen", "Unzureichende oder fehlerhafte Stromversorgung" },
                    new[] { "BIOS auf Standardwerte, BIOS-Update installieren", "RAM- und CPU-Stresstests einzeln durchführen, um die Komponente einzugrenzen", "PCIe-Karten/NVMe einzeln entfernen und erneut testen", "Netzteil gegentesten" }),
                [Software] = (
                    new[] { "Fehlerhafte Systemdatei oder beschädigte Windows-Installation", "Kritischer Systemprozess beendet" },
                    new[] { "sfc /scannow und DISM /RestoreHealth ausführen (über DiagnoseTool → Reparatur)", "Windows-Updates installieren" }),
                [App] = (
                    new[] { "Programmfehler im DiagnoseTool", "Programm wurde extern beendet (Task-Manager, Abmeldung)" },
                    new[] { "DiagnoseTool neu starten, Test fortsetzen", "Bei wiederholtem Auftreten Bericht an die Entwicklung weitergeben" }),
                [Planned] = (
                    new[] { "Geordnetes Herunterfahren/Neustarten (Benutzer, Windows Update, Richtlinie)" },
                    new[] { "Kein Handlungsbedarf, Test ggf. fortsetzen", "Windows-Update-Neustarts während Tests deaktivieren (Nutzungszeit/Updates pausieren)" }),
                [Unknown] = (
                    new[] { "Keine eindeutigen Einträge in der Ereignisanzeige" },
                    new[] { "Test wiederholen und Stromversorgung/Temperaturen beobachten" })
            };
    }

    public static class BugCheckCatalog
    {
        public class Entry
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public string Category { get; set; }
            public string Severity { get; set; }
        }

        private static Entry E(string name, string category, string severity, string description)
            => new Entry { Name = name, Category = category, Severity = severity, Description = description };

        private static readonly Dictionary<uint, Entry> Codes = new Dictionary<uint, Entry>
        {
            [0x0A] = E("IRQL_NOT_LESS_OR_EQUAL", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat auf einer zu hohen Interrupt-Ebene auf ungültigen Speicher zugegriffen. Meist Treiberfehler, gelegentlich instabiler RAM."),
            [0x19] = E("BAD_POOL_HEADER", CrashCategories.Driver, CrashSeverity.High, "Der Kernel-Speicherpool ist beschädigt. Ursache meist ein fehlerhafter Treiber oder instabiler RAM."),
            [0x1A] = E("MEMORY_MANAGEMENT", CrashCategories.Ram, CrashSeverity.Critical, "Schwerer Fehler der Speicherverwaltung. Sehr häufig defekter oder instabiler Arbeitsspeicher."),
            [0x1E] = E("KMODE_EXCEPTION_NOT_HANDLED", CrashCategories.Driver, CrashSeverity.High, "Ein Kernel-Treiber hat eine nicht behandelte Ausnahme ausgelöst."),
            [0x24] = E("NTFS_FILE_SYSTEM", CrashCategories.Storage, CrashSeverity.High, "Fehler im NTFS-Dateisystemtreiber – Datenträger- oder Dateisystemschaden möglich."),
            [0x3B] = E("SYSTEM_SERVICE_EXCEPTION", CrashCategories.Driver, CrashSeverity.High, "Ausnahme beim Wechsel von Benutzer- in Kernel-Modus – typischerweise Grafik-, Antiviren- oder Systemtreiber."),
            [0x4E] = E("PFN_LIST_CORRUPT", CrashCategories.Ram, CrashSeverity.Critical, "Die Seitenrahmen-Datenbank des Speichers ist beschädigt – starker Hinweis auf RAM-Defekt."),
            [0x50] = E("PAGE_FAULT_IN_NONPAGED_AREA", CrashCategories.Ram, CrashSeverity.High, "Zugriff auf ungültigen, nicht auslagerbaren Speicher – RAM-Fehler oder fehlerhafter Treiber."),
            [0x7A] = E("KERNEL_DATA_INPAGE_ERROR", CrashCategories.Storage, CrashSeverity.Critical, "Kerneldaten konnten nicht von der Auslagerungsdatei gelesen werden – Datenträger-, Kabel- oder Controllerfehler."),
            [0x7B] = E("INACCESSIBLE_BOOT_DEVICE", CrashCategories.Storage, CrashSeverity.Critical, "Das Startlaufwerk ist nicht erreichbar – Datenträger, Controller-Modus (AHCI/RAID) oder Treiber."),
            [0x7E] = E("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", CrashCategories.Driver, CrashSeverity.High, "Ein System-Thread (meist Treiber) hat eine nicht behandelte Ausnahme ausgelöst."),
            [0x7F] = E("UNEXPECTED_KERNEL_MODE_TRAP", CrashCategories.Hardware, CrashSeverity.Critical, "Unerwartete CPU-Ausnahme im Kernel – häufig Hardwaredefekt, instabile Übertaktung oder Speicherfehler."),
            [0x9C] = E("MACHINE_CHECK_EXCEPTION", CrashCategories.Cpu, CrashSeverity.Critical, "Die CPU hat einen schweren Hardwarefehler (Machine Check) gemeldet."),
            [0x9F] = E("DRIVER_POWER_STATE_FAILURE", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat einen Energiezustandswechsel (Standby/Ruhezustand) nicht korrekt abgeschlossen."),
            [0xA0] = E("INTERNAL_POWER_ERROR", CrashCategories.Power, CrashSeverity.Medium, "Fataler Fehler der Energieverwaltung (Ruhezustand/ACPI)."),
            [0xC2] = E("BAD_POOL_CALLER", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat eine ungültige Speicherpool-Anforderung gestellt."),
            [0xC5] = E("DRIVER_CORRUPTED_EXPOOL", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat den Systempool beschädigt."),
            [0xCA] = E("PNP_DETECTED_FATAL_ERROR", CrashCategories.Driver, CrashSeverity.High, "Plug-and-Play hat einen schweren Treiber-/Gerätefehler erkannt."),
            [0xD1] = E("DRIVER_IRQL_NOT_LESS_OR_EQUAL", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat auf ungültigen Speicher zugegriffen (häufig Netzwerk-, WLAN- oder Storage-Treiber)."),
            [0xE2] = E("MANUALLY_INITIATED_CRASH", CrashCategories.Planned, CrashSeverity.Low, "Der Bluescreen wurde absichtlich (Tastenkombination/Debugger) ausgelöst."),
            [0xEF] = E("CRITICAL_PROCESS_DIED", CrashCategories.Software, CrashSeverity.Critical, "Ein für Windows kritischer Prozess wurde beendet – Systemdateien, Datenträger oder RAM können Ursache sein."),
            [0xF4] = E("CRITICAL_OBJECT_TERMINATION", CrashCategories.Storage, CrashSeverity.Critical, "Kritischer Prozess beendet – sehr häufig durch Lesefehler des Systemdatenträgers."),
            [0xFC] = E("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber wollte nicht ausführbaren Speicher ausführen."),
            [0x101] = E("CLOCK_WATCHDOG_TIMEOUT", CrashCategories.Cpu, CrashSeverity.Critical, "Ein CPU-Kern reagiert nicht mehr – typisch für instabile CPU (Übertaktung/Undervolt, Spannung) oder CPU-Defekt."),
            [0x109] = E("CRITICAL_STRUCTURE_CORRUPTION", CrashCategories.Ram, CrashSeverity.Critical, "Kritische Kernel-Struktur beschädigt – Treiber oder defekter RAM."),
            [0x10E] = E("VIDEO_MEMORY_MANAGEMENT_INTERNAL", CrashCategories.Gpu, CrashSeverity.High, "Interner Fehler der Grafikspeicherverwaltung (Treiber oder VRAM)."),
            [0x113] = E("VIDEO_DXGKRNL_FATAL_ERROR", CrashCategories.Gpu, CrashSeverity.High, "Fataler Fehler im DirectX-Grafikkernel."),
            [0x116] = E("VIDEO_TDR_FAILURE", CrashCategories.Gpu, CrashSeverity.High, "Die Grafikkarte hat nicht mehr reagiert und konnte nicht zurückgesetzt werden (TDR) – Treiber, Übertaktung, Hitze oder GPU-Defekt."),
            [0x117] = E("VIDEO_TDR_TIMEOUT_DETECTED", CrashCategories.Gpu, CrashSeverity.High, "Zeitüberschreitung der Grafikkarte (TDR)."),
            [0x119] = E("VIDEO_SCHEDULER_INTERNAL_ERROR", CrashCategories.Gpu, CrashSeverity.High, "Interner Fehler des GPU-Schedulers – meist Grafiktreiber oder instabile GPU."),
            [0x124] = E("WHEA_UNCORRECTABLE_ERROR", CrashCategories.Hardware, CrashSeverity.Critical, "Nicht korrigierbarer Hardwarefehler (CPU, RAM, PCIe, Mainboard) – häufig instabile Spannung/Übertaktung oder Hardwaredefekt."),
            [0x127] = E("PAGE_NOT_ZERO", CrashCategories.Ram, CrashSeverity.Critical, "Eine als leer markierte Speicherseite enthält Daten – RAM-Defekt wahrscheinlich."),
            [0x12B] = E("FAULTY_HARDWARE_CORRUPTED_PAGE", CrashCategories.Ram, CrashSeverity.Critical, "Speicherseite durch Hardwarefehler beschädigt – RAM-Defekt."),
            [0x133] = E("DPC_WATCHDOG_VIOLATION", CrashCategories.Driver, CrashSeverity.High, "Ein Treiber hat zu lange blockiert (häufig SSD-Firmware/Storage-Treiber oder Grafik)."),
            [0x139] = E("KERNEL_SECURITY_CHECK_FAILURE", CrashCategories.Driver, CrashSeverity.High, "Kernel hat eine beschädigte Datenstruktur entdeckt – Treiber oder RAM."),
            [0x13A] = E("KERNEL_MODE_HEAP_CORRUPTION", CrashCategories.Driver, CrashSeverity.High, "Beschädigter Kernel-Heap – fehlerhafter Treiber."),
            [0x144] = E("BUGCODE_USB3_DRIVER", CrashCategories.Driver, CrashSeverity.High, "Fehler im USB-3-Treiber oder angeschlossenem USB-Gerät."),
            [0x14F] = E("PDC_WATCHDOG_TIMEOUT", CrashCategories.Power, CrashSeverity.Medium, "Zeitüberschreitung beim Moderner-Standby-Übergang."),
            [0x154] = E("UNEXPECTED_STORE_EXCEPTION", CrashCategories.Storage, CrashSeverity.Critical, "Fehler im Speicher-Komprimierungsspeicher – häufig Datenträger- oder RAM-Problem."),
            [0x15F] = E("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", CrashCategories.Power, CrashSeverity.Medium, "Zeitüberschreitung im Modern Standby."),
            [0x18B] = E("SECURE_KERNEL_ERROR", CrashCategories.Software, CrashSeverity.High, "Fehler im Secure Kernel (VBS/Hyper-V)."),
            [0x1C8] = E("MANUALLY_INITIATED_POWER_BUTTON_HOLD", CrashCategories.Hardware, CrashSeverity.High, "Das System hing und die Power-Taste wurde lange gedrückt gehalten."),
            [0xC000021A] = E("STATUS_SYSTEM_PROCESS_TERMINATED", CrashCategories.Software, CrashSeverity.Critical, "Ein Windows-Subsystemprozess wurde beendet – beschädigte Systemdateien.")
        };

        public static BugCheckInfo Describe(uint code)
        {
            Codes.TryGetValue(code, out var e);
            return new BugCheckInfo
            {
                Code = code,
                CodeHex = $"0x{code:X8}",
                Name = e?.Name ?? "UNBEKANNTER_BUGCHECK",
                Description = e?.Description ?? "Bluescreen mit nicht katalogisiertem Fehlercode – Speicherabbild auswerten (WinDbg: !analyze -v).",
                Category = e?.Category ?? CrashCategories.Driver,
                Severity = e?.Severity ?? CrashSeverity.High
            };
        }
    }
}
