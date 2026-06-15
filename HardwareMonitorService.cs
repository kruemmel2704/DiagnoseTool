using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;

namespace DiagnoseTool
{
    public class CpuCoreInfo
    {
        public string Name { get; set; }
        public float? Temperature { get; set; }
        public float? Load { get; set; }

        public string TemperatureDisplay => Temperature.HasValue ? $"{Temperature.Value:F1} °C" : "-- °C";
        public string LoadDisplay => Load.HasValue ? $"{Load.Value:F0} %" : "-- %";
        public string TemperatureColor => Temperature.HasValue 
            ? (Temperature.Value > 80 ? "#EF4444" : (Temperature.Value > 60 ? "#F59E0B" : "#10B981")) 
            : "#4B5563";
    }

    internal class TempCoreData
    {
        public string Name { get; set; }
        public List<float> Temps { get; } = new List<float>();
        public List<float> Loads { get; } = new List<float>();
    }

    public class HardwareDiagnostics
    {
        public string CpuName { get; set; } = "AMD / Intel Processor";
        public float? CpuAverageLoad { get; set; }
        public float? CpuAverageTemp { get; set; }
        public float? CpuClockSpeed { get; set; }
        public List<CpuCoreInfo> CpuCores { get; set; } = new List<CpuCoreInfo>();

        public string GpuName { get; set; } = "Keine dedizierte Grafikkarte / Integriert";
        public float? GpuLoad { get; set; }
        public float? GpuTemp { get; set; }
        public float? GpuVramUsage { get; set; }

        public float RamUsedGb { get; set; }
        public float RamTotalGb { get; set; }
        public float RamPercent { get; set; }

        public string CpuClockDisplay => CpuClockSpeed.HasValue && CpuClockSpeed.Value > 0 ? $"{CpuClockSpeed.Value / 1000.0:F2} GHz" : "-- GHz";
        public string CpuAverageLoadDisplay => CpuAverageLoad.HasValue ? $"{CpuAverageLoad.Value:F1} %" : "-- %";
        public string CpuAverageTempDisplay => CpuAverageTemp.HasValue ? $"{CpuAverageTemp.Value:F1} °C" : "-- °C";
        
        public string GpuLoadDisplay => GpuLoad.HasValue ? $"{GpuLoad.Value:F1} %" : "-- %";
        public string GpuTempDisplay => GpuTemp.HasValue ? $"{GpuTemp.Value:F1} °C" : "-- °C";
        public string GpuVramUsageDisplay => GpuVramUsage.HasValue ? $"{GpuVramUsage.Value:F1} %" : "-- %";

        public string RamUsageDisplay => RamTotalGb > 0 ? $"{RamUsedGb:F1} / {RamTotalGb:F1} GB" : "-- GB";

        public bool IsLhmDriverBlocked { get; set; }
        public bool IsHWiNFOFallbackActive { get; set; }
    }

    public class HardwareMonitorService : IDisposable
    {
        private readonly Computer _computer;
        private bool _isDisposed;

        public HardwareMonitorService()
        {
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false
                };
                _computer.Open();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to initialize hardware monitor: {ex.Message}");
            }
        }

        public HardwareDiagnostics GetDiagnostics()
        {
            var diag = new HardwareDiagnostics();
            if (_isDisposed || _computer == null) return diag;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    hardware.Update();

                    // --- CPU ---
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        diag.CpuName = hardware.Name;
                        float totalTemp = 0;
                        int tempCount = 0;
                        float totalClock = 0;
                        int clockCount = 0;
                        var coreDict = new Dictionary<int, TempCoreData>();
                        var coreNumberRegex = new System.Text.RegularExpressions.Regex(@"(?:CPU )?Core\s*#?\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature)
                            {
                                string sensorName = sensor.Name;
                                
                                // Check if it represents the package/overall temperature
                                bool isPackageTemp = sensorName.Contains("Package") || 
                                                     sensorName.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) || 
                                                     sensorName.Equals("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) || 
                                                     sensorName.Equals("Tctl", StringComparison.OrdinalIgnoreCase);

                                if (isPackageTemp)
                                {
                                    diag.CpuAverageTemp = sensor.Value;
                                }

                                var match = coreNumberRegex.Match(sensorName);
                                bool isIgnoredName = sensorName.Contains("Distance", StringComparison.OrdinalIgnoreCase) ||
                                                     sensorName.Contains("TjMax", StringComparison.OrdinalIgnoreCase) ||
                                                     sensorName.Contains("Tj Max", StringComparison.OrdinalIgnoreCase) ||
                                                     sensorName.Contains("Max", StringComparison.OrdinalIgnoreCase) ||
                                                     sensorName.Contains("Average", StringComparison.OrdinalIgnoreCase);

                                if (match.Success && !isIgnoredName)
                                {
                                    int coreNum = int.Parse(match.Groups[1].Value);
                                    if (!coreDict.TryGetValue(coreNum, out var coreData))
                                    {
                                        coreData = new TempCoreData { Name = $"CPU Core #{coreNum}" };
                                        coreDict[coreNum] = coreData;
                                    }
                                    
                                    if (sensor.Value.HasValue)
                                    {
                                        coreData.Temps.Add(sensor.Value.Value);
                                        totalTemp += sensor.Value.Value;
                                        tempCount++;
                                    }
                                }
                            }
                            else if (sensor.SensorType == SensorType.Load)
                            {
                                if (sensor.Name.Contains("Total"))
                                {
                                    diag.CpuAverageLoad = sensor.Value;
                                }
                                else
                                {
                                    var match = coreNumberRegex.Match(sensor.Name);
                                    bool isIgnoredName = sensor.Name.Contains("Max", StringComparison.OrdinalIgnoreCase) ||
                                                         sensor.Name.Contains("Average", StringComparison.OrdinalIgnoreCase);

                                    if (match.Success && !isIgnoredName)
                                    {
                                        int coreNum = int.Parse(match.Groups[1].Value);
                                        if (!coreDict.TryGetValue(coreNum, out var coreData))
                                        {
                                            coreData = new TempCoreData { Name = $"CPU Core #{coreNum}" };
                                            coreDict[coreNum] = coreData;
                                        }
                                        
                                        if (sensor.Value.HasValue)
                                        {
                                            coreData.Loads.Add(sensor.Value.Value);
                                        }
                                    }
                                }
                            }
                            else if (sensor.SensorType == SensorType.Clock)
                            {
                                if (sensor.Value.HasValue && !sensor.Name.Contains("Effective"))
                                {
                                    totalClock += sensor.Value.Value;
                                    clockCount++;
                                }
                            }
                        }

                        if (tempCount > 0 && (!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value == 0))
                        {
                            diag.CpuAverageTemp = totalTemp / tempCount;
                        }
                        if (clockCount > 0)
                        {
                            diag.CpuClockSpeed = totalClock / clockCount;
                        }

                        foreach (var kvp in coreDict.OrderBy(k => k.Key))
                        {
                            var coreData = kvp.Value;
                            diag.CpuCores.Add(new CpuCoreInfo
                            {
                                Name = coreData.Name,
                                Temperature = coreData.Temps.Count > 0 ? coreData.Temps.Average() : (float?)null,
                                Load = coreData.Loads.Count > 0 ? coreData.Loads.Average() : (float?)null
                            });
                        }
                    }

                    // --- GPU ---
                    else if (hardware.HardwareType == HardwareType.GpuNvidia || 
                             hardware.HardwareType == HardwareType.GpuAmd || 
                             hardware.HardwareType == HardwareType.GpuIntel)
                    {
                        diag.GpuName = hardware.Name;
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Temperature)
                            {
                                if (sensor.Name.Contains("Core") || sensor.Name.Contains("GPU"))
                                    diag.GpuTemp = sensor.Value;
                            }
                            else if (sensor.SensorType == SensorType.Load)
                            {
                                if (sensor.Name.Contains("Core") || sensor.Name.Contains("GPU"))
                                    diag.GpuLoad = sensor.Value;
                                else if (sensor.Name.Contains("Memory") || sensor.Name.Contains("VRAM"))
                                    diag.GpuVramUsage = sensor.Value;
                            }
                        }
                    }

                    // --- MEMORY ---
                    else if (hardware.HardwareType == HardwareType.Memory)
                    {
                        float used = 0;
                        float available = 0;
                        foreach (var sensor in hardware.Sensors)
                        {
                            if (sensor.SensorType == SensorType.Load)
                            {
                                diag.RamPercent = sensor.Value ?? 0;
                            }
                            else if (sensor.SensorType == SensorType.Data)
                            {
                                if (sensor.Name.Contains("Used"))
                                    used = sensor.Value ?? 0;
                                else if (sensor.Name.Contains("Available"))
                                    available = sensor.Value ?? 0;
                            }
                        }
                        diag.RamUsedGb = used;
                        diag.RamTotalGb = used + available;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating sensors: {ex.Message}");
            }

            // Check if CPU is detected but CPU average temperature is not available, and all core temperatures are empty
            bool cpuDetected = diag.CpuName != "AMD / Intel Processor";
            bool noCpuTemp = !diag.CpuAverageTemp.HasValue && 
                             (diag.CpuCores == null || diag.CpuCores.Count == 0 || diag.CpuCores.All(c => !c.Temperature.HasValue));
            diag.IsLhmDriverBlocked = cpuDetected && noCpuTemp;

            // Fallback: If CPU temp or GPU temp are missing, query HWiNFO registry
            bool noGpuTemp = !diag.GpuTemp.HasValue || diag.GpuTemp.Value == 0;
            if (diag.IsLhmDriverBlocked || noGpuTemp)
            {
                TryReadFromHWiNFORegistry(diag);
            }

            return diag;
        }

        private void TryReadFromHWiNFORegistry(HardwareDiagnostics diag)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\HWiNFO64\VSB"))
                {
                    if (key == null) return;

                    var valueNames = key.GetValueNames();
                    var indexes = new List<int>();
                    foreach (var name in valueNames)
                    {
                        if (name.StartsWith("Sensor") && int.TryParse(name.Substring(6), out int idx))
                        {
                            indexes.Add(idx);
                        }
                    }

                    float? cpuTemp = null;
                    float? cpuLoad = null;
                    float? gpuTemp = null;
                    float? gpuLoad = null;
                    float? gpuVram = null;
                    float? ramPercent = null;
                    float? ramUsed = null;
                    float? ramFree = null;

                    var coreTemps = new Dictionary<int, float>();
                    var coreLoads = new Dictionary<int, float>();

                    foreach (var idx in indexes)
                    {
                        string sensor = key.GetValue($"Sensor{idx}")?.ToString();
                        string label = key.GetValue($"Label{idx}")?.ToString();
                        string valueRawStr = key.GetValue($"ValueRaw{idx}")?.ToString();

                        if (string.IsNullOrEmpty(sensor) || string.IsNullOrEmpty(label) || string.IsNullOrEmpty(valueRawStr))
                            continue;

                        if (!float.TryParse(valueRawStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float rawVal))
                        {
                            if (!float.TryParse(valueRawStr, out rawVal))
                                continue;
                        }

                        // --- CPU ---
                        if (sensor.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Overall Name
                            if (diag.CpuName == "AMD / Intel Processor")
                            {
                                var parts = sensor.Split(':');
                                if (parts.Length > 1)
                                {
                                    string nameCandidate = parts[1].Trim();
                                    if (!string.IsNullOrEmpty(nameCandidate) && !nameCandidate.Contains("DTS") && !nameCandidate.Contains("Enhanced"))
                                    {
                                        diag.CpuName = nameCandidate;
                                    }
                                }
                            }

                            // Temperature
                            if (label.IndexOf("temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("die", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("package", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (label.IndexOf("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("Die (average)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("Durchschnitt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    label.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    cpuTemp = rawVal;
                                }
                                else if (!cpuTemp.HasValue)
                                {
                                    cpuTemp = rawVal;
                                }
                            }

                            // Core Temperature
                            var coreTempMatch = Regex.Match(label, @"(Core|Kern)\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
                            if (coreTempMatch.Success)
                            {
                                int coreNum = int.Parse(coreTempMatch.Groups[2].Value);
                                coreTemps[coreNum] = rawVal;
                            }

                            // CPU Load
                            if ((label.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Gesamt", StringComparison.OrdinalIgnoreCase) >= 0) && 
                                (label.IndexOf("Utility", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                 label.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                 label.IndexOf("Usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 label.IndexOf("Auslastung", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                cpuLoad = rawVal;
                            }
                            else if (label.Equals("CPU Total", StringComparison.OrdinalIgnoreCase) || label.IndexOf("CPU-Auslastung", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                cpuLoad = rawVal;
                            }

                            // Core Load
                            var coreLoadMatch = Regex.Match(label, @"(Core|Kern)\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
                            if (coreLoadMatch.Success && (label.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Usage", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("Auslastung", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                int coreNum = int.Parse(coreLoadMatch.Groups[2].Value);
                                coreLoads[coreNum] = rawVal;
                            }
                        }

                        // --- GPU ---
                        else if (sensor.IndexOf("GPU", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // GPU Name
                            if (diag.GpuName.Contains("Keine dedizierte") || diag.GpuName.Contains("Integriert"))
                            {
                                var parts = sensor.Split(':');
                                if (parts.Length > 1)
                                {
                                    string nameCandidate = parts[1].Trim();
                                    if (!string.IsNullOrEmpty(nameCandidate) && !nameCandidate.Contains("DTS") && !nameCandidate.Contains("Enhanced"))
                                    {
                                        diag.GpuName = nameCandidate;
                                    }
                                }
                            }

                            // Temperature
                            if (label.IndexOf("Temp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Kern", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (label.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf("Hotspot", StringComparison.OrdinalIgnoreCase) < 0)
                                {
                                    gpuTemp = rawVal;
                                }
                                else if (!gpuTemp.HasValue)
                                {
                                    gpuTemp = rawVal;
                                }
                            }

                            // GPU Load
                            if (label.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Auslastung", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Nutzung", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                gpuLoad = rawVal;
                            }

                            // GPU VRAM
                            if (label.IndexOf("Memory Usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("D3D Memory Dedicated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Speicher", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                gpuVram = rawVal;
                            }
                        }

                        // --- RAM (System Memory) ---
                        else if (sensor.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                 sensor.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 sensor.IndexOf("Arbeitsspeicher", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (label.IndexOf("Memory Load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Memory Usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Auslastung", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Belegung", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ramPercent = rawVal;
                            }
                            else if (label.IndexOf("Memory Used", StringComparison.OrdinalIgnoreCase) >= 0 || label.IndexOf("belegt", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ramUsed = rawVal / 1024.0f; // Convert MB to GB if needed
                            }
                            else if (label.IndexOf("Memory Available", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     label.IndexOf("Memory Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     label.IndexOf("frei", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ramFree = rawVal / 1024.0f;
                            }
                        }
                    }

                    // Apply Fallbacks
                    bool fallbackApplied = false;

                    if (cpuTemp.HasValue && (!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value == 0))
                    {
                        diag.CpuAverageTemp = cpuTemp;
                        fallbackApplied = true;
                    }

                    if (cpuLoad.HasValue && (!diag.CpuAverageLoad.HasValue || diag.CpuAverageLoad.Value == 0))
                    {
                        diag.CpuAverageLoad = cpuLoad;
                        fallbackApplied = true;
                    }

                    if (gpuTemp.HasValue && (!diag.GpuTemp.HasValue || diag.GpuTemp.Value == 0))
                    {
                        diag.GpuTemp = gpuTemp;
                        fallbackApplied = true;
                    }

                    if (gpuLoad.HasValue && (!diag.GpuLoad.HasValue || diag.GpuLoad.Value == 0))
                    {
                        diag.GpuLoad = gpuLoad;
                        fallbackApplied = true;
                    }

                    if (gpuVram.HasValue && (!diag.GpuVramUsage.HasValue || diag.GpuVramUsage.Value == 0))
                    {
                        diag.GpuVramUsage = gpuVram;
                        fallbackApplied = true;
                    }

                    if (ramPercent.HasValue && diag.RamPercent == 0)
                    {
                        diag.RamPercent = ramPercent.Value;
                        fallbackApplied = true;
                    }

                    if (ramUsed.HasValue && diag.RamUsedGb == 0)
                    {
                        diag.RamUsedGb = ramUsed.Value;
                        if (ramFree.HasValue)
                            diag.RamTotalGb = ramUsed.Value + ramFree.Value;
                        fallbackApplied = true;
                    }

                    // Core Temperatures & Loads Merge
                    var allCoreNums = coreTemps.Keys.Union(coreLoads.Keys).OrderBy(n => n);
                    foreach (var coreNum in allCoreNums)
                    {
                        string coreName = $"CPU Core #{coreNum}";
                        var existing = diag.CpuCores.FirstOrDefault(c => c.Name == coreName);
                        float? tVal = coreTemps.TryGetValue(coreNum, out float t) ? t : (float?)null;
                        float? lVal = coreLoads.TryGetValue(coreNum, out float l) ? l : (float?)null;

                        if (existing != null)
                        {
                            if (!existing.Temperature.HasValue && tVal.HasValue)
                            {
                                existing.Temperature = tVal;
                                fallbackApplied = true;
                            }
                            if (!existing.Load.HasValue && lVal.HasValue)
                            {
                                existing.Load = lVal;
                                fallbackApplied = true;
                            }
                        }
                        else
                        {
                            diag.CpuCores.Add(new CpuCoreInfo
                            {
                                Name = coreName,
                                Temperature = tVal,
                                Load = lVal
                            });
                            fallbackApplied = true;
                        }
                    }

                    if (fallbackApplied)
                    {
                        diag.IsHWiNFOFallbackActive = true;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed HWiNFO registry query fallback: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _computer?.Close();
            }
            catch
            {
                // Ignore failures on close
            }
        }
    }
}
