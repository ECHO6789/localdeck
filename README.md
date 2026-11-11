# LocalDeck

[![Build](https://github.com/ECHO6789/localdeck/actions/workflows/build.yml/badge.svg)](https://github.com/ECHO6789/localdeck/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-61e7b8.svg)](LICENSE)
[![Windows 10/11](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-6bb8ff.svg)](#requirements)

**LocalDeck is an open-source Windows all-in-one control deck for hardware
control, local proxy forwarding, clipboard history, screenshots, file search,
automation, system maintenance, local AI models, NVIDIA tuning and reversible
DLSS deployment.**

It combines the useful local functions of tools such as Twinkle Tray, Snipaste,
Everything, EarTrumpet, Geek Uninstaller, WizTree, Logitech G HUB and MSI
Afterburner into one privacy-first desktop application built with WPF,
WebView2, React and TypeScript.

LocalDeck starts with a small core. A first-run capability check identifies what
the current PC supports, then lets the user enable only the hardware, system,
automation, AI or game-enhancement plugin packs they actually need.

[Features](#features) · [Install](#install) · [Capability detection](#first-run-capability-detection) · [Plugins](docs/PLUGINS.md) · [Architecture](docs/ARCHITECTURE.md) · [Security](SECURITY.md) · [中文简介](#中文简介)

## Why LocalDeck?

- **Local-first:** no account, telemetry, advertising, remote control or
  automatic updater.
- **Core-first:** common screenshot, clipboard, search, launcher, hotkey and
  local-forwarding features stay small; larger tool areas are optional packs.
- **Hardware-aware:** detects the current motherboard family, display
  transports, NVIDIA GPU and supported peripherals before exposing controls.
- **Reversible:** risky operations such as DLSS game deployment create
  per-file backups and can be removed from the same UI.
- **No hidden external control panels:** vendor APIs are called directly when
  verified; unsupported hardware is reported instead of receiving guessed
  register writes.

## Features

### Core

| Area | Capabilities |
| --- | --- |
| Desktop overview | CPU, memory, disk, battery, network throughput and top processes |
| Local traffic forwarding | Built-in HTTP CONNECT + SOCKS5 listener on `127.0.0.1`; system proxy is disabled by default |
| Clipboard | Encrypted local history, search, pinned snippets, text/HTML/RTF/image support |
| Screenshots | Region capture, clipboard image pinning, separate capture-and-pin hotkeys |
| File search | Self-contained SQLite FTS5 index with open and Explorer-reveal actions |
| App launcher | Reuses the file index and executable catalog; games and applications are ranked first |
| Global shortcuts | Screenshot, pin, search, clipboard, launcher, proxy, macros and custom actions |
| Settings and privacy | Local JSON configuration, HKCU startup integration and no cloud sync |

### Optional Plugin Packs

| Pack | Included tools | Delivery |
| --- | --- | --- |
| Hardware | Brightness, Logitech HID++, device manager, vendor fan/power modes, drivers, NVIDIA tuning | Lightweight enablement |
| System tools | Uninstaller, leftover cleanup, disk analysis, Windows event logs and minidumps | Lightweight enablement |
| Automation | Side-button macros, recorder, startup items and scheduled tasks | Lightweight enablement |
| AI workbench | Local GGUF models, llama.cpp server, TPS benchmark and cloud-model shortcuts | Models and engine downloaded or imported on demand |
| Game enhancements | GPU Unlock compatibility report and reversible DLSS Unlocked deployment | DLSS package downloaded and hash-verified on demand |

See [Plugin architecture](docs/PLUGINS.md) for the support model and safety
rules.

## First-Run Capability Detection

LocalDeck opens the plugin center after installation and detects the local
machine before optional tools are shown.

Standalone releases also include a PowerShell detector:

```powershell
powershell -ExecutionPolicy Bypass -File .\detect-capabilities.ps1
```

The report is written to:

```text
%LOCALAPPDATA%\LocalDeck\capabilities.json
```

It checks Windows, WebView2, local forwarding, display controls, motherboard
identity, NVIDIA GPU availability, Logitech HID++ and local model tooling. The
report intentionally excludes usernames, hostnames, serial numbers, UUIDs,
proxy server addresses, subscription URLs and credentials.

## Install

1. Download the latest Windows x64 ZIP from
   [Releases](https://github.com/ECHO6789/localdeck/releases).
2. Verify the SHA-256 value published in the release notes.
3. Extract the archive and run `detect-capabilities.ps1`.
4. Launch `LocalDeck.exe`.
5. Review detected capabilities and enable only the plugin packs you need.

### Requirements

- Windows 10 build 19041 or newer / Windows 11
- WebView2 Runtime
- x64 desktop or laptop
- No Node.js or .NET SDK is required for the self-contained release

## Build from Source

### Requirements

- .NET SDK 10.0.404 or compatible
- Node.js 22+
- PowerShell 7
- WebView2 Runtime

```powershell
npm ci --prefix frontend
npm run build --prefix frontend
dotnet build tools/LocalDeck.Diagnostics/LocalDeck.Diagnostics.csproj -c Release
pwsh -NoProfile -File scripts/publish.ps1
```

Run the local build:

```powershell
dotnet run --project src/LocalDeck.App/LocalDeck.App.csproj
```

## Privacy and Security

- No telemetry, analytics, advertising SDK, account system or automatic update
  client is included.
- The frontend is mapped to a virtual local host with CORS denied; it is not
  served by a LAN HTTP server.
- Imported proxy YAML, generated Hysteria2 configs, VPS addresses, subscription
  URLs and node credentials are runtime-only data and are excluded from Git and
  release packaging.
- The system proxy is off by default. Local forwarding can run without changing
  Windows Internet Settings.
- Hardware writes are capability-gated. The main process stays non-elevated;
  explicit GPU operations use a short-lived UAC helper.
- GPU Unlock never substitutes user-mode register writes for the UEFI
  pre-driver stage. Disabled upstream profiles remain disabled.
- DLSS deployment is limited to a user-selected game directory and records
  every replaced file for rollback. Use it only with supported single-player
  games.

Read [SECURITY.md](SECURITY.md) and the [open-source audit](docs/OPEN_SOURCE_AUDIT.md)
before redistributing or publishing a fork.

## Hardware Support

| Hardware | Support model |
| --- | --- |
| Displays | WMI internal backlight, DDC/CI, verified Gamma fallback |
| Pegatron/Machenike | GFN local fan/power modes when vendor interfaces are installed |
| MSI, ASUS, GIGABYTE/AORUS, ASRock, Lenovo, HP/OMEN, Dell/Alienware, Acer/Predator, Razer, Tongfang, Clevo | Board identity and matching panel profile; writes require a verified local interface |
| NVIDIA RTX | NVML telemetry and capability-gated power/fan/clock controls |
| Logitech | Native HID++ 2.0 battery and DPI where the device exposes the feature |
| Bluetooth | Standard BLE GATT Battery Service only |

Unsupported vendor functions are shown as unavailable rather than approximated
with unsafe EC writes.

## Project Structure

```text
src/LocalDeck.App/       WPF host, Windows adapters and bridge
frontend/                React + TypeScript WebView2 UI
tools/LocalDeck.Diagnostics/
                         Hardware and release smoke tests
scripts/                 Build, publish and capability detection
docs/                    Architecture, plugin, proxy and audit documentation
vendor/hysteria/         Pinned MIT Hysteria2 client engine and source record
```

## Documentation

- [Project overview](docs/PROJECT_OVERVIEW.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Plugin architecture](docs/PLUGINS.md)
- [Proxy implementation](docs/PROXY.md)
- [Shortcuts and macros](docs/SHORTCUTS.md)
- [Capability verification](docs/VERIFICATION.md)
- [Open-source audit](docs/OPEN_SOURCE_AUDIT.md)
- [Changelog](CHANGELOG.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)
- [Contributing](CONTRIBUTING.md)
- [Code of conduct](CODE_OF_CONDUCT.md)

## Contributing

Contributions are welcome for:

- verified motherboard/laptop adapters with exact hardware and recovery details
- privacy or capability detection improvements
- Windows 10/11 compatibility fixes
- accessibility and localization
- tests that do not require proprietary binaries

Do not submit proxy subscriptions, generated Hysteria2 configs, API keys,
private keys, usernames, hostnames or machine serial numbers. See
[CONTRIBUTING.md](CONTRIBUTING.md) first.

## License

LocalDeck is released under the [MIT License](LICENSE). Third-party components
retain their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

LocalDeck is not affiliated with or endorsed by Microsoft, NVIDIA, Logitech,
Machenike, MSI, ASUS, GIGABYTE, ASRock, Lenovo, HP, Dell, Acer, Razer, Intel or
the maintainers of the referenced third-party tools.

## 中文简介

LocalDeck 是一个 MIT 开源的 Windows 本地一体化工具箱，覆盖亮度、代理流量转发、
剪贴板、截图贴图、文件搜索、快捷启动、全局快捷键、外设、系统维护、自动化、
本地模型、NVIDIA GPU 调校和可回滚的 DLSS 部署。

安装后首先运行能力检测，只显示当前电脑真正支持或用户主动启用的插件包。默认
不修改 Windows 系统代理，也不会把用户的 VPS、订阅、YAML、密码、令牌或本机
硬件序列号写入仓库和发布包。
