# Open-Source Readiness Audit

Date: 2026-11-11

## Scope

The audit covered:

- current tracked files
- all Git commits in the local repository
- Markdown links and repository documents
- release staging contents
- proxy, credential, personal-path and machine-identity patterns
- external runtime and binary dependencies

## Result Summary

| Area | Result |
| --- | --- |
| GitHub tokens / PATs | None found |
| API keys / private keys | None found |
| Proxy YAML / subscription files | Not tracked |
| VPS address, node password or UUID | None found |
| Release staging data directory | No settings, proxy profile, database or generated HY2 config |
| Broken relative Markdown links | None at audit time |
| Current-tree absolute Windows user paths | Sanitized before public release |
| Historical absolute Windows user paths | Present in older documentation commits; history must be handled before public visibility |
| VPS details in Git or release staging | None found |

The `sk-` pattern produced only false positives in words such as `disk-`; no
matching secret was found.

## Privacy Finding: Historical Documents

Earlier commits contain a Windows user profile path in verification and
outbound-audit documents. They also contain a locally assigned proxy node label,
but no VPS address, YAML, password, subscription URL or generated client config.

The current files have been sanitized. Git history has not been rewritten.

Before making the existing repository public, choose one of these options:

1. rewrite history and force-push after a backup;
2. create a new clean public repository from the sanitized current tree;
3. accept that the historical local path and node label will be public.

LocalDeck's automated recommendation is option 2 unless preserving the current
commit graph is required.

Create a clean, history-free snapshot after committing the current tree:

```powershell
pwsh -NoProfile -File scripts/create-public-snapshot.ps1
```

The command writes to `artifacts/localdeck-public-snapshot`, initializes a new
repository, and creates one clean commit. It does not push or change repository
visibility.

## Package Audit

The release staging directory was inspected for:

- `settings.json`
- `active.yaml`
- `imported-clash.yaml`
- generated Hysteria2 YAML
- databases and logs
- certificates and private keys

None of these files are copied by the publish script. `scripts/publish.ps1`
now fails if a known local-state file, personal path or key/token pattern is
present in the staging output.

## Self-Contained Runtime

The Windows x64 release contains:

- LocalDeck WPF/WebView2 host
- .NET runtime
- bundled Hysteria2 client engine
- licenses and documentation

External platform or optional components:

- WebView2 Runtime
- Windows vendor drivers where a hardware adapter requires them
- user-selected llama.cpp engine and GGUF models
- user-downloaded DLSS Unlocked package

These dependencies are documented and are not silently fetched by the core at
startup.

## Network Boundary

No repository file contains a usable proxy endpoint or subscription. The local
proxy listener is fixed to loopback in documentation and source. User-imported
YAML and generated Hysteria2 configs remain under the local application data
directory.

The frontend has no CDN fonts, analytics scripts, remote images or external
JavaScript bundles. Runtime cloud-model buttons open third-party websites only
after user action.

## Attribution

Relevant upstream licenses are recorded in
[THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md), including:

- Hysteria2
- Windows GPU Unlock
- DLSS Unlocked
- NuGet and npm dependencies

## Repository Status

The repository is still private at the time of this audit. No visibility change
was performed automatically.

Current-tree README and project documentation are ready for a public snapshot.
The historical-path decision above must be made before toggling repository
visibility.
