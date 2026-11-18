using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class ModCatalogTests
{
    [Fact]
    public void FindsOnlyTopLevelUserAssembliesAndRetainsDisabledState()
    {
        using var game = new TestGame();
        game.Write("Mods/disabled.DLL.disabled", [1]);
        game.Write("Mods/readme.txt", [2]);
        game.Write("Mods/assets/helper.dll", [3]);
        game.Write("MelonLoader/net6/MelonLoader.dll", [4]);
        game.Write("Plugins/disabled.dll.DISABLED", [5]);
        var entries = ModCatalog.Read(game.Root);
        Assert.Equal(2, entries.Count);
        Assert.False(Assert.Single(entries.Where(e => e.Name == "disabled.DLL")).Enabled);
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("Plugins/") || e.RelativePath.Contains("assets") || e.RelativePath.Contains("MelonLoader"));
    }

    [Fact]
    public void DisabledFilenameRetainsDllExtensionAndRoundTripsTheOriginalBytes()
    {
        using var game = new TestGame();
        ModEntry original = ModCatalog.Read(game.Root).Single(e => e.RelativePath == "Mods/user.dll");
        var disabled = ModCatalog.SetEnabled(game.Root, original, false);
        Assert.Equal("Mods/user.dll.DISABLED", disabled.RelativePath);
        Assert.False(File.Exists(Path.Combine(game.Root, original.RelativePath)));
        Assert.Equal(original.Sha256, FileOps.Hash(Path.Combine(game.Root, disabled.RelativePath)));
        var enabled = ModCatalog.SetEnabled(game.Root, disabled, true);
        Assert.Equal(original, enabled);
    }

    [Fact]
    public void AChangedModCannotBeRenamedUsingAStaleSnapshot()
    {
        using var game = new TestGame();
        var original = ModCatalog.Read(game.Root).Single(e => e.RelativePath == "Mods/user.dll");
        game.Write(original.RelativePath, [99]);
        Assert.Throws<IOException>(() => ModCatalog.SetEnabled(game.Root, original, false));
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(Path.Combine(game.Root, original.RelativePath)));
        Assert.False(File.Exists(Path.Combine(game.Root, original.RelativePath + ".DISABLED")));
    }

    [Theory]
    [InlineData("user.dll.DISABLED")]
    [InlineData("USER.DLL.disabled")]
    public void ExistingDestinationIsNeverOverwrittenIncludingCaseVariants(string conflictingName)
    {
        using var game = new TestGame();
        var original = ModCatalog.Read(game.Root).Single(e => e.RelativePath == "Mods/user.dll");
        string relative = "Mods/" + conflictingName;
        game.Write(relative, [88]);
        Assert.Throws<IOException>(() => ModCatalog.SetEnabled(game.Root, original, false));
        Assert.Equal(original.Sha256, FileOps.Hash(Path.Combine(game.Root, original.RelativePath)));
        Assert.Equal(new byte[] { 88 }, File.ReadAllBytes(Path.Combine(game.Root, relative)));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("MelonLoader/net6/MelonLoader.dll")]
    [InlineData("Mods/assets/dependency.dll")]
    [InlineData("Mods/readme.txt")]
    [InlineData("Plugins/plugin.dll")]
    [InlineData("Plugins/disabled.dll.DISABLED")]
    public void ForgedEntriesCannotRenameDependenciesPluginsOrFilesOutsideMods(string relative)
    {
        using var game = new TestGame();
        var entry = new ModEntry(relative, "forged", true, new string('A', 64));
        Assert.Throws<InvalidDataException>(() => ModCatalog.SetEnabled(game.Root, entry, false));
    }

    [Fact]
    public void CurrentStateRequestDoesNotTouchTheFileTimestamp()
    {
        using var game = new TestGame();
        var entry = ModCatalog.Read(game.Root).First();
        string path = Path.Combine(game.Root, entry.RelativePath);
        DateTime timestamp = File.GetLastWriteTimeUtc(path);
        Assert.Equal(entry, ModCatalog.SetEnabled(game.Root, entry, entry.Enabled));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void SymlinkedModCannotExposeOrRenameAnOutsideFile()
    {
        using var game = new TestGame();
        string outside = Path.Combine(game.Root, "outside.dll");
        string link = Path.Combine(game.Root, "Mods/linked.dll");
        File.WriteAllBytes(outside, [1, 2, 3]);
        try { File.CreateSymbolicLink(link, outside); }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) when (OperatingSystem.IsWindows()) { return; }
        try { Assert.Throws<InvalidDataException>(() => ModCatalog.Read(game.Root)); }
        finally { File.Delete(link); }
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(outside));
    }
}
