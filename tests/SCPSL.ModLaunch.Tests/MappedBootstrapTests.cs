using System.Runtime.InteropServices;
using System.Text;
using SCPSL.ModLaunch.Core;
using Xunit;

namespace SCPSL.ModLaunch.Tests;

public sealed class MappedBootstrapTests
{
    [Fact]
    public void RestorationParksAStillMappedWindowsImageInsteadOfDependingOnDeletion()
    {
        if (!OperatingSystem.IsWindows()) return; // The Windows image-section behavior is under test.
        using var game = new TestGame();
        var prepared = game.Prepare();
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
        Assert.True(File.Exists(source));
        game.SetBootstrap(prepared, File.ReadAllBytes(source));
        string expectedHash = prepared.BootstrapHash;

        var transaction = new SessionTransaction(game.Root);
        var journal = transaction.Create(prepared);
        transaction.Activate(journal);
        string activePath = Path.Combine(game.Root, "version.dll");
        // Use a copied Microsoft library, never MelonLoader or other downloaded code.
        IntPtr module = NativeLibrary.Load(activePath);
        try
        {
            var modulePath = new StringBuilder(32768);
            Assert.NotEqual(0u, GetModuleFileNameW(module, modulePath, (uint)modulePath.Capacity));
            Assert.Equal(Path.GetFullPath(activePath), Path.GetFullPath(modulePath.ToString()), ignoreCase: true);
            var restored = transaction.Restore(journal);
            Assert.True(restored.Success, string.Join(Environment.NewLine, restored.Failures));
            Assert.False(File.Exists(activePath));
            Assert.Null(transaction.LoadPending());
            string parked = Assert.Single(Directory.GetFiles(Path.Combine(game.Root, ".modlaunch", "temporary", "session-" + journal.Id), "version.dll.disabled"));
            Assert.Equal(expectedHash, FileOps.Hash(parked));
            foreach (var (name, original) in game.Originals)
                Assert.Equal(original, File.ReadAllBytes(Path.Combine(game.Root, name)));
        }
        finally { NativeLibrary.Free(module); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, uint size);
}
