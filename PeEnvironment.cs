using System;
using System.IO;

namespace DiagnoseTool
{
    /// <summary>
    /// Erkennt, ob das DiagnoseTool in einer WinPE-Umgebung (DiagnosePE) laeuft.
    /// In WinPE gibt es kein Direct3D -> die App rendert dann per Software (GDI)
    /// und startet im Vollbild; ausserdem erscheint der PE-only Netzwerk-Tab.
    /// </summary>
    public static class PeEnvironment
    {
        private static readonly Lazy<bool> _isWinPe = new Lazy<bool>(DetectWinPe);

        /// <summary>True, wenn wir in WinPE (DiagnosePE) laufen.</summary>
        public static bool IsWinPe => _isWinPe.Value;

        private static bool DetectWinPe()
        {
            try
            {
                // 1) Primaer: von DiagnosePE gesetzte Kennung (zuverlaessig, plattformneutral).
                var flag = Environment.GetEnvironmentVariable("DIAGNOSETOOL_PE");
                if (!string.IsNullOrEmpty(flag) &&
                    (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (!OperatingSystem.IsWindows())
                    return false;

                // 2) Heuristik: WinPE bootet als RAM-Disk auf X:, Systemverzeichnis X:\Windows\System32.
                var sysDir = Environment.SystemDirectory ?? string.Empty;
                var root = Path.GetPathRoot(sysDir) ?? string.Empty;
                bool onRamDrive = root.StartsWith("X:", StringComparison.OrdinalIgnoreCase);

                // WinPE-typische Marker (ohne Registry-Abhaengigkeit).
                bool hasPeShell = File.Exists(Path.Combine(sysDir, "winpeshl.ini"));
                bool hasWpeutil = File.Exists(Path.Combine(sysDir, "wpeutil.exe"));

                return onRamDrive && (hasPeShell || hasWpeutil);
            }
            catch
            {
                return false;
            }
        }
    }
}
