# Third-party components

SCPSL-ModLaunch is distributed under GPL-3.0-or-later. See `LICENSE`.

- **SCPSL-ModPatch** by hopperlopip / LostFire: the headless adapter incorporates and modifies upstream parsing and patching logic. Upstream: <https://github.com/hopperlopip/SCPSL-ModPatch>. License: MIT. The adapter's source and vendor notices identify the exact incorporated revision and modifications. Patch definitions and clean launchers are downloaded separately from that project.
- **Il2CppDumper** by Perfare and its contributors: used by the patch adapter. Upstream: <https://github.com/Perfare/Il2CppDumper>. License: MIT. See the accompanying vendor license.
- **MelonLoader** by LavaGang and its contributors: downloaded at runtime from <https://github.com/LavaGang/MelonLoader>, pinned to 0.7.3. Its own distribution includes its component notices and licenses.
- **.NET** by Microsoft and its contributors: the launcher and adapter require the Windows x64 .NET 6 Runtime and do not embed a runtime. The launcher reuses the host's .NET 6 installation for MelonLoader and adapter child processes, with runtime settings scoped to those processes. Upstream: <https://github.com/dotnet/runtime>. License: MIT and the runtime's third-party notices.
- **Tomlet** by Samboy063 and its contributors: used to preserve TOML configuration values and comments. Upstream: <https://github.com/SamboyCoding/Tomlet>. License: MIT.
- **Iced**: x86/x64 decoder used to validate patch instruction boundaries. Upstream: <https://github.com/icedland/iced>. License: MIT.
- **Newtonsoft.Json**: JSON support used by the adapter's strict patch-profile reader and its dependencies. Upstream: <https://github.com/JamesNK/Newtonsoft.Json>. License: MIT.
- **Cpp2IL** and **Il2CppInterop**: obtained by the adapter's compatibility generation path where required. Upstream: <https://github.com/SamboyCoding/Cpp2IL> and <https://github.com/BepInEx/Il2CppInterop>. Their distributions retain their respective license notices.

Game files and generated game assemblies are not part of this project's source or release artifacts. They remain in the user's installation or private cache.

When distributing modified binaries, distribute the corresponding source and retain the applicable upstream license notices. Publishing a release is an explicit maintainer operation; the build workflow only uploads build artifacts.
