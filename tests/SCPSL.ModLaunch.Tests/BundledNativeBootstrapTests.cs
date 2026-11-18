using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class BundledNativeBootstrapTests
{
    private static readonly byte[] NativeBytes = Encoding.UTF8.GetBytes("fixture native bootstrap");
    private static string NativeHash => Convert.ToHexString(SHA256.HashData(NativeBytes));

    [Fact]
    public void SelectsInMemoryArchiveWithoutSavingAnotherCopyOrChangingManagedFilesOrManifest()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var runtime = WriteRuntime(game);
        string official = prepared.MelonLoaderArchive;
        string officialHash = FileOps.Hash(official);
        string manifest = Path.Combine(game.Stage, "prepared.json");
        FileOps.WriteJson(manifest, prepared);
        byte[] manifestBytes = File.ReadAllBytes(manifest);
        Install(game, prepared);
        Assert.Null(prepared.BootstrapArchive);
        Assert.NotNull(prepared.BootstrapArchiveContents);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(official)!, "*", SearchOption.AllDirectories));
        Assert.Equal(official, prepared.MelonLoaderArchive);
        Assert.Equal(officialHash, FileOps.Hash(official));
        Assert.Equal(NativeHash, prepared.BootstrapHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(prepared.BootstrapArchiveContents!)), prepared.BootstrapArchiveSha256);
        Assert.DoesNotContain("bootstrapArchiveContents", JsonSerializer.Serialize(prepared, JsonDefaults.Options));
        Assert.Equal(manifestBytes, File.ReadAllBytes(manifest));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
        AssertRuntimeUnchanged(game, runtime);
    }

    [Fact]
    public void RepeatedSelectionReplacesMemoryWithoutWritingAnyArchive()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        byte[] contents = prepared.BootstrapArchiveContents!;
        string hash = prepared.BootstrapArchiveSha256!;
        contents[0] ^= 1;
        Install(game, prepared);
        Assert.NotSame(contents, prepared.BootstrapArchiveContents);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(prepared.BootstrapArchiveContents!)));
        Assert.Null(prepared.BootstrapArchive);
        Assert.Single(Directory.GetFiles(Path.Combine(Cache(game), "downloads"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ProductionSelectionHasNoFixtureHashOverrideAndRejectsMissingResource()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var missing = Assert.Throws<InvalidDataException>(() => BundledNativeBootstrap.InstallAndSelect(Cache(game), prepared, null));
        Assert.Contains("complete launcher package", missing.Message);
        using var fixture = Archive(("version.dll", NativeBytes));
        Assert.Throws<InvalidDataException>(() => BundledNativeBootstrap.InstallAndSelect(Cache(game), prepared, fixture));
        Assert.Null(prepared.BootstrapArchive);
    }

    [Theory]
    [InlineData("extra-managed")]
    [InlineData("wrong-path")]
    [InlineData("wrong-length")]
    [InlineData("wrong-hash")]
    public void InvalidBundleCannotSelectOrActivateFiles(string defect)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        string originalHash = prepared.BootstrapHash;
        byte[] changed = (byte[])NativeBytes.Clone();
        changed[0] ^= 1;
        using var resource = defect switch
        {
            "extra-managed" => Archive(("version.dll", NativeBytes), ("MelonLoader/net6/MelonLoader.dll", [1, 2, 3])),
            "wrong-path" => Archive(("nested/version.dll", NativeBytes)),
            "wrong-length" => Archive(("version.dll", NativeBytes[..^1])),
            _ => Archive(("version.dll", changed))
        };
        Assert.Throws<InvalidDataException>(() => BundledNativeBootstrap.InstallAndSelectFixture(Cache(game), prepared, resource, NativeHash, NativeBytes.Length));
        Assert.Null(prepared.BootstrapArchive);
        Assert.Null(prepared.BootstrapArchiveSha256);
        Assert.Equal(originalHash, prepared.BootstrapHash);
        AssertOriginals(game);
    }

    [Fact]
    public void SelectedBootstrapActivatesAndRestoresAfterOfficialDownloadIsRemoved()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        var runtime = WriteRuntime(game);
        Install(game, prepared);
        File.Delete(prepared.MelonLoaderArchive);
        Assert.Empty(Directory.GetFiles(Path.Combine(Cache(game), "downloads")));
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        Assert.Equal(NativeHash, journal.Entries.Single(entry => entry.RelativePath == "version.dll").PreparedHash);
        transaction.Activate(journal);
        Assert.Equal(NativeBytes, File.ReadAllBytes(Path.Combine(game.Root, "version.dll")));
        AssertRuntimeUnchanged(game, runtime);
        var restored = transaction.Restore(journal);
        Assert.True(restored.Success, string.Join("; ", restored.Failures));
        Assert.Null(transaction.LoadPending());
        AssertOriginals(game);
        AssertRuntimeUnchanged(game, runtime);
        Assert.False(File.Exists(prepared.MelonLoaderArchive));
    }

    [Theory]
    [InlineData("after-activate:version.dll")]
    [InlineData("after-activate:SCPSL.exe")]
    public void InterruptedNativeSelectionRecoversWithoutTheOriginalBundleArchive(string checkpoint)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        var transaction = new SessionTransaction(game.Root, checkpoint: point =>
        {
            if (point == checkpoint) throw new SimulatedInterruption();
        });
        var journal = transaction.Create(prepared);
        Assert.Throws<SimulatedInterruption>(() => transaction.Activate(journal));
        prepared.BootstrapArchiveContents = null;
        File.Delete(prepared.MelonLoaderArchive);
        var recovery = new SessionTransaction(game.Root);
        var pending = recovery.LoadPending();
        Assert.NotNull(pending);
        Assert.True(recovery.Restore(pending!).Success);
        Assert.Null(recovery.LoadPending());
        AssertOriginals(game);
    }

    [Fact]
    public void ChangedArchiveContainerIsRejectedBeforeAnyLiveActivation()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        prepared.BootstrapArchiveContents = prepared.BootstrapArchiveContents!.Concat(new byte[] { 1 }).ToArray();
        var transaction = new SessionTransaction(game.Root);
        Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
        Assert.Null(transaction.LoadPending());
        AssertOriginals(game);
    }

    [Fact]
    public void ChangedDllIsRejectedEvenWhenContainerHashWasUpdated()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        byte[] changed = (byte[])NativeBytes.Clone();
        changed[0] ^= 1;
        using var archive = Archive(("version.dll", changed));
        prepared.BootstrapArchiveContents = archive.ToArray();
        prepared.BootstrapArchiveSha256 = Convert.ToHexString(SHA256.HashData(prepared.BootstrapArchiveContents));
        var transaction = new SessionTransaction(game.Root);
        Assert.Throws<InvalidDataException>(() => transaction.Create(prepared));
        Assert.Null(transaction.LoadPending());
        AssertOriginals(game);
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("missing-archive")]
    [InlineData("empty-hash")]
    [InlineData("empty-archive")]
    [InlineData("invalid-hash")]
    public void OptionalArchiveRequiresItsCompleteSha256Pair(string defect)
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        switch (defect)
        {
            case "missing-hash": prepared.BootstrapArchiveSha256 = null; break;
            case "missing-archive": prepared.BootstrapArchiveContents = null; break;
            case "empty-hash": prepared.BootstrapArchiveSha256 = ""; break;
            case "empty-archive": prepared.BootstrapArchiveContents = null; prepared.BootstrapArchive = ""; break;
            default: prepared.BootstrapArchiveSha256 = "bad"; break;
        }
        Assert.Throws<InvalidDataException>(() => new SessionTransaction(game.Root).Create(prepared));
        AssertOriginals(game);
    }

    [Fact]
    public void SelectedArchiveCannotEscapeThePrivateCache()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        Install(game, prepared);
        string outside = Path.Combine(game.Root, "outside.zip");
        File.WriteAllBytes(outside, prepared.BootstrapArchiveContents!);
        prepared.BootstrapArchiveContents = null;
        prepared.BootstrapArchive = outside;
        Assert.Throws<InvalidDataException>(() => new SessionTransaction(game.Root).Create(prepared));
        AssertOriginals(game);
    }

    [Fact]
    public void LegacyManifestWithoutOptionalFieldsStillUsesItsOfficialArchive()
    {
        using var game = new TestGame();
        var original = game.Prepare();
        var json = JsonSerializer.SerializeToNode(original, JsonDefaults.Options)!.AsObject();
        json.Remove("bootstrapArchive");
        json.Remove("bootstrapArchiveSha256");
        var prepared = json.Deserialize<PreparedSession>(JsonDefaults.Options)!;
        Assert.Null(prepared.BootstrapArchive);
        Assert.Null(prepared.BootstrapArchiveSha256);
        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        Assert.Equal("prepared fixture version.dll", File.ReadAllText(Path.Combine(game.Root, "version.dll")));
        Assert.True(transaction.Restore(journal).Success);
        AssertOriginals(game);
    }

    [Fact]
    public void PendingSessionCannotChangeTheBundledSelection()
    {
        using var game = new TestGame();
        var prepared = game.Prepare();
        new SessionTransaction(game.Root).Create(prepared);
        using var archive = Archive(("version.dll", NativeBytes));
        Assert.Throws<InvalidOperationException>(() => BundledNativeBootstrap.InstallAndSelectFixture(Cache(game), prepared, archive, NativeHash, NativeBytes.Length));
        Assert.Null(prepared.BootstrapArchive);
        AssertOriginals(game);
    }

    private static string Cache(TestGame game) => Path.Combine(game.Root, ".modlaunch");
    private static void Install(TestGame game, PreparedSession prepared)
    {
        using var archive = Archive(("version.dll", NativeBytes));
        BundledNativeBootstrap.InstallAndSelectFixture(Cache(game), prepared, archive, NativeHash, NativeBytes.Length);
    }
    private static MemoryStream Archive(params (string Path, byte[] Bytes)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var output = entry.Open();
                output.Write(file.Bytes);
            }
        stream.Position = 0;
        return stream;
    }
    private static Dictionary<string, byte[]> WriteRuntime(TestGame game)
    {
        var files = new Dictionary<string, byte[]>();
        foreach (string path in new[] { "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll",
                     "MelonLoader/Dependencies/SupportModules/Il2Cpp.dll", ".modlaunch/runtime-owner.json" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes("stock fixture " + path);
            game.Write(path, bytes);
            files.Add(path, bytes);
        }
        return files;
    }
    private static void AssertRuntimeUnchanged(TestGame game, Dictionary<string, byte[]> files)
    {
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, path)));
    }
    private static void AssertOriginals(TestGame game)
    {
        foreach (var (path, bytes) in game.Originals) Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(game.Root, path)));
        Assert.False(File.Exists(Path.Combine(game.Root, "version.dll")));
    }
    private sealed class SimulatedInterruption : Exception { }
}
