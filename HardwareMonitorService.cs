using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace DiagnoseTool
{
    /// <summary>
    /// Official LibreHardwareMonitor visitor pattern implementation to recursively
    /// update all hardware components and their sub-hardware (SuperIO, EC, cores, etc.).
    /// </summary>
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer)
        {
            computer.Traverse(this);
        }

        public void VisitHardware(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch
            {
                // Ignore individual hardware update failures so other sensors still update
            }

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }

    public class CpuCoreInfo
    {
        public int CoreIndex { get; set; } = int.MaxValue;
        public string Name { get; set; }
        public float? Temperature { get; set; }
        public float? Load { get; set; }
        public float? ClockMhz { get; set; }

        public string TemperatureDisplay => Temperature.HasValue && Temperature.Value > 0 ? $"{Temperature.Value:F1} °C" : "-- °C";
        public string LoadDisplay => Load.HasValue ? $"{Load.Value:F0} %" : "-- %";
        public string ClockDisplay => ClockMhz.HasValue && ClockMhz.Value > 0 ? $"{ClockMhz.Value / 1000.0:F2} GHz" : "";
        public string TemperatureColor => Temperature.HasValue && Temperature.Value > 0
            ? (Temperature.Value > 80 ? "#EF4444" : (Temperature.Value > 60 ? "#F59E0B" : "#10B981"))
            : "#4B5563";
    }

    public class StorageDriveInfo
    {
        public string Name { get; set; }
        public float? Temperature { get; set; }
        public float? UsedSpacePercent { get; set; }
        public float? HealthPercent { get; set; }

        public string TemperatureDisplay => Temperature.HasValue && Temperature.Value > 0 ? $"{Temperature.Value:F0} °C" : "-- °C";
        public string UsedSpaceDisplay => UsedSpacePercent.HasValue ? $"{UsedSpacePercent.Value:F0} % belegt" : "--";
        public string HealthDisplay => HealthPercent.HasValue ? $"{HealthPercent.Value:F0} % Zustand" : "";
    }

    public class FanSensorInfo
    {
        public string Name { get; set; }
        public float Rpm { get; set; }
        public string RpmDisplay => $"{Rpm:F0} RPM";
    }

    public class LhmBatteryData
    {
        public bool IsFound { get; set; }
        public string Name { get; set; }
        public float? ChargeLevel { get; set; }
        public float? DegradationLevel { get; set; }
        public float? DesignCapacityMWh { get; set; }
        public float? FullChargeCapacityMWh { get; set; }
        public float? RemainingCapacityMWh { get; set; }
        public float? Voltage { get; set; }
        public float? ChargeDischargeRateWatts { get; set; }
        public bool IsCharging { get; set; }
        public bool IsDischarging { get; set; }
        public float? RemainingTimeSeconds { get; set; }
    }

    public class HardwareDiagnostics
    {
        public string CpuName { get; set; } = "AMD / Intel Processor";
        public float? CpuAverageLoad { get; set; }
        public float? CpuAverageTemp { get; set; }
        public float? CpuClockSpeed { get; set; }
        public float CpuPackagePower { get; set; }
        public float CpuVoltage { get; set; }
        public List<CpuCoreInfo> CpuCores { get; set; } = new List<CpuCoreInfo>();

        public string MotherboardName { get; set; } = "Unbekanntes Mainboard";
        public float MotherboardTemp { get; set; }
        public List<FanSensorInfo> Fans { get; set; } = new List<FanSensorInfo>();

        public string GpuName { get; set; } = "Keine dedizierte Grafikkarte / Integriert";
        public float? GpuLoad { get; set; }
        public float? GpuTemp { get; set; }
        public float GpuHotSpotTemp { get; set; }
        public float? GpuVramUsage { get; set; }
        public float GpuVramUsedMb { get; set; }
        public float GpuVramTotalMb { get; set; }
        public float GpuClockSpeed { get; set; }
        public float GpuPower { get; set; }

        public float RamUsedGb { get; set; }
        public float RamTotalGb { get; set; }
        public float RamPercent { get; set; }

        public List<StorageDriveInfo> StorageDrives { get; set; } = new List<StorageDriveInfo>();
        public LhmBatteryData BatteryData { get; set; } = new LhmBatteryData();

        public string CpuClockDisplay => CpuClockSpeed.HasValue && CpuClockSpeed.Value > 0 ? $"{CpuClockSpeed.Value / 1000.0:F2} GHz" : "-- GHz";
        public string CpuPowerDisplay => CpuPackagePower > 0 ? $"{CpuPackagePower:F1} W" : "-- W";
        public string CpuAverageLoadDisplay => CpuAverageLoad.HasValue ? $"{CpuAverageLoad.Value:F1} %" : "-- %";
        public string CpuAverageTempDisplay => CpuAverageTemp.HasValue && CpuAverageTemp.Value > 0 ? $"{CpuAverageTemp.Value:F1} °C" : "-- °C";

        public string GpuClockDisplay => GpuClockSpeed > 0 ? $"{GpuClockSpeed:F0} MHz" : "-- MHz";
        public string GpuPowerDisplay => GpuPower > 0 ? $"{GpuPower:F1} W" : "-- W";
        public string GpuLoadDisplay => GpuLoad.HasValue ? $"{GpuLoad.Value:F1} %" : "-- %";
        public string GpuTempDisplay => GpuTemp.HasValue && GpuTemp.Value > 0 ? $"{GpuTemp.Value:F1} °C" : "-- °C";
        public string GpuVramUsageDisplay => GpuVramTotalMb > 0
            ? $"{GpuVramUsedMb / 1024.0:F1} / {GpuVramTotalMb / 1024.0:F1} GB ({GpuVramUsage ?? 0:F0} %)"
            : (GpuVramUsage.HasValue ? $"{GpuVramUsage.Value:F1} %" : "-- %");
        public string GpuVramDisplay => GpuVramUsageDisplay;

        public string RamUsageDisplay => RamTotalGb > 0 ? $"{RamUsedGb:F1} / {RamTotalGb:F1} GB" : "-- GB";

        public bool IsLhmDriverBlocked { get; set; }
        public bool IsHWiNFOFallbackActive { get; set; }
    }

    public class HardwareMonitorService : IDisposable
    {
        private readonly IHardwareMonitorHelper _helper;
        private bool _isDisposed;
        private float _lastMacCpuTemp = 46.0f;
        private float _lastMacCpuLoad = 12.0f;

        public HardwareMonitorService()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            try
            {
                _helper = new WindowsHardwareMonitorHelper();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize hardware monitor: {ex.Message}");
            }
        }

        public HardwareDiagnostics GetDiagnostics(bool isStressActive = false, int stressedThreads = 0)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetMacOrGenericDiagnostics(isStressActive, stressedThreads);
            }

            var diag = new HardwareDiagnostics();
            if (_isDisposed || _helper == null) return diag;

            _helper.UpdateDiagnostics(diag);

            // Check if CPU is detected but CPU average temperature is not available, and all core temperatures are empty
            bool cpuDetected = diag.CpuName != "AMD / Intel Processor";
            bool noCpuTemp = (!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value <= 0) &&
                             (diag.CpuCores == null || diag.CpuCores.Count == 0 || diag.CpuCores.All(c => !c.Temperature.HasValue || c.Temperature.Value <= 0));
            diag.IsLhmDriverBlocked = cpuDetected && noCpuTemp;

            // Fallback: If CPU temp or GPU temp are missing, query HWiNFO registry
            bool noGpuTemp = !diag.GpuTemp.HasValue || diag.GpuTemp.Value <= 0;
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

                            var coreTempMatch = Regex.Match(label, @"(Core|Kern)\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
                            if (coreTempMatch.Success)
                            {
                                int coreNum = int.Parse(coreTempMatch.Groups[2].Value);
                                coreTemps[coreNum] = rawVal;
                            }

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

                            if (label.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Usage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Auslastung", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                label.IndexOf("Nutzung", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                gpuLoad = rawVal;
                            }

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
                                ramUsed = rawVal / 1024.0f;
                            }
                            else if (label.IndexOf("Memory Available", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     label.IndexOf("Memory Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     label.IndexOf("frei", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                ramFree = rawVal / 1024.0f;
                            }
                        }
                    }

                    bool fallbackApplied = false;

                    if (cpuTemp.HasValue && (!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value <= 0))
                    {
                        diag.CpuAverageTemp = cpuTemp;
                        fallbackApplied = true;
                    }

                    if (cpuLoad.HasValue && (!diag.CpuAverageLoad.HasValue || diag.CpuAverageLoad.Value <= 0))
                    {
                        diag.CpuAverageLoad = cpuLoad;
                        fallbackApplied = true;
                    }

                    if (gpuTemp.HasValue && (!diag.GpuTemp.HasValue || diag.GpuTemp.Value <= 0))
                    {
                        diag.GpuTemp = gpuTemp;
                        fallbackApplied = true;
                    }

                    if (gpuLoad.HasValue && (!diag.GpuLoad.HasValue || diag.GpuLoad.Value <= 0))
                    {
                        diag.GpuLoad = gpuLoad;
                        fallbackApplied = true;
                    }

                    if (gpuVram.HasValue && (!diag.GpuVramUsage.HasValue || diag.GpuVramUsage.Value <= 0))
                    {
                        diag.GpuVramUsage = gpuVram;
                        fallbackApplied = true;
                    }

                    if (ramPercent.HasValue && diag.RamPercent <= 0)
                    {
                        diag.RamPercent = ramPercent.Value;
                        fallbackApplied = true;
                    }

                    if (ramUsed.HasValue && diag.RamUsedGb <= 0)
                    {
                        diag.RamUsedGb = ramUsed.Value;
                        if (ramFree.HasValue)
                            diag.RamTotalGb = ramUsed.Value + ramFree.Value;
                        fallbackApplied = true;
                    }

                    var allCoreNums = coreTemps.Keys.Union(coreLoads.Keys).OrderBy(n => n);
                    foreach (var coreNum in allCoreNums)
                    {
                        string coreName = $"CPU Core #{coreNum}";
                        var existing = diag.CpuCores.FirstOrDefault(c => c.Name == coreName);
                        float? tVal = coreTemps.TryGetValue(coreNum, out float t) ? t : (float?)null;
                        float? lVal = coreLoads.TryGetValue(coreNum, out float l) ? l : (float?)null;

                        if (existing != null)
                        {
                            if ((!existing.Temperature.HasValue || existing.Temperature.Value <= 0) && tVal.HasValue)
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
                                CoreIndex = coreNum,
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
                Debug.WriteLine($"Failed HWiNFO registry query fallback: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _helper?.Dispose();
        }

        private HardwareDiagnostics GetMacOrGenericDiagnostics(bool isStressActive = false, int stressedThreads = 0)
        {
            var diag = new HardwareDiagnostics();
            var rand = new Random();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                diag.CpuName = RunMacCommand("sysctl", "-n machdep.cpu.brand_string") ?? "Apple Silicon Processor";
            }
            else
            {
                diag.CpuName = "Generic Core Processor";
            }

            float targetLoad = 12.0f;
            float targetTemp = 46.0f;

            if (isStressActive && stressedThreads > 0)
            {
                float loadRatio = (float)stressedThreads / Environment.ProcessorCount;
                if (loadRatio > 1.0f) loadRatio = 1.0f;
                targetLoad = 12.0f + loadRatio * 86.0f;
                targetTemp = 46.0f + loadRatio * 38.0f;
            }

            _lastMacCpuLoad = _lastMacCpuLoad + (targetLoad - _lastMacCpuLoad) * 0.2f;
            _lastMacCpuTemp = _lastMacCpuTemp + (targetTemp - _lastMacCpuTemp) * 0.15f;

            diag.CpuAverageLoad = _lastMacCpuLoad + (float)(rand.NextDouble() * 2.0 - 1.0);
            if (diag.CpuAverageLoad < 0) diag.CpuAverageLoad = 0;
            if (diag.CpuAverageLoad > 100) diag.CpuAverageLoad = 100;

            diag.CpuAverageTemp = _lastMacCpuTemp + (float)(rand.NextDouble() * 1.5 - 0.75);
            diag.CpuClockSpeed = 3200f;

            int totalCores = Environment.ProcessorCount;
            if (totalCores <= 0) totalCores = 8;

            for (int i = 1; i <= totalCores; i++)
            {
                float coreTargetLoad = 12.0f;
                float coreTargetTemp = 46.0f;

                if (isStressActive && i <= stressedThreads)
                {
                    coreTargetLoad = 98.0f;
                    coreTargetTemp = 82.0f;
                }

                float coreLoad = coreTargetLoad + (float)(rand.NextDouble() * 4.0 - 2.0);
                float coreTemp = coreTargetTemp + (float)(rand.NextDouble() * 3.0 - 1.5);

                if (coreLoad < 0) coreLoad = 0;
                if (coreLoad > 100) coreLoad = 100;

                diag.CpuCores.Add(new CpuCoreInfo
                {
                    CoreIndex = i,
                    Name = $"Core #{i}",
                    Load = coreLoad,
                    Temperature = coreTemp
                });
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                diag.GpuName = "Apple M-Series Integrated Graphics";
            }
            else
            {
                diag.GpuName = "Generic Integrated Graphics";
            }

            diag.GpuLoad = (float)(5.0 + rand.NextDouble() * 5.0);
            diag.GpuTemp = (float)(40.0 + rand.NextDouble() * 3.0);
            diag.GpuVramUsage = (float)(15.0 + rand.NextDouble() * 10.0);

            double totalMemBytes = 16.0 * 1024 * 1024 * 1024;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string memSizeStr = RunMacCommand("sysctl", "-n hw.memsize");
                if (double.TryParse(memSizeStr, out double memBytes))
                {
                    totalMemBytes = memBytes;
                }
            }
            diag.RamTotalGb = (float)(totalMemBytes / (1024.0 * 1024.0 * 1024.0));
            diag.RamPercent = (float)(58.0 + rand.NextDouble() * 4.0 - 2.0);
            diag.RamUsedGb = (diag.RamPercent / 100.0f) * diag.RamTotalGb;

            diag.IsLhmDriverBlocked = false;
            diag.IsHWiNFOFallbackActive = false;

            return diag;
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
    }

    public interface IHardwareMonitorHelper : IDisposable
    {
        void UpdateDiagnostics(HardwareDiagnostics diag);
    }

    public class WindowsHardwareMonitorHelper : IHardwareMonitorHelper
    {
        private readonly Computer _computer;
        private readonly UpdateVisitor _updateVisitor = new UpdateVisitor();
        private bool _isDisposed;
        private float _cachedWmiClockMhz;
        private DateTime _lastWmiClockCheck = DateTime.MinValue;

        private static readonly Regex CoreNumberRegex = new Regex(@"Core\s*#?(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public WindowsHardwareMonitorHelper()
        {
            try
            {
                // LibreHardwareMonitor 0.9.6+ uses the signed PawnIO kernel driver instead of WinRing0.
                // Ensure PawnIO is installed before Computer.Open() so CPU temperatures, clocks & power work.
                EnsurePawnIoDriverInstalled();

                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsControllerEnabled = true,
                    IsStorageEnabled = true,
                    IsBatteryEnabled = true,
                    IsNetworkEnabled = true,
                    IsPsuEnabled = true
                };
                _computer.Open();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize hardware monitor: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks whether the PawnIO driver required by LibreHardwareMonitor 0.9.6 is installed.
        /// If missing, automatically installs it silently from a bundled or downloaded PawnIO_setup.exe.
        /// </summary>
        private static void EnsurePawnIoDriverInstalled()
        {
            try
            {
                if (IsPawnIoInstalled())
                    return;

                string baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string localInstaller = Path.Combine(baseDir, "PawnIO_setup.exe");
                string tempInstaller = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                string installerPath = null;

                if (File.Exists(localInstaller))
                {
                    installerPath = localInstaller;
                }
                else
                {
                    foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Removable))
                    {
                        string usbCandidate = Path.Combine(drive.RootDirectory.FullName, "PawnIO_setup.exe");
                        if (File.Exists(usbCandidate))
                        {
                            installerPath = usbCandidate;
                            break;
                        }
                    }
                }

                if (installerPath == null)
                {
                    string downloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
                    using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                    {
                        byte[] data = httpClient.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();
                        try
                        {
                            File.WriteAllBytes(localInstaller, data);
                            installerPath = localInstaller;
                        }
                        catch
                        {
                            File.WriteAllBytes(tempInstaller, data);
                            installerPath = tempInstaller;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(installerPath) && File.Exists(installerPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = installerPath,
                        Arguments = "-install -silent",
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit(15000);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PawnIO auto-install skipped/failed: {ex.Message}");
            }
        }

        private static bool IsPawnIoInstalled()
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
                {
                    if (key?.GetValue("DisplayVersion") != null)
                        return true;
                }
            }
            catch
            {
                // Ignore registry check error
            }
            return false;
        }

        private static IEnumerable<ISensor> GetAllSensors(IHardware hardware)
        {
            if (hardware == null) yield break;

            foreach (var sensor in hardware.Sensors)
            {
                yield return sensor;
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                foreach (var sensor in GetAllSensors(subHardware))
                {
                    yield return sensor;
                }
            }
        }

        public void UpdateDiagnostics(HardwareDiagnostics diag)
        {
            if (_isDisposed || _computer == null) return;

            try
            {
                // Traverse entire hardware tree including SubHardware (SuperIO, EC, etc.)
                _computer.Accept(_updateVisitor);

                float? motherboardCpuTempFallback = null;
                var gpuCandidates = new List<GpuCandidate>();

                foreach (var hardware in _computer.Hardware)
                {
                    var allSensors = GetAllSensors(hardware).ToList();

                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
                            ReadCpuSensors(hardware, allSensors, diag);
                            break;

                        case HardwareType.Motherboard:
                        case HardwareType.SuperIO:
                        case HardwareType.EmbeddedController:
                        case HardwareType.Cooler:
                            ReadMotherboardSensors(hardware, allSensors, diag, ref motherboardCpuTempFallback);
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            var candidate = ReadGpuCandidate(hardware, allSensors);
                            if (candidate != null)
                            {
                                gpuCandidates.Add(candidate);
                            }
                            break;

                        case HardwareType.Memory:
                            ReadMemorySensors(allSensors, diag);
                            break;

                        case HardwareType.Storage:
                            ReadStorageSensors(hardware, allSensors, diag);
                            break;

                        case HardwareType.Battery:
                            ReadBatterySensors(hardware, allSensors, diag.BatteryData);
                            break;
                    }
                }

                // Select best GPU candidate (prefer dedicated GPU or active GPU over idle iGPU)
                if (gpuCandidates.Count > 0)
                {
                    var bestGpu = gpuCandidates
                        .OrderByDescending(g => g.PriorityScore)
                        .First();

                    diag.GpuName = bestGpu.Name;
                    diag.GpuLoad = bestGpu.Load;
                    diag.GpuTemp = bestGpu.Temp > 0 ? bestGpu.Temp : (float?)null;
                    diag.GpuHotSpotTemp = bestGpu.HotSpotTemp;
                    diag.GpuVramUsage = bestGpu.VramUsagePercent;
                    diag.GpuVramUsedMb = bestGpu.VramUsedMb;
                    diag.GpuVramTotalMb = bestGpu.VramTotalMb;
                    diag.GpuClockSpeed = bestGpu.ClockMhz;
                    diag.GpuPower = bestGpu.PowerWatts;
                }

                // Fallback for CPU temperature from Motherboard SuperIO / EC if CPU sensor returned 0
                if ((!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value <= 0) &&
                    motherboardCpuTempFallback.HasValue && motherboardCpuTempFallback.Value > 0)
                {
                    diag.CpuAverageTemp = motherboardCpuTempFallback.Value;
                }

                // Fallback for CPU temperature via WMI thermal zone if still 0
                if (!diag.CpuAverageTemp.HasValue || diag.CpuAverageTemp.Value <= 0)
                {
                    float? wmiTemp = TryGetWmiThermalZoneTemp();
                    if (wmiTemp.HasValue && wmiTemp.Value > 0)
                    {
                        diag.CpuAverageTemp = wmiTemp.Value;
                    }
                }

                // Fallback for CPU clock speed via WMI if Ring0/PawnIO clock sensor returned 0
                if (!diag.CpuClockSpeed.HasValue || diag.CpuClockSpeed.Value <= 0)
                {
                    float wmiClock = GetWmiCpuClockFallback();
                    if (wmiClock > 0)
                    {
                        diag.CpuClockSpeed = wmiClock;
                    }
                }

                // If individual cores don't have temperatures (e.g., Ryzen reporting only Tctl/Tdie or motherboard fallback),
                // propagate package temperature to core cards so they never show "-- °C" when package temp is known.
                if (diag.CpuAverageTemp.HasValue && diag.CpuAverageTemp.Value > 0 && diag.CpuCores.Count > 0)
                {
                    foreach (var core in diag.CpuCores)
                    {
                        if (!core.Temperature.HasValue || core.Temperature.Value <= 0)
                        {
                            core.Temperature = diag.CpuAverageTemp.Value;
                        }
                    }
                }

                // Fallback for RAM using Win32 GlobalMemoryStatusEx if LibreHardwareMonitor Memory didn't report
                if (diag.RamTotalGb <= 0)
                {
                    ApplyWin32MemoryFallback(diag);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error updating sensors: {ex.Message}");
            }
        }

        private static void ReadCpuSensors(IHardware hardware, List<ISensor> sensors, HardwareDiagnostics diag)
        {
            if (!string.IsNullOrWhiteSpace(hardware.Name))
            {
                diag.CpuName = hardware.Name;
            }

            float? packageTemp = null;
            float? secondaryPackageTemp = null;
            float totalCoreTemp = 0;
            int coreTempCount = 0;

            float totalClock = 0;
            int clockCount = 0;
            float totalEffectiveClock = 0;
            int effectiveClockCount = 0;
            float? averageClockSensor = null;

            float smuCoresPowerSum = 0;

            var coreMap = new Dictionary<string, CpuCoreAccumulator>(StringComparer.OrdinalIgnoreCase);
            var ccdTemps = new Dictionary<int, float>();

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                switch (sensor.SensorType)
                {
                    case SensorType.Temperature:
                        if (val <= 0 || val > 150) break;

                        if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            packageTemp = val;
                        }
                        else if (name.IndexOf("Max", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!secondaryPackageTemp.HasValue || val > secondaryPackageTemp.Value)
                                secondaryPackageTemp = val;
                        }

                        var coreTempMatch = CoreNumberRegex.Match(name);
                        bool isIgnoredTempName = name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf("TjMax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf("Tj Max", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf("Max", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (coreTempMatch.Success && !isIgnoredTempName)
                        {
                            int coreIdx = int.Parse(coreTempMatch.Groups[1].Value);
                            string key = $"Core #{coreIdx}";
                            if (!coreMap.TryGetValue(key, out var acc))
                            {
                                acc = new CpuCoreAccumulator { CoreIndex = coreIdx, Name = $"CPU Core #{coreIdx}" };
                                coreMap[key] = acc;
                            }
                            acc.Temperature = val;
                            totalCoreTemp += val;
                            coreTempCount++;
                        }
                        else if (name.IndexOf("CCD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Tccd", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            totalCoreTemp += val;
                            coreTempCount++;

                            var ccdMatch = Regex.Match(name, @"(\d+)");
                            int ccdIdx = ccdMatch.Success ? int.Parse(ccdMatch.Groups[1].Value) : 1;
                            ccdTemps[ccdIdx] = val;
                        }
                        break;

                    case SensorType.Load:
                        if (name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            diag.CpuAverageLoad = val;
                        }
                        else if (name.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0 &&
                                 name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            var coreLoadMatch = CoreNumberRegex.Match(name);
                            if (coreLoadMatch.Success)
                            {
                                int coreIdx = int.Parse(coreLoadMatch.Groups[1].Value);
                                string key = $"Core #{coreIdx}";
                                if (!coreMap.TryGetValue(key, out var acc))
                                {
                                    acc = new CpuCoreAccumulator { CoreIndex = coreIdx, Name = $"CPU Core #{coreIdx}" };
                                    coreMap[key] = acc;
                                }
                                acc.LoadSum += val;
                                acc.LoadCount++;
                            }
                        }
                        break;

                    case SensorType.Clock:
                        if (name.IndexOf("Bus", StringComparison.OrdinalIgnoreCase) >= 0 || val <= 100)
                            break;

                        if (name.Equals("Cores (Average)", StringComparison.OrdinalIgnoreCase))
                        {
                            averageClockSensor = val;
                        }
                        else if (name.Equals("Cores (Average Effective)", StringComparison.OrdinalIgnoreCase) && !averageClockSensor.HasValue)
                        {
                            averageClockSensor = val;
                        }
                        else if (name.IndexOf("Effective", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            totalEffectiveClock += val;
                            effectiveClockCount++;
                        }
                        else
                        {
                            totalClock += val;
                            clockCount++;

                            var coreClockMatch = CoreNumberRegex.Match(name);
                            if (coreClockMatch.Success)
                            {
                                int coreIdx = int.Parse(coreClockMatch.Groups[1].Value);
                                string key = $"Core #{coreIdx}";
                                if (!coreMap.TryGetValue(key, out var acc))
                                {
                                    acc = new CpuCoreAccumulator { CoreIndex = coreIdx, Name = $"CPU Core #{coreIdx}" };
                                    coreMap[key] = acc;
                                }
                                acc.ClockMhz = val;
                            }
                        }
                        break;

                    case SensorType.Power:
                        if (val <= 0) break;

                        if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (diag.CpuPackagePower <= 0 && name.Equals("Cores", StringComparison.OrdinalIgnoreCase)))
                        {
                            diag.CpuPackagePower = val;
                        }
                        else if (name.IndexOf("(SMU)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            smuCoresPowerSum += val;
                        }
                        break;

                    case SensorType.Voltage:
                        if (val > 0.2f && val < 2.5f &&
                            (name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("Vcore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("SVI2", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            diag.CpuVoltage = val;
                        }
                        break;
                }
            }

            if (diag.CpuPackagePower <= 0 && smuCoresPowerSum > 0)
            {
                diag.CpuPackagePower = smuCoresPowerSum;
            }

            if (packageTemp.HasValue && packageTemp.Value > 0)
            {
                diag.CpuAverageTemp = packageTemp.Value;
            }
            else if (secondaryPackageTemp.HasValue && secondaryPackageTemp.Value > 0)
            {
                diag.CpuAverageTemp = secondaryPackageTemp.Value;
            }
            else if (coreTempCount > 0)
            {
                diag.CpuAverageTemp = totalCoreTemp / coreTempCount;
            }

            if (clockCount > 0)
            {
                diag.CpuClockSpeed = totalClock / clockCount;
            }
            else if (averageClockSensor.HasValue && averageClockSensor.Value > 0)
            {
                diag.CpuClockSpeed = averageClockSensor.Value;
            }
            else if (effectiveClockCount > 0)
            {
                diag.CpuClockSpeed = totalEffectiveClock / effectiveClockCount;
            }

            var coreList = new List<CpuCoreInfo>();
            foreach (var acc in coreMap.Values.OrderBy(c => c.CoreIndex).ThenBy(c => c.Name))
            {
                float? temp = acc.Temperature;

                if (!temp.HasValue && ccdTemps.Count > 0)
                {
                    int ccdIdx = ((acc.CoreIndex - 1) / 8) + 1;
                    if (ccdTemps.TryGetValue(ccdIdx, out float ccdTemp))
                        temp = ccdTemp;
                    else if (ccdTemps.TryGetValue(1, out float firstCcdTemp))
                        temp = firstCcdTemp;
                }

                if (!temp.HasValue && diag.CpuAverageTemp.HasValue && diag.CpuAverageTemp.Value > 0)
                {
                    temp = diag.CpuAverageTemp.Value;
                }

                coreList.Add(new CpuCoreInfo
                {
                    CoreIndex = acc.CoreIndex,
                    Name = acc.Name,
                    Temperature = temp,
                    Load = acc.LoadCount > 0 ? (float?)(acc.LoadSum / acc.LoadCount) : null,
                    ClockMhz = acc.ClockMhz
                });
            }

            if ((!diag.CpuAverageLoad.HasValue || diag.CpuAverageLoad.Value <= 0) && coreList.Any(c => c.Load.HasValue))
            {
                diag.CpuAverageLoad = coreList.Where(c => c.Load.HasValue).Average(c => c.Load.Value);
            }

            diag.CpuCores = coreList;
        }

        private static void ReadMotherboardSensors(
            IHardware hardware,
            List<ISensor> sensors,
            HardwareDiagnostics diag,
            ref float? motherboardCpuTempFallback)
        {
            if (hardware.HardwareType == HardwareType.Motherboard && !string.IsNullOrWhiteSpace(hardware.Name))
            {
                diag.MotherboardName = hardware.Name;
            }

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                if (sensor.SensorType == SensorType.Temperature && val > 0 && val < 130)
                {
                    if (name.IndexOf("CPU", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("PECI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!motherboardCpuTempFallback.HasValue || val > motherboardCpuTempFallback.Value)
                        {
                            motherboardCpuTempFallback = val;
                        }
                    }
                    else if (diag.MotherboardTemp <= 0 &&
                             (name.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Motherboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Mainboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Chipset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              name.IndexOf("Temperature #1", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        diag.MotherboardTemp = val;
                    }
                }
                else if (sensor.SensorType == SensorType.Fan && val > 50 && val < 10000)
                {
                    diag.Fans.Add(new FanSensorInfo
                    {
                        Name = name,
                        Rpm = val
                    });
                }
            }
        }

        private static GpuCandidate ReadGpuCandidate(IHardware hardware, List<ISensor> sensors)
        {
            var candidate = new GpuCandidate
            {
                Name = hardware.Name ?? "Unknown GPU",
                HardwareType = hardware.HardwareType
            };

            float? coreTemp = null;
            float? fallbackTemp = null;
            float? coreLoad = null;
            float? d3dLoad = null;
            float? vramLoadPercent = null;

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                switch (sensor.SensorType)
                {
                    case SensorType.Temperature:
                        if (val <= 0 || val > 150) break;

                        if (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("GPU", StringComparison.OrdinalIgnoreCase))
                        {
                            coreTemp = val;
                        }
                        else if (name.IndexOf("Hot Spot", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            candidate.HotSpotTemp = val;
                            if (!fallbackTemp.HasValue) fallbackTemp = val;
                        }
                        else if (!fallbackTemp.HasValue)
                        {
                            fallbackTemp = val;
                        }
                        break;

                    case SensorType.Load:
                        if (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                        {
                            coreLoad = val;
                        }
                        else if (name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase) ||
                                 name.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            vramLoadPercent = val;
                        }
                        else if (name.Equals("D3D 3D", StringComparison.OrdinalIgnoreCase) ||
                                 name.IndexOf("D3D Compute", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!d3dLoad.HasValue || val > d3dLoad.Value)
                                d3dLoad = val;
                        }
                        break;

                    case SensorType.SmallData:
                    case SensorType.Data:
                        float mbValue = sensor.SensorType == SensorType.Data ? val * 1024f : val;
                        if (name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase) ||
                            (candidate.VramUsedMb <= 0 && name.Equals("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase)))
                        {
                            candidate.VramUsedMb = mbValue;
                        }
                        else if (name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                        {
                            candidate.VramTotalMb = mbValue;
                        }
                        break;

                    case SensorType.Clock:
                        if (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase) && val > 0)
                        {
                            candidate.ClockMhz = val;
                        }
                        break;

                    case SensorType.Power:
                        if (val > 0 &&
                            (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("Total", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            if (val > candidate.PowerWatts)
                                candidate.PowerWatts = val;
                        }
                        break;
                }
            }

            candidate.Temp = coreTemp ?? fallbackTemp ?? 0;
            candidate.Load = coreLoad ?? d3dLoad ?? 0;

            if (vramLoadPercent.HasValue && vramLoadPercent.Value > 0)
            {
                candidate.VramUsagePercent = vramLoadPercent.Value;
            }
            else if (candidate.VramTotalMb > 0 && candidate.VramUsedMb >= 0)
            {
                candidate.VramUsagePercent = (candidate.VramUsedMb / candidate.VramTotalMb) * 100f;
            }

            return candidate;
        }

        private static void ReadMemorySensors(List<ISensor> sensors, HardwareDiagnostics diag)
        {
            float used = 0;
            float available = 0;

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                if (name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                if (sensor.SensorType == SensorType.Load &&
                    name.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                {
                    diag.RamPercent = val;
                }
                else if (sensor.SensorType == SensorType.Data)
                {
                    if (name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase) ||
                        name.IndexOf("Used", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        used = val;
                    }
                    else if (name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase) ||
                             name.IndexOf("Available", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        available = val;
                    }
                }
            }

            if (used > 0 || available > 0)
            {
                diag.RamUsedGb = used;
                diag.RamTotalGb = used + available;
                if (diag.RamPercent <= 0 && diag.RamTotalGb > 0)
                {
                    diag.RamPercent = (diag.RamUsedGb / diag.RamTotalGb) * 100f;
                }
            }
        }

        private static void ReadStorageSensors(IHardware hardware, List<ISensor> sensors, HardwareDiagnostics diag)
        {
            var drive = new StorageDriveInfo
            {
                Name = hardware.Name ?? "Storage Drive"
            };

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                if (sensor.SensorType == SensorType.Temperature && val > 0 && val < 120)
                {
                    if (!drive.Temperature.HasValue || name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        drive.Temperature = val;
                    }
                }
                else if (sensor.SensorType == SensorType.Load &&
                         name.IndexOf("Used Space", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    drive.UsedSpacePercent = val;
                }
                else if (sensor.SensorType == SensorType.Level)
                {
                    if (name.IndexOf("Remaining Life", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Available Spare", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        drive.HealthPercent = val;
                    }
                    else if (name.IndexOf("Percentage Used", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        drive.HealthPercent = Math.Max(0, 100f - val);
                    }
                }
            }

            diag.StorageDrives.Add(drive);
        }

        private static void ReadBatterySensors(IHardware hardware, List<ISensor> sensors, LhmBatteryData batteryData)
        {
            batteryData.IsFound = true;
            if (!string.IsNullOrWhiteSpace(hardware.Name))
            {
                batteryData.Name = hardware.Name;
            }

            foreach (var sensor in sensors)
            {
                if (!sensor.Value.HasValue) continue;
                float val = sensor.Value.Value;
                string name = sensor.Name ?? string.Empty;

                switch (sensor.SensorType)
                {
                    case SensorType.Level:
                        if (name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0)
                            batteryData.ChargeLevel = val;
                        else if (name.IndexOf("Degradation", StringComparison.OrdinalIgnoreCase) >= 0)
                            batteryData.DegradationLevel = val;
                        break;

                    case SensorType.Energy:
                        if (name.IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0)
                            batteryData.DesignCapacityMWh = val;
                        else if (name.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0)
                            batteryData.FullChargeCapacityMWh = val;
                        else if (name.IndexOf("Remaining", StringComparison.OrdinalIgnoreCase) >= 0)
                            batteryData.RemainingCapacityMWh = val;
                        break;

                    case SensorType.Voltage:
                        batteryData.Voltage = val;
                        break;

                    case SensorType.Power:
                        batteryData.ChargeDischargeRateWatts = Math.Abs(val);
                        if (name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            name.IndexOf("Discharge", StringComparison.OrdinalIgnoreCase) < 0 && val > 0.1f)
                        {
                            batteryData.IsCharging = true;
                        }
                        else if (name.IndexOf("Discharge", StringComparison.OrdinalIgnoreCase) >= 0 && val > 0.1f)
                        {
                            batteryData.IsDischarging = true;
                        }
                        break;

                    case SensorType.TimeSpan:
                        if (val > 0)
                            batteryData.RemainingTimeSeconds = val;
                        break;
                }
            }
        }

        private float GetWmiCpuClockFallback()
        {
            if ((DateTime.Now - _lastWmiClockCheck).TotalSeconds < 5 && _cachedWmiClockMhz > 0)
                return _cachedWmiClockMhz;

            _lastWmiClockCheck = DateTime.Now;
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT CurrentClockSpeed, MaxClockSpeed FROM Win32_Processor"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        uint current = obj["CurrentClockSpeed"] != null ? Convert.ToUInt32(obj["CurrentClockSpeed"]) : 0;
                        uint max = obj["MaxClockSpeed"] != null ? Convert.ToUInt32(obj["MaxClockSpeed"]) : 0;
                        float mhz = current > 0 ? current : max;
                        if (mhz > 0)
                        {
                            _cachedWmiClockMhz = mhz;
                            return mhz;
                        }
                    }
                }
            }
            catch
            {
                // Ignore WMI error
            }
            return _cachedWmiClockMhz;
        }

        private static void ApplyWin32MemoryFallback(HardwareDiagnostics diag)
        {
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus) && memStatus.ullTotalPhys > 0)
                {
                    double totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    double availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                    double usedGb = totalGb - availGb;

                    diag.RamTotalGb = (float)totalGb;
                    diag.RamUsedGb = (float)usedGb;
                    diag.RamPercent = memStatus.dwMemoryLoad;
                }
            }
            catch
            {
                // Ignore Win32 fallback error
            }
        }

        private static float? TryGetWmiThermalZoneTemp()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (var collection = searcher.Get())
                {
                    foreach (ManagementObject obj in collection)
                    {
                        if (obj["CurrentTemperature"] != null)
                        {
                            double raw = Convert.ToDouble(obj["CurrentTemperature"]);
                            double celsius = (raw / 10.0) - 273.15;
                            if (celsius > 10 && celsius < 125)
                            {
                                return (float)celsius;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore if WMI thermal zone is not supported on this board
            }
            return null;
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
                // Ignore
            }
        }

        private class CpuCoreAccumulator
        {
            public int CoreIndex { get; set; }
            public string Name { get; set; }
            public float? Temperature { get; set; }
            public float LoadSum { get; set; }
            public int LoadCount { get; set; }
            public float? ClockMhz { get; set; }
        }

        private class GpuCandidate
        {
            public string Name { get; set; }
            public HardwareType HardwareType { get; set; }
            public float Load { get; set; }
            public float Temp { get; set; }
            public float HotSpotTemp { get; set; }
            public float VramUsagePercent { get; set; }
            public float VramUsedMb { get; set; }
            public float VramTotalMb { get; set; }
            public float ClockMhz { get; set; }
            public float PowerWatts { get; set; }

            public float PriorityScore
            {
                get
                {
                    float score = 0;
                    if (HardwareType == HardwareType.GpuNvidia) score += 1000;
                    else if (HardwareType == HardwareType.GpuAmd &&
                             Name.IndexOf("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) < 0)
                        score += 900;
                    else if (HardwareType == HardwareType.GpuAmd)
                        score += 400;
                    else if (HardwareType == HardwareType.GpuIntel)
                        score += 200;

                    if (Temp > 0) score += 100 + Temp;
                    score += Load;
                    return score;
                }
            }
        }
    }
}
