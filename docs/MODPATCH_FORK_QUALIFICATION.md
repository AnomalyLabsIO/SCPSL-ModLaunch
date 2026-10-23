# ModPatch fork integration qualification

Date: 2026-10-18. These checks ran on Windows with the x64 .NET 6 host. No game executable was launched, and neither installed Steam game directory was modified.

## Engine and ownership

The launcher invokes `SCPSL.ModPatch.Cli.exe` built from the shared source in `AnomalyLabsIO/SCPSL-ModPatch`. Its inputs are `patchinfo.json`, a staged game directory, and the selected clean Unity launcher. ModLaunch requests `--metadata-mode preserve`, verifies the JSON report and output hashes, corrects the Unity bundle version, and performs MelonLoader assembly generation when needed. It retains ownership of activation and restoration.

The fork received reusable integration support: metadata preservation, automatic initial parser selection from Unity identity, additional parser/profile fields in the JSON report, strict manifest-field validation, and a console build requiring only the base .NET 6 runtime. The pattern manifest now retains upstream's required `CentralServer.Init` entry patch for 14.1.3 onward. The two menu patches still use byte patterns. ModLaunch's old patch engine and 14.2.8-specific offset workaround were removed.

The imported source archive includes unpublished fork changes. Its SHA-256, rather than the base commit alone, identifies the exact source used. Engine changes are imported through `scripts/import-modpatch.ps1`; patch resources can update independently from an immutable fork commit. A published adapter without the fork CLI cannot replace the bundled engine.

| Pinned artifact | SHA-256 |
| --- | --- |
| Fork CLI ZIP | `7FCFFB5A3AEBF345FB98E72DA4E3D46840E7058888B139F771104E5DD469FE9C` |
| Exact fork source ZIP | `EB061CFEF5E38DB0E583E522B9CBF0255DAD849CFB97CBA4018BA497EE24C9A0` |

The CLI's runtime configuration requests `Microsoft.NETCore.App` 6.0.0, with normal patch roll-forward; it has no Windows Desktop runtime dependency. Its inherited System.IO.Hashing 9.0.5 dependency emits a net6 support warning at build time. The packaged CLI successfully completed both real-file qualification runs under .NET 6.0.36; the warning has not been suppressed.

## Test results

ModPatch: **173 passed, 0 failed**, including its existing synthetic PE/pattern/selector tests, command failure handling, GUI startup regression check, and staged 14.2.8 CLI cases. The 14.2.7 cases also passed; the first run exposed a test-fixture junction cleanup failure, corrected before the full 173-case pass.

ModLaunch's regression suite covers transaction recovery, runtime ownership, updates, cache handling, arguments, and the fork protocol. New cases reject corrupted CLI/manifest bundles, invalid or partial reports, unexpected file edits, legacy offset-only feeds, and unsupported online manifests. They also verify fork revision pinning, bundled fallback, and rejection of a pre-fork published adapter.

A separate harness exercised the actual packaged preparation helper and CLI, then the real preparation cache and session transaction, using fresh private fixture directories. It copied six original files from each installed game and checked their hashes before and after the pass. It used cached official prerequisites, with no pre-generated managed assemblies.

| Fixture | Original GameAssembly SHA-256 | Cold preparation | Repeat | Offline repeat |
| --- | --- | ---: | ---: | ---: |
| 14.2.7 | `0CF429625B379548101ACC6B7C3F40AFB363894B84A505E503D9DD5372E04BCF` | 47.06 s | 0.46 s | 0.46 s |
| 14.2.8 | `0DEB9ECC16D48642737C779E193C3E25EE638F3789E7A5AD3FF1F51CF5F017DA` | 45.98 s | 0.45 s | 0.44 s |

These are preparation timings on this machine with cached downloads and a controlled update transport, not first-download or time-to-menu measurements. The 14.2.7 case used bundled resources; 14.2.8 used a simulated published pattern response, then a simulated outage. The update simulation is not evidence that these assets are already published on GitHub. Both repeat paths reused the identical preparation fingerprint without invoking the patcher or generator.

Each cold preparation applied all five ModPatch patches, corrected `globalgamemanagers` from 14.1.0 to the detected version, generated 80 Cpp2IL dummy assemblies and 111 interop output files (109 DLLs), and retained original v23 runtime metadata. Activation parked SL-AC and enabled the loader; restoration returned all six original files to their initial hashes, removed the active root `version.dll`, and cleared the journal. All four user directories and generated assemblies remained. Prepared payloads contain no second MelonLoader directory.

All three prepared game files were SHA-256 identical to the intact previous-engine preparations for the same original inputs:

| Fixture | GameAssembly | globalgamemanagers |
| --- | --- | --- |
| 14.2.7 | `F63DD1163E941A7DEB935022E66E667F0ADE3C87D19AB6C80F4956E2C7A983EA` | `E8611D602077FFFBB11D7A7E1F80931B4E7BC0041BB7DEC1ED41EA6D949342F4` |
| 14.2.8 | `81139A0174A58EE57057426E1765872C2C6A6855D78A0310FDA5BE865958B453` | `70DA7CC810B9C9D605ABB98DF7DFB08B0688A970ABD00943DB0CB66BD132E965` |

Both use the same clean-launcher SHA-256: `34E3F55AF5DA555873B52ECF3AA9547680BC761DCD2022E22283D90ACDCFC64C`.

Local evidence is under `artifacts/modpatch-integration-20261018/`: the harness source, fixture logs, each fixture's `qualification.json`, and `previous-output-comparison.json`. ModPatch's direct test log is `../SCPSL-ModPatch/artifacts/cli-tests-1428.log`. Release checksums identify the packaged launcher and companion. The prior 0.7.3/PR13 work and settings remain in place.

## Remaining qualification

This is staged preparation and restoration evidence, not gameplay acceptance. Real game startup with the new launcher, Steam Stop, forced termination during play, vanilla startup afterward, and fresh-prefix Proton still require live qualification for this package. Future versions remain compatibility candidates even when a manifest range says `Current`. No fork commits, releases, or pull requests were published during this integration.

## Standard manifest filename, 2026-10-19

ModLaunch now uses `patchinfo.json` for the verified CLI bundle, immutable resource download, and cached patch definitions. Older published companions lacking that file fall back to the packaged companion. A structurally valid legacy-only online feed also uses verified bundled byte-pattern definitions; malformed feeds and corrupt bundled files still fail. The public fork endpoint at commit `1786a7f4d6ff8d1c7f3e4d1aefe48484334ab718` returned an offset-only standard feed during this check, and the actual resource resolver successfully selected the verified bundle.

All **350 ModLaunch tests passed**. The refreshed CLI/source archives and full launcher package passed inventory, source-archive, manifest, checksum, and embedded-companion verification. New private 14.2.7 and 14.2.8 fixtures passed cold preparation, repeat/offline reuse, activation, and restoration, each generating 109 assemblies. Runtime metadata remained v23, all six original files were restored by SHA-256, and the three prepared game outputs matched the previously captured qualified hashes.

Evidence is in `artifacts/standard-manifest-qualification/summary.json`, its test and fixture logs, and `artifacts/patchinfo/1427` / `1428`. The 14.2.8 feed transport was simulated; the separate resource-only check used the real public endpoint. Initial fixtures under the longer qualification path reached patching but exceeded Cpp2IL's extraction-path limit; the successful reruns use shorter private paths. No game was launched, and no commits, releases, or pull requests were published.

