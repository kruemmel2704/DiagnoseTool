using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Management;

namespace DiagnoseTool
{
    public class BatteryInfo
    {
        public bool IsPresent { get; set; }
        public string Manufacturer { get; set; } = "Unknown";
        public string DeviceName { get; set; } = "Unknown";
        public string Chemistry { get; set; } = "Unknown";
        public uint DesignCapacity { get; set; } // in mWh (or mAh)
        public uint FullChargeCapacity { get; set; } // in mWh (or mAh)
        public double HealthPercent { get; set; } = 100.0;
        
        // Real-time properties
        public double CurrentChargePercent { get; set; } // 0 - 100
        public bool IsConnectedToAc { get; set; }
        public bool IsCharging { get; set; }
        public int RemainingTimeSeconds { get; set; } // -1 if unknown/charging
        
        public string ChargingStatusDisplay
        {
            get
            {
                if (!IsPresent) return "Keine Batterie erkannt";
                if (IsConnectedToAc)
                {
                    return IsCharging ? "Wird geladen" : "Netzbetrieb (Voll / Nicht ladend)";
                }
                return "Entlädt sich";
            }
        }
        
        public string RemainingTimeDisplay
        {
            get
            {
                if (!IsPresent) return "--";
                if (IsConnectedToAc) return "-- (Netzbetrieb)";
                if (RemainingTimeSeconds < 0) return "Berechne...";
                var t = TimeSpan.FromSeconds(RemainingTimeSeconds);
                return string.Format("{0:D2} Std. {1:D2} Min.", t.Hours + t.Days * 24, t.Minutes);
            }
        }
        
        public string HealthDisplay => IsPresent ? $"{HealthPercent:F1} %" : "--";
        public string DesignCapacityDisplay => IsPresent ? $"{DesignCapacity:N0} mWh" : "--";
        public string FullChargeCapacityDisplay => IsPresent ? $"{FullChargeCapacity:N0} mWh" : "--";
    }

    public static class BatteryService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        public static BatteryInfo GetBatteryInfo()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetWindowsBatteryInfo();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return GetMacBatteryInfo();
            }
            else
            {
                // Fallback for non-supported OS
                return new BatteryInfo { IsPresent = false };
            }
        }

        private static BatteryInfo GetWindowsBatteryInfo()
        {
            var info = new BatteryInfo();
            
            // 1. Get real-time status using P/Invoke GetSystemPowerStatus
            try
            {
                if (GetSystemPowerStatus(out SystemPowerStatus status))
                {
                    if (status.BatteryLifePercent != 255)
                    {
                        info.CurrentChargePercent = status.BatteryLifePercent;
                        info.IsPresent = (status.BatteryFlag & 128) == 0; 
                    }
                    else
                    {
                        info.IsPresent = false;
                    }
                    
                    info.IsConnectedToAc = status.ACLineStatus == 1;
                    info.IsCharging = (status.BatteryFlag & 8) != 0;
                    info.RemainingTimeSeconds = status.BatteryLifeTime;
                }
                else
                {
                    info.IsPresent = false;
                }
            }
            catch
            {
                info.IsPresent = false;
            }

            if (!info.IsPresent)
            {
                return info;
            }

            // 2. Query static information using WMI root\wmi
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity, ManufacturerName, DeviceName, Chemistry FROM BatteryStaticData"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.DesignCapacity = (uint)obj["DesignedCapacity"];
                            info.Manufacturer = obj["ManufacturerName"]?.ToString() ?? "Unknown";
                            info.DeviceName = obj["DeviceName"]?.ToString() ?? "Unknown";
                            info.Chemistry = obj["Chemistry"]?.ToString() ?? "Unknown";
                            break;
                        }
                    }
                }

                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.FullChargeCapacity = (uint)obj["FullChargedCapacity"];
                            break;
                        }
                    }
                }

                if (info.DesignCapacity > 0)
                {
                    info.HealthPercent = ((double)info.FullChargeCapacity / info.DesignCapacity) * 100.0;
                    if (info.HealthPercent > 100.0) info.HealthPercent = 100.0;
                }
                else
                {
                    QueryFallbackCimv2(info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Battery Query failed: {ex.Message}");
                QueryFallbackCimv2(info);
            }

            return info;
        }

        private static void QueryFallbackCimv2(BatteryInfo info)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT EstimatedChargeRemaining, DesignCapacity, FullChargeCapacity, DeviceID FROM Win32_Battery"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.IsPresent = true;
                            
                            var design = obj["DesignCapacity"];
                            var full = obj["FullChargeCapacity"];
                            var deviceId = obj["DeviceID"];

                            if (design != null) info.DesignCapacity = Convert.ToUInt32(design);
                            if (full != null) info.FullChargeCapacity = Convert.ToUInt32(full);
                            if (deviceId != null) info.DeviceName = deviceId.ToString();

                            if (info.DesignCapacity > 0 && info.FullChargeCapacity > 0)
                            {
                                info.HealthPercent = ((double)info.FullChargeCapacity / info.DesignCapacity) * 100.0;
                                if (info.HealthPercent > 100.0) info.HealthPercent = 100.0;
                            }
                            
                            var charge = obj["EstimatedChargeRemaining"];
                            if (charge != null && info.CurrentChargePercent == 0)
                            {
                                info.CurrentChargePercent = Convert.ToDouble(charge);
                            }
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CIMV2 Fallback Battery Query failed: {ex.Message}");
            }
        }

        private static BatteryInfo GetMacBatteryInfo()
        {
            var info = new BatteryInfo { IsPresent = false };
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/sbin/ioreg",
                        Arguments = "-rn AppleSmartBattery",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (string.IsNullOrWhiteSpace(output) || !output.Contains("AppleSmartBattery"))
                {
                    return info; // Not present (e.g. desktop Mac)
                }

                info.IsPresent = true;
                info.Manufacturer = "Apple";
                info.DeviceName = "Internal Battery";
                info.Chemistry = "Lithium";

                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    if (trimmed.Contains("\"DesignCapacity\""))
                    {
                        info.DesignCapacity = ParseUintValue(trimmed);
                    }
                    else if (trimmed.Contains("\"MaxCapacity\""))
                    {
                        info.FullChargeCapacity = ParseUintValue(trimmed);
                    }
                    else if (trimmed.Contains("\"StateOfCharge\""))
                    {
                        info.CurrentChargePercent = ParseUintValue(trimmed);
                    }
                    else if (trimmed.Contains("\"ExternalConnected\""))
                    {
                        info.IsConnectedToAc = trimmed.Contains("Yes") || trimmed.Contains("true") || trimmed.Contains("1");
                    }
                    else if (trimmed.Contains("\"IsCharging\""))
                    {
                        info.IsCharging = trimmed.Contains("Yes") || trimmed.Contains("true") || trimmed.Contains("1");
                    }
                    else if (trimmed.Contains("\"TimeRemaining\""))
                    {
                        var mins = ParseUintValue(trimmed);
                        if (mins > 0 && mins < 10000)
                        {
                            info.RemainingTimeSeconds = (int)mins * 60;
                        }
                        else
                        {
                            info.RemainingTimeSeconds = -1;
                        }
                    }
                    else if (trimmed.Contains("\"DeviceName\""))
                    {
                        info.DeviceName = ParseStringValue(trimmed) ?? info.DeviceName;
                    }
                    else if (trimmed.Contains("\"Manufacturer\""))
                    {
                        info.Manufacturer = ParseStringValue(trimmed) ?? info.Manufacturer;
                    }
                    else if (trimmed.Contains("\"BatteryCellChemistry\""))
                    {
                        info.Chemistry = ParseStringValue(trimmed) ?? info.Chemistry;
                    }
                }

                if (info.DesignCapacity > 0)
                {
                    info.HealthPercent = ((double)info.FullChargeCapacity / info.DesignCapacity) * 100.0;
                    if (info.HealthPercent > 100.0) info.HealthPercent = 100.0;
                }
            }
            catch
            {
                info.IsPresent = false;
            }
            return info;
        }

        private static uint ParseUintValue(string line)
        {
            var parts = line.Split('=');
            if (parts.Length > 1)
            {
                var valStr = parts[1].Trim().Trim('"', ',');
                if (uint.TryParse(valStr, out uint val))
                {
                    return val;
                }
            }
            return 0;
        }

        private static string ParseStringValue(string line)
        {
            var parts = line.Split('=');
            if (parts.Length > 1)
            {
                return parts[1].Trim().Trim('"', ',');
            }
            return null;
        }
    }
}
