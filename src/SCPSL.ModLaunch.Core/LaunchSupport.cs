using System.Diagnostics;
using System.Text;
using Tomlet;
using Tomlet.Models;

namespace SCPSL.ModLaunch.Core;

public sealed record LaunchArguments(bool RecoverOnly, string[] GameArguments,
    bool NoRestore = false, bool NoMelonLoader = false, bool NoStart = false,
    bool Repair = false, bool Launch = false);

public enum LauncherMode { Gui, Cli }

public static class LauncherEntryPoint
{
    public static LauncherMode ResolveMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0) return LauncherMode.Gui;
        // A literal game argument after -- must not select the launcher's interface.
        bool gui = args.TakeWhile(argument => argument != "--").Contains("--modlaunch-gui", StringComparer.Ordinal);
        if (!gui) return LauncherMode.Cli;
        if (args.Length != 1)
            throw new ArgumentException("--modlaunch-gui cannot be combined with CLI options or game arguments.");
        return LauncherMode.Gui;
    }
}

public enum GuiLaunchAction { Launch, Prepare, Repair, Restore }

public static class GuiLaunchActions
{
    /// <summary>Runs the selected action through the CLI after the GUI releases its directory lock.</summary>
    public static string[] ArgumentsFor(GuiLaunchAction action, bool restoreVanillaOnExit = true)
    {
        string[] arguments = action switch
        {
            GuiLaunchAction.Launch => ["--modlaunch-cli", "--modlaunch-launch"],
            // --launch continues after restoring an earlier session; --no-start stops before game launch.
            GuiLaunchAction.Prepare => ["--modlaunch-cli", "--modlaunch-launch", "--modlaunch-no-start"],
            GuiLaunchAction.Repair => ["--modlaunch-cli", "--modlaunch-launch", "--modlaunch-repair", "--modlaunch-no-start"],
            GuiLaunchAction.Restore => ["--modlaunch-cli", "--modlaunch-recover-only"],
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown launcher action.")
        };
        return !restoreVanillaOnExit && action != GuiLaunchAction.Restore
            ? [.. arguments, "--modlaunch-no-restore"] : arguments;
    }
}

public static class GameArguments
{
    public static LaunchArguments Parse(string[] args)
    {
        _ = LauncherEntryPoint.ResolveMode(args);
        var forwarded = new List<string>();
        bool recover = false, noRestore = false, noMelonLoader = false, noStart = false, repair = false, launch = false;
        bool launcherModeOptions = true;
        foreach (string arg in args)
        {
            if (arg == "--")
            {
                launcherModeOptions = false;
                forwarded.Add(arg);
                continue;
            }
            switch (arg)
            {
                case "--modlaunch-cli" when launcherModeOptions: break;
                case "--modlaunch-gui" when launcherModeOptions: break;
                case "--modlaunch-repair" when launcherModeOptions: repair = true; break;
                case "--modlaunch-launch" when launcherModeOptions: launch = true; break;
                case "--modlaunch-recover-only": recover = true; break;
                case "--modlaunch-no-restore": noRestore = true; break;
                case "--modlaunch-no-melonloader": noMelonLoader = true; break;
                case "--modlaunch-no-start": noStart = true; break;
                default: forwarded.Add(arg); break;
            }
        }
        if (recover && (noRestore || noMelonLoader || noStart || repair || launch || forwarded.Count != 0))
            throw new ArgumentException("--modlaunch-recover-only cannot be combined with other launcher options or game arguments.");
        return new LaunchArguments(recover, forwarded.ToArray(), noRestore, noMelonLoader, noStart, repair, launch);
    }

    public static ProcessStartInfo StartInfo(string gameDirectory, string dotnetRoot, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(FileOps.SafePath(gameDirectory, "SCPSL.exe")) { WorkingDirectory = gameDirectory, UseShellExecute = false };
        foreach (string argument in args) psi.ArgumentList.Add(argument);
        psi.Environment["DOTNET_ROOT_X64"] = dotnetRoot;
        psi.Environment["DOTNET_ROOT"] = dotnetRoot;
        psi.Environment.TryGetValue("WINEDLLOVERRIDES", out string? existingOverrides);
        string[] overrides = (existingOverrides ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        // Split grouped override names so replacing version preserves the remaining names.
        var preserved = new List<string>();
        foreach (string rule in overrides)
        {
            string[] parts = rule.Split('=', 2);
            if (parts.Length != 2) { preserved.Add(rule); continue; }
            string[] names = parts[0].Split(',').Where(n => !n.Trim().TrimStart('*').Equals("version", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length != 0) preserved.Add(string.Join(',', names) + "=" + parts[1]);
        }
        preserved.Add("version=n,b");
        psi.Environment["WINEDLLOVERRIDES"] = string.Join(';', preserved);
        return psi;
    }
}

public static class LoaderConfig
{
    public static void Update(string path, string dotnetRoot)
        => Update(path, HostRuntime.ResolveFromRoot(dotnetRoot));

    public static void Update(string path, HostRuntimeInfo runtime)
    {
        FileOps.RejectLink(path);
        TomlDocument document = File.Exists(path) ? TomlParser.ParseFile(path) : new TomlParser().Parse("");
        Set(document, "console", "hide_console", TomlBoolean.False);
        // MelonLoader 0.7.3 expands a DLL override back to the runtime root, then loads
        // the first hostfxr it finds. Limit that scan to the selected .NET 6 version
        // so older side-by-side hosts cannot trigger a runtime install/download.
        string hostFxrDirectory = Path.GetDirectoryName(runtime.HostFxrPath)
            ?? throw new InvalidDataException("The selected hostfxr has no parent directory.");
        Set(document, "loader", "hostfxr_path_override", new TomlString(hostFxrDirectory));
        FileOps.WriteBytes(path, Encoding.UTF8.GetBytes(document.SerializedValue));
    }

    private static void Set(TomlDocument document, string section, string key, TomlValue value)
    {
        TomlTable table;
        if (document.ContainsKey(section)) table = document.GetSubTable(section);
        else { table = new TomlTable(); document.PutValue(section, table); }
        table.ForceNoInline = true;
        if (table.TryGetValue(key, out var old))
        {
            value.Comments.PrecedingComment = old.Comments.PrecedingComment;
            value.Comments.InlineComment = old.Comments.InlineComment;
        }
        table.PutValue(key, value);
    }
}

public sealed class DirectoryLock : IDisposable
{
    private readonly FileStream stream;
    public DirectoryLock(string gameDirectory)
    {
        string cache = FileOps.SafePath(gameDirectory, ".modlaunch");
        Directory.CreateDirectory(cache);
        string path = FileOps.SafePath(cache, "session.lock");
        try { stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("Another SCPSL-ModLaunch session is using this game directory.", ex); }
    }
    public void Dispose() => stream.Dispose();
}

public static class GameProcessGuard
{
    public static void EnsureStopped(string gameDirectory)
    {
        string target = Path.GetFullPath(Path.Combine(gameDirectory, "SCPSL.exe"));
        foreach (var process in new[] { "SCPSL", "SCPSL-ModLaunch.ModPatch.Headless", "SCPSL ModPatch", "SCPSL.ModPatch.Cli", "Cpp2IL" }.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    string? image = process.MainModule?.FileName;
                    if (image == null || string.Equals(Path.GetFullPath(image), target, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFullPath(image).StartsWith(Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("SCP:SL or its preparation helper is still running from this directory. Close it before preparation or recovery.");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new IOException("A running SCP:SL process could not be inspected. Close it before preparation or recovery.", ex);
                }
                catch (InvalidOperationException) { /* Process exited during inspection. */ }
            }
        }
    }
}
