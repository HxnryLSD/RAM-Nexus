# Contributing to RAM-Nexus

## Build

Everything builds from the CLI — see [BUILD.md](BUILD.md). Short version:

```bash
cd src
dotnet build RAM.App/RAM.App.csproj -c Debug -p:Platform=x64   # never the .slnx
dotnet test RAM.Core.Tests/RAM.Core.Tests.csproj -c Debug
```

## Ground rules

- **`RAM.Core` stays UI-free.** Roblox/domain logic goes in `RAM.Core`, testable in isolation; `RAM.App` is a thin WinUI 3 shell over it.
- **The patcher doesn't locate installs** — callers pass the version folder in.
- **Account file format stays upstream-compatible** (ic3w0lf22 DPAPI layout, JSON shape, plaintext marker file).
- Fast flags are **curated allow-list only** — don't add flags that can trigger anti-cheat.

## Before opening a PR

1. `dotnet build` — zero warnings expected, zero errors required.
2. `dotnet test` — all 125 tests green; add tests for new `RAM.Core` behavior.
3. One logical change per PR. UI changes: include a screenshot/GIF.

## Reporting bugs

Use the issue templates. Always attach `%LOCALAPPDATA%\Roblox Account Manager\crash.log`.
