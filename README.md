# SCPSL-ModLaunch

A Windows launcher that prepares **SCP: Secret Laboratory** for modded play using [SCPSL-ModPatch](https://github.com/AnomalyLabsIO/SCPSL-ModPatch) and **MelonLoader 0.7.3**. It patches the game, sets up the mod loader, and restores the original game files after the game closes by default.

Modded clients disable central authentication and the external anti-cheat. Patched clients can play with the `host` command, or connecting to servers with `online_mode` disabled.


## Game compatibility

I've tested from `14.2.7` through `14.2.9`. `15.0.0` beta support is being tested and will be added later.

Other game versions supported by ModPatch may work, but have not been tested.


## How to use it:

Prerequisites: 
- A separate copy of the game folder is recommended, but the launcher does restore the original game on close.
- Windows .NET 6 Desktop Runtime (x64), version 6.0.36 or later
1. Download `SCPSL-ModLaunch-win-x64.zip` from [Releases](https://github.com/AnomalyLabsIO/SCPSL-ModLaunch/releases).
2. Extract `SCPSL-ModLaunch.exe` beside `SCPSL.exe` in the game folder.
3. Open `SCPSL-ModLaunch.exe` and select **Launch**. The launcher prepares the game and installs MelonLoader automatically. Initial preparation requires internet access and may take a few minutes.
4. Quit the game normally and let the launcher finish restoring the original files.

When closed, the executable moves into `.modlaunch` and creates two shortcuts in the game folder:

- **SCPSL ModLaunch** — launch directly through the command line.
- **SCPSL ModLaunch GUI** — open the graphical interface to manage mods and launch options.

> You can add one of the shortcuts above to Steam as a non-Steam game to launch the modded client directly.


## Adding mods:

Copy compatible MelonLoader mod DLLs into the `Mods`, `Plugins`, `UserLibs`, and `UserData` folders in your game directory.


### Command line:

You can also launch without opening the graphical interface. After installation, run this from the game folder:

```bat
".modlaunch\SCPSL-ModLaunch.exe" --modlaunch-cli
```

Game arguments are passed through to SCP:SL. The following options control the launcher:

| Option | Description |
| --- | --- |
| `--modlaunch-cli` | Use command-line mode. Supplying launch options or game arguments also selects this mode. |
| `--modlaunch-gui` | Open the graphical interface. Use alone. |
| `--modlaunch-launch` | Restore an existing session if needed, then continue preparing and launching. |
| `--modlaunch-repair` | Rebuild preparation, restoring an existing session first. |
| `--modlaunch-no-restore` | Keep modded files active after the game closes, or reuse an existing active session. |
| `--modlaunch-no-melonloader` | Patch the game without installing or preparing MelonLoader. |
| `--modlaunch-no-start` | Prepare the game without starting it. Leaves modded files active. |
| `--modlaunch-recover-only` | Restore the original files and exit. Use without other launch options or game arguments. |

The three `--modlaunch-no-*` options can be combined. Add `--modlaunch-no-start` to `--modlaunch-repair` to rebuild without launching.

If modded files were left active, `--modlaunch-cli` alone restores them and exits. Add `--modlaunch-launch` to restore and continue into a fresh modded session, or use `--modlaunch-no-restore` to reuse the active files.


## What download should I use?

- `SCPSL-ModLaunch-win-x64.zip` — the full Windows x64 download, including the launcher, documentation, and license notices.
- `SCPSL-ModLaunch.exe` — the launcher executable, uncompressed and some browsers may block downloading the file type.


## Linux Support

Linux support is experimental. Use the Windows build through Wine or Proton, with **Windows .NET 6 Desktop Runtime (x64), version 6.0.36 or later in the 6.0 series**, installed in the same prefix. Linux startup and gameplay have not yet been confirmed.


## Discord server

Join the community on [Discord](https://discord.gg/HVpmehSGBY).


## Used libraries/programs:

1. [SCPSL-ModPatch](https://github.com/AnomalyLabsIO/SCPSL-ModPatch) for game patching, based on the [original by hopperlopip / LostFire](https://github.com/hopperlopip/SCPSL-ModPatch).
2. [MelonLoader](https://github.com/LavaGang/MelonLoader) for loading mods.
3. [Il2CppDumper](https://github.com/Perfare/Il2CppDumper), [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL), and [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) for preparing game assemblies.
4. [Tomlet](https://github.com/SamboyCoding/Tomlet) for MelonLoader configuration support.

See [third-party notices](THIRD_PARTY_NOTICES.md) for component licenses and source details. SCPSL-ModLaunch is licensed under [GPL-3.0-or-later](LICENSE) and is not affiliated with Northwood Studios or MelonLoader.
