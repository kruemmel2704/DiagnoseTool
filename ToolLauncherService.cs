using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Reflection;

namespace DiagnoseTool
{
    public class TestTool
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string ExeName { get; set; }
        public string AlternateExeName { get; set; }
        public string Description { get; set; }
        public string Path { get; set; }
        public bool IsEmbedded { get; set; }
        public bool IsFurMark => Key == "FurMark";
        public bool IsFound => !string.IsNullOrEmpty(Path) && File.Exists(Path);
        public string DisplayStatus => IsFound 
            ? (IsEmbedded ? "Bereit (Integriert in App)" : "Bereit (Gefunden)") 
            : "Nicht gefunden (USB einstecken oder auswählen)";
    }

    public class ToolLauncherService
    {
        private const string ConfigFileName = "tool_paths.txt";
        private readonly Dictionary<string, string> _manualPaths = new Dictionary<string, string>();
        private static readonly object _extractLock = new object();
        
        public List<TestTool> Tools { get; private set; }

        public ToolLauncherService()
        {
            LoadManualPaths();
            InitializeTools();
        }

        private void InitializeTools()
        {
            Tools = new List<TestTool>
            {
                new TestTool
                {
                    Key = "OCCT",
                    Name = "OCCT",
                    ExeName = "OCCT.exe",
                    AlternateExeName = "OCCTPersonal.exe",
                    Description = "Zuverlässiges Tool für CPU-, GPU- und Netzteil-Stabilitätstests sowie Fehlererkennung."
                },
                new TestTool
                {
                    Key = "FurMark",
                    Name = "FurMark GPU Stress",
                    ExeName = "FurMark_GUI.exe",
                    AlternateExeName = "furmark.exe",
                    Description = "Intensiver GPU-Stresstest (Donut-Generator) zur Überprüfung von Grafikkarten-Stabilität und maximaler Auslastung."
                },
                new TestTool
                {
                    Key = "Prime95",
                    Name = "Prime95",
                    ExeName = "prime95.exe",
                    AlternateExeName = "prime95w.exe",
                    Description = "Legendärer CPU-Stresstest mit Primzahlberechnungen zur Ermittlung von Rechen- und Hardwarefehlern."
                },
                new TestTool
                {
                    Key = "Victoria",
                    Name = "Victoria SSD/HDD Utility",
                    ExeName = "Victoria.exe",
                    AlternateExeName = "Victoria64.exe",
                    Description = "Erweiterte Datenträger-Diagnose. Prüft Speicherblöcke, Zugriffszeiten, SMART-Zustand und defekte Sektoren."
                }
            };

            RefreshPaths();
        }

        public void RefreshPaths()
        {
            foreach (var tool in Tools)
            {
                // 1. Check if there is a manual override saved
                if (_manualPaths.TryGetValue(tool.Key, out string manualPath) && File.Exists(manualPath))
                {
                    tool.Path = manualPath;
                    tool.IsEmbedded = false;
                    continue;
                }

                // 2. Scan USB sticks
                string usbPath = ScanUsbForTool(tool.ExeName, tool.AlternateExeName);
                if (!string.IsNullOrEmpty(usbPath))
                {
                    tool.Path = usbPath;
                    tool.IsEmbedded = false;
                    continue;
                }

                // 3. Embedded Tool fallback (e.g. FurMark)
                if (tool.Key == "FurMark")
                {
                    string embeddedPath = EnsureEmbeddedToolExtracted("FurMark", tool.ExeName, tool.AlternateExeName);
                    if (!string.IsNullOrEmpty(embeddedPath) && File.Exists(embeddedPath))
                    {
                        tool.Path = embeddedPath;
                        tool.IsEmbedded = true;
                        continue;
                    }
                }

                tool.Path = null;
                tool.IsEmbedded = false;
            }
        }

        public static string EnsureEmbeddedToolExtracted(string toolKey, string primaryExe, string altExe = null)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string targetDir = Path.Combine(localAppData, "DiagnoseTool", "Tools", toolKey);

                string primaryPath = Path.Combine(targetDir, primaryExe);
                if (File.Exists(primaryPath))
                {
                    return primaryPath;
                }

                if (!string.IsNullOrEmpty(altExe))
                {
                    string altPath = Path.Combine(targetDir, altExe);
                    if (File.Exists(altPath))
                    {
                        return altPath;
                    }
                }

                lock (_extractLock)
                {
                    if (File.Exists(primaryPath)) return primaryPath;

                    var assembly = Assembly.GetExecutingAssembly();
                    string resourceName = assembly.GetManifestResourceNames()
                        .FirstOrDefault(r => r.EndsWith($"{toolKey}.zip", StringComparison.OrdinalIgnoreCase) ||
                                             (r.IndexOf(toolKey, StringComparison.OrdinalIgnoreCase) >= 0 && r.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));

                    if (string.IsNullOrEmpty(resourceName))
                    {
                        return null;
                    }

                    Directory.CreateDirectory(targetDir);

                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null) return null;
                        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                        {
                            archive.ExtractToDirectory(targetDir, overwriteFiles: true);
                        }
                    }

                    if (File.Exists(primaryPath)) return primaryPath;
                    if (!string.IsNullOrEmpty(altExe) && File.Exists(Path.Combine(targetDir, altExe)))
                    {
                        return Path.Combine(targetDir, altExe);
                    }

                    var anyExe = Directory.GetFiles(targetDir, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                    return anyExe;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ToolLauncherService] Failed to extract {toolKey}: {ex.Message}");
                return null;
            }
        }

        public void SaveManualPath(string key, string path)
        {
            if (File.Exists(path))
            {
                _manualPaths[key] = path;
                var tool = Tools.FirstOrDefault(t => t.Key == key);
                if (tool != null)
                {
                    tool.Path = path;
                }
                SaveManualPaths();
            }
        }

        public void ClearManualPath(string key)
        {
            if (_manualPaths.ContainsKey(key))
            {
                _manualPaths.Remove(key);
                SaveManualPaths();
                RefreshPaths();
            }
        }

        public bool LaunchTool(string key, out string errorMessage)
        {
            return LaunchToolInternal(key, directDonut: true, out errorMessage);
        }

        public bool LaunchFurMarkDirect(out string errorMessage)
        {
            return LaunchToolInternal("FurMark", directDonut: true, out errorMessage);
        }

        public bool LaunchFurMarkGui(out string errorMessage)
        {
            return LaunchToolInternal("FurMark", directDonut: false, out errorMessage);
        }

        public bool StopFurMark(out string errorMessage)
        {
            errorMessage = null;
            try
            {
                var names = new[] { "furmark", "FurMark_GUI", "FurMark" };
                int killed = 0;
                foreach (var name in names)
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            proc.Kill();
                            proc.WaitForExit(1000);
                            killed++;
                        }
                        catch { }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public bool IsFurMarkRunning()
        {
            try
            {
                var names = new[] { "furmark", "FurMark_GUI", "FurMark" };
                foreach (var name in names)
                {
                    if (Process.GetProcessesByName(name).Length > 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private bool LaunchToolInternal(string key, bool directDonut, out string errorMessage)
        {
            errorMessage = null;
            var tool = Tools.FirstOrDefault(t => t.Key == key);
            if (tool == null)
            {
                errorMessage = "Tool configuration not found.";
                return false;
            }

            if (!tool.IsFound)
            {
                errorMessage = "The tool executable could not be found. Please plug in the USB stick or select the path manually.";
                return false;
            }

            try
            {
                string targetPath = tool.Path;
                string arguments = "";
                string toolDir = Path.GetDirectoryName(tool.Path);

                if (key == "FurMark")
                {
                    if (directDonut)
                    {
                        // Direct start of the animated 3D Donut test without GUI launcher
                        string cliExe = Path.Combine(toolDir, "furmark.exe");
                        if (File.Exists(cliExe))
                        {
                            targetPath = cliExe;
                            arguments = "--demo furmark-gl --width 1920 --height 1080";
                        }
                        else
                        {
                            // FurMark 1.x or GUI fallback
                            arguments = "/nogui /width=1920 /height=1080 /run_stress";
                        }
                    }
                    else
                    {
                        // Open configuration GUI
                        string guiExe = Path.Combine(toolDir, "FurMark_GUI.exe");
                        if (File.Exists(guiExe))
                        {
                            targetPath = guiExe;
                            arguments = "";
                        }
                        else
                        {
                            arguments = "";
                        }
                    }
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = targetPath,
                    Arguments = arguments,
                    WorkingDirectory = toolDir,
                    UseShellExecute = true // Runs independently of our app
                };
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to start the process: {ex.Message}";
                return false;
            }
        }

        private string ScanUsbForTool(string primaryExe, string altExe)
        {
            try
            {
                string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Removable || (d.DriveType == DriveType.Fixed && !d.RootDirectory.FullName.Equals(systemRoot, StringComparison.OrdinalIgnoreCase))));

                foreach (var drive in drives)
                {
                    string root = drive.RootDirectory.FullName;

                    // 1. Check root directory
                    string path = CheckFileExists(root, primaryExe, altExe);
                    if (path != null) return path;

                    // 2. Scan first-level subdirectories and folders like 'Tools' or 'Testing'
                    var directories = Directory.GetDirectories(root);
                    foreach (var dir in directories)
                    {
                        string subPath = CheckFileExists(dir, primaryExe, altExe);
                        if (subPath != null) return subPath;

                        string dirName = Path.GetFileName(dir);
                        if (dirName.Equals("Tools", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Equals("Testing", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Contains("Diagnose"))
                        {
                            try
                            {
                                var secondLevelDirs = Directory.GetDirectories(dir);
                                foreach (var subDir in secondLevelDirs)
                                {
                                    string deepPath = CheckFileExists(subDir, primaryExe, altExe);
                                    if (deepPath != null) return deepPath;
                                }
                            }
                            catch { /* Skip folder read errors */ }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error scanning drives: {ex.Message}");
            }

            return null;
        }

        private string CheckFileExists(string dir, string primary, string alt)
        {
            try
            {
                string path = Path.Combine(dir, primary);
                if (File.Exists(path)) return path;

                if (!string.IsNullOrEmpty(alt))
                {
                    string altPath = Path.Combine(dir, alt);
                    if (File.Exists(altPath)) return altPath;
                }
            }
            catch { }
            return null;
        }

        private string GetConfigFilePath()
        {
            string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            string localPath = Path.Combine(exeDir, ConfigFileName);
            try
            {
                if (File.Exists(localPath))
                {
                    using (var fs = File.OpenWrite(localPath)) { }
                    return localPath;
                }
                else
                {
                    string testFile = Path.Combine(exeDir, "write_test.tmp");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);
                    return localPath;
                }
            }
            catch
            {
                string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DiagnoseTool");
                try
                {
                    if (!Directory.Exists(appDataFolder))
                    {
                        Directory.CreateDirectory(appDataFolder);
                    }
                }
                catch { }
                return Path.Combine(appDataFolder, ConfigFileName);
            }
        }

        private void LoadManualPaths()
        {
            try
            {
                string configPath = GetConfigFilePath();
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length == 2)
                        {
                            string key = parts[0].Trim();
                            string path = parts[1].Trim();
                            if (File.Exists(path))
                            {
                                _manualPaths[key] = path;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveManualPaths()
        {
            try
            {
                string configPath = GetConfigFilePath();
                var lines = _manualPaths.Select(kvp => $"{kvp.Key}={kvp.Value}");
                File.WriteAllLines(configPath, lines);
            }
            catch { }
        }
    }
}
