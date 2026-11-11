# Verification

Date: 2026-11-11

Reference machine:

- Windows 10/11 laptop with Pegatron/Machenike vendor interfaces
- Intel CPU with PGFNEX temperature/fan telemetry
- NVIDIA RTX laptop GPU with NVML telemetry
- Internal panel, external DDC/CI monitor and Gamma-fallback monitor
- Logitech HID++ receiver
- Windows 10/11 build with .NET SDK 10.0.404

## Automated checks

| Check | Result |
| --- | --- |
| Frontend TypeScript + Vite build | Pass |
| oxlint | Pass |
| Release .NET build | Pass, 0 warnings, 0 errors |
| Diagnostic project target alignment | Pass, `net10.0-windows10.0.19041.0` |
| Built-in proxy HTTPS smoke | Pass, `status=204` |
| Hysteria2 HTTPS smoke | Pass with a locally configured node, `status=204`; no endpoint or credential is stored in the repository |
| Rule-mode HY2 proxy | Pass with a local select group, `status=204` |
| HTTP CONNECT local port | Pass on `127.0.0.1:7897`, `status=204` |
| SOCKS5 local port | Pass on `127.0.0.1:7897`, `status=204` |
| Busy-port fallback | Pass: occupied 17899, listener selected 17900 and HY2 smoke returned 204 |
| Macro hook and command execution | Pass, `MACRO_OK` |
| Device enumeration | Pass, 51 devices |
| Startup item enumeration | Pass, 11 real items |
| Scheduled task enumeration | Pass, 179 tasks |
| Installed application inventory | Pass, 109 apps |
| Signed driver inventory | Pass, 196 drivers |
| General tool smoke | Pass: models 3, logs 5, providers 1, minidumps 0 |
| EXE catalog | Pass: 1,039 catalog candidates, 1,083 indexed EXE rows |
| Launcher result cap | Pass: 300 rows returned in smoke; UI requests up to 1,200 |
| Bluetooth BLE API path | Compiled and enumerated; no connected BLE peripheral available for a live battery read |

## Previously verified hardware paths

| Check | Result |
| --- | --- |
| Internal brightness read/write | Pass (`WMI`) |
| External brightness read/write | Pass (`DDC/CI`) |
| PGFNEX CPU/GPU temperature and fan RPM | Pass |
| Office/gaming/turbo mode path | Available on reference machine |
| Windows pointer speed write | Pass |
| Logitech battery/DPI display | Pass via direct HID++: 96%, 3200 DPI |
| Logitech direct HID++ DPI write | Pass after G HUB removal: 3200→2400, readback 2400, restored 3200 |
| Logitech direct battery | Pass via HID++ `0x1001` voltage, 96% |
| Vendor Gamma fallback discovery | Pass on an external monitor with no DDC/CI brightness channel |
| Vendor Gamma software brightness | Pass: Gamma fallback set 70% and restored 100% |
| Full display enumeration | Pass across WMI, DDC/CI and Gamma transports |
| Sidebar scrolling | CSS flex height and scrollbar gutter corrected |
| Performance overlay metrics | Pass: CPU/GPU utilization, temperatures, NVML source and device battery panel rendered |
| Exact FPS helper | Pass after UAC: DXGI Present events produced `28 FPS` for Chrome |
| FPS fallback | Pass: without elevation, displays 100Hz refresh and an explicit “精确 FPS” action |
| All overlay metrics | Pass: 15 metric cards rendered from CPU/GPU/NVML/PGFNEX/system/network/disk data |
| Overlay background opacity | Pass at 33%; background was translucent while all metric text and colors remained fully opaque |
| Overlay dimensions | Pass: 310×340 resized to 430×440 through the native window path; responsive layout changed from 3 to 4 columns and schema 13 persisted both values |
| Overlay live settings | Pass via `--overlay-live-smoke`: 3→1 metrics and 520×500→620×560 occurred immediately; background alpha 101→211 while CPU color stayed `#61E7B8` and window opacity stayed 1 |
| Overlay multi-display position | Pass: saved negative virtual-desktop coordinates are requested from WPF and clamped to the virtual screen instead of the primary monitor |
| Toolbox navigation | Pass: primary navigation reduced from 18 sidebar items to 6 grouped categories with in-page tool-window tabs |
| Board adaptation | Pass: reference Pegatron/Machenike board detected as `GFN Control`; vendor service found and telemetry available |
| GPU control capability probe | Pass: reference RTX GPU reported current/enforced power limits and clocks; unsupported mobile fan and VF-offset controls were correctly gated |
| Default traffic policy | Pass on schema 13→14 migration: local listener `127.0.0.1:7897` running, `ProxyAutoStart=true`, `ProxySystemProxyEnabled=false`, Windows `ProxyEnable=0` |
| GPU Unlock inventory | Pass: reference RTX fingerprint collected; no matching public profile and `WriteEnabled=false` |
| DLSS package integrity | Pass: pinned `NR-v0.9.86` standalone downloaded with resume after the proxy timeout fix; final SHA-256 matched `78436B…360FA` |
| DLSS deploy/rollback | Pass: temporary game directory backed up and deployed `dxgi.dll`, then uninstalled while restoring the original proxy and preserving `game.exe` |
| Long proxy transfer | Pass: replaced the 25-second whole-tunnel cap; the 600 MB package resumed from 61% and completed |
| Plugin capability detection | Pass: 5 optional packs and 10 local capabilities detected without proxy credentials |
| Core-first onboarding | Pass: schema 15 starts with no optional packs enabled and opens capability detection first |
| Standalone detector | Pass: release script produces a sanitized JSON report with OS, display, Nvidia, Logitech and model availability |
| Process-specific FPS | Pass: selected `process:chrome`, helper reported `46 FPS` |
| Screen FPS source | Pass: selected `\\.\DISPLAY5`, reported 100Hz |
| Overlay dragging/persistence | Pass: moved to a new position and saved coordinates |
| Proxy watchdog | Pass: force-killing LocalDeck set `ProxyEnable=0` within 3 seconds |
| Clipboard listener and encrypted SQLite row | Pass |
| File index | Pass (previously 16,593 indexed user files) |
| Core Audio sessions | Pass |
| Bundled Hysteria2 engine cleanup | Pass; child process absent after proxy stop |
| LocModel engine migration | Pass; copied into LocalDeck-owned data directory |
| Local GGUF CUDA benchmark | Pass; Qwen3-4B CUDA backend loaded and `llama-bench` completed |
| File-search metadata join | Pass; PNG and JSON queries each returned 20 results |
| Orphan startup cleanup | Pass; removed Everything, Snipaste and Clash Verge registrations |
| Clash uninstall | Pass; install directory and uninstall registration absent |
| Post-uninstall system proxy | Pass; `ProxyEnable=1`, `127.0.0.1:7897`, HTTPS 204 |

## Shortcut behavior

`F1` (screenshot) and `F3` (pin clipboard image) registered successfully in the
final published build while Snipaste was not running. The shortcut center
reports conflicts instead of crashing or silently rebinding.

During the published-build check, `Ctrl+Alt+L` was already owned by another
local application. Schema version 7 therefore migrates the untouched default
launcher shortcut to `Ctrl+Alt+E`; `Ctrl+Alt+E` was verified available with
`RegisterHotKey`.

The test automation for macro recording excludes injected keyboard/mouse
events by design. Synthetic input therefore cannot validate the recorder; the
side-button execution path itself passed `MACRO_OK`. Physical recording should
be checked by the user in the separate `Ctrl+Alt+M` window.

## REA investigation

Tool:

```text
%LOCALAPPDATA%\Programs\ReverseEngineering\rea-7.1.0\rea.exe
```

Commands run against LocModel:

```text
rea info
rea decompile
rea LocModel.exe
```

Reports:

- `%LOCALAPPDATA%\LocalDeck\audits\LocModel\re-LocModel.md`
- `%LOCALAPPDATA%\LocalDeck\audits\LocModel\re-LocModel.pdf`

Findings:

- x64 PE with process, network, cryptography, registry and filesystem imports.
- PDB `locmodel.pdb`; approximately 48,967 recovered functions.
- About 904 KB remained high entropy/encrypted, so no definitive internal
  download endpoint could be recovered from that region.
- Local configuration established a configurable `models_dir`, port `8686`, and
  a bundled llama.cpp engine under `%LOCALAPPDATA%\LocModel\engines`.
- The installed GGUF files were not recognized by `rea model` as a supported
  model artifact, so model behavior was verified through LocalDeck's own GGUF
  scanner and llama.cpp executable detection.

LocalDeck does not call LocModel's UI, account or update flow. It reads the
local settings only as a migration default and lets the user copy the engine
to its own data directory.

## Known limitations

- Hysteria2 is provided by the bundled client engine and verified through rule
  and global modes.
- Classic Bluetooth has no universal battery characteristic; only connected BLE
  GATT battery devices are reported.
- Some proprietary Logitech features outside DPI/battery still require the
  device firmware's own HID++ feature set.
- The llama.cpp engine is not in Git because the installed CUDA build is about
  1.25 GB and must match the target computer's GPU/runtime.
- ACL-protected directories are skipped by the normal file enumeration.
