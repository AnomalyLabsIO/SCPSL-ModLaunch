# MelonLoader 0.7.3 startup qualification

On 2026-09-25, two controlled Windows runs in `E:\Steam\steamapps\common\SCP Secret Laboratory - Copy` reached the main menu without a startup crash. The original Steam installation was not modified.

## Inputs

- Game: SCP:SL 14.2.7; Unity 6000.0.43f1.
- Loader: official [MelonLoader v0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3), Windows x64 ZIP, 20,155,622 bytes.
- Loader archive SHA-256: `5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416`, verified against the GitHub release asset digest and the downloaded file.
- ModPatch resources: revision `1786a7f4d6ff8d1c7f3e4d1aefe48484334ab718`; six patch results succeeded.
- Arguments: `-noauth -logFile <per-run evidence path>`. No user mods, plugins, or user libraries were installed.
- The initial Copy fixture had no MelonLoader directory or ModLaunch preparation cache. Its existing `Loader.cfg` was saved before testing.

## Startup and restoration

The headless adapter generated 80 Cpp2IL dummy assemblies and 111 interop files. It used parser-normalized metadata while leaving the runtime metadata unchanged. The adapter staged three modified outputs: `SCPSL.exe`, `GameAssembly.dll`, and `SCPSL_Data/globalgamemanagers`. The stored game version was corrected to 14.2.7.

Both runs reported MelonLoader 0.7.3, loaded the Il2Cpp support module, and logged the `NewMainMenu` scene. MelonLoader accepted the prepared assemblies without running its own generation. The test sampled the game process every 15 seconds and requested a normal window close 90 seconds after observing the game process. Samples after initialization showed a responsive `SCPSL` window; no forced termination was used.

| Run | Launcher start (UTC) | Game start (UTC) | Normal close request (UTC) | Game / launcher exit |
| --- | --- | --- | --- | --- |
| Empty cache, initial 0.7.3 build | 18:25:12 | 18:26:01 | 18:27:43 | 0 / 0 |
| Final build, corrected runtime selection | 18:31:36 | 18:32:09 | 18:33:51 | 0 / 0 |

The second build changed the adapter identity, so it performed fresh preparation again using cached downloads. It is not evidence of an unchanged-build warm-cache launch.

After each run, the six recorded originals (`SCPSL.exe`, `GameAssembly.dll`, `UnityPlayer.dll`, `SL-AC.dll`, `globalgamemanagers`, and `global-metadata.dat`) matched their starting hashes. Final checks found no root `version.dll`, recovery journal, root launcher executable, or remaining test process. The installed launcher matched the final release hash and ownership record, and `SCPSL-ModLaunch.lnk` remained in the root. `Mods`, `Plugins`, `UserData`, `UserLibs`, and the 111 generated files remained available.

## Runtime-selection correction

The first run exposed a 0.7.3 integration issue. Its [DotnetHandler](https://github.com/LavaGang/MelonLoader/blob/v0.7.3/MelonLoader.Bootstrap/RuntimeHandlers/Dotnet/DotnetHandler.cs) expands an exact hostfxr DLL override to the enclosing .NET root and recursively selects the first hostfxr DLL. On this machine that scan encounters .NET 2.1.30 before .NET 6.0.36. The initial run attempted the loader's runtime-installer fallback and downloaded a private 6.0.25 runtime (71,248,213 extracted bytes).

ModLaunch now gives 0.7.3 the selected .NET 6 host version directory as its override, restricting discovery to that host. A regression test covers a runtime root containing both .NET 2.1 and 6.0. The test-created private runtime was moved to the qualification archive before the second run. The final build explicitly logged `C:\Program Files\dotnet\host\fxr\6.0.36\hostfxr.dll`, with no installer/download messages and no recreated private runtime directory.

## Limits and evidence

The logs contain a nonfatal Il2CppInterop class-initialization fallback warning and game-side `NativeHttp`/`HttpQuery` initialization exceptions (Win32 error 126). These did not stop menu startup or clean exit. Their effect on networking was not tested; this result does not qualify server connections, user mods, gameplay, Proton, or later game builds.

The final package passed 207 automated tests. Its hashes are recorded in [VALIDATION.md](VALIDATION.md).

Local evidence is under `artifacts/qualification/ML073-20260925-182243/`. The first run is at that root; the final run is under `host-runtime-fix/`. Each includes process/exit results, launcher logs, Unity logs, and restoration hashes. The final run also has `MelonLoader.log`, `prepared.json`, `patch-result.json`, `package-hashes.json`, and `final-state.json`. The old release, first test release, initial configuration, and test-created fallback runtime are retained in that archive for diagnosis.
