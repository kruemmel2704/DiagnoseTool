using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
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
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                info.Manufacturer = "Apple";
                info.Model = RunMacCommand("sysctl", "-n hw.model") ?? "Mac";
                info.SerialNumber = ParseMacSerialNumber() ?? "Unbekannt";
                info.MotherboardManufacturer = "Apple";
                info.MotherboardModel = "Apple Silicon Motherboard";
                info.MotherboardSerialNumber = "Unbekannt";
            }
            else
            {
                // General Linux / Generic fallback
                info.Manufacturer = "Generic";
                info.Model = "Generic Device";
                info.SerialNumber = "Unbekannt";
                info.MotherboardManufacturer = "Generic";
                info.MotherboardModel = "Motherboard";
                info.MotherboardSerialNumber = "Unbekannt";
            }

            _cachedInfo = info;
            return info;
        }

        private static string RunMacCommand(string fileName, string args)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return output.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string ParseMacSerialNumber()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/usr/sbin/ioreg",
                        Arguments = "-rd1 -c IOPlatformExpertDevice",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("IOPlatformSerialNumber"))
                    {
                        var parts = line.Split('=');
                        if (parts.Length > 1)
                        {
                            return parts[1].Replace("\"", "").Trim();
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return null;
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
