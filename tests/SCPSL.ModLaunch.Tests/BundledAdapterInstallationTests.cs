using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class BundledAdapterInstallationTests
{
    [Fact]
    public void UpgradePublishesCompleteSetAndRetiresFilesRemovedFromBundle()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var original = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"), ("removed.dll", "old dependency"));
        string target = BundledAdapterInstallation.Install(cache, original);
        using var updated = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"), ("nested/added.dll", "new dependency"));
        Assert.Equal(target, BundledAdapterInstallation.Install(cache, updated));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
        Assert.False(File.Exists(Path.Combine(target, "removed.dll")));
        Assert.Equal("new dependency", File.ReadAllText(Path.Combine(target, "nested", "added.dll")));
        Assert.Equal("old dependency", File.ReadAllText(Path.Combine(cache, "patching-tools", "bundled.invalid-previous", "removed.dll")));
    }

    [Fact]
    public void MatchingBundleDoesNotRewriteFilesOrCreateAnotherStagingDirectory()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var archive = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "same"));
        string target = BundledAdapterInstallation.Install(cache, archive);
        string executable = Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe");
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(executable, timestamp);
        archive.Position = 0;
        BundledAdapterInstallation.Install(cache, archive);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(executable));
        Assert.Single(Directory.GetDirectories(Path.Combine(cache, CacheLayout.Tools)));
    }

    [Fact]
    public void ExtraStaleFilesForceWholeDirectoryRefreshEvenWhenBundleIsUnchanged()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var archive = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "same"));
        string target = BundledAdapterInstallation.Install(cache, archive);
        File.WriteAllText(Path.Combine(target, "unlisted.dll"), "stale");
        archive.Position = 0;
        BundledAdapterInstallation.Install(cache, archive);
        Assert.False(File.Exists(Path.Combine(target, "unlisted.dll")));
        Assert.Equal("stale", File.ReadAllText(Path.Combine(cache, "patching-tools", "bundled.invalid-previous", "unlisted.dll")));
    }

    [Fact]
    public void LegacyDirectoryWithoutManifestIsReplacedOnlyAfterBundleValidates()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        string target = Path.Combine(cache, "patching-tools", "bundled");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe"), "legacy");
        File.WriteAllText(Path.Combine(target, "removed.dll"), "legacy dependency");
        using var malformed = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(cache, malformed));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
        using var valid = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "updated"));
        BundledAdapterInstallation.Install(cache, valid);
        Assert.False(File.Exists(Path.Combine(target, "removed.dll")));
    }

    [Theory]
    [InlineData("bundle-staged")]
    [InlineData("bundle-retired")]
    [InlineData("bundle-installed")]
    public void InterruptedRefreshRecoversToCompleteInstallation(string point)
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var old = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"), ("removed.dll", "old dependency"));
        string target = BundledAdapterInstallation.Install(cache, old);
        using var updated = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"), ("added.dll", "new dependency"));
        Assert.Throws<SimulatedTermination>(() => BundledAdapterInstallation.Install(cache, updated, checkpoint =>
        {
            if (checkpoint == point) throw new SimulatedTermination();
        }));
        if (point == "bundle-retired") Assert.False(Directory.Exists(target));
        updated.Position = 0;
        BundledAdapterInstallation.Install(cache, updated);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
        Assert.Equal("new dependency", File.ReadAllText(Path.Combine(target, "added.dll")));
        Assert.False(File.Exists(Path.Combine(target, "removed.dll")));
    }

    [Fact]
    public void InterruptedSwapRestoresPreviousInstallationEvenWhenNextArchiveIsMalformed()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var old = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"));
        string target = BundledAdapterInstallation.Install(cache, old);
        using var updated = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"));
        Assert.Throws<SimulatedTermination>(() => BundledAdapterInstallation.Install(cache, updated, point =>
        {
            if (point == "bundle-retired") throw new SimulatedTermination();
        }));
        using var malformed = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(cache, malformed));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/outside.dll")]
    [InlineData("C:/outside.dll")]
    [InlineData("folder/entry.dll.")]
    [InlineData("folder/version.dll")]
    [InlineData("VERSION.DLL")]
    [InlineData(".bundle.json")]
    [InlineData("SCPSL-MODLAUNCH.MODPATCH.HEADLESS.EXE")]
    public void UnsafeOrCollidingArchiveDoesNotChangeExistingAdapter(string invalidPath)
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var old = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"));
        string target = BundledAdapterInstallation.Install(cache, old);
        using var invalid = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"), (invalidPath, "invalid"));
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(cache, invalid));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
        Assert.Single(Directory.GetDirectories(Path.Combine(cache, CacheLayout.Tools)));
        Assert.False(File.Exists(Path.Combine(game.Root, "outside.dll")));
    }

    [Theory]
    [InlineData("Folder/first.dll", "folder/second.dll")]
    [InlineData("Folder", "Folder/second.dll")]
    public void ParentDirectoryCaseAndFileCollisionsAreRejected(string first, string second)
    {
        using var game = new TestGame();
        using var archive = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "adapter"), (first, "first"), (second, "second"));
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(Cache(game), archive));
    }

    [Fact]
    public void SymbolicLinkArchiveEntryIsRejectedBeforeExtraction()
    {
        using var game = new TestGame();
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("SCPSL-ModLaunch.ModPatch.Headless.exe");
            entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var output = new StreamWriter(entry.Open());
            output.Write("../outside.exe");
        }
        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(Cache(game), archive));
    }

    [Fact]
    public void OversizedEntryIsRejectedWithoutDecompressionOrCurrentChanges()
    {
        using var game = new TestGame();
        using var old = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"));
        string target = BundledAdapterInstallation.Install(Cache(game), old);
        using var archive = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"));
        byte[] bytes = archive.ToArray();
        int central = Enumerable.Range(0, bytes.Length - 4).First(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)) == 0x02014B50);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 24, 4), 65 * 1024 * 1024);
        using var oversized = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => BundledAdapterInstallation.Install(Cache(game), oversized));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
    }

    [Fact]
    public void JournalBlocksAdapterRefreshAndRetainsItsFiles()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var old = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "old"));
        string target = BundledAdapterInstallation.Install(cache, old);
        File.WriteAllText(Path.Combine(cache, "session.json"), "unfinished");
        using var updated = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "new"));
        Assert.Throws<InvalidOperationException>(() => BundledAdapterInstallation.Install(cache, updated));
        Assert.Equal("old", File.ReadAllText(Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe")));
    }

    [Fact]
    public void CorruptedFileCannotBeAdoptedByEditingItsLocalManifestHash()
    {
        using var game = new TestGame();
        string cache = Cache(game);
        using var archive = Bundle(("SCPSL-ModLaunch.ModPatch.Headless.exe", "good"));
        string target = BundledAdapterInstallation.Install(cache, archive);
        string executable = Path.Combine(target, "SCPSL-ModLaunch.ModPatch.Headless.exe");
        string originalHash = FileOps.Hash(executable);
        File.WriteAllText(executable, "corrupt");
        string manifest = Path.Combine(target, ".bundle.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(originalHash, FileOps.Hash(executable), StringComparison.Ordinal));
        archive.Position = 0;
        BundledAdapterInstallation.Install(cache, archive);
        Assert.Equal("good", File.ReadAllText(executable));
    }

    private static string Cache(TestGame game) => Path.Combine(game.Root, ".modlaunch");

    private static MemoryStream Bundle(params (string Name, string Contents)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, contents) in files)
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(Encoding.UTF8.GetBytes(contents));
            }
        stream.Position = 0;
        return stream;
    }

    private sealed class SimulatedTermination : Exception;
}
