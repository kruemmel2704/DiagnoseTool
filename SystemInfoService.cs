using System;
using System.Management;

namespace DiagnoseTool
{
    public class SystemInfo
    {
        public string Manufacturer { get; set; } = "Unbekannt";
        public string Model { get; set; } = "Unbekannt";
        public string SerialNumber { get; set; } = "Unbekannt";
        
        public string MotherboardManufacturer { get; set; } = "Unbekannt";
        public string MotherboardModel { get; set; } = "Unbekannt";
        public string MotherboardSerialNumber { get; set; } = "Unbekannt";

        public string FullSystemName
        {
            get
            {
                if (Manufacturer == "Unbekannt" && Model == "Unbekannt")
                    return "Unbekannt";
                if (Manufacturer == "Unbekannt")
                    return Model;
                if (Model == "Unbekannt")
                    return Manufacturer;
                if (Model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase))
                    return Model;
                return $"{Manufacturer} {Model}";
            }
        }
    }

    public static class SystemInfoService
    {
        private static SystemInfo _cachedInfo;

        public static SystemInfo GetSystemInfo()
        {
            if (_cachedInfo != null) return _cachedInfo;

            var info = new SystemInfo();

            // 1. Query Win32_ComputerSystem for Manufacturer and Model
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.Manufacturer = SanitizeValue(obj["Manufacturer"]?.ToString());
                            info.Model = SanitizeValue(obj["Model"]?.ToString());
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to query Win32_ComputerSystem: {ex.Message}");
            }

            // 2. Query Win32_BIOS for SerialNumber
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT SerialNumber FROM Win32_BIOS"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.SerialNumber = SanitizeValue(obj["SerialNumber"]?.ToString());
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to query Win32_BIOS: {ex.Message}");
            }

            // 3. Query Win32_BaseBoard for Motherboard Info
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard"))
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (ManagementObject obj in collection)
                        {
                            info.MotherboardManufacturer = SanitizeValue(obj["Manufacturer"]?.ToString());
                            info.MotherboardModel = SanitizeValue(obj["Product"]?.ToString());
                            info.MotherboardSerialNumber = SanitizeValue(obj["SerialNumber"]?.ToString());
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to query Win32_BaseBoard: {ex.Message}");
            }

            _cachedInfo = info;
            return info;
        }

        private static string SanitizeValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val))
                return "Unbekannt";

            val = val.Trim();

            string[] placeholders = new[]
            {
                "to be filled by o.e.m.",
                "to be filled by oem",
                "system product name",
                "system manufacturer",
                "base board product name",
                "base board manufacturer",
                "default string",
                "none",
                "unknown",
                "o.e.m.",
                "fill by o.e.m."
            };

            foreach (var placeholder in placeholders)
            {
                if (val.Equals(placeholder, StringComparison.OrdinalIgnoreCase))
                    return "Unbekannt";
            }

            return val;
        }
    }
}
