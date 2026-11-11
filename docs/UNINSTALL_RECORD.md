# Uninstall record

Date: 2026-11-11

The reference computer was migrated only after the replacement path was
verified. Clash was the last proxy application removed, after LocalDeck's rule
mode, Hysteria2 node, system proxy, HTTP CONNECT and SOCKS5 listener all passed.

## Removed

| Program | Replacement verification |
| --- | --- |
| Clash Verge | Rule mode with a locally configured HY2 node; HTTP and SOCKS5 through LocalDeck returned HTTPS 204 |
| Everything 1.4.1 | SQLite FTS search returned 20 PNG and 20 JSON hits from 17,632 indexed files |
| Snipaste 2.11.3 | F1 screenshot and F3 pin-image shortcuts registered |
| Twinkle Tray 1.17.2 | Internal WMI and external DDC/CI brightness read successfully |
| WizTree 4.33 | Local space scan completed with 2,587 files / 793,801,897 bytes |
| Geek Uninstaller 1.5.3 | LocalDeck installed-app and uninstall registry reader passed |
| EarTrumpet 2.3.0 | Windows Core Audio returned three live sessions |
| LocModel 2.1.0 | Engine migrated; Qwen3-4B CUDA llama-bench completed |
| Logitech G HUB 2026.7 | Direct HID++ DPI write/readback and `96%` battery read passed after G HUB removal |

The old portable application copies and redundant model-engine directories were
moved to the Windows Recycle Bin after replacement checks. The LocModel reports
were copied to
`%LOCALAPPDATA%\LocalDeck\audits\LocModel` first.

Three orphan startup registrations left by uninstallers were removed:

- Clash Verge startup shortcut
- Everything HKCU Run value
- Snipaste HKCU Run value

LocalDeck now starts from `%LOCALAPPDATA%\Programs\LocalDeck`, writes its HKCU
Run entry there, and starts the local forwarding listener on login. Windows
system proxy remains disabled unless the user enables it explicitly.

## Deliberately retained

| Program | Reason |
| --- | --- |
| HWiNFO | LocalDeck covers common PGFNEX temperatures/fans, not HWiNFO's complete sensor database |
| Machenike control center | Its uninstall package shares the PHotkey/PGFNEX hardware layer; it was not removed until driver ownership can be isolated safely |

These are hardware/vendor boundaries rather than proxy functionality
dependencies. They are not required for Hysteria2, file search, screenshots,
brightness, audio, disk analysis, uninstalling, or general desktop operation.
