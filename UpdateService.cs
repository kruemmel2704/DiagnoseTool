using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiagnoseTool
{
    /// <summary>Ergebnis einer Update-Prüfung gegen die GitHub-Releases.</summary>
    public class UpdateInfo
    {
        public string CurrentVersion { get; set; } = AppInfo.Version;
        public string LatestVersion { get; set; }
        public string TagName { get; set; }
        public string ReleaseName { get; set; }
        public string ReleaseNotes { get; set; }
        public string ReleaseUrl { get; set; }
        public DateTime? PublishedAtUtc { get; set; }
        public bool IsUpdateAvailable { get; set; }
        public string AssetName { get; set; }
        public string AssetUrl { get; set; }
        public long AssetSize { get; set; }
        public string AssetSha256 { get; set; }
        public bool CanInstallAutomatically { get; set; }
        public DateTime LastCheckedUtc { get; set; } = DateTime.UtcNow;
        public string Error { get; set; }
    }

    /// <summary>
    /// Automatische Aktualisierung über GitHub-Releases (kruemmel2704/DiagnoseTool).
    /// Prüft beim Start und danach regelmäßig auf einen neueren Release-Tag. Unter Windows wird
    /// die passende Exe geladen, per SHA-256 geprüft, gegen die laufende Exe getauscht und neu gestartet.
    /// </summary>
    public static class UpdateService
    {
        public const string RepoOwner = "kruemmel2704";
        public const string RepoName = "DiagnoseTool";
        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

        public const string StateIdle = "idle";
        public const string StateChecking = "checking";
        public const string StateDownloading = "downloading";
        public const string StateInstalling = "installing";
        public const string StateFailed = "failed";

        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static Timer _timer;
        private static UpdateInfo _last;
        private static string _state = StateIdle;
        private static double _progress;
        private static string _message;

        /// <summary>Ausgelöst bei jeder Zustandsänderung (Prüfung, Fortschritt, Fehler). Kann aus Hintergrund-Threads kommen.</summary>
        public static event Action StateChanged;

        /// <summary>Wird aufgerufen, sobald die neue Version bereitliegt und die App beendet werden muss.</summary>
        public static Action<string> RequestShutdownForUpdate { get; set; }

        public static UpdateInfo LastResult => _last;
        public static string State => _state;
        public static double DownloadProgress => _progress;
        public static string StatusMessage => _message;
        public static bool IsBusy => _state == StateChecking || _state == StateDownloading || _state == StateInstalling;

        public static bool IsDisabled =>
            string.Equals(Environment.GetEnvironmentVariable("DIAGNOSETOOL_NO_UPDATE_CHECK"), "1", StringComparison.Ordinal);

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CPM-DiagnoseTool", AppInfo.Version));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return c;
        }

        private static string DismissFile => Path.Combine(SessionLifecycleService.DataDirectory, "update_dismissed.txt");

        // ------------------------------------------------------------------
        // Start / periodische Prüfung
        // ------------------------------------------------------------------

        public static void Start()
        {
            CleanupAfterUpdate();
            if (IsDisabled || _timer != null) return;
            _timer = new Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(15), CheckInterval);
        }

        public static void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        }

        private static void SetState(string state, string message = null, double? progress = null)
        {
            _state = state;
            if (message != null) _message = message;
            if (progress.HasValue) _progress = progress.Value;
            try { StateChanged?.Invoke(); } catch { }
        }

        /// <summary>Alte Exe (".old") vom letzten Update entfernen.</summary>
        private static void CleanupAfterUpdate()
        {
            try
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                string old = exe + ".old";
                for (int i = 0; i < 5 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch { Thread.Sleep(500); }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Prüfung
        // ------------------------------------------------------------------

        public static Version ParseVersion(string tagOrVersion)
        {
            if (string.IsNullOrWhiteSpace(tagOrVersion)) return null;
            string s = tagOrVersion.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            var parts = s.Split('.').Where(p => p.Length > 0).Take(4).ToList();
            if (parts.Count == 0 || parts.Any(p => !int.TryParse(p, out _))) return null;
            while (parts.Count < 3) parts.Add("0");
            return Version.TryParse(string.Join(".", parts), out var v) ? v : null;
        }

        private static string ExpectedAssetName()
        {
            if (OperatingSystem.IsWindows())
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "DiagnoseTool-Windows-arm64.exe"
                    : "DiagnoseTool-Windows-x64.exe";
            if (OperatingSystem.IsMacOS())
                return "DiagnoseTool-macOS-Universal.dmg";
            return null;
        }

        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
        {
            if (!await Gate.WaitAsync(0, ct)) return _last;
            try
            {
                SetState(StateChecking, "Suche nach Updates...");
                var info = new UpdateInfo();

                using var resp = await Http.GetAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    info.Error = $"GitHub antwortete mit HTTP {(int)resp.StatusCode}.";
                    _last = info;
                    SetState(StateFailed, info.Error);
                    return info;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                info.TagName = Str(root, "tag_name");
                info.ReleaseName = Str(root, "name") ?? info.TagName;
                info.ReleaseNotes = Str(root, "body");
                info.ReleaseUrl = Str(root, "html_url");
                if (DateTime.TryParse(Str(root, "published_at"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var pub))
                    info.PublishedAtUtc = pub;

                var latest = ParseVersion(info.TagName);
                var current = ParseVersion(AppInfo.Version);
                info.LatestVersion = latest?.ToString(3) ?? info.TagName;
                info.IsUpdateAvailable = latest != null && current != null && latest > current;

                string wanted = ExpectedAssetName();
                if (wanted != null && root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (!string.Equals(Str(a, "name"), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                        info.AssetName = Str(a, "name");
                        info.AssetUrl = Str(a, "browser_download_url");
                        info.AssetSize = a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : 0;
                        string digest = Str(a, "digest");
                        if (!string.IsNullOrEmpty(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                            info.AssetSha256 = digest.Substring(7).ToLowerInvariant();
                        break;
                    }
                }

                info.CanInstallAutomatically = info.IsUpdateAvailable && info.AssetUrl != null
                                               && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
                _last = info;
                SetState(StateIdle, info.IsUpdateAvailable
                    ? $"Update auf v{info.LatestVersion} verfügbar."
                    : $"DiagnoseTool ist aktuell (v{AppInfo.Version}).");
                return info;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var info = new UpdateInfo { Error = $"Update-Prüfung fehlgeschlagen: {ex.Message}" };
                _last = info;
                SetState(StateFailed, info.Error);
                return info;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // ------------------------------------------------------------------
        // "Später" (pro Version ausblenden)
        // ------------------------------------------------------------------

        public static bool IsDismissed(string version)
        {
            try { return File.Exists(DismissFile) && File.ReadAllText(DismissFile).Trim() == version; }
            catch { return false; }
        }

        public static void Dismiss(string version)
        {
            try { File.WriteAllText(DismissFile, version ?? ""); } catch { }
        }

        // ------------------------------------------------------------------
        // Herunterladen & Installieren
        // ------------------------------------------------------------------

        public static async Task<(bool Success, string Message)> DownloadAndInstallAsync(CancellationToken ct = default)
        {
            var info = _last;
            if (info == null || !info.IsUpdateAvailable)
            {
                info = await CheckAsync(ct);
            }
            if (info == null || !info.IsUpdateAvailable) return (false, "Kein Update verfügbar.");
            if (string.IsNullOrEmpty(info.AssetUrl)) return (false, "Für dieses System gibt es im Release keine passende Datei.");
            if (!await Gate.WaitAsync(0, ct)) return (false, "Es läuft bereits eine Update-Aktion.");

            string tempFile = Path.Combine(Path.GetTempPath(), $"DiagnoseTool_update_{info.LatestVersion}_{Guid.NewGuid():N}{Path.GetExtension(info.AssetName)}");
            try
            {
                SetState(StateDownloading, $"Lade v{info.LatestVersion} herunter...", 0);
                await DownloadAsync(info, tempFile, ct);

                SetState(StateInstalling, "Prüfe Download...", 1);
                string verifyError = Verify(info, tempFile);
                if (verifyError != null)
                {
                    SetState(StateFailed, verifyError);
                    return (false, verifyError);
                }

                if (OperatingSystem.IsMacOS())
                {
                    string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", info.AssetName);
                    File.Copy(tempFile, target, true);
                    Process.Start(new ProcessStartInfo("open", $"\"{target}\"") { UseShellExecute = false });
                    string msg = $"Update v{info.LatestVersion} wurde geöffnet. Bitte DiagnoseTool beenden und die neue App in den Programme-Ordner ziehen.";
                    SetState(StateIdle, msg);
                    return (true, msg);
                }

                return InstallWindows(info, tempFile);
            }
            catch (Exception ex)
            {
                string msg = $"Update fehlgeschlagen: {ex.Message}";
                SetState(StateFailed, msg);
                return (false, msg);
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                Gate.Release();
            }
        }

        private static async Task DownloadAsync(UpdateInfo info, string target, CancellationToken ct)
        {
            using var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.AssetSize;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            double lastReported = -1;
            while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                {
                    double p = (double)read / total;
                    if (p - lastReported >= 0.02)
                    {
                        lastReported = p;
                        SetState(StateDownloading, $"Lade v{info.LatestVersion} herunter... {p * 100:F0} %", p);
                    }
                }
            }
        }

        private static string Verify(UpdateInfo info, string file)
        {
            var len = new FileInfo(file).Length;
            if (info.AssetSize > 0 && len != info.AssetSize)
                return $"Download unvollständig ({len} von {info.AssetSize} Bytes).";

            if (!string.IsNullOrEmpty(info.AssetSha256))
            {
                using var fs = File.OpenRead(file);
                string hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (hash != info.AssetSha256)
                    return "Prüfsumme (SHA-256) des Downloads stimmt nicht – Update abgebrochen.";
            }
            return null;
        }

        private static (bool, string) InstallWindows(UpdateInfo info, string downloaded)
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Fail("Pfad der laufenden Programmdatei konnte nicht ermittelt werden.");

            string old = exe + ".old";
            string staged = exe + ".new";
            int pid = Environment.ProcessId;

            try
            {
                // Laufende Exe darf unter Windows umbenannt (nicht überschrieben) werden
                if (File.Exists(old)) File.Delete(old);
                File.Copy(downloaded, staged, true);
                File.Move(exe, old);
                File.Move(staged, exe);
            }
            catch (UnauthorizedAccessException)
            {
                // Kein Schreibrecht (z.B. Programme-Ordner): Austausch über erhöhtes Hilfsskript
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                return InstallWindowsElevated(info, downloaded, exe, pid);
            }
            catch (IOException ex)
            {
                // Rückgängig machen, falls nur halb getauscht
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
                return Fail($"Programmdatei konnte nicht ersetzt werden: {ex.Message}");
            }

            // Neue Version startet erst, wenn dieser Prozess beendet ist (Port 8085 wird frei)
            Process.Start(new ProcessStartInfo(exe, $"--wait-for-pid={pid} --updated-from={AppInfo.Version}")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)
            });

            string msg = $"Update auf v{info.LatestVersion} installiert – DiagnoseTool wird neu gestartet.";
            SetState(StateInstalling, msg, 1);
            RequestShutdownForUpdate?.Invoke(msg);
            return (true, msg);
        }

        private static (bool, string) InstallWindowsElevated(UpdateInfo info, string downloaded, string exe, int pid)
        {
            string dir = Path.Combine(Path.GetTempPath(), "DiagnoseToolUpdate");
            Directory.CreateDirectory(dir);
            string payload = Path.Combine(dir, $"DiagnoseTool_{info.LatestVersion}.exe");
            File.Copy(downloaded, payload, true);

            string script = Path.Combine(dir, "update.cmd");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine($"move /y \"{exe}\" \"{exe}.old\" >nul");
            sb.AppendLine($"copy /y \"{payload}\" \"{exe}\" >nul || move /y \"{exe}.old\" \"{exe}\" >nul");
            sb.AppendLine($"start \"\" \"{exe}\" --updated-from={AppInfo.Version}");
            sb.AppendLine($"del \"{payload}\" >nul 2>&1");
            File.WriteAllText(script, sb.ToString(), Encoding.ASCII);

            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception ex)
            {
                return Fail($"Administratorrechte für das Update wurden nicht erteilt ({ex.Message}).");
            }

            string msg = $"Update auf v{info.LatestVersion} wird mit Administratorrechten installiert – DiagnoseTool wird neu gestartet.";
            SetState(StateInstalling, msg, 1);
            RequestShutdownForUpdate?.Invoke(msg);
            return (true, msg);
        }

        private static (bool, string) Fail(string msg)
        {
            SetState(StateFailed, msg);
            return (false, msg);
        }

        /// <summary>Beim Start mit "--wait-for-pid=N": warten, bis die alte Instanz beendet ist.</summary>
        public static void WaitForPreviousInstance(string[] args)
        {
            var arg = args?.FirstOrDefault(a => a.StartsWith("--wait-for-pid=", StringComparison.OrdinalIgnoreCase));
            if (arg == null || !int.TryParse(arg.Substring("--wait-for-pid=".Length), out int pid)) return;
            try
            {
                using var p = Process.GetProcessById(pid);
                p.WaitForExit(20000);
            }
            catch { }
        }

        public static string UpdatedFromVersion(string[] args)
        {
            var arg = args?.FirstOrDefault(a => a.StartsWith("--updated-from=", StringComparison.OrdinalIgnoreCase));
            return arg?.Substring("--updated-from=".Length);
        }

        public static object GetStatus() => new
        {
            currentVersion = AppInfo.Version,
            state = _state,
            message = _message,
            downloadProgress = _progress,
            isDisabled = IsDisabled,
            checkIntervalHours = CheckInterval.TotalHours,
            latest = _last
        };
    }
}
