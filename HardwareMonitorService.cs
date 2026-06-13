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

    public class HardwareDiagnostics
    {
        public string CpuName { get; set; } = "AMD / Intel Processor";
        public float CpuAverageLoad { get; set; }
        public float CpuAverageTemp { get; set; }
        public float CpuClockSpeed { get; set; }
        public List<CpuCoreInfo> CpuCores { get; set; } = new List<CpuCoreInfo>();

        public string GpuName { get; set; } = "None / Intel Integrated";
        public float GpuLoad { get; set; }
        public float GpuTemp { get; set; }
        public float GpuVramUsage { get; set; }

        public float RamUsedGb { get; set; }
        public float RamTotalGb { get; set; }
        public float RamPercent { get; set; }

        public string CpuClockDisplay => CpuClockSpeed > 0 ? $"{CpuClockSpeed / 1000.0:F2} GHz" : "-- GHz";
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
                        var coreDict = new Dictionary<string, CpuCoreInfo>();

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

                                // Check if it represents a core, CCD, or junction temperature
                                bool isCoreOrCcdTemp = sensorName.Contains("Core") || 
                                                       sensorName.Contains("CPU Core") || 
                                                       sensorName.Contains("Tctl") || 
                                                       sensorName.Contains("Tdie") || 
                                                       sensorName.Contains("Tccd") || 
                                                       sensorName.Contains("CCD");

                                if (isPackageTemp)
                                {
                                    diag.CpuAverageTemp = sensor.Value ?? 0;
                                }

                                if (isCoreOrCcdTemp)
                                {
                                    if (!coreDict.ContainsKey(sensorName))
                                        coreDict[sensorName] = new CpuCoreInfo { Name = sensorName };
                                    
                                    coreDict[sensorName].Temperature = sensor.Value;
                                    if (sensor.Value.HasValue)
                                    {
                                        totalTemp += sensor.Value.Value;
                                        tempCount++;
                                    }
                                }
                            }
                            else if (sensor.SensorType == SensorType.Load)
                            {
                                if (sensor.Name.Contains("Total"))
                                {
                                    diag.CpuAverageLoad = sensor.Value ?? 0;
                                }
                                else if (sensor.Name.Contains("Core") || sensor.Name.Contains("CPU Core"))
                                {
                                    string coreName = sensor.Name.Replace(" Thread", "").Replace(" Load", "");
                                    if (!coreDict.ContainsKey(coreName))
                                        coreDict[coreName] = new CpuCoreInfo { Name = coreName };
                                    
                                    coreDict[coreName].Load = sensor.Value;
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

                        if (tempCount > 0 && diag.CpuAverageTemp == 0)
                        {
                            diag.CpuAverageTemp = totalTemp / tempCount;
                        }
                        if (clockCount > 0)
                        {
                            diag.CpuClockSpeed = totalClock / clockCount;
                        }

                        diag.CpuCores = coreDict.Values.OrderBy(c => c.Name).ToList();
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
                                    diag.GpuTemp = sensor.Value ?? 0;
                            }
                            else if (sensor.SensorType == SensorType.Load)
                            {
                                if (sensor.Name.Contains("Core") || sensor.Name.Contains("GPU"))
                                    diag.GpuLoad = sensor.Value ?? 0;
                                else if (sensor.Name.Contains("Memory") || sensor.Name.Contains("VRAM"))
                                    diag.GpuVramUsage = sensor.Value ?? 0;
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
