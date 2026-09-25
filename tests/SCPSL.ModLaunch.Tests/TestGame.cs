using SCPSL.ModLaunch.Core;

namespace SCPSL.ModLaunch.Tests;

internal sealed class TestGame : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "modlaunch-tests", Guid.NewGuid().ToString("N"));
    public string Stage => Path.Combine(Root, ".modlaunch", "prepared", "fixture");
    public Dictionary<string, byte[]> Originals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TestGame()
    {
        foreach (var name in new[] { "SCPSL.exe", "GameAssembly.dll", "UnityPlayer.dll", "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", "SL-AC.dll" })
        {
            var data = System.Text.Encoding.UTF8.GetBytes("original fixture " + name);
            Write(name, data);
            Originals.Add(name, data);
        }
        foreach (var name in new[] { "Mods/user.dll", "Plugins/plugin.dll", "UserData/user.cfg", "UserLibs/library.dll" })
            Write(name, System.Text.Encoding.UTF8.GetBytes("user owned " + name));
        byte[] bundle = BundleBytes("14.1.0");
        Write(UnityBundleVersion.RelativePath, bundle);
        Originals.Add(UnityBundleVersion.RelativePath, bundle);
    }

    public PreparedSession Prepare()
    {
        var session = new PreparedSession
        {
            LayoutVersion = 2,
            PreparedDirectory = Stage,
            Fingerprint = new string('a', 64),
            GameVersion = "14.2.7",
            UnityVersion = "2021.3.17f1",
            Inputs = Originals.Keys.Select(name => new FileIdentity(name, FileOps.Hash(Path.Combine(Root, name)))).ToList()
        };
        foreach (var name in new[] { "SCPSL.exe", "GameAssembly.dll" })
        {
            string source = name + ".bin";
            var destination = Path.Combine(Stage, source);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "prepared fixture " + name);
            session.Files.Add(new PreparedFile(name, FileOps.Hash(destination), source));
        }
        string bundleSource = "bundle.bin";
        string bundlePath = Path.Combine(Stage, bundleSource);
        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);
        File.WriteAllBytes(bundlePath, BundleBytes(session.GameVersion));
        session.Files.Add(new PreparedFile(UnityBundleVersion.RelativePath, FileOps.Hash(bundlePath), bundleSource));
        string runtimeSource = "runtime.bin";
        File.WriteAllText(Path.Combine(Stage, runtimeSource), "prepared fixture MelonLoader/net6/MelonLoader.dll");
        session.RuntimeFiles.Add(new PreparedFile("MelonLoader/net6/MelonLoader.dll", FileOps.Hash(Path.Combine(Stage, runtimeSource)), runtimeSource));
        SetBootstrap(session, System.Text.Encoding.UTF8.GetBytes("prepared fixture version.dll"));
        return session;
    }

    public void SetBootstrap(PreparedSession prepared, byte[] bytes)
    {
        string archivePath = Path.Combine(Root, ".modlaunch", "downloads", "melonloader.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (var output = new FileStream(archivePath, FileMode.Create))
        using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("version.dll").Open()) entry.Write(bytes);
        prepared.MelonLoaderArchive = archivePath;
        prepared.BootstrapHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public static byte[] BundleBytes(string version)
    {
        byte[] value = System.Text.Encoding.ASCII.GetBytes(version);
        byte[] data = Enumerable.Repeat((byte)0xA5, 64).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), value.Length);
        value.CopyTo(data, 20);
        data.AsSpan(20 + value.Length, (4 - value.Length % 4) % 4).Clear();
        return data;
    }

    public void Write(string relative, byte[] data)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
