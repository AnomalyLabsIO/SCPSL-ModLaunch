# First-run launcher relocation

SCPSL-ModLaunch starts beside the game for initial setup, then keeps its executable in `.modlaunch` and a Windows shortcut in the root. This keeps the launcher out of the root-level executable checks performed by vanilla SL-AC.

## Behavior

1. Place `SCPSL-ModLaunch.exe` beside `SCPSL.exe` and run it normally.
2. ModLaunch prepares and runs the modded session. The game executable and `SCPSL_Data` names remain unchanged.
3. After the game exits, ModLaunch disables the loader and restores/verifies all game originals, restoring `SCPSL.exe` last.
4. ModLaunch creates `SCPSL-ModLaunch.lnk`, targeting `.modlaunch/SCPSL-ModLaunch.exe` with the game directory as its working directory, then moves its own running EXE to that target.
5. Subsequent sessions start from the shortcut or the installed EXE. The launcher resolves the same game root from that specific private location.

The shortcut is created before moving the EXE, so a completed move has a launch entry ready. No updater process, scheduled task, reboot, or background watcher is needed. Successful interrupted-session recovery also finishes installation. An idle `--modlaunch-recover-only` invocation can finish setup without starting a game. Failed preparation or incomplete game restoration does not trigger relocation.

## Interruption and ownership

The game directory lock covers the handoff. An existing game recovery journal prevents relocation. A separate `.modlaunch/launcher-installation.json` record tracks the installed and pending executable identities so interrupted handoffs can be retried. If the first handoff stops before the move, rerun the root EXE; after the move, use the private EXE or shortcut.

An unknown or modified destination EXE is not overwritten. To update an established installation, place the new release EXE in the root and run it; successful cleanup replaces the previously recorded private EXE. A matching existing shortcut is preserved, including user arguments, icon settings, and Windows-maintained metadata. A shortcut pointing elsewhere is not overwritten; resolve the reported conflict before retrying. A moved game directory may require recreating its old shortcut.

Relocation failure is reported separately from successful game restoration. The message explains that a remaining root launcher EXE can still block vanilla startup and that `--modlaunch-recover-only` can retry setup. The original Steam profile is never changed.

## Proton boundary

Wine implements the Windows file and Shell Link interfaces used here. Creating the `.lnk` and moving the EXE within the same game filesystem are compatible with that architecture. Proton also routes non-EXE targets through Wine's shell, which supports `.lnk` execution.

Native Linux desktop integration is separate. A Linux file manager does not automatically know which Proton version, Steam runtime environment, or prefix belongs to a Windows shortcut. ModLaunch does not register a file handler or produce a native `.desktop` file. Launch the installed EXE through the already configured Proton environment; the `.lnk` can also be opened through Wine's shell in that environment. Retain the prefix containing Windows x64 .NET 6.

A separate modded Steam entry that still targets the initial root EXE needs a one-time target change to the installed EXE or created `.lnk`. The ordinary Steam game entry remains untouched. Online source compatibility does not establish fresh-prefix Proton or Linux file-manager qualification; those remain separate live checks.

Exact package identities and qualification results are recorded in [VALIDATION.md](VALIDATION.md).

Primary source checks, reviewed 2026-09-19:

- [Microsoft Shell Links](https://learn.microsoft.com/en-us/windows/win32/shell/links) describes shortcut targets, working directories, arguments, and persistence.
- [Valve Wine Shell Link implementation](https://github.com/ValveSoftware/wine/blob/15b9d16177695756d95285442070e0494a4d95f2/dlls/shell32/shelllink.c#L343) implements the corresponding persistence interface.
- [Wine MoveFileExW](https://github.com/ValveSoftware/wine/blob/15b9d16177695756d95285442070e0494a4d95f2/dlls/kernelbase/file.c#L2427) reaches the [server rename implementation](https://github.com/ValveSoftware/wine/blob/15b9d16177695756d95285442070e0494a4d95f2/server/fd.c#L2962), which uses the Linux filesystem rename operation.
- [Proton's launch helper](https://github.com/ValveSoftware/Proton/blob/5b89db940e0ebe3a137a6009a3589232fe084c09/steam_helper/steam.c#L299) selects shell execution for non-EXE targets; [ShellExecuteW dispatch](https://github.com/ValveSoftware/Proton/blob/5b89db940e0ebe3a137a6009a3589232fe084c09/steam_helper/steam.c#L590) enables `.lnk` handling.
- [Proton prefix selection](https://github.com/ValveSoftware/Proton/blob/5b89db940e0ebe3a137a6009a3589232fe084c09/proton#L1686) derives Wine's prefix from the selected compatibility data.
- [Wine menu integration](https://github.com/ValveSoftware/wine/blob/15b9d16177695756d95285442070e0494a4d95f2/programs/winemenubuilder/winemenubuilder.c#L2128) ignores shortcuts outside recognized desktop/start-menu locations; a game-root `.lnk` is not a native Linux desktop entry.
