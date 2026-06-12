using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;

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
        public bool IsFound => !string.IsNullOrEmpty(Path) && File.Exists(Path);
        public string DisplayStatus => IsFound ? "Ready (Found)" : "Not Found (Insert USB or browse)";
    }

    public class ToolLauncherService
    {
        private const string ConfigFileName = "tool_paths.txt";
        private readonly Dictionary<string, string> _manualPaths = new Dictionary<string, string>();
        
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
                    Description = "Excellent tool for CPU, GPU, and Power Supply stability testing and error detection."
                },
                new TestTool
                {
                    Key = "FurMark",
                    Name = "FurMark",
                    ExeName = "FurMark.exe",
                    AlternateExeName = "FurMark2.exe",
                    Description = "Intense GPU stress testing tool (also known as the 'donut generator') to verify graphics stability."
                },
                new TestTool
                {
                    Key = "Prime95",
                    Name = "Prime95",
                    ExeName = "prime95.exe",
                    AlternateExeName = "prime95w.exe",
                    Description = "Legendary CPU stress test using Prime Number calculations to expose hardware faults."
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
                    continue;
                }

                // 2. Scan USB sticks
                string usbPath = ScanUsbForTool(tool.ExeName, tool.AlternateExeName);
                if (!string.IsNullOrEmpty(usbPath))
                {
                    tool.Path = usbPath;
                }
                else
                {
                    tool.Path = null;
                }
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
                var startInfo = new ProcessStartInfo
                {
                    FileName = tool.Path,
                    WorkingDirectory = Path.GetDirectoryName(tool.Path),
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
                var drives = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Removable && d.IsReady);

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

        private void LoadManualPaths()
        {
            try
            {
                if (File.Exists(ConfigFileName))
                {
                    var lines = File.ReadAllLines(ConfigFileName);
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
                var lines = _manualPaths.Select(kvp => $"{kvp.Key}={kvp.Value}");
                File.WriteAllLines(ConfigFileName, lines);
            }
            catch { }
        }
    }
}
