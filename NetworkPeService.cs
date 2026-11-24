using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace DiagnoseTool
{
    /// <summary>
    /// Netzwerk-Funktionen fuer den PE-Modus (DiagnosePE): LAN-/WLAN-Adapter
    /// auflisten, WLAN scannen/verbinden, DHCP erneuern und Verbindung testen.
    /// WLAN laeuft ueber 'netsh wlan' (benoetigt WinPE-WiFi-Package + WlanSvc).
    /// </summary>
    public static class NetworkPeService
    {
        public enum AdapterKind { Lan, Wlan, Other }

        public class AdapterInfo
        {
            public string Name { get; set; }
            public string Description { get; set; }
            public AdapterKind Kind { get; set; }
            public string Status { get; set; }
            public string Ip { get; set; } = "-";
            public string Mac { get; set; } = "-";
            public string SpeedDisplay { get; set; } = "-";

            public string KindDisplay => Kind == AdapterKind.Wlan ? "WLAN" : (Kind == AdapterKind.Lan ? "LAN" : "Sonstige");
            public string Headline => $"{KindDisplay} · {Name}";
            public string SubLine => $"{Status} · IP {Ip} · {SpeedDisplay}";
        }

        public class WifiNetwork
        {
            public string Ssid { get; set; }
            public int SignalPercent { get; set; }
            public string Authentication { get; set; } = "";
            public bool Secured => !string.IsNullOrEmpty(Authentication) &&
                                   !Authentication.Contains("Open", StringComparison.OrdinalIgnoreCase) &&
                                   !Authentication.Contains("Offen", StringComparison.OrdinalIgnoreCase);

            public string Display => $"{Ssid}  ({SignalPercent}% · {(Secured ? "gesichert" : "offen")})";
        }

        // -----------------------------------------------------------------
        //  Prozess-Helfer
        // -----------------------------------------------------------------
        private static (bool ok, string output) Run(string file, string args, int timeoutMs = 20000)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return (false, "Prozess konnte nicht gestartet werden.");
                string outp = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (false, "Zeitueberschreitung."); }
                return (p.ExitCode == 0, string.IsNullOrWhiteSpace(outp) ? err : outp);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static (bool ok, string output) Netsh(string args, int timeoutMs = 20000) => Run("netsh.exe", args, timeoutMs);

        // -----------------------------------------------------------------
        //  Adapter auflisten
        // -----------------------------------------------------------------
        public static List<AdapterInfo> GetAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var kind = ni.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => AdapterKind.Wlan,
                        NetworkInterfaceType.Ethernet => AdapterKind.Lan,
                        NetworkInterfaceType.GigabitEthernet => AdapterKind.Lan,
                        NetworkInterfaceType.FastEthernetT => AdapterKind.Lan,
                        _ => AdapterKind.Other
                    };

                    string ip = "-";
                    try
                    {
                        var v4 = ni.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (v4 != null) ip = v4.Address.ToString();
                    }
                    catch { }

                    string speed = "-";
                    try { if (ni.Speed > 0) speed = $"{ni.Speed / 1_000_000} Mbit/s"; } catch { }

                    string mac = "-";
                    try { var pa = ni.GetPhysicalAddress()?.ToString(); if (!string.IsNullOrEmpty(pa)) mac = string.Join(":", Enumerable.Range(0, pa.Length / 2).Select(i => pa.Substring(i * 2, 2))); } catch { }

                    list.Add(new AdapterInfo
                    {
                        Name = ni.Name,
                        Description = ni.Description,
                        Kind = kind,
                        Status = ni.OperationalStatus == OperationalStatus.Up ? "verbunden" : ni.OperationalStatus.ToString(),
                        Ip = ip,
                        Mac = mac,
                        SpeedDisplay = speed
                    });
                }
            }
            catch { }
            return list.OrderByDescending(a => a.Kind == AdapterKind.Lan).ThenBy(a => a.Name).ToList();
        }

        // -----------------------------------------------------------------
        //  WLAN: Dienst, Scan, Verbinden
        // -----------------------------------------------------------------
        public static bool IsWlanAvailable()
        {
            try
            {
                var res = Netsh("wlan show interfaces", 8000);
                return res.ok || res.output.Contains("SSID", StringComparison.OrdinalIgnoreCase)
                              || res.output.Contains("Interface", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>WLAN-Netze suchen. mode=bssid liefert Signalstaerke.</summary>
        public static List<WifiNetwork> ScanWifi(out string message)
        {
            message = "";
            var nets = new List<WifiNetwork>();

            // Scan anstossen (nicht alle Treiber unterstuetzen das explizit; ignorieren).
            Netsh("wlan refresh", 8000);

            var res = Netsh("wlan show networks mode=bssid", 20000);
            if (string.IsNullOrWhiteSpace(res.output))
            {
                message = "Keine WLAN-Antwort. Ist ein WLAN-Treiber geladen und WlanSvc aktiv?";
                return nets;
            }

            // Bloecke je SSID parsen. Zeilen sind z. B.:
            //   SSID 1 : MeinNetz
            //   Authentication : WPA2-Personal
            //   Signal : 82%
            WifiNetwork current = null;
            foreach (var raw in res.output.Split('\n'))
            {
                var line = raw.Trim();
                var mSsid = Regex.Match(line, @"^SSID\s+\d+\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (mSsid.Success)
                {
                    if (current != null && !string.IsNullOrWhiteSpace(current.Ssid)) nets.Add(current);
                    current = new WifiNetwork { Ssid = mSsid.Groups[1].Value.Trim() };
                    continue;
                }
                if (current == null) continue;

                var mAuth = Regex.Match(line, @"^(Authentication|Authentifizierung)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (mAuth.Success) { current.Authentication = mAuth.Groups[2].Value.Trim(); continue; }

                var mSig = Regex.Match(line, @"^(Signal)\s*:\s*(\d+)\s*%", RegexOptions.IgnoreCase);
                if (mSig.Success && int.TryParse(mSig.Groups[2].Value, out int pct))
                {
                    if (pct > current.SignalPercent) current.SignalPercent = pct;
                }
            }
            if (current != null && !string.IsNullOrWhiteSpace(current.Ssid)) nets.Add(current);

            // je SSID nur den staerksten Eintrag behalten
            var grouped = nets.Where(n => !string.IsNullOrWhiteSpace(n.Ssid))
                              .GroupBy(n => n.Ssid)
                              .Select(g => g.OrderByDescending(x => x.SignalPercent).First())
                              .OrderByDescending(n => n.SignalPercent)
                              .ToList();

            if (grouped.Count == 0) message = "Keine WLAN-Netze gefunden.";
            else message = $"{grouped.Count} WLAN-Netz(e) gefunden.";
            return grouped;
        }

        /// <summary>Mit einem WLAN verbinden (erzeugt ein Profil und verbindet).</summary>
        public static bool ConnectWifi(string ssid, string password, bool secured, out string status)
        {
            status = "";
            if (string.IsNullOrWhiteSpace(ssid)) { status = "Kein WLAN ausgewaehlt."; return false; }

            try
            {
                string profileXml = BuildProfileXml(ssid, password, secured);
                string tmp = Path.Combine(Path.GetTempPath(), $"wlan_{Guid.NewGuid():N}.xml");
                File.WriteAllText(tmp, profileXml, new UTF8Encoding(false));

                var add = Netsh($"wlan add profile filename=\"{tmp}\" user=all", 15000);
                try { File.Delete(tmp); } catch { }
                if (!add.ok && !add.output.Contains("added", StringComparison.OrdinalIgnoreCase)
                            && !add.output.Contains("hinzugef", StringComparison.OrdinalIgnoreCase))
                {
                    status = "Profil konnte nicht angelegt werden: " + Shorten(add.output);
                    return false;
                }

                var con = Netsh($"wlan connect name=\"{ssid}\" ssid=\"{ssid}\"", 15000);
                if (!con.ok && !con.output.Contains("completed", StringComparison.OrdinalIgnoreCase)
                            && !con.output.Contains("abgeschlossen", StringComparison.OrdinalIgnoreCase))
                {
                    status = "Verbindungsbefehl fehlgeschlagen: " + Shorten(con.output);
                    return false;
                }

                // Auf Verbindung warten (max ~15 s)
                for (int i = 0; i < 15; i++)
                {
                    System.Threading.Thread.Sleep(1000);
                    var (connected, ssidNow, ip) = GetWifiState();
                    if (connected && (string.IsNullOrEmpty(ssidNow) || ssidNow.Equals(ssid, StringComparison.OrdinalIgnoreCase)))
                    {
                        status = $"Verbunden mit '{ssid}'" + (string.IsNullOrEmpty(ip) || ip == "-" ? "" : $", IP {ip}");
                        return true;
                    }
                }
                status = $"Verbindung zu '{ssid}' nicht bestaetigt (Passwort/Reichweite pruefen).";
                return false;
            }
            catch (Exception ex)
            {
                status = "Fehler: " + ex.Message;
                return false;
            }
        }

        public static bool DisconnectWifi(out string status)
        {
            var res = Netsh("wlan disconnect", 10000);
            status = res.ok ? "WLAN getrennt." : Shorten(res.output);
            return res.ok;
        }

        /// <summary>Aktuellen WLAN-Status (verbunden, SSID, IP) ermitteln.</summary>
        public static (bool connected, string ssid, string ip) GetWifiState()
        {
            string ssid = "";
            bool connected = false;
            var res = Netsh("wlan show interfaces", 10000);
            foreach (var raw in res.output.Split('\n'))
            {
                var line = raw.Trim();
                var mState = Regex.Match(line, @"^(State|Zustand)\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (mState.Success)
                {
                    var v = mState.Groups[2].Value.Trim();
                    if (v.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                        v.Contains("verbunden", StringComparison.OrdinalIgnoreCase)) connected = true;
                }
                var mSsid = Regex.Match(line, @"^SSID\s*:\s*(.*)$", RegexOptions.IgnoreCase);
                if (mSsid.Success && string.IsNullOrEmpty(ssid)) ssid = mSsid.Groups[1].Value.Trim();
            }

            string ip = "-";
            if (connected)
            {
                var wlanAdapter = GetAdapters().FirstOrDefault(a => a.Kind == AdapterKind.Wlan);
                if (wlanAdapter != null) ip = wlanAdapter.Ip;
            }
            return (connected, ssid, ip);
        }

        // -----------------------------------------------------------------
        //  LAN: DHCP erneuern
        // -----------------------------------------------------------------
        public static bool RenewDhcp(out string status)
        {
            var rel = Run("ipconfig.exe", "/release", 20000);
            var ren = Run("ipconfig.exe", "/renew", 40000);
            status = ren.ok ? "DHCP erneuert." : "DHCP-Erneuerung meldete einen Fehler.";
            return ren.ok;
        }

        // -----------------------------------------------------------------
        //  Verbindungstest (Ping) -> Rueckmeldung
        // -----------------------------------------------------------------
        public static (bool ok, string message) PingTest(string host = "8.8.8.8")
        {
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(host, 3000);
                if (reply != null && reply.Status == IPStatus.Success)
                    return (true, $"Antwort von {host}: {reply.RoundtripTime} ms (Internet erreichbar).");
                return (false, $"Keine Antwort von {host} ({reply?.Status}).");
            }
            catch (Exception ex)
            {
                return (false, "Ping fehlgeschlagen: " + ex.Message);
            }
        }

        // -----------------------------------------------------------------
        //  Hilfsfunktionen
        // -----------------------------------------------------------------
        private static string BuildProfileXml(string ssid, string password, bool secured)
        {
            string ssidHex = BitConverter.ToString(Encoding.UTF8.GetBytes(ssid)).Replace("-", "");
            string esc = System.Security.SecurityElement.Escape(ssid);

            if (!secured || string.IsNullOrEmpty(password))
            {
                return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{esc}</name>
  <SSIDConfig><SSID><hex>{ssidHex}</hex><name>{esc}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>
  </security></MSM>
</WLANProfile>";
            }

            string pwd = System.Security.SecurityElement.Escape(password);
            return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{esc}</name>
  <SSIDConfig><SSID><hex>{ssidHex}</hex><name>{esc}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
    <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{pwd}</keyMaterial></sharedKey>
  </security></MSM>
</WLANProfile>";
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(keine Ausgabe)";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }
    }
}
