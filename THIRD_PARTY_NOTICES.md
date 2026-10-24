# Third-party components

SCPSL-ModLaunch is distributed under GPL-3.0-or-later. See `LICENSE`.

- **SCPSL-ModPatch** by hopperlopip / LostFire and contributors: the launcher invokes the .NET 6 compatibility build from <https://github.com/AnomalyLabsIO/SCPSL-ModPatch>, a fork of <https://github.com/hopperlopip/SCPSL-ModPatch>. License: MIT. The same single executable supports GUI and command line use. Its exact source archive, binary inventory, and SHA-256 sidecars are retained in `vendor/ModPatch/`; source accompanies release artifacts. Pattern definitions and clean launchers are bundled and can update from a pinned commit in the fork.
- **Il2CppDumper** by Perfare and its contributors: used by the patch adapter. Upstream: <https://github.com/Perfare/Il2CppDumper>. License: MIT. See the accompanying vendor license.
- **MelonLoader** by LavaGang and its contributors: downloaded at runtime from <https://github.com/LavaGang/MelonLoader>, pinned to 0.7.3. Its own distribution includes its component notices and licenses.
- **Native bootstrap / Dobby PR 13**: the bundled Windows x64 `version.dll` is rebuilt from stock MelonLoader 0.7.3 with BepInEx/Dobby PR 13. MelonLoader source is otherwise unchanged. Source pins, binary identity, reproduction instructions and retained licenses/notices are in `vendor/native-bootstrap-pr13/`. Upstream: <https://github.com/BepInEx/Dobby/pull/13>. MelonLoader and Dobby are Apache-2.0; the bootstrap distribution retains its transitive component notices.
- **.NET** by Microsoft and its contributors: the launcher, adapter, and bundled ModPatch do not embed a runtime. ModPatch requires the Windows x64 .NET 6 Desktop Runtime, which also supplies the .NET 6 Runtime used by the launcher and adapter. The launcher reuses the host's .NET 6 installation for MelonLoader and adapter child processes, with runtime settings scoped to those processes. Upstream: <https://github.com/dotnet/runtime>. License: MIT and the runtime's third-party notices.
- **Tomlet** by Samboy063 and its contributors: used to preserve TOML configuration values and comments. Upstream: <https://github.com/SamboyCoding/Tomlet>. License: MIT.
- **Iced**: x86/x64 decoder used by the ModPatch CLI. Upstream: <https://github.com/icedland/iced>. License: MIT.
- **Newtonsoft.Json**: JSON support used by the ModPatch CLI's strict manifest reader and its dependencies. Upstream: <https://github.com/JamesNK/Newtonsoft.Json>. License: MIT.
- **AssetsTools.NET** by nesrak1 and contributors: Unity asset support included in the unified ModPatch build. Upstream: <https://github.com/nesrak1/AssetsTools.NET>. License: MIT.
- **Microsoft ASP.NET Web API Client**: HTTP formatting support included in the unified ModPatch build. Upstream: <https://github.com/aspnet/AspNetWebStack>. License: Microsoft .NET Library license; see the accompanying `NET_Library_EULA_ENU.txt` notice.
- **Cpp2IL** and **Il2CppInterop**: obtained by the adapter's compatibility generation path where required. Upstream: <https://github.com/SamboyCoding/Cpp2IL> and <https://github.com/BepInEx/Il2CppInterop>. Their distributions retain their respective license notices.

Game files and generated game assemblies are not part of this project's source or release artifacts. They remain in the user's installation or private cache.

When distributing modified binaries, distribute the corresponding source and retain the applicable upstream license notices. Publishing a release is an explicit maintainer operation; the build workflow only uploads build artifacts.
