# Security

LocalDeck is designed as a per-user desktop application. The main UI does not
request elevation and no service is installed. Explicit GPU write actions may
start a short-lived UAC-approved helper.

## Trust boundaries

- The UI is local application code, not a remote web page.
- WebView2 uses a virtual local host mapping with CORS denied.
- Clash controller addresses are restricted to loopback.
- Power/fan switching writes vendor hardware state. The UI warns before enabling the highest mode.
- Clipboard payloads are encrypted with DPAPI for the current Windows user.
- Imported proxy YAML contains node credentials and is stored unencrypted under
  the current user's LocalAppData directory. Its ACL follows the user profile.
- VPS addresses, subscription links, generated Hysteria2 configs and proxy
  passwords must never be committed. The publish script blocks known runtime
  config filenames and personal-path patterns from release staging.
- User-created macros can run local commands. Only import or create macros you
  understand.
- Disabling Plug and Play devices and changing HKLM startup/task entries can
  require administrator rights. LocalDeck runs as the current user by default.
- Uninstall cleanup moves a confirmed directory under LocalDeck's quarantine
  folder. Registry deletion is restricted to uninstall registration keys and
  writes a `.reg` export first.
- The model downloader accepts user-supplied HTTP(S) URLs and writes only `.gguf`
  files into the configured model directory. Do not download untrusted model
  files.
- Adopting a llama.cpp engine performs a full local directory copy. Use a known
  engine build obtained from a trusted source; the repository does not pin or
  redistribute a GPU runtime.
- The on-demand EXE catalog scans well-known installation locations. It does not
  crawl arbitrary drive roots and skips reparse points and known caches.
- The bundled Hysteria2 engine runs as a per-user child process with update
  checks disabled and an ephemeral loopback-only SOCKS5 listener. Generated
  configs contain node credentials under the current user's LocalDeck data
  directory and are removed when the engine stops.
- Exact FPS uses an optional elevated `--fps-helper` instance of the same
  executable. It only consumes DXGI/D3D9 ETW events and writes local counters;
  the main UI process remains non-elevated.
- GPU power/fan/clock writes use an optional `--gpu-helper` instance. Values are
  validated from NVML capability ranges before the elevated process starts.
- A same-user proxy watchdog disables the system proxy when the LocalDeck
  process disappears or its loopback listener stops. It only acts when the
  current system proxy still points to LocalDeck's exact port.
- GPU Unlock hardware changes are not implemented as user-mode writes. LocalDeck
  only exports sanitized inventory and profile compatibility while the upstream
  profile remains disabled.
- DLSS Unlocked is downloaded only after explicit user action and must match a
  pinned SHA-256. Deployment writes only inside the selected game directory,
  backs up every replaced file, and supports rollback.
- DLSS game modifications can conflict with anti-cheat systems. Use them only in
  supported single-player titles.
- Optional plugin packs are hidden until enabled. The first-run detector writes
  only feature availability and hardware class information; it excludes proxy
  credentials, VPS endpoints, usernames, hostnames and serial numbers.

## Reporting

Use GitHub's private vulnerability reporting:

https://github.com/ECHO6789/localdeck/security/advisories/new

Do not include clipboard contents, proxy secrets, subscription URLs, or full file listings in a report. Include only `%LOCALAPPDATA%\LocalDeck\localdeck.log` after removing machine-specific paths if necessary.
