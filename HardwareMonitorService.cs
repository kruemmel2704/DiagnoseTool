using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
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
        public float CpuAverageLoad { get; set; }
        public float CpuAverageTemp { get; set; }
        public float CpuClockSpeed { get; set; }
        public float CpuPackagePower { get; set; }
        public float CpuVoltage { get; set; }
        public List<CpuCoreInfo> CpuCores { get; set; } = new List<CpuCoreInfo>();

        public string MotherboardName { get; set; } = "Unbekanntes Mainboard";
        public float MotherboardTemp { get; set; }
        public List<FanSensorInfo> Fans { get; set; } = new List<FanSensorInfo>();

        public string GpuName { get; set; } = "None / Integrated";
        public float GpuLoad { get; set; }
        public float GpuTemp { get; set; }
        public float GpuHotSpotTemp { get; set; }
        public float GpuVramUsage { get; set; }
        public float GpuVramUsedMb { get; set; }
        public float GpuVramTotalMb { get; set; }
        public float GpuClockSpeed { get; set; }
        public float GpuPower { get; set; }

        public float RamUsedGb { get; set; }
        public float RamTotalGb { get; set; }
        public float RamPercent { get; set; }

        public List<StorageDriveInfo> StorageDrives { get; set; } = new List<StorageDriveInfo>();
        public LhmBatteryData BatteryData { get; set; } = new LhmBatteryData();

        public string CpuClockDisplay => CpuClockSpeed > 0 ? $"{CpuClockSpeed / 1000.0:F2} GHz" : "-- GHz";
        public string CpuPowerDisplay => CpuPackagePower > 0 ? $"{CpuPackagePower:F1} W" : "-- W";
        public string GpuClockDisplay => GpuClockSpeed > 0 ? $"{GpuClockSpeed:F0} MHz" : "-- MHz";
        public string GpuPowerDisplay => GpuPower > 0 ? $"{GpuPower:F1} W" : "-- W";
        public string GpuVramDisplay => GpuVramTotalMb > 0
            ? $"{GpuVramUsedMb / 1024.0:F1} / {GpuVramTotalMb / 1024.0:F1} GB ({GpuVramUsage:F0} %)"
            : $"{GpuVramUsage:F1} %";
        public string RamUsageDisplay => RamTotalGb > 0 ? $"{RamUsedGb:F1} / {RamTotalGb:F1} GB" : "-- GB";
    }

    public class HardwareMonitorService : IDisposable
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

        public HardwareMonitorService()
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

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localInstaller = Path.Combine(baseDir, "PawnIO_setup.exe");
                string tempInstaller = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");
                string installerPath = null;

                if (File.Exists(localInstaller))
                {
                    installerPath = localInstaller;
                }
                else
                {
                    // Also check removable USB drives for a bundled PawnIO_setup.exe
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

                // If not found locally, download the official PawnIO installer
                if (installerPath == null)
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    using (var client = new WebClient())
                    {
                        string downloadUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";
                        string targetPath = localInstaller;
                        try
                        {
                            client.DownloadFile(downloadUrl, targetPath);
                            installerPath = targetPath;
                        }
                        catch
                        {
                            client.DownloadFile(downloadUrl, tempInstaller);
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

        public HardwareDiagnostics GetDiagnostics()
        {
            var diag = new HardwareDiagnostics();
            if (_isDisposed || _computer == null) return diag;

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
                    diag.GpuTemp = bestGpu.Temp;
                    diag.GpuHotSpotTemp = bestGpu.HotSpotTemp;
                    diag.GpuVramUsage = bestGpu.VramUsagePercent;
                    diag.GpuVramUsedMb = bestGpu.VramUsedMb;
                    diag.GpuVramTotalMb = bestGpu.VramTotalMb;
                    diag.GpuClockSpeed = bestGpu.ClockMhz;
                    diag.GpuPower = bestGpu.PowerWatts;
                }

                // Fallback for CPU temperature from Motherboard SuperIO / EC if CPU sensor returned 0
                if (diag.CpuAverageTemp <= 0 && motherboardCpuTempFallback.HasValue && motherboardCpuTempFallback.Value > 0)
                {
                    diag.CpuAverageTemp = motherboardCpuTempFallback.Value;
                }

                // Fallback for CPU temperature via WMI thermal zone if still 0
                if (diag.CpuAverageTemp <= 0)
                {
                    float? wmiTemp = TryGetWmiThermalZoneTemp();
                    if (wmiTemp.HasValue && wmiTemp.Value > 0)
                    {
                        diag.CpuAverageTemp = wmiTemp.Value;
                    }
                }

                // Fallback for CPU clock speed via WMI if Ring0/PawnIO clock sensor returned 0
                if (diag.CpuClockSpeed <= 0)
                {
                    diag.CpuClockSpeed = GetWmiCpuClockFallback();
                }

                // If individual cores don't have temperatures (e.g., Ryzen reporting only Tctl/Tdie or motherboard fallback),
                // propagate package temperature to core cards so they never show "-- °C" when package temp is known.
                if (diag.CpuAverageTemp > 0 && diag.CpuCores.Count > 0)
                {
                    foreach (var core in diag.CpuCores)
                    {
                        if (!core.Temperature.HasValue || core.Temperature.Value <= 0)
                        {
                            core.Temperature = diag.CpuAverageTemp;
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

            return diag;
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

                        // 1. Primary package temperatures (Intel "CPU Package", AMD "Core (Tctl/Tdie)", "Core (Tdie)", "Core (Tctl)")
                        if (name.IndexOf("Package", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Tctl", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
                        {
                            packageTemp = val;
                        }
                        // 2. Secondary overall CPU temperatures ("Core Max", "Cores (Average)", "CCDs Max (Tdie)", "CCDs Average (Tdie)")
                        else if (name.IndexOf("Max", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("Average", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!secondaryPackageTemp.HasValue || val > secondaryPackageTemp.Value)
                                secondaryPackageTemp = val;
                        }

                        // 3. Per-core temperatures ("CPU Core #1", "Core #1", etc.)
                        var coreTempMatch = CoreNumberRegex.Match(name);
                        if (coreTempMatch.Success && name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) < 0)
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
                        // 4. AMD CCD temperatures ("CCD1 (Tdie)", "CCD2 (Tdie)")
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
                        else if (name.IndexOf("Max", StringComparison.OrdinalIgnoreCase) < 0)
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

            // Determine overall CPU temperature
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

            // Determine CPU clock speed
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

            // Build sorted list of CPU cores
            var coreList = new List<CpuCoreInfo>();
            foreach (var acc in coreMap.Values.OrderBy(c => c.CoreIndex).ThenBy(c => c.Name))
            {
                float? temp = acc.Temperature;

                // On AMD Ryzen, map CCD1 (Cores 1..8) / CCD2 (Cores 9..16) temperature if per-core temp isn't exposed
                if (!temp.HasValue && ccdTemps.Count > 0)
                {
                    int ccdIdx = ((acc.CoreIndex - 1) / 8) + 1;
                    if (ccdTemps.TryGetValue(ccdIdx, out float ccdTemp))
                        temp = ccdTemp;
                    else if (ccdTemps.TryGetValue(1, out float firstCcdTemp))
                        temp = firstCcdTemp;
                }

                if (!temp.HasValue && diag.CpuAverageTemp > 0)
                {
                    temp = diag.CpuAverageTemp;
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

            // Fallback if CPU load wasn't reported by "CPU Total"
            if (diag.CpuAverageLoad <= 0 && coreList.Any(c => c.Load.HasValue))
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
                        // LibreHardwareMonitor reports GPU memory in MB under SmallData ("GPU Memory Used", "GPU Memory Total", "D3D Dedicated Memory Used")
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

                // Explicitly skip "Virtual Memory" sensors so physical RAM is never overwritten by pagefile metrics
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
                // Ignore failures on close
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
                    // Prefer dedicated Nvidia/AMD GPUs over integrated Intel/AMD Radeon(TM) Graphics
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
