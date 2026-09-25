# SCPSL-ModLaunch

A lightweight launcher that prepares **SCP: Secret Laboratory** for modded play and restores the original game files when it closes. It applies [SCPSL-ModPatch](https://github.com/hopperlopip/SCPSL-ModPatch) through a headless adapter and installs **MelonLoader 0.7.3**. It uses your installed game and leaves mod selection to you.

## Requirements

- An installed Windows x64 copy of SCP:SL, with no conflicting mod loader.
- **Windows x64 .NET 6 Runtime**, preferably 6.0.36. The launcher and adapter are framework-dependent; a newer .NET major alone does not satisfy this prerequisite. .NET 6 is out of Microsoft support.
- Internet access for initial preparation. Complete validated caches can be reused offline.

## Install and launch

1. Close SCP:SL and put `SCPSL-ModLaunch.exe` beside `SCPSL.exe`.
2. Put your mods in `Mods`, then run `SCPSL-ModLaunch.exe`.
3. Leave the launcher running while you play. Initial downloads and assembly generation can take several minutes.
4. When the game closes, wait for restoration to finish. The launcher creates **SCPSL-ModLaunch.lnk** in the game directory and moves itself to **.modlaunch/SCPSL-ModLaunch.exe**.
5. Use that shortcut for future modded sessions. Use the normal Steam game entry for vanilla after cleanup completes.

All game arguments retain their order and values, including empty arguments. Nothing automatically adds `-noauth` or `-scs`:

```powershell
# Initial launch
.\SCPSL-ModLaunch.exe -noauth

# Installed launcher
.\.modlaunch\SCPSL-ModLaunch.exe -noauth
```

You can also set arguments on the generated shortcut. Its matching target and your settings are preserved on later launches. A separate non-Steam shortcut should target `.modlaunch/SCPSL-ModLaunch.exe`, with the game directory as its Start In directory. A shortcut initially targeting the root EXE needs its target updated after installation. The ordinary Steam profile is never changed.

## Recovery

Do not start vanilla during an active or interrupted modded session. After forced termination or power loss, run SCPSL-ModLaunch again: it restores the pending session and exits. Run it once more to start modded play.

To restore without launching, use the reserved argument:

```powershell
.\.modlaunch\SCPSL-ModLaunch.exe --modlaunch-recover-only
```

If initial setup stopped before relocation, use the root `SCPSL-ModLaunch.exe` instead. Successful recovery also completes shortcut setup. All arguments other than `--modlaunch-recover-only`, including a standalone `--`, are forwarded to the game.

Restoration refuses to overwrite files changed unexpectedly by Steam or another program. A failure dialog identifies the affected files and advises Steam verification. Steam may not remove extra loader files such as `version.dll`; follow the dialog before starting vanilla. Keep `.modlaunch` until recovery is resolved. Recovery can only run while the launcher is executing.

## Files and storage

The launcher stages patches before activation and keeps hash-verified originals in `.modlaunch`. It applies other changes first and replaces `SCPSL.exe` last. Cleanup disables the loader first, restores and verifies the other originals, and restores `SCPSL.exe` last. The game executable and `SCPSL_Data` directory retain their normal names.

Preparation corrects Unity's stored game version in `SCPSL_Data/globalgamemanagers` from the version detected in `GameAssembly.dll`. Parser metadata normalization operates only on temporary copies; runtime metadata remains original.

`Mods`, `Plugins`, `UserData`, and `UserLibs` stay in place. One persistent `MelonLoader` directory retains generated assemblies and tools. Its root `version.dll` entry point is removed or parked after use. Cached game payloads use neutral `.bin` names, and the official loader ZIP supplies the bootstrap. Nothing is recompressed on shutdown.

`.modlaunch` holds the installed launcher, settings, downloads, prepared payloads, original backups, ownership records, logs, and recovery state. Successful preparation retires obsolete caches; unfinished recovery preserves its evidence. User configuration is retained, with the console enabled in `UserData/Loader.cfg`. Explicit MelonLoader arguments retain their override behavior.

## Dependencies and updates

Patch resources are pinned to a recorded upstream revision and validated in staging. Unsupported profiles, missing required patches, or invalid outputs stop activation. Prepared caches depend on input hashes and dependency identities, so changed bytes require fresh preparation even when version labels match.

The launcher checks for companion releases at **AnomalyLabsIO/SCPSL-ModLaunch** and includes a fallback adapter. MelonLoader stays pinned to **0.7.3**. The installed Windows x64 .NET 6 runtime is reused for helpers and MelonLoader. The loader override points to the selected .NET 6 host directory so its runtime scan cannot pick an older side-by-side host.

SCPSL-ModLaunch also checks its own [GitHub releases](https://github.com/AnomalyLabsIO/SCPSL-ModLaunch/releases) before game preflight. A newer stable launcher is downloaded in the background, checked against its SHA-256 and Windows executable version, and installed during final cleanup for **the next launch**. The check continues if preparation or game startup fails, with a 30-second total network budget. Missing releases, missing assets, network errors, and invalid downloads are logged without blocking the current launcher.

Updates use the standalone `SCPSL-ModLaunch.exe` release asset and its `.sha256` sidecar or GitHub-provided SHA-256 digest. Temporary downloads, update state, and the previous executable stay under `.modlaunch/updates` with neutral `.bin` filenames. An interrupted update is reconciled on the next invocation; if it still needs to replace the EXE, the launcher restarts the updated copy with the same arguments. Shortcut settings and pending game recovery are preserved. Locked or unexpectedly changed files defer replacement.

Configuration lives in `.modlaunch/settings.json`:

```json
{
  "melonLoaderVersion": "0.7.3",
  "adapterRepository": "AnomalyLabsIO/SCPSL-ModLaunch"
}
```

The adapter repository publishes `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` plus its `.sha256` sidecar or GitHub-provided SHA-256 digest. If a release has no companion asset, the bundled adapter is used. That setting selects a source of executable code; use a repository you trust. Launcher updates always use `AnomalyLabsIO/SCPSL-ModLaunch`.

## Linux through Proton

Use the Windows EXE through a Proton environment containing **Windows x64 .NET 6**. Native Linux .NET does not satisfy this requirement. For a separate modded Steam shortcut, select Proton and use:

```text
WINEDLLOVERRIDES="version=n,b" %command% -noauth
```

Remove `-noauth` if unwanted. After installation, target the EXE in `.modlaunch` and preserve the prefix containing the runtime. Child game and helper processes use that same environment.

Wine implements the file and Windows shortcut APIs used by the launcher. Native Linux file-manager handling of `.lnk` files still needs the correct Proton version and prefix; SCPSL-ModLaunch does not register a handler or generate a native `.desktop` file. See [launcher installation](docs/LAUNCHER_HANDOFF.md) for the source references and [validation](docs/VALIDATION.md) for the live qualification boundary.

## Build

Use the .NET 8 SDK or newer, plus .NET 6 Runtime for tests and the published executables:

```powershell
dotnet build SCPSL-ModLaunch.sln -c Release
dotnet test tests/SCPSL.ModLaunch.Tests/SCPSL.ModLaunch.Tests.csproj -c Release
.\scripts\package.ps1
```

The package script runs tests, publishes both tools, embeds the adapter, and writes these artifacts with checksum sidecars and license notices:

```text
artifacts/release/SCPSL-ModLaunch.exe
artifacts/release/SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip
artifacts/release/SCPSL-ModLaunch-win-x64.zip
```

The full `SCPSL-ModLaunch-win-x64.zip` includes both tools, documentation, and license notices. Self-updates download the separate EXE; the GitHub Actions artifact wrapper is not an updater download format.

Pushes, pull requests, and manual workflow runs build CI artifacts. When a maintainer publishes a stable GitHub release with a numeric tag such as `v0.1.1`, the workflow builds that tag, stamps both tools with its version, and attaches the EXE, both ZIPs, and their checksum sidecars after Windows and portable core tests pass. The workflow does not create releases. Tags must increase for clients to detect a newer launcher. For equivalent local packaging, run `.\scripts\package.ps1 -ReleaseVersion v0.1.1`.

`-SkipTests` is available for packaging after tests have passed. See [third-party notices](THIRD_PARTY_NOTICES.md), [validation](docs/VALIDATION.md), and [remaining qualification](docs/NEXT_STEPS.md).
