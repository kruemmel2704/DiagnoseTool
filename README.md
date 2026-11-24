# CPM DiagnoseTool

Hardware-Diagnose mit Live-Sensorik (LibreHardwareMonitor), CPU-/RAM-/GPU-Stresstests (FurMark), Akkutest, Werte-Aufzeichnung, PDF-Prüfberichten, Absturzanalyse der Windows-Ereignisanzeige und REST-API (Port 8085) für die Fernsteuerung durch **CPM Hardware-Check**.

## Download

Die aktuelle Version gibt es unter [Releases](https://github.com/kruemmel2704/DiagnoseTool/releases/latest).

| System | Datei |
|---|---|
| Windows (Intel/AMD) | `DiagnoseTool-Windows-x64.exe` |
| Windows (ARM) | `DiagnoseTool-Windows-arm64.exe` |
| macOS Apple Silicon (M1–M4) | `DiagnoseTool-macOS-AppleSilicon.dmg` |
| macOS Intel | `DiagnoseTool-macOS-Intel.dmg` |

## macOS ohne Apple-Developer-Account

Die Mac-App ist **ad-hoc signiert**. Das ist ohne Developer-Account möglich und auf Apple Silicon Voraussetzung dafür, dass macOS das Programm überhaupt startet. Notarisiert ist die App nicht, daher warnt Gatekeeper bei Dateien, die **im Browser** geladen wurden.

**Empfohlen: Installation per Terminal (keine Warnung, wählt automatisch Apple Silicon/Intel):**

```bash
curl -fsSL https://raw.githubusercontent.com/kruemmel2704/DiagnoseTool/main/install-macos.sh | bash
```

Per `curl` geladene Dateien bekommen keine Quarantäne-Markierung, deshalb startet die App ohne Gatekeeper-Dialog. Das Skript installiert nach `/Applications` (ggf. mit Passwortabfrage) und startet das DiagnoseTool.

**Alternativ, wenn das DMG im Browser geladen wurde:** App aus dem DMG nach „Programme“ ziehen und einmalig freigeben:
- macOS 15 (Sequoia) und neuer: App öffnen → Meldung schließen → **Systemeinstellungen → Datenschutz & Sicherheit → „Dennoch öffnen“**
- macOS 14 und älter: Rechtsklick auf die App → **Öffnen** → **Öffnen**
- oder im Terminal: `xattr -dr com.apple.quarantine /Applications/DiagnoseTool.app`

**Updates:** Der eingebaute Auto-Updater lädt neue Versionen selbst herunter (ohne Quarantäne), tauscht die App aus und startet sie neu – eine erneute Freigabe ist nicht nötig.

## Updates

Das DiagnoseTool prüft beim Start und alle 6 Stunden auf ein neueres Release und bietet das Update in einer Hinweisleiste an (Windows: Exe-Austausch mit SHA-256-Prüfung, macOS: App-Austausch aus dem DMG). Abschaltbar mit `DIAGNOSETOOL_NO_UPDATE_CHECK=1`.

## PE-Edition (DiagnosePE / WinPE)

Das DiagnoseTool erkennt automatisch, wenn es in einer **WinPE-Umgebung** läuft
(gesetzt über `DIAGNOSETOOL_PE=1`, erkannt via [PeEnvironment](PeEnvironment.cs)).
Im PE-Modus:

- **Software-Rendering** statt Direct3D/ANGLE (WinPE hat kein D3D) – siehe `Program.cs`.
- Start als **Vollbild-Kiosk** (`App.axaml.cs`).
- Zusätzlicher Menüpunkt **„Netzwerk (PE)“**: LAN-/WLAN-Adapter anzeigen, WLAN
  suchen/verbinden (`netsh wlan`), DHCP erneuern und Verbindung per Ping testen –
  mit Status-Rückmeldung ([NetworkPeService](NetworkPeService.cs),
  [MainViewModel.NetworkPe](MainViewModel.NetworkPe.cs)).
- **FurMark** läuft über **OpenGL** (`--demo furmark-gl`) bzw. **Vulkan**
  (`--demo furmark-vk`), nie über Direct3D.

Das bootfähige WinPE-Image wird im separaten Repo
[**DiagnosePE**](https://github.com/kruemmel2704/DiagnosePE) gebaut (Windows ADK),
inklusive NVIDIA-/AMD-GPU- und LAN-/WLAN-Treibern. Dieses Repo liefert nur die App;
DiagnosePE zieht sich die Release-Exe und verpackt sie.

## Release erstellen

Version in `DiagnoseTool.csproj` erhöhen und unter **Actions → Build and Release → Run workflow** starten (oder einen Tag `v<Version>` pushen). Der Workflow baut alle Windows-Exes und die signierten macOS-DMGs und veröffentlicht das Release.
