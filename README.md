# Roblox Account Manager (WinUI 3 rewrite)

A from-scratch [WinUI 3](https://learn.microsoft.com/windows/apps/winui/) rewrite of
[ic3w0lf22/Roblox-Account-Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager) — the
tool that keeps all your Roblox accounts in one place and lets you launch them with one click.

This fork throws away the old WinForms + CefSharp stack and rebuilds the app as a decoupled
**`RAM.Core` domain core** under a thin **WinUI 3 shell**, with fast flags management,
an FPS unlocker, and RDD-style (Roblox Deployment Downloader) install support.

> **Status: early but working.** The shell, account storage, settings, fast flags, FPS
> unlock and RDD install/download page are implemented, with fast flags and the FPS
> unlock applied to RDD installs only. The long-tail feature ports from upstream (server
> list, avatar viewer, Nexus remote control, multi-Roblox, …) are still on the roadmap —
> see [Roadmap](#roadmap).

---

## Features

| Feature | Description |
| :--- | :--- |
| **Account management** | Add accounts by cookie, username + password, or bulk paste (usernames auto-fetched); search, group, and Bitwarden-style vault fields (alias, notes, stored password); encrypted backup/restore — all persisted to `AccountData.json`. |
| **Account encryption** | Upstream-compatible DPAPI (LocalMachine) encryption by default; optional AES-256-GCM password lock with an Argon2id-derived key (OWASP-recommended memory-hard KDF, versioned format; legacy PBKDF2 files still decrypt); plaintext opt-out via the `NoEncryption.IUnderstandTheRisks.iautamor` marker file. Existing upstream account files load as-is. Set / change / remove the master password from **Settings → Security**; a password-locked file prompts to unlock at startup, stays locked for every save that session, and **Lock now** re-locks it (clearing the in-memory accounts) without restarting — saves are refused until it's unlocked again. Bitwarden-style **auto-lock** (Settings → Security → Auto-lock) re-locks it after a configurable inactivity timeout and/or immediately on minimize, re-prompting for the password when the window is restored. |
| **FPS unlock** | Writes `DFIntTaskSchedulerTargetFps` (30–1000, default 240) into the active RDD install's `ClientAppSettings.json`. |
| **Fast Flags** | Curated catalog (Geometry / Rendering / User Interface) with typed editors — booleans and integers with min/max/step/suggested values — written as proper JSON types (`true`, `400`) into the **active RDD install's** `ClientAppSettings.json`. |
| **Clean settings patching** | Merges RAM changes into `ClientAppSettings.json` without clobbering Roblox's own keys; strips deactivated RAM-managed flags; deletes the file entirely when nothing is active (install returns to stock defaults). |
| **RDD install page** | One-click deployment download straight from Roblox's CDN: pick an exploit (fed by the WEAO API) or `Default` for the latest build, watch a determinate progress bar, cancel mid-download, and manage installed deployments from the list — **re-download** to repair an install, or **delete** it (with confirmation). |
| **RDD installs** | Downloads full Roblox deployments straight from Roblox's CDN (`setup.rbxcdn.com`) using the official `rbxPkgManifest.txt`, tagged with the selected exploit (or `Default`) via a `.ram-tag` file. Streaming extraction (no temp zip residue), unique per-run temp dirs (no concurrent-install collisions), **bounded parallel downloads** (up to 4 manifest files at once by default — the RDD page's *Parallel downloads* toggle mirrors the reference page's checkbox), **atomic staged installs** (everything downloads into a hidden staging folder and is renamed into place only once verified — a failed or cancelled install can never damage the previous copy, and force re-downloads swap the old folder out instead of deleting it first), and **one install per tag** — installing a newer version under an existing tag supersedes the old folder. One install is **active** at a time (marked on the RDD page, or picked by the Fast Flags page) — Accounts Launch and the Fast Flags page both target it. Orchestration lives in a testable `InstallManager` in `RAM.Core`. Exploit/version data from the WEAO API (`weao.gg`), **cached with a 5-minute TTL and stale-while-revalidate** — page loads are served instantly from the last-known-good copy and stay populated offline (the Refresh button forces a fresh fetch). |
| **Self-maintaining Default client** | The Default install keeps itself on the latest live build: the app checks Roblox's channel API at launch and hourly, downloads a newer build under the Default tag (the old folder is superseded only once the new one is fully in place), and **re-applies fast flags + FPS unlock** to the fresh install. Opt out anytime in Settings → Roblox → *Auto-update Default client*. Manual RDD downloads and background updates are serialized so they can never race. |
| **Auto-update** | Check for a newer release (the repo's own releases API by default — the release workflow bakes it into the build — URL adjustable in settings), download it with byte-level progress and cancel, then **restart & install**: the update is first extracted into a sibling `app-<version>` folder (the live app is never touched mid-extract), and a detached helper then swaps that folder into place once the app exits — rename-based, so files removed in the new build actually go away — falling back to extract-over-the-folder if the swap can't run. |
| **Roblox Web API client** | Cookie-authenticated client: X-CSRF + `rbx-authentication-ticket` (what the Accounts page Launch hands to the player). |
| **Launcher** | Starts a place (optionally with a JobId) through the resolved install's `RobloxPlayerLauncher.exe` / `RobloxPlayerBeta.exe`, or just starts the player itself (RDD page Play button). **Accounts page Launch** fetches an `rbx-authentication-ticket` with the selected account's `.ROBLOSECURITY` cookie and hands it to the player (`--app -t -j`) so Roblox runs as that account — rejoins the last-used place in one click, or prompts for a Place ID / Job ID. |
| **Mica / Acrylic backdrop** | Frosted-glass window material (Mica, Mica Alt, Acrylic or None) with a transparency toggle, applied live from the Settings tab. |
| **Custom title bar** | `OverlappedPresenter` shell with a custom drag region and app identity; caption buttons (minimize / maximize / close) are drawn by the OS. |

### Not ported yet (upstream parity, deferred)

Server list / load region, games & favorites browsing, avatar & outfit viewers, account
control (Nexus / websockets), multi-Roblox, automatic cookie refresh, quick log-in, themes,
player finder, close-Roblox-beta watcher, local web API. See [Roadmap](#roadmap).

---

## Architecture

```
src/
├── RAM.Core          # Domain core — no UI, no WinUI dependency
│   ├── Models        # Account (upstream-compatible serialization)
│   ├── Infrastructure# AccountStore (encryption modes), IniFile, SettingsStore
│   ├── Security      # Cryptography: DPAPI + AES-256-GCM password mode
│   └── Roblox        # Launcher, ApiClient, ClientSettingsPatcher
│       ├── FastFlags # Catalog + typed store
│       └── Rdd       # Deployment service, manifest parser, WEAO API client
├── RAM.App           # WinUI 3 shell (NavigationView, custom title bar)
│   ├── Views         # Accounts, Settings, Fast Flags, About
│   └── Dialogs       # AddAccountDialog, UnlockDialog, PasswordDialog (password lock flow)
└── RAM.Core.Tests    # xUnit — 95 tests (store, patcher, RDD, API, launcher, paths)
```

Design rules:

- **`RAM.Core` never touches the UI** — every Roblox interaction is testable in isolation.
- **Settings are typed** through `SettingsStore` (`Get<T>(key, default)` / `Set`) backed by a
  plain `RAMSettings.ini`.
- **The patcher doesn't locate the install** — callers pass the version folder in, so the same
  code works against the default install and any RDD-tagged install.
- **Account file format is upstream-compatible** — DPAPI byte layout, JSON shape and the
  `NoEncryption.IUnderstandTheRisks.iautamor` marker all match upstream.

## Tech stack

| | |
| :--- | :--- |
| UI | WinUI 3 (Windows App SDK **1.8.260710003**) |
| Framework | .NET 8 (`net8.0-windows10.0.19041.0`, min platform `10.0.17763.0`) |
| Packaging | Unpackaged (`WindowsPackageType=None`), self-contained WASDK — no runtime install needed |
| Platform | x64 only |
| JSON | Newtonsoft.Json 13.0.3 |
| Tests | xUnit 2.9.3 (125 tests, all green) |

## Building

See **[BUILD.md](BUILD.md)** — everything builds from the command line, no Visual Studio
required:

```bash
cd src
dotnet build RAM.App/RAM.App.csproj -c Debug -p:Platform=x64
```

## Installer & portable builds

Ready-made artifacts live in `artifacts/` after running the build script:

| Artifact | What it is |
| :--- | :--- |
| `RobloxAccountManager-Setup.exe` | **Single-file offline installer** (~115 MB). Double-click → pick *Install for all users* (Program Files + Start Menu shortcut + uninstall entry in Settings → Apps) or *Portable* (extract to a folder, nothing system-wide). Everything is bundled — no internet, no .NET install, no other dependencies. |
| `RobloxAccountManager-portable.zip` | **Portable ZIP** (~66 MB, deflate) — unzip anywhere and run `Roblox Account Manager.exe`; all data still lives in `%LOCALAPPDATA%\Roblox Account Manager`, so the folder can be moved or deleted freely. |
| `RobloxAccountManager-update.zip` | **In-app update ZIP** — the About page's auto-update downloads this (root-level layout, swap-able into the app folder) from the GitHub release the pipeline published. |

Details:

- **Modern UI**: the installer is a dark, Win11-style WPF app (rounded corners, Mica-style
  dark chrome) — a custom .NET bootstrapper, not a legacy wizard.
- **Low-footprint decompression**: the app payload is stored as per-file **Brotli** streams
  (quality 11, 22-bit window ≈ 4 MB of working memory) — extracting is fast and runs fine
  on low-end machines. The ZIP uses plain deflate for the same reason.
- **Machine installs self-elevate**: one UAC prompt when writing to Program Files; portable
  installs never need it.
- **Uninstall**: Settings → Apps → Roblox Account Manager, or `Roblox Account Manager.exe --uninstall`
  — removes the shortcut, the uninstall entry, and the install folder (your accounts in
  AppData are never touched).
- **App icon**: both binaries carry a proper multi-resolution `.ico` (blue squircle
  tile + white padlock, 16–256 px) from `src/RAM.App/Assets/ram.ico`, generated by
  `src/RAM.IconGen` (pure BCL, SDF-rendered — rerun it to re-derive the icon).
- **Code signing**: the build signs both binaries with an Authenticode signature (a
  self-signed cert created once and reused, or a real cert via `CERT_PFX`/`CERT_PASSWORD`).
  A self-signed signature verifies integrity but does **not** clear SmartScreen — only a
  cert from a trusted CA does; the first builds with a real cert still show "More info →
  Run anyway" until the file builds reputation.

Build them with:

```bash
./scripts/build-installer.sh   # publishes the app, packs the payload, builds all three artifacts
```

The same pipeline runs as a GitHub Actions workflow (`.github/workflows/release.yml`): push a
`v*` tag (or run the workflow manually) and the installer, portable zip and update zip are
built on Windows and attached to a release. The workflow bakes the repo's own releases API
URL into the build (`UpdateManifestUrl` MSBuild property → `UpdateService.DefaultManifestUrl`),
so the About page's update check always points at the release this workflow publishes. A
separate `ci.yml` job builds the app and runs the full test suite on every push / PR, so
regressions fail before a release is ever cut.

## Data files

All app data lives in **one folder under the user's LocalAppData** —
`%LOCALAPPDATA%\Roblox Account Manager` — so it survives reinstalls, folder moves and
per-machine installs. On first launch after this change, files that lived next to the old
executable are **moved into the data root automatically** (never overwriting anything).

| File | Purpose |
| :--- | :--- |
| `AccountData.json` | Encrypted account list (`.backup` = hourly-rotated previous copy, `.bak` = pre-corruption rescue copy) |
| `RAMSettings.ini` | Settings: `UnlockFPS`, `MaxFPSValue`, `BackdropEnabled`, `BackdropMode`, `BackdropTransparency`, `AutoLockEnabled`, `AutoLockTimeoutMinutes`, `AutoLockOnMinimize`, `AutoLockOnIdle`, `RDDInstallRoot`, `RDDActiveInstall`, `ClientAutoUpdate`, `RDDParallelDownloads`, `UpdateManifestUrl` |
| `FastFlags.json` | Activated fast flags (name → value, plain JSON) |
| `crash.log` | Unhandled-exception log (appends timestamped type / message / stack; rotates past ~1 MB, keeping `crash.log.old`) |
| `RDD\` | Downloaded Roblox deployments (the RDD page's default install root; changeable in the RDD page) |
| `updates\` | Downloaded update zips waiting to be installed |
| `NoEncryption.IUnderstandTheRisks.iautamor` | Marker file that disables encryption (opt-in, at your own risk) |
| `<Roblox version>\ClientSettings\ClientAppSettings.json` | What the patcher writes into the *install* (FPS + fast flags) |
| `<Roblox version>\.ram-tag` | RDD tag on downloaded installs (exploit name or `Default`) |

> ⚠️ **Never share `AccountData.json`** with anyone. It is encrypted with your machine as the
> key (DPAPI LocalMachine) — copying it to another PC makes it undecryptable (same as upstream).

## Vault backup (export / import)

The **Vault** button on the Accounts page backs up and restores the whole account list as a
portable, password-encrypted JSON file — move vaults between PCs, or keep an off-site copy.
Everything is included: usernames, cookies, groups, aliases, notes, and stored passwords.

### File format

| Shape | What it is |
| :--- | :--- |
| `*.ramvault` | `RAMHeader` + AES-256-GCM payload, key derived from the backup password with Argon2id — the same on-disk layout as a password-locked `AccountData.json` (v2; legacy v1 password files also import). The plaintext inside is ordinary account JSON, so the format stays upstream-compatible. |
| `AccountData.json` (DPAPI, same PC) | Decrypted with this PC's DPAPI key — no password asked. |
| `AccountData.json` (DPAPI, another PC) | **Cannot** be decrypted — DPAPI is machine-scoped. Import shows a clear message instead of failing silently. |
| `AccountData.json` (plaintext, no-encryption marker) | Read as-is — no password asked. |

Backups are self-identifying: the RAMHeader tells the importer a file is encrypted, so the
password prompt only appears for files that actually need one.

### Password requirements

- At least **4 characters** (the same rule as the master password).
- **No recovery.** The backup password is the only key to the file — lose it and the backup is
  gone. Keep it in a password manager.
- The backup password is independent of the vault's master password: exporting from a DPAPI- or
  plaintext-protected vault still produces an encrypted `.ramvault`.

### Export workflow

1. Accounts → **Vault → Export encrypted backup…**.
2. Pick where to save (`ram-accounts-vault.ramvault` is suggested).
3. Enter a backup password twice and confirm.

### Restore workflow

1. Accounts → **Vault → Import from backup…** and pick the file.
2. Only `.ramvault` files ask for the password — DPAPI / plaintext `AccountData.json` files
   import directly.
3. Imported accounts are **merged, not replaced**: accounts whose cookie is already in the
   vault are skipped (case-insensitive), accounts without a cookie are skipped, and everything
   else is appended before the merged vault is saved. Importing the same file twice is a no-op.

> ⚠️ A backup contains live `.ROBLOSECURITY` cookies — treat it like the account file itself.
> Never share it, and delete it once you no longer need it.

## Roadmap

1. ✅ Shell + core (Phases 0–3): restructure, `RAM.Core` foundation, WinUI 3 shell
2. ✅ Launcher + client settings patcher, RDD-aware (Phase 4)
3. ✅ Fast flags UI, FPS unlock, backdrop material, RDD install/download page (install manager, re-download/delete, auto-update machinery)
4. ✅ Release pipeline + auto-update: `release.yml` builds and publishes releases on `v*` tags; the app checks the repo it was built from and installs updates via a staged, swap-based apply
5. ⬜ Feature ports (Phase 5+): server list, games, avatar/outfit viewers, account utilities
6. ⬜ Nexus / remote account control + local web API (deferred, Phase 6+)
7. ⬜ Automatic cookie refresh, multi-Roblox, quick log-in

## Safety notes

- The app **runs elevated** (UAC prompt at launch) because Roblox is often installed under
  `Program Files`, where writing `ClientAppSettings.json` silently fails for unelevated
  processes.
- Fast flags are restricted to a curated allow list — flags outside it can trigger Roblox
  anti-exploit detection. Changing them is at your own risk.
- RDD installs and exploit-tagged versions exist for testing/legacy compat; exploits are
  third-party software and this project is not affiliated with any of them.

## Credits

- Upstream: [ic3w0lf22/Roblox-Account-Manager](https://github.com/ic3w0lf22/Roblox-Account-Manager)
- RDD idea & exploit data: [vnnaworks/rdd](https://github.com/vnnaworks/rdd) and
  [weao.gg](https://weao.gg)