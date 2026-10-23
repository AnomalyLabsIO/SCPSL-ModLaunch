# SCPSL-ModLaunch validation

Qualification records must identify the exact launcher and adapter hashes. Automated tests, packaged-tool checks, and live game qualification are distinct gates.

## Automated coverage

The .NET 6 test suite covers transactional restoration and interruption checkpoints, conflicting writes, locked files, concurrent locks, safe paths and archives, cache identity changes, adapter packaging, runtime ownership, argument forwarding, configuration preservation, version correction, and launcher installation/shortcut ownership. Runtime configuration also covers side-by-side .NET hosts so MelonLoader 0.7.3 cannot select an older host through its recursive scan. Launcher update tests cover missing releases/assets, corrupt downloads, release versions, interrupted atomic replacement, ownership preservation, locked files, bounded network waits, and completion after game preparation fails.

## Packaged tool checks

The release should contain `SCPSL-ModLaunch.exe`, the `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` companion, `SCPSL-ModPatch.Cli-sources.zip`, and the full `SCPSL-ModLaunch-win-x64.zip`, with SHA-256 sidecars. The full ZIP includes documentation and licenses. The bundled and standalone adapter must use the same executable identity. Native Windows checks should verify root-to-`.modlaunch` relocation, the actual `SCPSL-ModLaunch.lnk` target, installed recovery-only invocation, and preserved originals. Published tags must match the embedded executable versions; the updater consumes the standalone EXE.

## Fork CLI integration: 2026-10-18

The fork's CLI and new pattern manifest replace ModLaunch's duplicated patch engine. Staged 14.2.7 and 14.2.8 checks cover cold Cpp2IL/Interop generation, repeat/offline cache reuse, activation, and byte-identical restoration. All three prepared game outputs also match the previous engine's verified outputs for each fixture. See [MODPATCH_FORK_QUALIFICATION.md](MODPATCH_FORK_QUALIFICATION.md) for identities, results, and the gameplay qualification boundary.

## Current updater package: 2026-09-25

The package build passed **259 tests** with no failed or skipped tests. The 52 added update cases use controlled HTTP responses and staged files; they cover SHA-256 verification, missing releases/assets, bad manifests, version comparison, interrupted replacement checkpoints, locked executables, preserved game journals, and background completion after a simulated preparation failure. The workflow passed actionlint 1.7.12, and a separate `-ReleaseVersion v0.1.1` package verified version stamping.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `SCPSL-ModLaunch.exe` | 1,795,869 | `9b826d7ad013efb9a8b1a963b0bc67afaf425481fdd812027415022d0f9ffa2f` |
| `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` | 1,359,787 | `85a6c23e6af413eadcec1da04b11543ec7eb55f95ccc90abd96e605dd3101ad2` |

Native Windows checks used the actual packaged launcher in synthetic game directories. Both the root drop-in and installed `.modlaunch` paths applied a staged newer EXE and restarted it successfully. The installed shortcut retained custom arguments and its exact bytes. A checksum-valid executable with the wrong embedded version was rejected without blocking recovery-only use. Invalid settings failed preparation while the live GitHub check still completed with the expected no-release result. Evidence: `artifacts/qualification/UPD-20260925-143757-93d78a/result.json` and its per-fixture logs.

A separate native replacement probe on the same source before the final repackage verified that restart preserves seven arguments, including spaces, embedded quotes, an empty argument, a trailing backslash, `-noauth`, and `--`, and returns the replacement process's exit code 37. Evidence: `artifacts/updater-development/argument-probe/fixture-e38ba7378e764e8aba226a8151c2aa0c/result.json`.

To repeat the packaged replacement checks with two differently versioned builds:

```powershell
.\scripts\test-launcher-update.ps1 -LauncherPath <current-exe> -UpdatedLauncherPath <newer-exe>
```

The release workflow uses GitHub's [published release event](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#release) and [release asset upload](https://cli.github.com/manual/gh_release_upload). It publishes the standalone EXE and checksum expected by the updater, the companion ZIP, and a full distribution ZIP. No public release or hosted workflow execution was performed in this pass. Real hosted-asset updates and Proton replacement remain qualification gates. These updater checks did not launch the game; the exact earlier package's MelonLoader startup evidence follows separately.

## MelonLoader 0.7.3 game qualification package: 2026-09-25

`scripts/package.ps1` completed successfully: **207 tests passed**, with no failed or skipped tests. The package targets the official Windows x64 MelonLoader 0.7.3 release.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `SCPSL-ModLaunch.exe` | 1,773,853 | `675774d7d04d1eacecaaea84771cd21c6e2dd1edb996520ad340f15222f90ed1` |
| `SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip` | 1,355,104 | `63b1c501a0b9e7f8d6b587483919f8f87cf2347977c94b21ea8311708f0779d0` |

Two live Windows startup runs reached SCP:SL 14.2.7's `NewMainMenu` under MelonLoader 0.7.3. Preparation generated 80 Cpp2IL dummy assemblies and 111 interop files from the original game inputs; no seeded assemblies were required. Both the game and launcher exited with code 0 after a normal window-close request. All six recorded original files matched their starting SHA-256 hashes after both runs.

The final package additionally verified the selected system .NET 6.0.36 host, with no runtime download or fallback runtime directory. The launcher completed root-to-`.modlaunch` installation, retained its expected executable hash and ownership record, and left its root shortcut. No active `version.dll`, pending recovery journal, root launcher executable, or test process remained. User folders and generated assemblies were retained.

Exact run details, the runtime-selection correction, observed nonfatal diagnostics, and local evidence paths are recorded in [MELONLOADER_073_QUALIFICATION.md](MELONLOADER_073_QUALIFICATION.md). Earlier synthetic installation and shortcut checks remain archived under `artifacts/modlaunch-validation` and are not exact-package gameplay evidence for this build.

## Live qualification boundary

SCP:SL 14.2.7 / Unity 6000.0.43f1 is the initial game fixture. These startup runs loaded no user mods or plugins and did not join a server. They do not establish mod compatibility, gameplay, or vanilla-launch acceptance. Fresh-prefix Proton, Steam Stop, interactive gameplay, and later game builds require their own checks. See [NEXT_STEPS.md](NEXT_STEPS.md).

Wine/Proton source references for filesystem movement and shortcut dispatch are recorded in [LAUNCHER_HANDOFF.md](LAUNCHER_HANDOFF.md). Source support does not establish successful runtime operation on a Linux host.
