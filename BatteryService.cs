using System;
using System.Management;
using System.Runtime.InteropServices;

namespace DiagnoseTool
{
    public class BatteryInfo
    {
        public bool IsPresent { get; set; }
        public string Manufacturer { get; set; } = "Unknown";
        public string DeviceName { get; set; } = "Unknown";
        public string Chemistry { get; set; } = "Unknown";
        public uint DesignCapacity { get; set; } // in mWh
        public uint FullChargeCapacity { get; set; } // in mWh
        public double HealthPercent { get; set; } = 100.0;

        // Real-time properties
        public double CurrentChargePercent { get; set; } // 0 - 100
        public bool IsConnectedToAc { get; set; }
        public bool IsCharging { get; set; }
        public int RemainingTimeSeconds { get; set; } // -1 if unknown/charging
        public double Voltage { get; set; }
        public double ChargeRateWatts { get; set; }

        public string ChargingStatusDisplay
        {
            get
            {
                if (!IsPresent) return "Keine Batterie erkannt";
                if (IsConnectedToAc)
                {
                    if (IsCharging)
                        return ChargeRateWatts > 0.1 ? $"Wird geladen ({ChargeRateWatts:F1} W)" : "Wird geladen";
                    return "Netzbetrieb (Voll / Nicht ladend)";
                }
                return ChargeRateWatts > 0.1 ? $"Entlädt sich ({ChargeRateWatts:F1} W)" : "Entlädt sich";
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
        public string DesignCapacityDisplay => IsPresent && DesignCapacity > 0 ? $"{DesignCapacity:N0} mWh" : "--";
        public string FullChargeCapacityDisplay => IsPresent && FullChargeCapacity > 0 ? $"{FullChargeCapacity:N0} mWh" : "--";
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

        public static BatteryInfo GetBatteryInfo(LhmBatteryData lhmBattery = null)
        {
            var info = new BatteryInfo();

            // 1. Get real-time status using P/Invoke GetSystemPowerStatus
            if (GetSystemPowerStatus(out SystemPowerStatus status))
            {
                // BatteryLifePercent is 255 if unknown, otherwise 0-100
                if (status.BatteryLifePercent != 255)
                {
                    info.CurrentChargePercent = status.BatteryLifePercent;
                    info.IsPresent = (status.BatteryFlag & 128) == 0; // if BatteryFlag has 128 bit, battery is not present
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

            // 2. Enrich with LibreHardwareMonitor Battery hardware sensors if available
            if (lhmBattery != null && lhmBattery.IsFound)
            {
                info.IsPresent = true;

                if (!string.IsNullOrWhiteSpace(lhmBattery.Name))
                    info.DeviceName = lhmBattery.Name;

                if (lhmBattery.ChargeLevel.HasValue && lhmBattery.ChargeLevel.Value > 0)
                    info.CurrentChargePercent = lhmBattery.ChargeLevel.Value;

                if (lhmBattery.DesignCapacityMWh.HasValue && lhmBattery.DesignCapacityMWh.Value > 0)
                    info.DesignCapacity = (uint)Math.Round(lhmBattery.DesignCapacityMWh.Value);

                if (lhmBattery.FullChargeCapacityMWh.HasValue && lhmBattery.FullChargeCapacityMWh.Value > 0)
                    info.FullChargeCapacity = (uint)Math.Round(lhmBattery.FullChargeCapacityMWh.Value);

                if (lhmBattery.DegradationLevel.HasValue && lhmBattery.DegradationLevel.Value >= 0 && lhmBattery.DegradationLevel.Value <= 100)
                {
                    info.HealthPercent = Math.Max(0.0, Math.Min(100.0, 100.0 - lhmBattery.DegradationLevel.Value));
                }
                else if (info.DesignCapacity > 0 && info.FullChargeCapacity > 0)
                {
                    info.HealthPercent = Math.Min(100.0, ((double)info.FullChargeCapacity / info.DesignCapacity) * 100.0);
                }

                if (lhmBattery.Voltage.HasValue && lhmBattery.Voltage.Value > 0)
                    info.Voltage = lhmBattery.Voltage.Value;

                if (lhmBattery.ChargeDischargeRateWatts.HasValue && lhmBattery.ChargeDischargeRateWatts.Value > 0)
                    info.ChargeRateWatts = lhmBattery.ChargeDischargeRateWatts.Value;

                if (lhmBattery.IsCharging)
                    info.IsCharging = true;

                if (info.RemainingTimeSeconds < 0 && lhmBattery.RemainingTimeSeconds.HasValue && lhmBattery.RemainingTimeSeconds.Value > 0)
                    info.RemainingTimeSeconds = (int)lhmBattery.RemainingTimeSeconds.Value;
            }

            if (!info.IsPresent)
            {
                return info;
            }

            // 3. Query static information using WMI root\wmi if capacities or metadata are still missing
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT DesignedCapacity, ManufacturerName, DeviceName, Chemistry FROM BatteryStaticData"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            if (info.DesignCapacity == 0 && obj["DesignedCapacity"] != null)
                                info.DesignCapacity = Convert.ToUInt32(obj["DesignedCapacity"]);

                            string manufacturer = obj["ManufacturerName"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(manufacturer))
                                info.Manufacturer = manufacturer;

                            string devName = obj["DeviceName"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(devName) && info.DeviceName == "Unknown")
                                info.DeviceName = devName;

                            string chem = obj["Chemistry"]?.ToString();
                            if (!string.IsNullOrWhiteSpace(chem))
                                info.Chemistry = chem;
                            break;
                        }
                    }
                }

                if (info.FullChargeCapacity == 0)
                {
                    using (var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity"))
                    {
                        using (var collection = searcher.Get())
                        {
                            foreach (ManagementObject obj in collection)
                            {
                                if (obj["FullChargedCapacity"] != null)
                                    info.FullChargeCapacity = Convert.ToUInt32(obj["FullChargedCapacity"]);
                                break;
                            }
                        }
                    }
                }

                if (info.DesignCapacity > 0 && info.FullChargeCapacity > 0)
                {
                    info.HealthPercent = ((double)info.FullChargeCapacity / info.DesignCapacity) * 100.0;
                    if (info.HealthPercent > 100.0) info.HealthPercent = 100.0;
                }
                else if (info.DesignCapacity == 0)
                {
                    // Fallback to CIMV2 Win32_Battery
                    QueryFallbackCimv2(info);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Battery Query failed: {ex.Message}");
                if (info.DesignCapacity == 0)
                {
                    QueryFallbackCimv2(info);
                }
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

                            if (design != null && info.DesignCapacity == 0) info.DesignCapacity = Convert.ToUInt32(design);
                            if (full != null && info.FullChargeCapacity == 0) info.FullChargeCapacity = Convert.ToUInt32(full);
                            if (deviceId != null && info.DeviceName == "Unknown") info.DeviceName = deviceId.ToString();

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
    }
}
