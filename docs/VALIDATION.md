# SCPSL-ModLaunch validation

Qualification records must identify the exact launcher and adapter hashes. Automated tests, packaged-tool checks, and live game qualification are distinct gates.

## Automated coverage

The .NET 6 test suite covers transactional restoration and interruption checkpoints, conflicting writes, locked files, concurrent locks, safe paths and archives, cache identity changes, adapter packaging, runtime ownership, argument forwarding, configuration preservation, version correction, and launcher installation/shortcut ownership.

## Packaged tool checks

The release should contain `SCPSL-ModLaunch.exe`, the `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` companion, SHA-256 sidecars, documentation, and licenses. The bundled and standalone adapter must use the same executable identity. Native Windows checks should verify root-to-`.modlaunch` relocation, the actual `SCPSL-ModLaunch.lnk` target, installed recovery-only invocation, and preserved originals.

## Current package: 2026-09-25

`scripts/package.ps1` completed successfully: **206 tests passed**, with no failed or skipped tests. All four solution projects built under their current names.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `SCPSL-ModLaunch.exe` | 1,773,853 | `5084a883153b2f47d27b01b98d31379d6bc36067769f220edd2698114b931aa7` |
| `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` | 1,354,879 | `79db7565bff6ff16a08a72f2eeb3ea913a6296a8de4dabbdf18f496ac824e7aa` |

The packaged launcher passed a native Windows smoke using a synthetic game directory. Its root invocation with `--modlaunch-recover-only` exited successfully, created `SCPSL-ModLaunch.lnk`, and moved into `.modlaunch`. The installed executable retained the release hash. The shortcut targeted that executable with the game directory as its working directory and no stored arguments. Running the actual shortcut with the recovery-only argument also exited successfully. The original sentinel file remained byte-identical, and no recovery journal remained. No game was executed.

The standalone adapter ZIP contained the expected companion executable. Invoking it without arguments reached its usage validation and returned the expected exit code 1, confirming that the packaged adapter loaded successfully.

Local evidence is retained under `artifacts/modlaunch-validation`: `package.log`, `smoke.json`, `root-smoke.log`, and `adapter-smoke.log`.

## Live qualification boundary

SCP:SL 14.2.7 / Unity 6000.0.43f1 is the initial game fixture. A packaged-tool smoke using synthetic files does not establish modded gameplay or vanilla acceptance. Fresh-prefix Proton, Steam Stop, interactive gameplay, later game builds, and MelonLoader 0.7.3 require their own checks. See [NEXT_STEPS.md](NEXT_STEPS.md).

Wine/Proton source references for filesystem movement and shortcut dispatch are recorded in [LAUNCHER_HANDOFF.md](LAUNCHER_HANDOFF.md). Source support does not establish successful runtime operation on a Linux host.
