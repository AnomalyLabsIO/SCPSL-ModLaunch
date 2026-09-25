# ModPatch adapter provenance

The headless adapter derives the patch profile contract, method mapping, patch operations, and version discovery from [SCPSL-ModPatch v2.1.1](https://github.com/hopperlopip/SCPSL-ModPatch/tree/1786a7f4d6ff8d1c7f3e4d1aefe48484334ab718) (MIT, Copyright 2024 LostFire). It replaces GUI prompts and permissive skips with explicit failures. It supports hex, `[NOP]`, and `[RET]` patch data; FASM and unknown schema fields fail closed. Protected binaries are never executed or unpacked.

Il2CppDumper and Mono.Cecil binaries were copied from the local upstream checkout at `374b0f578bb07373c4ec0ce2f91fc2d55b7216ca`, `SCPSL ModPatch/Il2CppDumper/`. Il2CppDumper.dll was independently downloaded at pinned v2.1.1 commit `1786a7f4d6ff8d1c7f3e4d1aefe48484334ab718` and verified byte-identical. The vendor bytes are tracked so builds do not depend on another checkout.

| File | SHA-256 |
| --- | --- |
| Il2CppDumper.dll | 9CEFCDFAAA8E4911CB5DF8C7472A83949722A55E269AB377E13833D2E250CC18 |
| Mono.Cecil.dll | 769A59793D4B8885BBBFBC5AEE8F57A0D4E34D275C56C60C03994309B87F67E9 |
| Mono.Cecil.Mdb.dll | 61637F9940E5E336571CBF945BE0F36D6D6050E06288DF0F0232D93B26F0BDE7 |
| Mono.Cecil.Pdb.dll | A5249A04AD8FD7DFD47E4D0A620AED0F7EB6051E1DDEC102C541CA3E12F6E2A8 |
| Mono.Cecil.Rocks.dll | 273292BABD45F9F34DE5054BD9CDFE1D859A7DBF6F4AD5974FE4EAD70698ED5B |

Original Il2CppDumper project: https://github.com/Perfare/Il2CppDumper (MIT). Mono.Cecil: https://github.com/jbevain/cecil (MIT). License texts accompany these binaries. NuGet dependencies: Iced 1.21.0 (MIT), Newtonsoft.Json 13.0.3 (MIT).

`MetadataHeaderNormalizer.cs` and `MelonInteropAssemblyGenerator.cs` are GPL-3.0-or-later components for parser-copy normalization and Cpp2IL/Il2CppInterop generation. Runtime metadata is never rewritten.

Cpp2IL `2022.1.0-pre-release.21` and its StrippedCodeRegSupport plugin are fetched from the pinned release; SHA-256 digests from the GitHub release API are embedded in the adapter. Unity managed libraries use a Unity-version-specific release and enforce a published digest when available; older releases lack upstream digests, so downloads are cached with a local SHA-256. These hashes detect corruption, not a compromised upstream publisher.
