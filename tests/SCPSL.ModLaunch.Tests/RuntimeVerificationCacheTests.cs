using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class RuntimeVerificationCacheTests
{
    private const string Fingerprint = "verified-runtime-fixture";
    private static readonly string[] RequiredRuntime =
    [
        "MelonLoader/net6/MelonLoader.dll", "MelonLoader/net6/Il2CppInterop.Runtime.dll",
        "MelonLoader/net6/MelonLoader.runtimeconfig.json", "MelonLoader/net6/MelonLoader.deps.json"
    ];

    [Fact]
    public void RetiredFileDeletionRequiresFreshContentsRatherThanCachedMetadata()
    {
        using var game = new TestGame();
        string stage = Path.Combine(game.Root, "runtime-stage");
        const string retired = "MelonLoader/net6/obsolete.dll";
        var files = StageFiles(stage, RequiredRuntime.Append(retired));
        RuntimeInstallation.InstallFromStage(game.Root, stage, files, Fingerprint);
        var verification = new FileVerificationCache(game.Root);
        string path = Path.Combine(game.Root, retired);
        string original = verification.Hash(path);
        byte[] changed = ChangeContentsPreservingMetadata(path);
        Assert.True(verification.Matches(path, original));

        RuntimeInstallation.InstallFromStage(game.Root, stage, files.Where(file => file.RelativePath != retired).ToList(), "new-runtime-fixture");
        Assert.Equal(changed, File.ReadAllBytes(path));
        var owner = FileOps.ReadJson<RuntimeOwnership>(Path.Combine(game.Root, ".modlaunch", "runtime-owner.json"));
        Assert.Contains(retired, owner.OwnedPaths);
        Assert.Equal(original, owner.Files.Single(file => file.RelativePath == retired).Sha256);
    }

    private static List<PreparedFile> StageFiles(string stage, IEnumerable<string> paths)
    {
        var files = new List<PreparedFile>();
        foreach (string relative in paths)
        {
            string path = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, relative == RuntimeInstallation.GeneratorConfig ? "[Il2CppAssemblyGenerator]\n" : "owned runtime fixture " + relative);
            files.Add(new PreparedFile(relative, FileOps.Hash(path)));
        }
        return files;
    }

    private static byte[] ChangeContentsPreservingMetadata(string path)
    {
        DateTime created = File.GetCreationTimeUtc(path);
        DateTime written = File.GetLastWriteTimeUtc(path);
        byte[] changed = Enumerable.Repeat((byte)0x77, (int)new FileInfo(path).Length).ToArray();
        File.WriteAllBytes(path, changed);
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(created, File.GetCreationTimeUtc(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        return changed;
    }
}
