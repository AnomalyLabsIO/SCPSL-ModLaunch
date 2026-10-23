# Native bootstrap: upstream Dobby PR13

This directory identifies the native `version.dll` embedded by the standard launcher package. It is the stock MelonLoader 0.7.3 bootstrap linked with the unchanged upstream BepInEx/Dobby PR13 source tree. `provenance.json` pins the exact 11,058,688-byte DLL, source commits/trees, static library hash, build route and bounded qualification evidence. No managed MelonLoader or Il2CppInterop replacement is part of this payload.

Run the sibling `MelonLoader.NativeBackend/Initialize-Repositories.ps1` to reconstruct the pinned upstream repositories, then build the Dobby PR13 library and stock bootstrap using that workspace's documented route and `source-lock.json`. The launcher packaging step consumes the qualified binary and verifies its exact identity; it does not silently rebuild or replace it with an official bootstrap.

From a complete launcher checkout, packaging uses the tracked ordinary binary in this directory and needs no sibling build artifacts:

```powershell
./scripts/package.ps1
```

To select an independently stored exact copy of the same qualified binary:

```powershell
./scripts/package.ps1 -NativeBootstrapPath ../MelonLoader.NativeBackend/artifacts/pr13-bootstrap-20261018/qualified/version.dll
```

Both the default `vendor/native-bootstrap-pr13/version.dll` input and the override must match the hard-pinned hash and length. The package constructs a normal one-entry `native-bootstrap-pr13.zip` with fixed ZIP timestamp and optimal compression, embeds it under that resource name, and embeds this provenance separately as `native-bootstrap-pr13.provenance.json`. Missing or mismatched inputs fail packaging. The single-file launcher uses the ordinary .NET bundle format with compression disabled; no encoding, hiding or concealment layer is added.

The release includes this directory's provenance, README, licenses and notices under `third-party-licenses/native-bootstrap-pr13/`, and the embedded adapter retains those same documents for standalone EXE distribution. The vendor DLL is packaged only through the separate native bootstrap ZIP resource; it is not copied into the adapter or notices directories. See [the integration qualification note](../../docs/NATIVE_BOOTSTRAP_PR13_QUALIFICATION.md) for live acceptance evidence and limits. The upstream 160 emitted-address checks and isolated 80-child execution suites do not establish every instruction, game, concurrent-hook or failure path.

MelonLoader and Dobby are Apache-2.0. Their unchanged license and applicable notice files are retained here. Upstream source URLs and exact revisions are listed in the machine-readable provenance.
