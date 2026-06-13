using System;
using System.Collections.Generic;
using System.Linq;
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

            return diag;
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
