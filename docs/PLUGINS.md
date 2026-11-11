# Plugin Architecture

## Goals

LocalDeck uses plugin packs to keep the default workspace focused:

- expose common desktop tools without requiring configuration
- detect unsupported hardware before showing advanced controls
- keep dangerous writes unavailable unless their capability is verified
- keep heavy AI and game-enhancement assets outside the base download
- preserve a single trusted, signed desktop host for native Windows adapters

## Core Surface

The following tools are always available:

- overview
- local traffic forwarding
- clipboard and screenshots
- file search
- executable launcher
- global shortcuts
- settings
- capability detection and plugin management

## Optional Packs

### `hardware`

Pages: performance, GPU unlock, displays and peripherals, device manager,
drivers.

The pack is available on Windows. Individual controls remain capability-gated by
display transport, vendor interfaces, HID++ feature discovery and NVML.

### `system-tools`

Pages: uninstaller, disk analysis, Windows logs.

These tools use local Windows APIs and require no cloud service.

### `automation`

Pages: macros and startup/scheduled-task management.

The low-level mouse hook is installed only while this pack is enabled. Disabling
the pack removes the hook.

### `ai-workbench`

Pages: model center.

The UI can be enabled without a model. GGUF files and llama.cpp engines are
downloaded or imported on demand and remain outside Git.

### `game-enhancements`

Pages: GPU Unlock and DLSS.

This pack requires an NVIDIA RTX GPU. Enabling the pack does not apply a hardware
unlock. GPU Unlock remains gated by upstream profile state, and DLSS resources
are downloaded separately after an explicit action.

## Capability Detection

The backend classification is:

- `supported`: the tested API or resource is available
- `limited`: the component is present but some writes are unavailable
- `unavailable`: required hardware or software is absent
- `on-demand`: the tool can be enabled after downloading/importing resources

The plugin center also writes a local capability report in the application data
directory. Distribution builds include `detect-capabilities.ps1` for the same
checks before first launch.

## Enable and Disable

Enabled plugin IDs are stored in local application settings. The frontend maps
each optional page to exactly one pack:

| Page | Pack |
| --- | --- |
| performance, devices, device-manager, drivers | `hardware` |
| gpu-unlock | `game-enhancements` |
| uninstaller, disk, logs | `system-tools` |
| macros, tasks | `automation` |
| models | `ai-workbench` |

Disabling a pack removes its pages and releases runtime hooks associated with
that pack.

## Trust Boundary

The base application contains native Windows adapters so users do not need to
download unsigned DLLs into the host process. Plugin packs control activation
and optional resources; they do not bypass the host's capability checks.

This design is intentional:

- native adapters remain signed and testable with the application
- optional heavy resources are not redistributed by default
- no plugin can silently enable an unsupported hardware write

## Adding a Plugin

1. Add the page mapping in `frontend/src/App.tsx`.
2. Add a plugin definition in `PluginCatalogService`.
3. Define detection and capability status.
4. Add bridge methods with explicit input validation.
5. Keep expensive services lazy.
6. Document rollback and external downloads.
7. Add a smoke test that does not require destructive hardware writes.

Plugins that perform network, elevation, persistence or hardware writes require
an explicit threat-model review.
