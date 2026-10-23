# SCPSL-ModLaunch

A lightweight launcher that prepares **SCP: Secret Laboratory** for modded play and restores the original game files when it closes. It runs the CLI from [our SCPSL-ModPatch fork](https://github.com/AnomalyLabsIO/SCPSL-ModPatch), using its pattern manifest, and installs **MelonLoader 0.7.3**. It uses your installed game and leaves mod selection to you.

*Based on [SCPSL-ModPatch by hopperlopip / LostFire](https://github.com/hopperlopip/SCPSL-ModPatch). Not affiliated with MelonLoader or Northwood Studios.*

## Requirements

- An installed Windows x64 copy of SCP:SL, with no conflicting mod loader.
  - Linux/Proton should also work, but is not yet confirmed.
- **Windows x64 .NET 6 Runtime**, preferably 6.0.36.
- Internet access for initial preparation and updates.

## Install and launch

For a first time install:
1. Download the latest release from [GitHub](https://github.com/AnomalyLabsIO/SCPSL-ModLaunch/releases).
2. Preferably, create a copy of your game directory for modded play.
3. Place `SCPSL-ModLaunch.exe` beside `SCPSL.exe` in the desired game directory.
4. Run `SCPSL-ModLaunch.exe`. It will check for preparation-tool updates, install MelonLoader, and prepare the game. A verified copy of the ModPatch CLI and pattern resources is bundled. The first run may take a couple minutes.
5. After the game launches, quit the game normally. The launcher restores the original game files and exits.

The launcher will leave a shortcut in the game directory named **SCPSL-ModLaunch.lnk**. Use that shortcut for future modded sessions. *SCP:SL's anti-cheat will block vanilla play if the exe is in the game root directory.*

>You can also add SCPSL-ModLaunch to Steam as a non-Steam game and set the target to `.modlaunch/SCPSL-ModLaunch.exe` with the game directory as its Start In directory.

## Native bootstrap

SCP:SL modded sessions use the bundled Windows x64 bootstrap built from stock MelonLoader 0.7.3 with upstream Dobby PR 13. The managed MelonLoader, support module and Il2CppInterop files remain stock. The standard launcher and shortcut select this bootstrap automatically; a separate fixed-backend script is unnecessary. The official MelonLoader archive remains unchanged, and persisted preparations continue to identify its stock bootstrap. See [native bootstrap qualification](docs/NATIVE_BOOTSTRAP_PR13_QUALIFICATION.md).

## Adding Mods

SCPSL-ModLaunch prepares MelonLoader 0.7.3 on its first run. You can add Melon Mods using the `Mods`, `Plugins`, `UserLibs`, and `UserData` folders now in the game directory. The launcher will load them on the next run.



## Recovery

Do not start vanilla during an active or interrupted modded session. After forced termination or power loss, run SCPSL-ModLaunch again: it restores the pending session and exits. Run it once more to start modded play.

To restore without launching, use the reserved argument:

```powershell
.\.modlaunch\SCPSL-ModLaunch.exe --modlaunch-recover-only
```

## Launch diagnostics

ModPatch's `patchinfo.json` locates the `SimpleMenu.StartLoad` patches by original bytes within their native methods. ModLaunch validates the CLI's patch outcomes and before/after file hashes before activation. The shipped `global-metadata.dat` header remains **v23** for the 14.2.7/14.2.8 builds throughout preparation and play. Il2CppDumper and Cpp2IL use a private v31 parser copy; it is never published or activated as runtime metadata. An open-ended manifest range does not itself qualify a future game build.

Patch definitions and clean launchers are resolved to the same commit in `AnomalyLabsIO/SCPSL-ModPatch`. If those resources are unavailable, the verified bundled set is used. Invalid downloaded definitions fail preparation. Prepared-cache identity includes the engine package, resource revision, original files, and MelonLoader version; caches made by the previous patch engine are rebuilt.

Routine preparation checks reuse previously computed SHA-256 values when a file's size, UTC modification time, and creation time match the saved metadata. Changed metadata triggers hashing of that file; a timestamp-only change does not force preparation. This cache has no time-based expiry. Downloads, copies, backups, restoration, and ownership checks before file deletion still receive full content verification.

The metadata is stored separately in `.modlaunch/file-verification.json` and refreshed after verified restoration, including interrupted-session recovery. Missing or invalid metadata falls back to full hashing. Size and timestamps cannot detect every same-size change with preserved timestamps; with the launcher closed, deleting only this metadata file forces routine checks to read contents again on the next invocation.

Launcher logs in `.modlaunch/logs/` include individual startup phase timings, including the upstream revision and patch-definition requests, file verification, backups, and activation. The startup total ends when the SCP:SL process starts; it does not measure time to the game menu. A verification-cache summary reports metadata reuse and full reads made through the cache; transaction checks that use the original hashing helpers are not included in those counters.

## Removing the launcher

If you need to remove the launcher from the game directory, make sure the game is not running, then delete the following files and folders:
```
.modlaunch\
MelonLoader\
Mods\
Plugins\
UserData\
UserLibs\
SCPSL-ModLaunch.lnk
```

## FAQ
- ### Q. Will I be banned for using SCPSL-ModLaunch?
  - A. No, we use SCPSL-Mod**Patch** to disable anti-cheat and online mode checks when launching the modded game. Local Modifications are allowed by Northwood EULA, as long as those criteria are met. Additional Modifications that attempt to bypass these checks or connect to online-mode servers risk permanent bans.
- ### Q. Can I update the game with Steam while using SCPSL-ModLaunch? 
  - A. Yes, though some of your mods may need to be updated to be compatible.
- ### Q. Can I Verify the integrity of the game files with Steam while using SCPSL-ModLaunch?
  - A. Yes, SCPSL-ModLaunch does not leave any modified game files after it exits and will update its internal modded files if the  verification changes the original game files.
- ### Q. Can I run SCPSL.exe vanilla safely while leaving the modded files in the game directory?
  - A. *At the moment, Yes*. ModLaunch restores original game files and deactivates MelonLoader's injection files so nothing attempts to run in vanilla play, and anti-cheat doesn't alert on them.
  I cannot guarantee this will always be the case, so if you want to be safe, use a separate copy of the game for modded play.


## Suggested mods

- Local development/debugging: [UnityExplorer.ML.IL2CPP.CoreCLR](https://github.com/yukieiji/UnityExplorer/releases/latest)
- SCP:SL Modding Framework: [Anomaly](https://github.com/AnomalyLabsIO/Anomaly/releases/latest)

## Building and updating ModPatch

`./scripts/package.ps1` tests and builds the launcher using the pinned CLI archives in `vendor/ModPatch/`. It does not need a sibling ModPatch checkout. The release output includes the CLI's exact source archive and SHA-256 sidecars.

To import a changed fork, run `./scripts/import-modpatch.ps1 -SourceRoot ../SCPSL-ModPatch`, then run the normal packaging script. Both scripts accept `-NuGetSource` for an offline package source. Review the resulting archive hashes and rerun fixture qualification before distributing a new engine. Engine changes need a rebuilt package; compatible pattern definitions can update independently.

ModPatch owns version detection, parser selection, byte-pattern matching, patch application, and the JSON result. ModLaunch's preparation helper invokes that CLI and owns Unity's displayed bundle-version correction and MelonLoader assembly generation. Session activation, backups, and restoration remain in ModLaunch. See [ModPatch provenance](vendor/ModPatch/PROVENANCE.md).
