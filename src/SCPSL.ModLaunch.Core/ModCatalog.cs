namespace SCPSL.ModLaunch.Core;

public sealed record ModEntry(string RelativePath, string Name, bool Enabled, string Sha256);

/// <summary>Lists user mods without loading them. The caller holds the game directory lock while editing.</summary>
public static class ModCatalog
{
    private const string DisabledSuffix = ".DISABLED";

    public static IReadOnlyList<ModEntry> Read(string gameDirectory)
    {
        string root = Path.GetFullPath(gameDirectory);
        var entries = new List<ModEntry>();
        string directory = FileOps.SafePath(root, "Mods");
        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string relative = "Mods/" + Path.GetFileName(file);
                if (!TryGetEnabled(relative, out bool enabled)) continue;
                string safe = FileOps.SafePath(root, relative);
                string name = Path.GetFileName(enabled ? relative : relative[..^DisabledSuffix.Length]);
                entries.Add(new ModEntry(relative, name, enabled, FileOps.Hash(safe)));
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ModEntry SetEnabled(string gameDirectory, ModEntry snapshot, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string root = Path.GetFullPath(gameDirectory);
        GameProcessGuard.EnsureStopped(root);
        string relative = snapshot.RelativePath.Replace('\\', '/');
        string[] segments = relative.Split('/');
        if (segments.Length != 2 || segments[0] != "Mods" ||
            !TryGetEnabled(relative, out bool currentEnabled) || currentEnabled != snapshot.Enabled ||
            snapshot.Sha256 is not { Length: 64 } || !snapshot.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Only detected assemblies in Mods can be enabled or disabled.");
        string source = FileOps.SafePath(root, relative);
        if (!File.Exists(source) || !FileOps.HashEquals(FileOps.Hash(source), snapshot.Sha256))
            throw new IOException("The mod changed since the list was refreshed. Refresh before changing it.");
        if (enabled == currentEnabled) return snapshot;
        string targetRelative = enabled ? relative[..^DisabledSuffix.Length] : relative + DisabledSuffix;
        string target = FileOps.SafePath(root, targetRelative);
        string directory = Path.GetDirectoryName(target)!;
        if (Directory.EnumerateFileSystemEntries(directory).Any(path =>
                Path.GetFileName(path).Equals(Path.GetFileName(target), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Both enabled and disabled copies exist. Resolve the filename conflict before changing this mod.");
        File.Move(source, target, overwrite: false);
        if (!FileOps.HashEquals(FileOps.Hash(target), snapshot.Sha256))
        {
            if (!File.Exists(source)) File.Move(target, source, overwrite: false);
            throw new IOException("The mod changed while its filename was being updated. Refresh the list.");
        }
        return snapshot with { RelativePath = targetRelative, Enabled = enabled };
    }

    private static bool TryGetEnabled(string path, out bool enabled)
    {
        enabled = !path.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
        string assembly = enabled ? path : path[..^DisabledSuffix.Length];
        return assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }
}
