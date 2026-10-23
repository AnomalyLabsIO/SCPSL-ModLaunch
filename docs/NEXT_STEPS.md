# SCPSL-ModLaunch qualification checklist

SCPSL-ModLaunch targets Windows x64 .NET 6, SCP:SL 14.2.7 qualification, and MelonLoader 0.7.3. Later game builds require their own compatibility checks. An upstream open-ended version range is not proof of support.

Implemented behavior:

- Staged patching, game-version correction, hash-verified originals, durable recovery, and refusal to overwrite unexpected changes.
- `.modlaunch` storage, one persistent loader runtime, neutral prepared payloads, retained generated assemblies and user folders, and bounded cache cleanup.
- Exact game argument forwarding; only `--modlaunch-recover-only` is reserved.
- Executable-last game activation/restoration, followed by tool relocation and `SCPSL-ModLaunch.lnk` creation.
- Installed Windows .NET 6 discovery with child-only runtime settings.
- Early background launcher release checks, verified downloads, and recoverable replacement for the next launch, including failed game startup.

Before release, qualify the exact packaged artifacts and record original/restored hashes:

- [ ] Empty-cache game startup with mod initialization and visible console.
- [ ] Repeat and offline launch without unnecessary regeneration.
- [ ] Actual game crash, forced launcher termination, and successful recovery before vanilla.
- [ ] Vanilla startup, ordinary menu interaction, and clean exit after restoration.
- [ ] Actual non-Steam shortcut, argument handling, and Steam Stop.
- [ ] Fresh-prefix Proton with Windows .NET 6, shortcut handling, normal exit, and interrupted recovery.
- [ ] A second game build and a new upstream patch revision, including unchanged version labels with different file contents.
- [ ] Physical disk exhaustion and actual updater/file-lock conflicts on disposable copies.
- [ ] Download from an actual published launcher release and qualify replacement under a fresh Proton prefix. Mock release responses and native Windows update tests do not substitute for the hosted release gate.
- [x] MelonLoader 0.7.3 cold generation, Windows menu startup, and normal-exit restoration on the initial fixture. See [the startup qualification](MELONLOADER_073_QUALIFICATION.md); the broader platform and gameplay checks above remain outstanding.

Automated coverage and packaged-tool checks are recorded in [VALIDATION.md](VALIDATION.md). Windows results cannot substitute for Proton qualification. The game keeps its original `SCPSL.exe` and `SCPSL_Data` path names.
