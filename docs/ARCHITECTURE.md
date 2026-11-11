# Architecture

## Process layout

LocalDeck is one per-user WPF process with an embedded WebView2 UI. It does not
install a Windows service, driver, browser extension, or background helper.

```text
React UI (virtual local host)
  -> typed WebView2 JSON bridge
    -> WPF host / BridgeDispatcher
      -> always-on Windows integrations
      -> lazy optional modules
      -> self-contained mixed proxy
      -> SQLite local state
```

The frontend is mapped as `https://localdeck.app` to `wwwroot` with
`DenyCors`. It is not served by a TCP HTTP server.

The desktop UI starts with a focused core. Optional plugin packs add their pages
to the six navigation groups and expose their tools through an in-page window
rail. Unsupported or disabled packs are not shown.

## Always-on modules

These provide the desktop behavior the user expects immediately:

- global hotkey registration
- screenshot and pin-image actions
- clipboard format listener and encrypted history
- file index status and search
- system overview polling
- mouse side-button macro hook only when the automation pack is enabled
- proxy control object; the local mixed listener starts with LocalDeck by default
- tray icon and optional HKCU Run entry

## Lazy modules

`BridgeDispatcher` creates heavier modules with `Lazy<T>` and touches them only
when their bridge method is invoked:

| Module | Trigger |
| --- | --- |
| Device manager | First device-manager request |
| Startup items and scheduled tasks | Enter startup/task area |
| Uninstaller and leftovers | Enter uninstall area |
| Disk analyzer | Enter space analyzer |
| Driver inventory and Windows Update query | Enter driver area |
| Windows event log analysis | Enter log area |
| Executable catalog scan | First launcher request |
| Bluetooth BLE battery | Enter device manager |
| Macro recorder hooks | Start recording in macro window |
| llama.cpp model service | Enter model center or start server |
| NVIDIA GPU control | Enter hardware control |
| GPU Unlock inventory and profile report | Enter GPU unlock window |
| DLSS Unlocked package download/deploy | Enter GPU unlock window or start download |
| Plugin capability detection | First-run plugin center or explicit refresh |

Loaded objects remain in the process after first use, but their expensive
operations are idle until called. Disk scanning and model inference can be
stopped independently. The launcher executable scan is one-shot per process.

## Persistent state

Runtime data is stored under `%LOCALAPPDATA%\LocalDeck`:

- `settings.json`: app, hotkey, proxy, macro, model and folder configuration.
- `localdeck.db`: clipboard, file FTS, EXE catalog and usage counts.
- `screenshots/`: captured PNG files.
- `profiles/`: imported proxy YAML.
- `quarantine/`: directories moved out by the cleanup tool.
- `registry-backups/`: `.reg` exports created before registry deletion.
- `engines/`: optional llama.cpp engine copied from an external installation.
- `models/`: default model directory when no external one is selected.
- `localdeck.log`: operational diagnostics without clipboard bodies.
- `dlss-unlocked/packages/`: explicitly downloaded, hash-verified standalone ZIP.
- `dlss-unlocked/backups/`: original game files required for rollback.
- `dlss-unlocked/installations.json`: one record per deployed game directory.
- `capabilities.json`: sanitized first-run or standalone capability report.

Clipboard body data is protected with current-user DPAPI. File paths, proxy
credentials and model paths are local configuration and are not uploaded.

## Adapters

- `BrightnessService`: WMI internal panel, `dxva2.dll` DDC/CI external monitors.
- `ProxyService`: local mixed HTTP CONNECT/SOCKS5 listener and upstream clients.
- `Hysteria2EngineService`: private per-node HY2 client child, generated config,
  ephemeral loopback SOCKS5 port, update-check disabled, stopped with the proxy.
- `ProxyProfileService`: local Clash-format YAML import, no Clash runtime.
- `LogitechHidService`: native HID++ 2.0 long reports for feature discovery,
  `0x2201/0x2202` DPI and `0x1000/0x1004/0x1001` battery paths. LGHUB is not
  required; its old local cache is only a display-name fallback.
- `BrightnessService`: enumerates every active GDI/WMI display target and selects
  WMI, DDC/CI, or a verified GDI gamma-ramp fallback per display.
- `PerformanceOverlayService`: composes CPU utilization, PGFNEX temperatures,
  NVML/Windows GPU usage, Bluetooth batteries and foreground Present events.
- `HardwareProfileService`: reads SMBIOS baseboard/computer identity, maps the
  active vendor family to its corresponding panel, and exposes only verified
  local write capabilities.
- `GpuControlService`: reads NVML power, clocks, fan and VF-offset capability;
  write actions run through a short-lived UAC-approved helper.
- `GpuUnlockService`: reads NVIDIA PCI/subsystem identity, enforces exact public
  profile state, exports a sanitized compatibility report, and never substitutes
  user-mode writes for the UEFI pre-driver stage.
- `DlssUnlockedService`: downloads the pinned DLSS Unlocked standalone package,
  verifies its SHA-256, deploys to a selected game directory, records per-file
  backups, and restores those files on uninstall.
- `FpsMonitorService`: consumes Microsoft-Windows-DXGI/D3D9 Present ETW events.
  Because ETW requires elevation, the same executable can run as a minimal
  `--fps-helper` child and publish counters through a local JSON snapshot.
- `PerformanceOverlayWindow`: native WPF, dynamically rebuilds metric cards from
  the selected metric list and available width, supports native edge resizing,
  remembers drag position and dimensions across the virtual desktop, and applies
  opacity only to background brushes so metric colors remain fully opaque.
- `ProxyWatchdogService`: a separate same-user guard process. It disables the
  system proxy after an unexpected LocalDeck exit, but only when the current
  proxy is still the exact loopback endpoint owned by LocalDeck.
- The local HTTP/SOCKS5 listener starts by default. The system proxy remains
  disabled until explicitly enabled in the network page.
- `BluetoothBatteryService`: connected BLE GATT battery service.
- `HotkeyService`: dynamic `RegisterHotKey` registrations.
- `ScreenshotService`: virtual-screen capture and WPF pin windows.
- `MacroService` / `MacroRecorderService`: low-level hooks and `SendInput`.
- `DeviceManagerService`: PnP enumeration and conservative enable/disable.
- `TaskStartupService`: HKCU/HKLM Run, Startup folders and Task Scheduler COM.
- `UninstallerService`: 32/64-bit uninstall registry, quarantine and `.reg` backup.
- `DiskAnalyzerService`: cancellable top-level directory aggregation.
- `DriverService`: signed-driver inventory and explicit Windows Update COM query.
- `WindowsLogService`: Event Log reader and minidump discovery.
- `ExecutableCatalogService`: file-index reuse plus known application/game roots.
- `PluginCatalogService`: detects local capabilities, maps optional pages to
  plugin packs, and persists only enabled plugin IDs.

## Launcher catalog

The launcher first queries EXEs already present in the file index. It also
stores discovered application executables in `exe_catalog` and copies new paths
into `files`/`file_fts`. This means normal file search benefits from the scan,
while launcher entries survive a later rebuild of the user-document index.

Known roots include:

- Program Files and Program Files (x86)
- `%LOCALAPPDATA%\Programs`
- WindowsApps aliases when accessible
- Steam libraries from the registry and `libraryfolders.vdf`
- Epic manifests from ProgramData
- WeGame, Riot Games, Battle.net and common `Games` directories

Traversal skips reparse points and known cache/installer trees, caps depth to
avoid scanning arbitrary drive roots, and excludes setup/uninstall/crash tools.
Entries are classified as game, application, or utility. Pinned entries and
usage counts take precedence; games then applications then uncommon tools are
shown. The frontend initially renders at most 48 entries per group to keep the
WebView light, offers “show all” for each group, and searches the complete
catalog up to 1,200 results.

## Local model ownership

The model center can use a selected GGUF directory and a selected llama.cpp
engine. Existing LocModel discovery is only a convenience migration path. The
“copy engine to LocalDeck” action copies the external engine under
`%LOCALAPPDATA%\LocalDeck\engines` and updates the configured path. Once that
copy succeeds, LocModel can be uninstalled.

The engine and models are intentionally not committed to Git or embedded in a
small source release. Users choose a CPU/CUDA build appropriate for each PC.
All model downloads are explicit user-entered HTTP(S) actions.

## Trust boundaries

- The UI cannot expose a general-purpose HTTP server.
- The bundled Hysteria2 child listens only on an ephemeral `127.0.0.1` SOCKS5
  port. Node credentials are written to a user-profile config and the temporary
  config is deleted when the engine stops.
- Macro commands are powerful and execute only from local user configuration.
- GPU power/fan/clock writes are validated against NVML capabilities and only
  occur inside an explicit UAC-approved helper process.
- GPU Unlock hardware writes are not exposed while the upstream profile is
  disabled. DLSS deployment is restricted to a user-selected game folder and
  is fully reversible from LocalDeck.
- Proxy YAML, generated HY2 client configs, VPS endpoints and subscriptions are
  runtime-only data. They are excluded from source and release packaging.
- Device disabling and protected task changes may require administrator rights.
- Disk cleanup moves directories only after a UI confirmation; registry deletion
  exports a backup first and is restricted to uninstall registration keys.
