using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace DiagnoseTool
{
    /// <summary>
    /// REST-Fernsteuerung: Implementierung von <see cref="IDiagnoseRemoteControl"/>.
    /// Alle Zugriffe auf UI-gebundene Zustände werden auf den Avalonia UI-Thread gemarshallt.
    /// </summary>
    public partial class MainViewModel
    {
        private static readonly TimeSpan RemoteUiTimeout = TimeSpan.FromSeconds(45);

        private static readonly (string Key, string Description, string Command)[] RepairCommands =
        {
            ("sfc", "System File Checker (SFC)", "sfc /scannow"),
            ("dism_check", "DISM Check Health", "dism /online /cleanup-image /checkhealth"),
            ("dism_restore", "DISM Restore Health", "dism /online /cleanup-image /restorehealth"),
            ("chkdsk", "Chkdsk C: (Read-only)", "chkdsk C:")
        };

        private static T OnUi<T>(Func<T> func)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return func();
            }

            var task = Dispatcher.UIThread.InvokeAsync(func).GetTask();
            if (!task.Wait(RemoteUiTimeout))
            {
                throw new TimeoutException("Die Oberfläche des DiagnoseTools hat nicht rechtzeitig reagiert.");
            }
            return task.Result;
        }

        private static string FormatRemaining(TimeSpan t)
            => string.Format("{0:D2}:{1:D2}", (int)t.TotalMinutes, t.Seconds);

        private TestTool FindTool(string key)
            => _launcherService.Tools?.FirstOrDefault(t => string.Equals(t.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

        private object ToolDto(TestTool t) => new
        {
            key = t.Key,
            name = t.Name,
            description = t.Description,
            exeName = t.ExeName,
            alternateExeName = t.AlternateExeName,
            path = t.Path,
            isFound = t.IsFound,
            isEmbedded = t.IsEmbedded,
            displayStatus = t.DisplayStatus,
            isRunning = t.IsFurMark ? _launcherService.IsFurMarkRunning() : IsProcessRunning(t.ExeName, t.AlternateExeName)
        };

        private static bool IsProcessRunning(params string[] exeNames)
        {
            try
            {
                foreach (var exe in exeNames)
                {
                    if (string.IsNullOrWhiteSpace(exe)) continue;
                    string name = Path.GetFileNameWithoutExtension(exe);
                    if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0) return true;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------
        // Status / Informationen
        // ------------------------------------------------------------------

        public object GetAppState() => OnUi(() => new
        {
            appName = "CPM DiagnoseTool",
            version = AppInfo.Version,
            machineName = Environment.MachineName,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            currentTime = CurrentTime,
            selectedTab = SelectedTab,
            selectedTabName = TabResolver.NameOf(SelectedTab),
            availableTabs = TabResolver.TabNames,
            statusMessage = StatusMessage,
            isStatusError = IsStatusError,
            isAdmin = IsAdmin,
            adminStatusText = AdminStatusText,
            hasDriverError = HasDriverError,
            isLhmDriverBlocked = IsLhmDriverBlocked,
            isHWiNFOFallbackActive = IsHWiNFOFallbackActive,
            isHWiNFOPortableFound = IsHWiNFOPortableFound,
            isHWiNFORunning = IsHWiNFORunning(),
            isSleepPrevented = SleepManagementService.IsActive,
            orderNumber = OrderNumber,
            onlineMonitoring = new
            {
                isActive = IsOnlineMonitoringActive,
                statusText = OnlineMonitoringStatusText,
                localIp = LhmHttpServer.GetPrimaryLocalIp(),
                port = 8085
            },
            hardwareCheck = new
            {
                isConnected = IsHardwareCheckConnected,
                serverHost = HardwareCheckServerInfo,
                statusText = HardwareCheckStatusDisplay
            },
            stressDurationOptions = StressDurationOptions,
            isCpuStressRunning = IsCpuStressRunning,
            isRamStressRunning = IsRamStressRunning,
            isGpuStressRunning = _launcherService.IsFurMarkRunning(),
            isBatteryTestRunning = IsBatteryTestRunning,
            isRecording = IsRecording
        });

        public object GetDiagnosticsSnapshot() => new
        {
            timestamp = DateTime.Now.ToString("o"),
            diagnostics = Diagnostics,
            system = SystemInfo,
            battery = BatteryInfo
        };

        public object GetTools() => OnUi(() => new
        {
            tools = (_launcherService.Tools ?? new List<TestTool>()).Select(ToolDto).ToList()
        });

        public object GetRepairCommands() => new
        {
            isAdmin = IsAdmin,
            isWindows = OperatingSystem.IsWindows(),
            commands = RepairCommands.Select(c => new { key = c.Key, description = c.Description, command = c.Command }).ToList()
        };

        public object GetHWiNFOState() => OnUi(() => new
        {
            isPortableFound = IsHWiNFOPortableFound,
            path = GetHWiNFOPath(),
            isRunning = IsHWiNFORunning(),
            isFallbackActive = IsHWiNFOFallbackActive,
            isLhmDriverBlocked = IsLhmDriverBlocked,
            hasDriverError = HasDriverError
        });

        public object GetCpuStressDetails() => OnUi(() => new
        {
            isRunning = IsCpuStressRunning,
            isInfinite = _isCpuStressInfinite,
            remaining = CpuStressRemainingDisplay,
            threads = CpuStressSelectedThreads,
            maxThreads = MaxThreads,
            durationIndex = CpuStressSelectedDurationIndex,
            durationText = StressDurationOptions.ElementAtOrDefault(CpuStressSelectedDurationIndex),
            durationOptions = StressDurationOptions,
            completed = CpuStressCompleted,
            results = CpuStressResults,
            log = CpuStressLog.ToList(),
            currentLoad = Diagnostics?.CpuAverageLoad,
            currentTemp = Diagnostics?.CpuAverageTemp
        });

        public object GetRamStressDetails() => OnUi(() => new
        {
            isRunning = IsRamStressRunning,
            isInfinite = _isRamStressInfinite,
            remaining = RamStressRemainingDisplay,
            targetPercent = RamStressTargetPercent,
            durationIndex = RamStressSelectedDurationIndex,
            durationText = StressDurationOptions.ElementAtOrDefault(RamStressSelectedDurationIndex),
            durationOptions = StressDurationOptions,
            allocatedGb = RamStressAllocatedGb,
            speedGbps = RamStressSpeedGbps,
            passCount = RamStressPassCount,
            errorCount = RamStressErrorCount,
            statusText = RamStressStatusText,
            completed = RamStressCompleted,
            results = RamStressResults,
            log = RamStressLog.ToList(),
            currentRamPercent = Diagnostics?.RamPercent
        });

        public object GetGpuStressDetails() => OnUi(() => new
        {
            isRunning = _launcherService.IsFurMarkRunning(),
            isFurMarkFound = IsFurMarkFound,
            furMarkPath = FurMarkTool?.Path,
            isEmbedded = FurMarkTool?.IsEmbedded ?? false,
            hasAutoStop = _furMarkAutoStopCts != null,
            gpuName = Diagnostics?.GpuName,
            currentLoad = Diagnostics?.GpuLoad,
            currentTemp = Diagnostics?.GpuTemp
        });

        public object GetBatteryTestDetails() => OnUi(() => new
        {
            hasBattery = BatteryInfo?.IsPresent ?? false,
            isRunning = IsBatteryTestRunning,
            completed = BatteryTestCompleted,
            durationMinutes = _batteryTestTotalDuration.TotalMinutes,
            remaining = BatteryTestRemainingDisplay,
            startTime = IsBatteryTestRunning || BatteryTestCompleted ? _batteryTestStartTime.ToString("o") : null,
            startPercent = BatteryTestStartPercent,
            currentPercent = BatteryTestCurrentPercent,
            drainPercent = Math.Max(0, BatteryTestStartPercent - BatteryTestCurrentPercent),
            isConnectedToAc = BatteryInfo?.IsConnectedToAc ?? false,
            results = BatteryTestResults,
            log = BatteryTestLog.ToList(),
            canExportReport = BatteryTestCompleted,
            battery = BatteryInfo
        });

        public object GetRecordingDetails(bool includeSamples, int? lastSamples) => OnUi(() =>
        {
            IEnumerable<LogDataPoint> samples = _recordedData;
            if (lastSamples.HasValue && lastSamples.Value > 0)
            {
                samples = _recordedData.Skip(Math.Max(0, _recordedData.Count - lastSamples.Value));
            }

            return (object)new
            {
                isRecording = IsRecording,
                startTime = _recordedData.Count > 0 || IsRecording ? _recordingStartTime.ToString("o") : null,
                duration = RecordingTimeDisplay,
                samplesCount = _recordedData.Count,
                orderNumber = OrderNumber,
                samples = includeSamples ? samples.Select(p => new
                {
                    timestamp = p.Timestamp.ToString("o"),
                    cpuLoad = p.CpuCpuLoad,
                    cpuTemp = p.CpuCpuTemp,
                    gpuLoad = p.GpuGpuLoad,
                    gpuTemp = p.GpuGpuTemp,
                    ramPercent = p.RamPercent
                }).ToList() : null
            };
        });

        // ------------------------------------------------------------------
        // Tools
        // ------------------------------------------------------------------

        private void ReloadToolsBinding()
        {
            Tools = null;
            Tools = _launcherService.Tools;
        }

        public RemoteActionResult LaunchTool(string key) => OnUi(() =>
        {
            var tool = FindTool(key);
            if (tool == null)
            {
                return RemoteActionResult.Fail("tool_launch", $"Unbekanntes Tool '{key}'.");
            }

            if (_launcherService.LaunchTool(tool.Key, out string error))
            {
                ShowStatus($"{tool.Name} über Remote gestartet.", false);
                return RemoteActionResult.Ok("tool_launch", $"{tool.Name} gestartet.", ToolDto(tool));
            }

            ShowStatus(error ?? $"{tool.Name} konnte nicht gestartet werden.", true);
            return RemoteActionResult.Fail("tool_launch", error ?? $"{tool.Name} konnte nicht gestartet werden.", ToolDto(tool));
        });

        public RemoteActionResult SetToolPath(string key, string path) => OnUi(() =>
        {
            var tool = FindTool(key);
            if (tool == null)
            {
                return RemoteActionResult.Fail("tool_path", $"Unbekanntes Tool '{key}'.");
            }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path.Trim()))
            {
                return RemoteActionResult.Fail("tool_path", $"Datei '{path}' wurde auf dem Client nicht gefunden.");
            }

            _launcherService.SaveManualPath(tool.Key, path.Trim());
            ReloadToolsBinding();
            ShowStatus($"Pfad für {tool.Key} über Remote aktualisiert.", false);
            return RemoteActionResult.Ok("tool_path", $"Pfad für {tool.Name} gespeichert.", ToolDto(FindTool(tool.Key)));
        });

        public RemoteActionResult ClearToolPath(string key) => OnUi(() =>
        {
            var tool = FindTool(key);
            if (tool == null)
            {
                return RemoteActionResult.Fail("tool_clear_path", $"Unbekanntes Tool '{key}'.");
            }

            ExecuteClearPath(tool.Key);
            return RemoteActionResult.Ok("tool_clear_path", $"Pfad für {tool.Name} auf automatische Erkennung zurückgesetzt.", ToolDto(FindTool(tool.Key)));
        });

        public RemoteActionResult RefreshTools() => OnUi(() =>
        {
            ExecuteRefreshUsb();
            return RemoteActionResult.Ok("tools_refresh", "Laufwerke & USB-Sticks nach Diagnose-Tools durchsucht.",
                _launcherService.Tools.Select(ToolDto).ToList());
        });

        // ------------------------------------------------------------------
        // Reparatur / HWiNFO
        // ------------------------------------------------------------------

        public RemoteActionResult RunRepair(string key) => OnUi(() =>
        {
            string normalized = (key ?? "").Trim().ToLowerInvariant().Replace("-", "_");
            if (!RepairCommands.Any(c => c.Key == normalized))
            {
                return RemoteActionResult.Fail("repair", $"Unbekannter Reparaturbefehl '{key}'. Erlaubt: {string.Join(", ", RepairCommands.Select(c => c.Key))}.");
            }

            bool ok = RunRepairCore(normalized, out string message);
            return ok ? RemoteActionResult.Ok("repair", message, new { key = normalized })
                      : RemoteActionResult.Fail("repair", message, new { key = normalized });
        });

        public RemoteActionResult StartHWiNFO() => OnUi(() =>
        {
            bool ok = StartHWiNFOCore(out string message);
            return ok ? RemoteActionResult.Ok("hwinfo_start", message) : RemoteActionResult.Fail("hwinfo_start", message);
        });

        // ------------------------------------------------------------------
        // Stresstest-Voreinstellungen
        // ------------------------------------------------------------------

        public RemoteActionResult ConfigureCpuStress(int? threads, int? durationIndex) => OnUi(() =>
        {
            if (threads.HasValue && threads.Value > 0)
            {
                CpuStressSelectedThreads = Math.Min(threads.Value, Environment.ProcessorCount * 2);
            }
            if (durationIndex.HasValue && durationIndex.Value >= 0 && durationIndex.Value < StressDurationOptions.Count)
            {
                CpuStressSelectedDurationIndex = durationIndex.Value;
                _overrideCpuDurationSeconds = null;
            }
            return RemoteActionResult.Ok("cpu_config", "CPU-Stresstest Einstellungen übernommen.", new
            {
                threads = CpuStressSelectedThreads,
                durationIndex = CpuStressSelectedDurationIndex,
                durationText = StressDurationOptions[CpuStressSelectedDurationIndex]
            });
        });

        public RemoteActionResult ConfigureRamStress(int? targetPercent, int? durationIndex) => OnUi(() =>
        {
            if (targetPercent.HasValue && targetPercent.Value >= 30 && targetPercent.Value <= 95)
            {
                RamStressTargetPercent = targetPercent.Value;
            }
            if (durationIndex.HasValue && durationIndex.Value >= 0 && durationIndex.Value < StressDurationOptions.Count)
            {
                RamStressSelectedDurationIndex = durationIndex.Value;
                _overrideRamDurationSeconds = null;
            }
            return RemoteActionResult.Ok("ram_config", "RAM-Stresstest Einstellungen übernommen.", new
            {
                targetPercent = RamStressTargetPercent,
                durationIndex = RamStressSelectedDurationIndex,
                durationText = StressDurationOptions[RamStressSelectedDurationIndex]
            });
        });

        // ------------------------------------------------------------------
        // Akkutest
        // ------------------------------------------------------------------

        public RemoteActionResult StartBatteryTest(int? durationMinutes, bool openVideo) => OnUi(() =>
        {
            if (IsBatteryTestRunning)
            {
                return RemoteActionResult.Ok("battery_test_start", "Akkutest läuft bereits.", new { remaining = BatteryTestRemainingDisplay });
            }

            bool ok = StartBatteryTestCore(durationMinutes, openVideo);
            if (!ok)
            {
                return RemoteActionResult.Fail("battery_test_start", "Keine Batterie erkannt. Der Akkutest ist nur auf Laptops möglich.");
            }

            SelectedTab = 4;
            return RemoteActionResult.Ok("battery_test_start",
                $"Akkutest über Remote gestartet ({_batteryTestTotalDuration.TotalMinutes:F0} Min.).",
                new { durationMinutes = _batteryTestTotalDuration.TotalMinutes, openVideo, startPercent = BatteryTestStartPercent });
        });

        public RemoteActionResult StopBatteryTest() => OnUi(() =>
        {
            if (!IsBatteryTestRunning)
            {
                return RemoteActionResult.Ok("battery_test_stop", "Es läuft kein Akkutest.");
            }
            ExecuteStopBatteryTest();
            return RemoteActionResult.Ok("battery_test_stop", "Akkutest über Remote abgebrochen.", new { results = BatteryTestResults });
        });

        // ------------------------------------------------------------------
        // Aufzeichnung & Berichte
        // ------------------------------------------------------------------

        public RemoteActionResult ClearRecording() => OnUi(() =>
        {
            int count = _recordedData.Count;
            _recordedData.Clear();
            if (IsRecording)
            {
                _recordingStartTime = DateTime.Now;
                RecordingTimeDisplay = "00:00:00";
            }
            OnPropertyChanged(nameof(RecordedSamplesCount));
            ShowStatus("Aufgezeichnete Messwerte über Remote verworfen.", false);
            return RemoteActionResult.Ok("recording_clear", $"{count} Messpunkte verworfen.");
        });

        private string BuildFileSuffix()
        {
            string order = string.IsNullOrWhiteSpace(OrderNumber)
                ? ""
                : "_" + string.Join("_", OrderNumber.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
            return $"{order}_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        public RemoteFileResult ExportRecordingCsv() => OnUi(() =>
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp;CpuLoadPercent;CpuTempC;GpuLoadPercent;GpuTempC;RamPercent");
            foreach (var p in _recordedData)
            {
                sb.Append(p.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", inv)).Append(';')
                  .Append(p.CpuCpuLoad?.ToString("F1", inv)).Append(';')
                  .Append(p.CpuCpuTemp?.ToString("F1", inv)).Append(';')
                  .Append(p.GpuGpuLoad?.ToString("F1", inv)).Append(';')
                  .Append(p.GpuGpuTemp?.ToString("F1", inv)).Append(';')
                  .Append(p.RamPercent.HasValue ? p.RamPercent.Value.ToString("F1", inv) : "")
                  .AppendLine();
            }

            return new RemoteFileResult
            {
                Content = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
                FileName = $"DiagnoseTool_Aufzeichnung{BuildFileSuffix()}.csv",
                ContentType = "text/csv; charset=utf-8"
            };
        });

        private static RemoteFileResult GeneratePdfToBytes(string fileName, Action<string> writer)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"DiagnoseTool_{Guid.NewGuid():N}.pdf");
            try
            {
                writer(tempFile);
                return new RemoteFileResult
                {
                    Content = File.ReadAllBytes(tempFile),
                    FileName = fileName,
                    ContentType = "application/pdf"
                };
            }
            catch (Exception ex)
            {
                return new RemoteFileResult { Error = $"PDF-Bericht konnte nicht erstellt werden: {ex.Message}" };
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        public RemoteFileResult GenerateSystemReportPdf() => OnUi(() =>
        {
            var points = new List<LogDataPoint>(_recordedData);
            if (points.Count == 0 && Diagnostics != null)
            {
                // Ohne Aufzeichnung wird ein Bericht mit dem aktuellen Messpunkt erstellt
                points.Add(new LogDataPoint
                {
                    Timestamp = DateTime.Now,
                    CpuCpuLoad = Diagnostics.CpuAverageLoad,
                    CpuCpuTemp = Diagnostics.CpuAverageTemp,
                    GpuGpuLoad = Diagnostics.GpuLoad,
                    GpuGpuTemp = Diagnostics.GpuTemp,
                    RamPercent = Diagnostics.RamPercent
                });
            }

            DateTime start = points.Count > 0 ? points.First().Timestamp : DateTime.Now;
            DateTime end = points.Count > 0 ? points.Last().Timestamp : DateTime.Now;

            return GeneratePdfToBytes($"System_Pruefbericht{BuildFileSuffix()}.pdf", path =>
                PdfReportService.GenerateReport(
                    path,
                    Diagnostics?.CpuName ?? "Unknown CPU",
                    Diagnostics?.GpuName ?? "Unknown GPU",
                    Diagnostics?.RamUsageDisplay ?? "Unknown RAM",
                    SystemInfo,
                    start,
                    end,
                    points,
                    string.IsNullOrWhiteSpace(OrderNumber) ? null : OrderNumber));
        });

        public RemoteFileResult GenerateBatteryReportPdf() => OnUi(() =>
        {
            if (!(BatteryInfo?.IsPresent ?? false))
            {
                return new RemoteFileResult { Error = "Keine Batterie erkannt." };
            }
            if (!BatteryTestCompleted && !IsBatteryTestRunning)
            {
                return new RemoteFileResult { Error = "Es wurde noch kein Akkutest durchgeführt." };
            }

            return GeneratePdfToBytes($"Batteriebericht{BuildFileSuffix()}.pdf", WriteBatteryReport);
        });

        // ------------------------------------------------------------------
        // Oberfläche / System
        // ------------------------------------------------------------------

        public RemoteActionResult SelectTab(string tab) => OnUi(() =>
        {
            if (!TabResolver.TryResolve(tab, out int index))
            {
                return RemoteActionResult.Fail("ui_tab", $"Unbekannter Tab '{tab}'. Erlaubt: 0-6 oder {string.Join(", ", TabResolver.TabNames)}.");
            }
            SelectedTab = index;
            return RemoteActionResult.Ok("ui_tab", $"Tab '{TabResolver.NameOf(index)}' ausgewählt.", new { selectedTab = index });
        });

        public RemoteActionResult ShowStatusMessage(string message, bool isError) => OnUi(() =>
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return RemoteActionResult.Fail("ui_status", "Keine Nachricht angegeben.");
            }
            ShowStatus(message.Trim(), isError);
            return RemoteActionResult.Ok("ui_status", "Statusmeldung angezeigt.");
        });

        public RemoteActionResult WindowAction(string action) => OnUi(() =>
        {
            if (!(Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) || desktop.MainWindow == null)
            {
                return RemoteActionResult.Fail("ui_window", "Kein Hauptfenster verfügbar.");
            }

            var window = desktop.MainWindow;
            switch ((action ?? "").Trim().ToLowerInvariant())
            {
                case "minimize":
                    window.WindowState = WindowState.Minimized;
                    break;
                case "maximize":
                    window.WindowState = WindowState.Maximized;
                    break;
                case "restore":
                case "normal":
                    window.WindowState = WindowState.Normal;
                    break;
                case "focus":
                case "show":
                case "activate":
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    break;
                default:
                    return RemoteActionResult.Fail("ui_window", $"Unbekannte Fensteraktion '{action}'. Erlaubt: minimize, maximize, restore, focus.");
            }
            return RemoteActionResult.Ok("ui_window", $"Fensteraktion '{action}' ausgeführt.", new { windowState = window.WindowState.ToString() });
        });

        public RemoteActionResult SetSleepPrevention(bool enabled)
        {
            if (enabled) SleepManagementService.PreventSleepAndHibernate();
            else SleepManagementService.RestoreNormalSleep();

            return RemoteActionResult.Ok("power_sleep",
                enabled ? "Standby & Ruhezustand werden blockiert." : "Normaler Energiesparmodus wiederhergestellt.",
                new { isSleepPrevented = SleepManagementService.IsActive, isSupported = OperatingSystem.IsWindows() });
        }

        // ------------------------------------------------------------------
        // Sitzungsüberwachung / Absturzanalyse
        // ------------------------------------------------------------------

        private void InitializeSessionLifecycle()
        {
            SessionLifecycleService.OrderNumberProvider = () => _orderNumber;
            SessionLifecycleService.ServerProvider = () =>
            {
                var st = _udpDiscoveryClient?.CurrentState;
                if (st != null && !string.IsNullOrWhiteSpace(st.ServerHost))
                    return (st.ServerHost, st.ServerPort);
                return null;
            };
            SessionLifecycleService.ActivityProvider = () =>
            {
                var d = Diagnostics;
                var b = BatteryInfo;
                return new SessionActivitySnapshot
                {
                    IsCpuStressRunning = IsCpuStressRunning,
                    IsRamStressRunning = IsRamStressRunning,
                    IsGpuStressRunning = _launcherService.IsFurMarkRunning(),
                    IsBatteryTestRunning = IsBatteryTestRunning,
                    IsRecording = IsRecording,
                    CpuTemp = d?.CpuAverageTemp,
                    CpuLoad = d?.CpuAverageLoad,
                    CpuPowerWatts = d != null && d.CpuPackagePower > 0 ? d.CpuPackagePower : (float?)null,
                    GpuTemp = d?.GpuTemp,
                    GpuHotSpotTemp = d != null && d.GpuHotSpotTemp > 0 ? d.GpuHotSpotTemp : (float?)null,
                    GpuLoad = d?.GpuLoad,
                    GpuPowerWatts = d != null && d.GpuPower > 0 ? d.GpuPower : (float?)null,
                    RamPercent = d?.RamPercent,
                    MainboardTemp = d != null && d.MotherboardTemp > 0 ? d.MotherboardTemp : (float?)null,
                    BatteryPercent = b != null && b.IsPresent ? b.CurrentChargePercent : (double?)null,
                    IsOnAcPower = b != null && b.IsPresent ? b.IsConnectedToAc : (bool?)null
                };
            };

            SessionLifecycleService.CrashReportCreated += report =>
            {
                Dispatcher.UIThread.Post(() =>
                    ShowStatus($"Letzte Sitzung nicht ordnungsgemäß beendet: {report.Headline} [{report.Severity}]",
                        report.IsRealCrash && CrashSeverity.Rank(report.Severity) >= 3));
            };

            if (SessionLifecycleService.PreviousUncleanSession != null)
            {
                ShowStatus("Letzte Sitzung wurde nicht ordnungsgemäß beendet – Absturzanalyse der Ereignisanzeige läuft...", false);
            }
        }

        public RemoteActionResult AnnounceToHardwareCheck()
        {
            string order = OnUi(() => OrderNumber);
            System.Threading.Tasks.Task.Run(() => LhmHttpServer.RegisterWithHardwareCheckAsync(order));
            _udpDiscoveryClient?.TriggerImmediateBroadcastAsync();
            return RemoteActionResult.Ok("hardware_check_announce", "Anmeldung bei Hardware-Check (HTTP + UDP) ausgelöst.");
        }
    }
}
