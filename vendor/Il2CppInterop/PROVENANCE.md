# Il2CppInterop generation-only compatibility build

Upstream: https://github.com/BepInEx/Il2CppInterop
Fixed commit: 455c42a6f35fd272c2ab3023208defa593fb1d70
Fix: https://github.com/BepInEx/Il2CppInterop/pull/266

This archive contains the unchanged Generator and Common source files, project files, and shared build properties at the fixed commit, plus upstream LICENSE/README. The UPSTREAM-MANIFEST.json records each upstream Git blob SHA-1. No production ModLaunch source or MelonLoader runtime binaries are modified.

The fixed Generator is built with the 1.5.1.0 assembly identity for MelonLoader 0.7.3's bundled Common dependency. FileVersion is 1.5.3.0 and InformationalVersion includes the exact upstream source commit. This metadata-only identity adjustment is passed on the build command line in Build.ps1; no upstream source is patched.

Build tested with .NET SDK 10.0.404, target netstandard2.1, Release. Run ./Build.ps1 from the archive root. NuGet restore uses the versions pinned in the upstream project files. The build output is source/bin/Il2CppInterop.Generator/netstandard2.1/Il2CppInterop.Generator.dll. Only that DLL is intended to be used by ModLaunch's isolated generation AssemblyLoadContext; do not replace runtime Common or Runtime DLLs.

Common compatibility: the Il2CppInterop.Common Git tree is exactly 15c18298f477c93c0af3cb948285e4a8ff2206a1 in both bundled f03c8f4ae507d47ea814f3d11d1ec6b0391c1576 and fixed 455c42a6f35fd272c2ab3023208defa593fb1d70. Generator project/dependency versions are unchanged. The comparison has 10 commits, and Generator changes are limited to Pass79UnstripTypes, Pass50GenerateMethods, and ILGeneratorEx (two small ref-value-type fixes).

Generated DLL SHA-256: 0FD112D3986BEF4810E8FF9DAC69C18502626B076243CEAE4514173CC3DEA67E
AssemblyVersion: 1.5.1.0
FileVersion: 1.5.3.0
InformationalVersion: 1.5.3+455c42a6f35fd272c2ab3023208defa593fb1d70

Upstream library license is LGPL-3.0-only, provided in source/LICENSE. The accompanying GPL-3.0 text is included as GPL-3.0.txt (standard COPYING3 text from https://github.com/gcc-mirror/gcc/blob/master/COPYING3). Preserve these notices and corresponding source when redistributing.

Source download used the GitHub connector because local codeload DNS was unavailable. The source archive was locally assembled, not downloaded as an official release artifact.

