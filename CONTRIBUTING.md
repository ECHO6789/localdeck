# Contributing to LocalDeck

## Before You Start

Open an issue for changes involving:

- hardware or firmware writes
- additional proxy protocols
- elevation
- persistence or startup registration
- new network destinations
- third-party binaries
- game patches or anti-cheat interactions

Small fixes, tests, documentation, localization and capability reporting can go
directly to a pull request.

## Privacy Checklist

Never commit:

- proxy YAML, generated Hysteria2 configs or subscription URLs
- VPS/server addresses, passwords, UUIDs or authentication tokens
- `settings.json`, databases, logs or quarantine contents
- usernames, hostnames, serial numbers, MAC addresses or machine UUIDs
- unreviewed screenshots from a personal desktop
- API keys, private keys, certificates or GitHub tokens

Run a final scan:

```powershell
git diff --check
rg -n "C:\\\\Users\\\\|ghp_|github[_]pat[_]|BEGIN .*PRIVATE KEY|hysteria2://" .
```

Report secret exposure privately according to [SECURITY.md](SECURITY.md).

## Development Setup

```powershell
npm ci --prefix frontend
npm run build --prefix frontend
dotnet build tools/LocalDeck.Diagnostics/LocalDeck.Diagnostics.csproj -c Release
```

Run:

```powershell
dotnet run --project src/LocalDeck.App/LocalDeck.App.csproj
```

## Verification

At minimum run:

```powershell
npm run lint --prefix frontend
npm run build --prefix frontend
dotnet build tools/LocalDeck.Diagnostics/LocalDeck.Diagnostics.csproj -c Release
dotnet run --project tools/LocalDeck.Diagnostics/LocalDeck.Diagnostics.csproj -- --plugin-smoke
```

Hardware-specific changes need evidence for:

- exact device or platform class without exposing serial numbers
- capability detection result
- read-back behavior
- rollback or recovery behavior
- behavior when the vendor interface is missing

Never test firmware writes, arbitrary EC registers or unsupported VBIOS changes
in CI.

## Code Guidelines

- Keep Windows adapters behind lazy services.
- Validate all bridge parameters.
- Fail closed on unsupported hardware.
- Prefer local Windows APIs over a new external dependency.
- Keep the main process non-elevated.
- Put destructive actions behind an explicit user action and backup path.
- Preserve Windows 10 19041+ compatibility.
- Keep user-facing behavior accessible from keyboard controls where practical.

## Pull Requests

Include:

- problem statement
- approach and security impact
- test evidence
- screenshots only when sanitized
- known limitations

By contributing, you agree that your contribution is licensed under the
repository's MIT License.
