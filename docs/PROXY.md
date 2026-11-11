# Built-in proxy

LocalDeck no longer starts, queries, or controls Clash/Mihomo.

## Local listener

- Binds only to `127.0.0.1`.
- Speaks HTTP CONNECT and SOCKS5 on the same port.
- HTTP and HTTPS clients can use `http://127.0.0.1:<port>`.
- Applications with native SOCKS5 can use `socks5://127.0.0.1:<port>`.

## Upstreams

| Type | Transport | Notes |
| --- | --- | --- |
| DIRECT | TCP | No proxy |
| ss | Shadowsocks SIP004 AEAD | AES-128/192/256-GCM, ChaCha20-IETF-Poly1305 |
| trojan | TLS | Optional certificate verification bypass from YAML |
| hysteria2 / hy2 | QUIC | Bundled Hysteria2 v2.13.0 client engine, lazily started per node |
| http | HTTP CONNECT | Optional Basic auth |
| socks5 | SOCKS5 | Optional username/password auth |

Hysteria2 is not rewritten to another protocol. LocalDeck generates a
private per-node client config, starts the bundled engine with update checks
disabled, and points it at a dynamically allocated `127.0.0.1` SOCKS5 port.
The existing routing listener still owns the user-facing HTTP/SOCKS5 port.
Stopping the LocalDeck proxy stops every Hysteria2 child process.

Supported imported Hysteria2 fields include `server`, `port`, `password`,
`sni`, `skip-cert-verify`, `obfs`/`obfs-password` for Salamander, `fingerprint`,
`up`, and `down`. Unsupported protocol-specific fields fail closed instead of
being silently reinterpreted.

## Rules

The built-in router understands:

- `DOMAIN`
- `DOMAIN-SUFFIX`
- `DOMAIN-KEYWORD`
- `IP-CIDR`
- `IP-CIDR6`
- `MATCH` / `FINAL`

Rules whose destination is a select group use the first supported node in that
group. Explicit node policies are honored. Unsupported rule types such as
`RULE-SET`, `PROCESS-NAME`, or `GEOIP` are skipped and eventually reach the final
policy.

## Migration

Importing an existing Clash-compatible YAML copies it under
`%LOCALAPPDATA%\LocalDeck\profiles\` (`active.yaml` for a manual import or
`imported-clash.yaml` for automatic migration). After that, Clash Verge can be
uninstalled without breaking the LocalDeck proxy. The imported file contains
node credentials and must stay private.
