# SCPSL-ModLaunch

A lightweight launcher that prepares **SCP: Secret Laboratory** for modded play and restores the original game files when it closes. It applies [SCPSL-ModPatch](https://github.com/hopperlopip/SCPSL-ModPatch) and installs **MelonLoader 0.7.3**. It uses your installed game and leaves mod selection to you.

*Not affiliated with SCPSL-ModPatch, MelonLoader, or Northwood Studios.*

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
4. Run `SCPSL-ModLaunch.exe`. It will download and install the adapter, MelonLoader, and any required runtime dependencies. The first run may take a couple minutes.
5. After the game launches, quit the game normally. The launcher restores the original game files and exits.

The launcher will leave a shortcut in the game directory named **SCPSL-ModLaunch.lnk**. Use that shortcut for future modded sessions. *SCP:SL's anti-cheat will block vanilla play if the exe is in the game root directory.*

>You can also add SCPSL-ModLaunch to Steam as a non-Steam game and set the target to `.modlaunch/SCPSL-ModLaunch.exe` with the game directory as its Start In directory.

## Adding Mods

SCPSL-ModLaunch prepares MelonLoader 0.7.3 on its first run. You can add Melon Mods using the `Mods`, `Plugins`, `UserLibs`, and `UserData` folders now in the game directory. The launcher will load them on the next run.



## Recovery

Do not start vanilla during an active or interrupted modded session. After forced termination or power loss, run SCPSL-ModLaunch again: it restores the pending session and exits. Run it once more to start modded play.

To restore without launching, use the reserved argument:

```powershell
.\.modlaunch\SCPSL-ModLaunch.exe --modlaunch-recover-only
```

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
