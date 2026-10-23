# Default native bootstrap: Dobby PR13

ModLaunch selects the qualified PR13-only Windows x64 `version.dll` automatically for modded sessions. Stock MelonLoader 0.7.3 supplies the persistent managed runtime, support module and Il2CppInterop assemblies. No managed-runtime overlay or separate fixed-backend script is required.

## Source and artifact identity

- MelonLoader: unmodified official `982ed9981ac5b0619648a2e79bcc02fd8861db33`.
- Dobby: official BepInEx base `9d271803881f467aabfc24593c7dcaeb710fe861`, merged with upstream PR13 head `490d7581cb61d960c0a185c2b70ade30a59cd377`.
- Qualified merge: `128308e1b85da55adc82159d89ff6569d293738a`; source tree equals the PR head.
- Native DLL SHA-256: `18B6DA09BB8976F0036AEAE428F21D95E2C222420A17C8B985AF4B6D59551BF0`; length 11,058,688 bytes.
- Official managed-runtime archive SHA-256: `5B2B2F3D1CD42B59EC886C5BDC2663EDAE87A0097A4F4A8F58C0965A99DDA416`.

Source pins, build instructions and upstream notices are retained in `vendor/native-bootstrap-pr13/`. The sibling `MelonLoader.NativeBackend` workspace records the reproducible native build. Its PR13-only lane preserves the broader experiment separately and applies no additional native, managed-loader or Interop patches.

## Selection and restoration

The release package embeds a normal one-entry native-bootstrap ZIP. ModLaunch validates the qualified DLL identity and caches the archive with its SHA-256. It selects that archive in memory only after ordinary preparation and stock-runtime validation. The official archive remains unchanged, and persisted preparations continue to identify its stock bootstrap. A rebuilt adapter can trigger ordinary cache regeneration before the launch-only selection. The session journal records and restores the selected bootstrap through the existing game-file transaction.

This change adds no anti-cheat concealment, encoding, detection bypass or new binary-hiding behavior. Unmodified launches rely on actual recovery of original game inputs and removal of the active root proxy. Do not start the stock game during an active or interrupted modded session; rerun ModLaunch to recover first. Existing installation/recovery behavior remains subject to its own validation and does not establish anti-cheat acceptance.

## Evidence boundary

Before this integration, the exact native DLL passed the upstream 160-case relocation test, 80 isolated shared-backend execution checks and 80 actual bootstrap checks. It also passed a captured SCP:SL 14.2.8 local-host session: Facility loaded, Class-D round started, inventory opened/closed, normal exit 0, original game files restored, and all 163 official runtime files matched.

Integration tests and deployment results are recorded in the local `artifacts/pr13-default-integration-20261018/` and `artifacts/pr13-default-deployment-20261018/` receipts. The main installation is 14.2.8; Copy is 14.2.7. Each installation requires its own runtime/restoration evidence. Tests/builds and earlier native-only results do not establish the integrated launcher's live behavior by themselves.

## Integrated launcher qualification: 2026-10-18

`./scripts/package.ps1` passed all 323 tests (20 new native-bootstrap cases) and published the framework-dependent Windows x64 launcher and bundled adapter. The ordinary native resource ZIP contains exactly the pinned version.dll, and an independent byte audit found those exact ZIP bytes once in the final launcher. The vendor DLL, source pins and upstream license/notice files are tracked as ordinary repository files, so the default packaging input does not depend on an ignored sibling build artifact.

- Released/deployed launcher SHA-256: `1DF43B296E3968C048C7659A74D2AFFC54549C9CFDD3EC9C630957F1649A0690` (6,597,405 bytes).
- Embedded native ZIP SHA-256: `D228EB92268A6BD7BD4D2D50BA6A67D4A43B2F508930947A5EAD76FF53A588D7`.
- Normal entry point in both installs: `.modlaunch/SCPSL-ModLaunch.exe`, also reached by each existing `SCPSL-ModLaunch.lnk`.

The same executable was reversibly deployed to `E:/Steam/steamapps/common/SCP Secret Laboratory` and `E:/Steam/steamapps/common/SCP Secret Laboratory - Copy`. Each old executable, ownership record and shortcut was separately backed up and SHA-256 verified. Installation used the existing pending/completed ownership contract and atomic replacement; both shortcuts retained their original bytes.

The main 14.2.8 installation ran through the normal launcher (launcher PID 10592, game PID 44872) with no fixed-backend wrapper. Native CDB capture was armed before hosting. The local host loaded Facility, force-started a Class-D round at 100 HP, opened/closed inventory and opened/closed F1 role information. CDB `.lastevent` recorded normal game exit code 0 at 18:49:25.007 UTC. No crash dump was generated. Managed socket exceptions and a missing Anomaly neutral-texture warning were observed; they did not prevent this acceptance sequence and were not changed by this work.

Fresh SHA-256 comparisons verified all 163 non-bootstrap entries from the official MelonLoader ZIP in both installs before deployment, during the main live session and after restoration. The live root version.dll and the journal's selected identity matched the qualified PR13 DLL. After exit, all six original game inputs in each installation matched their preparation, original SL-AC bytes were restored, and the root proxy plus game/overlay/update journals were absent. Neither installation has a managed-runtime overlay.

The rebuilt adapter caused normal creation of main preparation `EE937470F90DEAD3D479FA3FAE42D75A372D8DAA0168F805069B6BADC074BE43`, replacing obsolete preparation `098496E4225AE79100BEEB72053396E36D6287798D415C29F47679F3B529343D`. Its persisted bootstrap remains the official `0CE7A4E530C7F83F172A2C44AED45EEDB3BDCF06B83760F143A0FBA6885FA929`; the PR13 archive selection is not persisted there. Copy's 14.2.7 preparation remained byte-identical.

Copy passed `--modlaunch-recover-only` (PID 48600, exit code 0). Its active VR mod and hardware state were preserved; no Copy game or headset acceptance was performed. Vanilla startup with SL-AC was not tested. This bounded local-host test does not establish long-session or every-mod compatibility.

## Local evidence and rollback

- Full package/test log and embedded-resource audit: `artifacts/pr13-default-integration-20261018/`.
- Native debugger, normal-launch console, MelonLoader/Player logs: `artifacts/pr13-default-deployment-20261018/live-main/`.
- Fresh archive/input checks: `before-deployment-full-archive/`, `during-main-live/`, `after-main-restoration/` beneath the deployment evidence folder.
- Backups and deployment journal: `artifacts/pr13-default-deployment-20261018/deployment-1847/`.
- Final file/ownership/shortcut/rollback checks: `artifacts/pr13-default-deployment-20261018/final-deployment-summary.json`.

The rollback helper passed isolated deployment, failure-recovery and external-change preservation tests. Its real post-launch preconditions and backup hashes were checked read-only. To restore both previous launcher executables and ownership records with the game and launcher closed:

```powershell
& 'E:/Projects/AnomalyLabs/SCPSL-ModLaunch/artifacts/pr13-default-deployment-20261018/Restore-DefaultPr13.ps1' -DeploymentDirectory 'E:/Projects/AnomalyLabs/SCPSL-ModLaunch/artifacts/pr13-default-deployment-20261018/deployment-1847'
```

Rollback preserves changed external files and reports a conflict instead of overwriting an unknown identity. The existing shortcut bytes are not rewritten.
The older optional fixed-backend profile still references the pruned 098496 preparation. It was left unchanged and is no longer the supported entry point for this deployed default. Launcher rollback restores the previous executable and ownership; disposable preparation caches can be regenerated by the normal launcher. Reusing the old manual profile requires its preparation to be available and separately validated.
