# OpenUsage on Windows

The macOS menu-bar **app** is Apple-only (SwiftUI/AppKit/Sparkle) and does **not** run on Windows.
What runs on Windows is the **`openusage` CLI** — the same one-shot usage reader documented in
[docs/cli.md](docs/cli.md), built on the same provider/refresh/cache core as the app.

## Build

Requires the [Swift for Windows](https://www.swift.org/install/windows/) toolchain (built with 6.3.3).

```sh
swift build -c release --product openusage-cli
```

`Package.swift` branches on `#if os(Windows)`: it drops the GUI targets and the Apple-only
dependencies (Sparkle, KeyboardShortcuts, PostHog) and adds
[swift-crypto](https://github.com/apple/swift-crypto) as the CryptoKit substitute. The macOS build is
untouched.

The binary lands at `.build/release/openusage-cli.exe`. A copy is installed as
`%LOCALAPPDATA%\OpenUsage\bin\openusage.exe`, which is on the user `PATH`, so from a **new** terminal:

```sh
openusage            # all enabled providers, JSON to stdout (5-min shared cache)
openusage claude     # one provider or family
openusage --force    # bypass the cache freshness window
```

Output, exit codes (`0` ok, `2` bad usage, `4` warnings), and the cache are identical to the macOS
CLI. The cache lives in `%LOCALAPPDATA%\OpenUsage`.

## Provider support on Windows

Credentials are read from the same local sources the app uses, resolved to their Windows paths:

- **Claude** — works (reads the local Claude CLI/OAuth credentials). ✅ verified
- **Codex / Cursor / Copilot / Devin / Grok / OpenCode** — work when that tool is signed in on this
  machine; otherwise they report a clear "not logged in" message.
- **OpenRouter / Z.ai** — work via API key (`OPENROUTER_API_KEY` / `ZAI_API_KEY`, or
  `~/.config/openusage/<provider>.json`).
- **Antigravity** and the **Claude Desktop** credential source read the macOS **Keychain**, which has
  no Windows equivalent — those specific sources are unavailable and degrade to a clear message.

## Known Windows limitations (vs macOS)

- No menu-bar UI, global shortcut, notifications, or Sparkle auto-update (GUI-only features).
- No local HTTP API server (`/v1/limits`) — it used Apple's Network.framework; the CLI covers the
  same data.
- Proxy config (`~/.openusage/config.json`) is parsed but **not applied** (Network.framework only).
- Anonymous PostHog telemetry is inert (SDK not built on Windows).
- Written files use Windows ACLs (per-user profile scoping) rather than POSIX `0600`.
