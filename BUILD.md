# Building Roblox Account Manager

Everything here builds from the **command line** — no Visual Studio required. The only prerequisite is the .NET 8 SDK.

## Prerequisites

| Requirement | Version | Notes |
| :--- | :--- | :--- |
| OS | Windows 10 1809+ (build 17763+) or Windows 11 | WinUI 3 / Windows App SDK requirement |
| .NET SDK | **8.0.x** | Get it from https://dotnet.microsoft.com/download/dotnet/8.0 |
| NuGet | included with the SDK | First build restores packages from nuget.org — needs internet |

Check your SDK:

```bash
dotnet --version   # must print 8.0.x
```

No other tooling (Visual Studio, Windows SDK installer, WASDK runtime) is needed — the project is unpackaged and ships the Windows App SDK self-contained.

## Build (Debug)

```bash
cd src
dotnet build RAM.App/RAM.App.csproj -c Debug -p:Platform=x64
```

That single command builds the app **and** its `RAM.Core` dependency transitively.

> **Do not build the solution file:**
> ```bash
> dotnet build RAM.slnx -p:Platform=x64   # fails with MSB4126
> ```
> `RAM.slnx` only defines an `AnyCPU` platform, so passing `-p:Platform=x64` makes the build fail with `MSB4126: The specified solution configuration "x64|Debug" is invalid`. Always build the **project** file directly. (`AnyCPU` builds of the app also fail — WinUI 3 requires an explicit platform.)

### Output

```
src/RAM.App/bin/x64/Debug/net8.0-windows10.0.19041.0/
└── Roblox Account Manager.exe   <- run this
```

The output folder is **self-contained** (Windows App SDK DLLs included) — copy the whole folder anywhere and run it; no installer needed.

## Run

```bash
./RAM.App/bin/x64/Debug/net8.0-windows10.0.19041.0/"Roblox Account Manager.exe"
```

- **You get a UAC prompt — that is expected.** The app runs elevated (`requireAdministrator` in `app.manifest`) because Roblox is often installed under `Program Files`, and writing fast flags to `ClientSettings\ClientAppSettings.json` there silently fails for unelevated processes.
- On first launch the app creates `AccountData.json`, `RAMSettings.ini`, `FastFlags.json` and `crash.log` under **`%LOCALAPPDATA%\Roblox Account Manager`** (RDD installs and update downloads live there too). Files from older builds that sat next to the executable are moved there automatically once.

## Run the tests

```bash
cd src
dotnet test RAM.Core.Tests/RAM.Core.Tests.csproj -c Debug
```

Expect **95 tests, all passing** (account store & Argon2id/AES-GCM encryption, password session/verify/lock-guard helpers, settings patcher, RDD manifest parser + install, install resolver, Roblox API client, update service, data-path migration).

## Release build

```bash
cd src
dotnet build RAM.App/RAM.App.csproj -c Release -p:Platform=x64
```

Output lands in `src/RAM.App/bin/x64/Release/net8.0-windows10.0.19041.0/`.

## Installer and portable ZIP (end-user distribution)

```bash
./scripts/build-installer.sh
```

Produces `artifacts/RobloxAccountManager-Setup.exe` (single-file offline installer with a
modern WPF UI, ~115 MB) and `artifacts/RobloxAccountManager-portable.zip` (~66 MB). See the
README's *Installer & portable builds* section for how they behave.

Two gotchas the script handles automatically:

- **`dotnet publish` omits the compiled XAML** for unpackaged WinUI 3 apps (the known
  `0x802B000A`/`LoadComponent` gap): `App.xbf`, `MainWindow.xbf`, `Views\` and `Dialogs\`
  XBF folders, and the `.pri` resource index are missing from the publish output and must be
  copied from the build output — the script does this in step 1.
- **Unused WindowsAppSDK ML/AI files are excluded** from the payload (~45 MB): the
  onnxruntime/DirectML inference stack and the Vision/AI metadata stubs are dead weight for
  this app. Each batch in the exclusion list was verified against a live launch. (Keep
  `Microsoft.InteractiveExperiences.Projection.dll` — WinUI needs it at startup.)

For a plain standalone folder instead:

```bash
cd src
dotnet publish RAM.App/RAM.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -o ../dist
# then copy App.xbf, MainWindow.xbf, Views\, Dialogs\, and Roblox Account Manager.pri
# from the Release build output into ../dist (see the gotcha above)
```

## Troubleshooting

| Symptom | Cause / fix |
| :--- | :--- |
| `MSB4126: The specified solution configuration "x64\|Debug" is invalid` | You built the `.slnx`. Build `RAM.App/RAM.App.csproj` directly instead. |
| `error MSB4184` / weird XAML errors | A code-behind compile error broke the XAML compiler (`WMC9999`). Fix the C# error and rebuild — don't touch the XAML. |
| `The process cannot access ... 'RAM.Core.dll' because it is being used by another process` | An instance of the app is still running and locks the output DLLs. Close the app (Task Manager -> Roblox Account Manager) and rebuild. |
| App starts but fast flags never reach Roblox | The app must be **elevated** (see above) — check the UAC prompt wasn't dismissed. |
| `No valid Roblox installation found` | The resolver scans `%LOCALAPPDATA%\Roblox\Versions`, `%ProgramFiles%` and `%ProgramFiles(x86)%`, then falls back to the `HKCR\roblox\DefaultIcon` registry key. If Roblox is installed somewhere exotic, that is why. |
| Build succeeds but app won't start | Make sure you are on Windows 10 1809+ — the target platform minimum is 17763. |

## IDE users

Visual Studio 2022 (17.9+) with the **Windows application development workload** can open `src/RAM.slnx` directly — but the CLI commands above are the supported, canonical path and work identically on any machine.