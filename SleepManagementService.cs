using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace DiagnoseTool
{
    /// <summary>
    /// Verhindert zuverlässig, dass Windows während der Ausführung des DiagnoseTools
    /// oder während Stresstests/Aufzeichnungen in den Ruhezustand (Hibernate),
    /// Standby (Sleep) oder Energiesparmodus wechselt.
    /// </summary>
    public static class SleepManagementService
    {
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_SYSTEM_REQUIRED = 0x00000001,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_USER_PRESENT = 0x00000004,
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private static Timer _heartbeatTimer;
        private static bool _isActive = false;

        public static bool IsActive => _isActive;

        public static void PreventSleepAndHibernate()
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                // 1. Windows API: Kontinuierliche Anforderung für System & Display
                SetThreadExecutionState(
                    EXECUTION_STATE.ES_CONTINUOUS |
                    EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                    EXECUTION_STATE.ES_AWAYMODE_REQUIRED |
                    EXECUTION_STATE.ES_DISPLAY_REQUIRED
                );

                _isActive = true;

                // 2. Heartbeat alle 30 Sekunden: Verhindert, dass aggressive OEM-Energiepläne
                // (z.B. Lenovo Vantage, Dell Power Manager, HP Assistant) den Status zurücksetzen
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = new Timer(_ =>
                {
                    try
                    {
                        if (_isActive && OperatingSystem.IsWindows())
                        {
                            SetThreadExecutionState(
                                EXECUTION_STATE.ES_SYSTEM_REQUIRED |
                                EXECUTION_STATE.ES_DISPLAY_REQUIRED
                            );
                        }
                    }
                    catch { }
                }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                // 3. PowerCFG: Standby- und Ruhezustand-Timeout für Netzbetrieb auf "Nie" (0) setzen
                OverridePowerCfgTimeouts();

                Debug.WriteLine("[SleepManagementService] Standby & Hibernate erfolgreich blockiert.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SleepManagementService] Fehler beim Deaktivieren des Energiesparmodus: {ex.Message}");
            }
        }

        private static void OverridePowerCfgTimeouts()
        {
            try
            {
                // Setzt Standby und Hibernate Timeout für Netzbetrieb (AC) und Akku (DC) auf 0 (Nie)
                var commands = new[]
                {
                    "/change standby-timeout-ac 0",
                    "/change hibernate-timeout-ac 0",
                    "/change monitor-timeout-ac 0"
                };

                foreach (var cmd in commands)
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "powercfg",
                        Arguments = cmd,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(1000);
                }
            }
            catch { }
        }

        public static void RestoreNormalSleep()
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                _isActive = false;
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = null;

                // Setzt den Thread-Status wieder auf normal zurück
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                Debug.WriteLine("[SleepManagementService] Normaler Energiesparmodus wiederhergestellt.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SleepManagementService] Fehler beim Wiederherstellen: {ex.Message}");
            }
        }
    }
}

