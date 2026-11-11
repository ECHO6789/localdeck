# LocalDeck Project Overview

## What Is LocalDeck?

LocalDeck is an open-source, local-first Windows control deck. It provides one
desktop application for the small utilities that normally require many
separately installed programs: display brightness, clipboard history,
screenshots, file search, application launching, global shortcuts, audio
control, peripheral management, system maintenance, automation, local AI models,
NVIDIA tuning and reversible game-enhancement deployment.

The project is built with:

- WPF and .NET for native Windows integration
- WebView2 for the desktop UI shell
- React and TypeScript for the frontend
- SQLite FTS5 for local indexing
- Direct Win32, WMI, HID++ and NVML adapters where vendor tools are unnecessary

LocalDeck does not provide a cloud account, hosted dashboard, advertising
feed, remote-control channel or automatic updater.

## Who Is It For?

LocalDeck is designed for Windows users who:

- want a single, lightweight control surface instead of a tray full of utilities
- prefer local processing and explicit network actions
- manage laptops or desktops with vendor-specific fan, power or display controls
- need clipboard, screenshot and file-search tools that work offline
- use NVIDIA GPUs for gaming or local AI
- want optional advanced tools without exposing them to users who cannot use them

## Core and Plugin Packs

The core installation includes the most frequently used desktop functions:

- system overview
- local HTTP CONNECT and SOCKS5 forwarding
- clipboard history and screenshots
- file search
- executable launcher
- global shortcuts
- privacy and local settings

Optional plugin packs are hidden until supported hardware is detected and the
user enables them:

| Plugin ID | Purpose |
| --- | --- |
| `hardware` | Displays, peripherals, vendor controls, drivers and NVIDIA tuning |
| `system-tools` | Uninstaller, disk analysis and Windows log analysis |
| `automation` | Side-button macros, recorder, startup items and scheduled tasks |
| `ai-workbench` | Local GGUF models, llama.cpp and cloud-model shortcuts |
| `game-enhancements` | GPU Unlock compatibility and DLSS Unlocked deployment |

The runtime remains lazy: enabling a pack reveals its tool window, while heavier
operations start only when used. Large resources such as AI models or the DLSS
standalone package are downloaded or imported on demand.

## Capability Detection

LocalDeck performs a first-run capability check before presenting optional
features. The detector examines:

- Windows APIs and WebView2
- local forwarding availability
- display transports
- motherboard family and vendor service availability
- NVIDIA GPU and NVML support
- Logitech HID++ devices
- local llama.cpp installation

Supported, partially supported and unavailable functions are shown separately.
The detector never reads proxy credentials or exports VPS endpoints.

An equivalent standalone script is included in releases:

```powershell
powershell -ExecutionPolicy Bypass -File .\detect-capabilities.ps1
```

## Hardware and Software Boundaries

LocalDeck uses direct interfaces only when they can be verified:

- WMI for internal display backlight
- DDC/CI for compatible external displays
- a verified Gamma fallback for displays without a hardware brightness channel
- native HID++ for compatible Logitech devices
- PGFNEX/PHotkey for supported Pegatron/Machenike platforms
- NVML for NVIDIA telemetry and capability-gated tuning

An identified motherboard family does not automatically imply arbitrary register
access. For unsupported vendor controls, LocalDeck reports a limited or
unavailable state.

GPU Unlock changes hardware state before the Windows driver loads. LocalDeck
therefore provides inventory, profile gating and compatibility reports, but
does not emulate that UEFI stage with unsafe user-mode writes.

## Network Model

LocalDeck contains a local mixed proxy listener and supports imported Clash-format
YAML, Shadowsocks, Trojan, Hysteria2, HTTP and SOCKS5 upstreams.

Important defaults:

- local forwarding starts with LocalDeck when a profile is available
- Windows system proxy is disabled by default
- the listener binds only to loopback
- the bundled Hysteria2 engine disables update checks
- proxy YAML, node credentials, subscription links and generated configs remain
  in the local user profile and are never included in releases

## Store and Clipboard Privacy

Clipboard bodies are protected for the current Windows user with DPAPI. File
paths, model settings and application configuration remain in
`%LOCALAPPDATA%\LocalDeck`.

No telemetry endpoint or hosted user profile exists. Diagnostics are written
locally without clipboard bodies.

## Why Another All-in-One Tool?

Many desktop utilities combine useful functionality with accounts, update
services, recommendations, remote configuration or cloud synchronization.
LocalDeck separates functional network access from nonessential outbound
behavior:

- proxy connections and user-requested model downloads are explicit functions
- account, advertising, telemetry and remote-control paths are excluded
- vendor applications are replaced only where LocalDeck has a verified local API
- unsupported functionality remains visible as a boundary instead of being
  silently approximated

## Development and Verification

The repository includes:

- a Windows diagnostic console
- proxy protocol smoke tests
- HID++ and display probing
- overlay live-update verification
- capability and plugin detection tests
- DLSS package integrity and rollback tests
- a Windows GitHub Actions build

See [VERIFICATION.md](VERIFICATION.md) and
[OPEN_SOURCE_AUDIT.md](OPEN_SOURCE_AUDIT.md).

## Open-Source Distribution

The source is licensed under MIT. Self-contained Windows x64 releases include
the .NET runtime and the pinned Hysteria2 engine. WebView2 Runtime is expected
to be supplied by Windows.

Third-party binaries, required licenses and source records are documented in
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Chinese Summary

LocalDeck 是 MIT 开源的 Windows 本地工具控制台。它把亮度、剪贴板、截图、文件
搜索、快捷启动、快捷键、外设、系统维护、自动化、本地模型和 GPU 调校整合到一个
WPF + WebView2 应用中。

核心保持精简，硬件、系统工具、自动化、AI 和游戏增强作为可选插件包。首次启动
会检测本机能力，只展示可支持或用户主动启用的功能。用户的 VPS、订阅、YAML、
代理密码和本机敏感标识不会进入 Git 仓库或发布包。
