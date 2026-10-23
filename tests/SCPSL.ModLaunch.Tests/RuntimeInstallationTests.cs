using SCPSL.ModLaunch.Core;
using Tomlet;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class RuntimeInstallationTests
{
    [Fact]
    public void RepairPreservesMutableConfigurationGeneratedExtrasAndUserFiles()
    {
        using var game = new TestGame();
        var files = Stage(game);
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "first");
        const string custom = "# keep these exact bytes\n[Il2CppAssemblyGenerator]\nGameAssemblyHash = 'first'\nCustom = 123\n";
        game.Write(RuntimeInstallation.GeneratorConfig, System.Text.Encoding.UTF8.GetBytes(custom));
        game.Write("MelonLoader/Il2CppAssemblies/user-generated.dll", [10, 20, 30]);
        game.Write("MelonLoader/net6/MelonLoader.dll", [99]);
        Assert.False(RuntimeInstallation.ValidateInstalled(game.Root, files, "first"));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "first");
        Assert.True(RuntimeInstallation.ValidateInstalled(game.Root, files, "first"));
        Assert.Equal(custom, File.ReadAllText(Path.Combine(game.Root, RuntimeInstallation.GeneratorConfig)));
        Assert.Equal(new byte[] { 10, 20, 30 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/user-generated.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Fact]
    public void UpgradeRetiresOnlyHashVerifiedOwnedFilesAndMergesGeneratorIdentity()
    {
        using var game = new TestGame();
        var files = Stage(game);
        files.Add(StageFile(game, "MelonLoader/net6/retired.dll", "old"));
        files.Add(StageFile(game, "MelonLoader/net6/changed-retired.dll", "old"));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "first");
        game.Write("MelonLoader/net6/changed-retired.dll", [66]);
        game.Write(RuntimeInstallation.GeneratorConfig, System.Text.Encoding.UTF8.GetBytes("[Il2CppAssemblyGenerator]\nGameAssemblyHash='old'\nUserSetting=42\n[other]\nkeep=true\n"));
        var updated = Stage(game, "second");
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, updated, "second");
        Assert.False(File.Exists(Path.Combine(game.Root, "MelonLoader/net6/retired.dll")));
        Assert.Equal(new byte[] { 66 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/net6/changed-retired.dll")));
        var cfg = TomlParser.ParseFile(Path.Combine(game.Root, RuntimeInstallation.GeneratorConfig));
        Assert.Equal("second", cfg.GetSubTable("Il2CppAssemblyGenerator").GetString("GameAssemblyHash"));
        Assert.Equal(42, cfg.GetSubTable("Il2CppAssemblyGenerator").GetInteger("UserSetting"));
        Assert.True(cfg.GetSubTable("other").GetBoolean("keep"));
        Assert.True(RuntimeInstallation.ValidateInstalled(game.Root, updated, "second"));
        Assert.False(RuntimeInstallation.ValidateInstalled(game.Root, files, "first"));
    }

    [Theory]
    [InlineData("version.dll")]
    [InlineData("winhttp.dll")]
    [InlineData("doorstop_config.ini")]
    public void ExistingProxyIsRefusedAndLeftIntact(string path)
    {
        using var game = new TestGame();
        game.Write(path, [42]);
        Assert.Throws<InvalidDataException>(() => RuntimeInstallation.Preflight(game.Root));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }

    [Fact]
    public void UnownedRuntimeCannotBeAdoptedOrOverwritten()
    {
        using var game = new TestGame();
        game.Write("MelonLoader/net6/MelonLoader.dll", [42]);
        Assert.Throws<InvalidDataException>(() => RuntimeInstallation.Preflight(game.Root));
        Assert.Throws<InvalidDataException>(() => RuntimeInstallation.InstallFromStage(game.Root, game.Stage, Stage(game), "first"));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/net6/MelonLoader.dll")));
    }

    [Fact]
    public void SkippingMelonLoaderLeavesAnUnownedDormantRuntimeUntouched()
    {
        using var game = new TestGame();
        game.Write("MelonLoader/net6/MelonLoader.dll", [42]);

        RuntimeInstallation.Preflight(game.Root, skipMelonLoader: true);

        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/net6/MelonLoader.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, ".modlaunch/runtime-owner.json")));
    }

    [Theory]
    [InlineData("version.dll")]
    [InlineData("winhttp.dll")]
    public void SkippingMelonLoaderStillRejectsExistingLoaderEntryPoints(string path)
    {
        using var game = new TestGame();
        game.Write(path, [42]);

        Assert.Throws<InvalidDataException>(() => RuntimeInstallation.Preflight(game.Root, skipMelonLoader: true));

        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }

    [Fact]
    public void OwnershipMarkerCannotClaimUserFiles()
    {
        using var game = new TestGame();
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch", "runtime-owner.json"), new RuntimeOwnership { OwnedPaths = ["Mods/user.dll"] });
        Assert.Throws<InvalidDataException>(() => RuntimeInstallation.InstallFromStage(game.Root, game.Stage, Stage(game), "first"));
        Assert.Equal("user owned Mods/user.dll", File.ReadAllText(Path.Combine(game.Root, "Mods/user.dll")));
    }

    [Fact]
    public void LegacyOwnershipUsesPreparedHashesToRetireOldGenerationScratch()
    {
        using var game = new TestGame();
        const string stale = "MelonLoader/Dependencies/Il2CppAssemblyGenerator/Cpp2IL/cpp2il_out/old.dll";
        game.Write(stale, [55]);
        var prior = new PreparedFile(stale, FileOps.Hash(Path.Combine(game.Root, stale)));
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch/runtime-owner.json"), new RuntimeOwnership { OwnedPaths = [stale] });
        FileOps.WriteJson(Path.Combine(game.Root, ".modlaunch/prepared/legacy/prepared.json"), new { session = new PreparedSession { Files = [prior] } });
        var files = Stage(game);
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "new");
        Assert.False(File.Exists(Path.Combine(game.Root, stale)));
        Assert.True(RuntimeInstallation.ValidateInstalled(game.Root, files, "new"));
    }

    [Fact]
    public void OrdinaryGenerationIsCapturedForLaterOwnedUpgradeAndRetirement()
    {
        using var game = new TestGame();
        var files = Stage(game).Where(file => file.RelativePath != RuntimeInstallation.GeneratorConfig).ToList();
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "first");
        game.Write("MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll", [1, 2, 3]);
        game.Write("MelonLoader/Il2CppAssemblies/retired.dll", [4, 5, 6]);
        game.Write("MelonLoader/Il2CppAssemblies/user-file.dll", [7, 8, 9]);
        WriteGeneratedConfig(game, "['Assembly-CSharp.dll', 'retired.dll']");
        Assert.True(RuntimeInstallation.CaptureGeneratedFiles(game.Root));
        RuntimeOwnership owner = FileOps.ReadJson<RuntimeOwnership>(Path.Combine(game.Root, ".modlaunch/runtime-owner.json"));
        Assert.Contains(RuntimeInstallation.GeneratorConfig, owner.OwnedPaths);
        Assert.Contains("MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll", owner.OwnedPaths);
        Assert.DoesNotContain("MelonLoader/Il2CppAssemblies/user-file.dll", owner.OwnedPaths);
        var updated = Stage(game, "second");
        updated.Add(StageFile(game, "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll", "new generated assembly"));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, updated, "second");
        Assert.Equal("new generated assembly", File.ReadAllText(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll")));
        Assert.False(File.Exists(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/retired.dll")));
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/user-file.dll")));
    }

    [Theory]
    [InlineData("['../user.dll']")]
    [InlineData("['C:/user.dll']")]
    [InlineData("['nested/user.dll']")]
    [InlineData("['valid.dll', 'missing.dll']")]
    [InlineData("['valid.dll', 'VALID.DLL']")]
    [InlineData("[123]")]
    [InlineData("[]")]
    public void IncompleteOrUnsafeGeneratorManifestDoesNotPartiallyClaimFiles(string entries)
    {
        using var game = new TestGame();
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, Stage(game), "first");
        game.Write("MelonLoader/Il2CppAssemblies/valid.dll", [1, 2, 3]);
        WriteGeneratedConfig(game, entries);
        string marker = Path.Combine(game.Root, ".modlaunch/runtime-owner.json");
        byte[] before = File.ReadAllBytes(marker);
        Assert.False(RuntimeInstallation.CaptureGeneratedFiles(game.Root));
        Assert.Equal(before, File.ReadAllBytes(marker));
    }

    [Fact]
    public void CaptureRefusesUnfinishedJournalAndUnownedLoader()
    {
        using var game = new TestGame();
        game.Write("MelonLoader/Il2CppAssemblies/valid.dll", [1, 2, 3]);
        WriteGeneratedConfig(game, "['valid.dll']");
        Assert.False(RuntimeInstallation.CaptureGeneratedFiles(game.Root));
        // Remove the deliberately unowned config before installing the known fixture.
        File.Delete(Path.Combine(game.Root, RuntimeInstallation.GeneratorConfig));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, Stage(game), "first");
        WriteGeneratedConfig(game, "['valid.dll']");
        game.Write(".modlaunch/session.json", [42]);
        Assert.False(RuntimeInstallation.CaptureGeneratedFiles(game.Root));
        Assert.DoesNotContain("MelonLoader/Il2CppAssemblies/valid.dll", FileOps.ReadJson<RuntimeOwnership>(Path.Combine(game.Root, ".modlaunch/runtime-owner.json")).OwnedPaths);
    }

    [Fact]
    public void CaptureDoesNotRefreshHashOfPreviouslyOwnedChangedAssembly()
    {
        using var game = new TestGame();
        var files = Stage(game);
        files.Add(StageFile(game, "MelonLoader/Il2CppAssemblies/changed.dll", "original"));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, files, "first");
        game.Write("MelonLoader/Il2CppAssemblies/changed.dll", [99]);
        WriteGeneratedConfig(game, "['changed.dll']");
        Assert.True(RuntimeInstallation.CaptureGeneratedFiles(game.Root));
        RuntimeInstallation.InstallFromStage(game.Root, game.Stage, Stage(game, "second"), "second");
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Path.Combine(game.Root, "MelonLoader/Il2CppAssemblies/changed.dll")));
    }

    private static void WriteGeneratedConfig(TestGame game, string entries) =>
        game.Write(RuntimeInstallation.GeneratorConfig, System.Text.Encoding.UTF8.GetBytes($"[Il2CppAssemblyGenerator]\nGameAssemblyHash='first'\nOldFiles={entries}\n"));

    private static List<PreparedFile> Stage(TestGame game, string identity = "first") =>
    [
        StageFile(game, "MelonLoader/net6/MelonLoader.dll", "loader"),
        StageFile(game, "MelonLoader/net6/Il2CppInterop.Runtime.dll", "interop"),
        StageFile(game, "MelonLoader/net6/MelonLoader.runtimeconfig.json", "{}"),
        StageFile(game, "MelonLoader/net6/MelonLoader.deps.json", "{}"),
        StageFile(game, RuntimeInstallation.GeneratorConfig, $"[Il2CppAssemblyGenerator]\nGameAssemblyHash='{identity}'\nOldFiles=[]\n")
    ];

    private static PreparedFile StageFile(TestGame game, string relative, string content)
    {
        string path = Path.Combine(game.Stage, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return new PreparedFile(relative, FileOps.Hash(path));
    }
}
