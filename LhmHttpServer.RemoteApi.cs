using System;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DiagnoseTool
{
    /// <summary>
    /// Erweiterte REST-Endpunkte für die vollständige Fernsteuerung des DiagnoseTools durch Hardware-Check.
    /// Übersicht aller Endpunkte: GET /api
    /// </summary>
    public partial class LhmHttpServer
    {
        private bool TryHandleExtendedGet(HttpListenerRequest req, HttpListenerResponse resp, string path)
        {
            path = path.TrimEnd('/');
            if (path.Length == 0) path = "/";

            if (path == "/api" || path == "/api/capabilities" || path == "/api/endpoints")
            {
                SendResponseJson(resp, RemoteApiCatalog.Build(Port));
                return true;
            }

            if (path == "/api/system")
            {
                SendResponseJson(resp, (object)_systemInfoProvider?.Invoke() ?? new { error = "Keine Systeminformationen verfügbar" });
                return true;
            }

            if (path == "/api/battery")
            {
                SendResponseJson(resp, (object)_batteryInfoProvider?.Invoke() ?? new { isPresent = false });
                return true;
            }

            var rc = RemoteControl;
            if (rc == null)
            {
                return false;
            }

            switch (path)
            {
                case "/api/app/state":
                case "/api/app":
                    SendResponseJson(resp, rc.GetAppState());
                    return true;
                case "/api/diagnostics":
                    SendResponseJson(resp, rc.GetDiagnosticsSnapshot());
                    return true;
                case "/api/tools":
                    SendResponseJson(resp, rc.GetTools());
                    return true;
                case "/api/repair":
                    SendResponseJson(resp, rc.GetRepairCommands());
                    return true;
                case "/api/hwinfo":
                    SendResponseJson(resp, rc.GetHWiNFOState());
                    return true;
                case "/api/stress/cpu":
                    SendResponseJson(resp, rc.GetCpuStressDetails());
                    return true;
                case "/api/stress/ram":
                    SendResponseJson(resp, rc.GetRamStressDetails());
                    return true;
                case "/api/stress/gpu":
                    SendResponseJson(resp, rc.GetGpuStressDetails());
                    return true;
                case "/api/battery-test":
                case "/api/stress/battery":
                    SendResponseJson(resp, rc.GetBatteryTestDetails());
                    return true;
                case "/api/recording":
                {
                    bool includeSamples = IsTrue(req.QueryString["samples"]);
                    SendResponseJson(resp, rc.GetRecordingDetails(includeSamples, ParseInt(req.QueryString["last"])));
                    return true;
                }
                case "/api/recording/samples":
                    SendResponseJson(resp, rc.GetRecordingDetails(true, ParseInt(req.QueryString["last"])));
                    return true;
                case "/api/recording/export":
                case "/api/recording/export.csv":
                    SendResponseFile(resp, rc.ExportRecordingCsv());
                    return true;
                case "/api/report/system":
                case "/api/report/system.pdf":
                    SendResponseFile(resp, rc.GenerateSystemReportPdf());
                    return true;
                case "/api/report/battery":
                case "/api/report/battery.pdf":
                    SendResponseFile(resp, rc.GenerateBatteryReportPdf());
                    return true;
            }

            return false;
        }

        private bool TryHandleExtendedPost(HttpListenerRequest req, HttpListenerResponse resp, string path, JsonElement? root)
        {
            var rc = RemoteControl;
            if (rc == null)
            {
                return false;
            }

            path = path.TrimEnd('/');
            string action;
            if (path.StartsWith("/api/control/", StringComparison.Ordinal))
                action = path.Substring("/api/control/".Length);
            else if (path.StartsWith("/api/", StringComparison.Ordinal))
                action = path.Substring("/api/".Length);
            else
                return false;

            string[] seg = action.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (seg.Length == 0) return false;

            RemoteActionResult result = null;

            switch (action)
            {
                case "battery-test/start":
                case "stress/battery/start":
                case "battery/start":
                    result = rc.StartBatteryTest(
                        GetIntProperty(root, "durationMinutes", "duration"),
                        GetBoolProperty(root, "openVideo") ?? true);
                    break;
                case "battery-test/stop":
                case "stress/battery/stop":
                case "battery/stop":
                    result = rc.StopBatteryTest();
                    break;
                case "stress/cpu/config":
                    result = rc.ConfigureCpuStress(GetIntProperty(root, "threads"), GetIntProperty(root, "durationIndex"));
                    break;
                case "stress/ram/config":
                    result = rc.ConfigureRamStress(GetIntProperty(root, "targetPercent", "percent"), GetIntProperty(root, "durationIndex"));
                    break;
                case "recording/clear":
                    result = rc.ClearRecording();
                    break;
                case "tools/refresh":
                case "tools/scan":
                    result = rc.RefreshTools();
                    break;
                case "hwinfo/start":
                    result = rc.StartHWiNFO();
                    break;
                case "ui/tab":
                    result = rc.SelectTab(GetStringProperty(root, "tab", "index"));
                    break;
                case "ui/status":
                    result = rc.ShowStatusMessage(GetStringProperty(root, "message", "text"), GetBoolProperty(root, "isError") ?? false);
                    break;
                case "ui/window":
                    result = rc.WindowAction(GetStringProperty(root, "action", "state"));
                    break;
                case "power/sleep":
                {
                    bool? prevent = GetBoolProperty(root, "preventSleep", "enabled");
                    if (!prevent.HasValue)
                    {
                        SendResponseJson(resp, new { success = false, error = "Parameter 'preventSleep' (bool) fehlt." }, 400);
                        return true;
                    }
                    result = rc.SetSleepPrevention(prevent.Value);
                    break;
                }
                case "hardware-check/announce":
                case "hardware-check/register":
                    result = rc.AnnounceToHardwareCheck();
                    break;
            }

            if (result == null && seg.Length == 3 && seg[0] == "tools")
            {
                // /api/control/tools/{key}/{launch|path|clear-path}
                string key = seg[1];
                switch (seg[2])
                {
                    case "launch":
                    case "start":
                        result = rc.LaunchTool(key);
                        break;
                    case "path":
                        result = rc.SetToolPath(key, GetStringProperty(root, "path"));
                        break;
                    case "clear-path":
                    case "reset-path":
                        result = rc.ClearToolPath(key);
                        break;
                }
            }

            if (result == null && seg.Length == 2 && seg[0] == "repair")
            {
                result = rc.RunRepair(seg[1]);
            }

            if (result == null && seg.Length == 2 && seg[0] == "ui" && seg[1].StartsWith("tab-", StringComparison.Ordinal))
            {
                result = rc.SelectTab(seg[1].Substring(4));
            }

            if (result == null)
            {
                return false;
            }

            SendResponseJson(resp, new
            {
                success = result.Success,
                action = result.Action,
                message = result.Message,
                error = result.Success ? null : result.Message,
                data = result.Data
            }, result.Success ? 200 : 400);
            return true;
        }

        private static void SendResponseFile(HttpListenerResponse resp, RemoteFileResult file)
        {
            if (file == null || !file.Success)
            {
                SendResponseJson(resp, new { success = false, error = file?.Error ?? "Datei konnte nicht erstellt werden." }, 409);
                return;
            }

            resp.StatusCode = 200;
            resp.ContentType = file.ContentType;
            resp.AddHeader("Content-Disposition", $"attachment; filename=\"{file.FileName}\"");
            resp.AddHeader("Access-Control-Expose-Headers", "Content-Disposition");
            resp.ContentLength64 = file.Content.Length;
            resp.OutputStream.Write(file.Content, 0, file.Content.Length);
        }

        private static bool IsTrue(string value)
            => !string.IsNullOrEmpty(value) && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

        private static int? ParseInt(string value)
            => int.TryParse(value, out int n) ? n : (int?)null;

        private static bool TryGetProp(JsonElement? root, out JsonElement val, params string[] names)
        {
            val = default;
            if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object) return false;
            foreach (var name in names)
            {
                if (name != null && root.Value.TryGetProperty(name, out val)) return true;
            }
            return false;
        }

        private static string GetStringProperty(JsonElement? root, params string[] names)
        {
            if (!TryGetProp(root, out var val, names)) return null;
            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString(),
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        private static bool? GetBoolProperty(JsonElement? root, params string[] names)
        {
            if (!TryGetProp(root, out var val, names)) return null;
            switch (val.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return val.TryGetInt32(out int n) ? n != 0 : (bool?)null;
                case JsonValueKind.String:
                    string s = val.GetString();
                    if (IsTrue(s)) return true;
                    if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "no", StringComparison.OrdinalIgnoreCase)) return false;
                    return null;
                default:
                    return null;
            }
        }
    }
}
