using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SCPSL.ModLaunch.Core;

/// <summary>Moves the drop-in launcher out of the retail game's root after restoration.</summary>
public static class LauncherInstallation
{
    public const string ExecutableName = "SCPSL-ModLaunch.exe";
    public const string ShortcutName = "SCPSL-ModLaunch.lnk";
    private const string PrivateDirectory = ".modlaunch";
    private const string OwnershipName = "launcher-installation.json";

    public static string ResolveGameDirectory(string executablePath)
    {
        string executable = Path.GetFullPath(executablePath);
        if (!Path.GetFileName(executable).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Keep the launcher named {ExecutableName}.");
        string directory = Path.GetDirectoryName(executable) ?? throw new InvalidDataException("Cannot locate the launcher directory.");
        string root = Path.GetFileName(directory).Equals(PrivateDirectory, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory) ?? throw new InvalidDataException("Cannot locate the game directory.")
            : directory;
        FileOps.RejectLink(executable);
        FileOps.RejectLink(directory);
        FileOps.RejectLink(root);
        // A missing retail EXE must not prevent recovery of an interrupted replacement.
        if (!File.Exists(FileOps.SafePath(root, "SCPSL.exe")) &&
            !File.Exists(FileOps.SafePath(root, PrivateDirectory + "/session.json")))
            throw new InvalidDataException($"Place {ExecutableName} beside SCPSL.exe or in that game's {PrivateDirectory} directory.");
        return root;
    }

    public static void Complete(string gameDirectory, string executablePath, Action<string>? log = null) =>
        Complete(gameDirectory, executablePath, new WindowsLauncherShortcutStore(), log);

    // The caller holds the game's exclusive directory lock and has completed restoration.
    internal static void Complete(string gameDirectory, string executablePath, ILauncherShortcutStore shortcuts,
        Action<string>? log = null, Action<string>? checkpoint = null)
    {
        string? temporaryShortcut = null;
        try
        {
            string root = Path.GetFullPath(gameDirectory);
            string cache = FileOps.SafePath(root, PrivateDirectory);
            string source = Path.GetFullPath(executablePath);
            string rootExecutable = FileOps.SafePath(root, ExecutableName);
            string installed = FileOps.SafePath(cache, ExecutableName);
            string shortcut = FileOps.SafePath(root, ShortcutName);
            string ownershipPath = FileOps.SafePath(cache, OwnershipName);
            bool fromRoot = SamePath(source, rootExecutable);
            if (!fromRoot && !SamePath(source, installed))
                throw new InvalidDataException("The launcher is outside its supported root or private-directory location.");
            EnsureNoPendingSession(cache);
            foreach (string path in new[] { source, installed, shortcut, ownershipPath, rootExecutable }) RequireRegularFileOrMissing(path);
            if (!File.Exists(source)) throw new FileNotFoundException("The running launcher's file is missing.", source);
            if (!fromRoot && File.Exists(rootExecutable))
                throw new IOException($"A launcher also exists at {rootExecutable}. Run that root copy to finish installing the replacement.");

            LauncherInstallationOwnership? ownership = File.Exists(ownershipPath)
                ? FileOps.ReadJson<LauncherInstallationOwnership>(ownershipPath) : null;
            if (ownership != null) ValidateOwnership(ownership);
            string sourceHash = FileOps.Hash(source);
            string? installedHash = HashIfPresent(installed);
            string? shortcutHash = HashIfPresent(shortcut);
            // A manually relocated, currently running launcher can establish its own ownership.
            // An existing destination encountered by a root copy needs prior recorded ownership.
            if (installedHash != null && (fromRoot || ownership != null) &&
                !MatchesEither(installedHash, ownership?.ExecutableSha256, ownership?.PendingExecutableSha256))
                throw new IOException($"The destination launcher is unrecognized or changed: {installed}. It was not overwritten.");
            bool shortcutCorrect = shortcutHash != null && shortcuts.Matches(shortcut, installed, root);
            // Explorer may update link metadata, and users may add arguments or customize its icon.
            // Keep such links byte-for-byte when their target and working directory still match.
            // A changed destination is never silently retargeted, even if its old bytes were owned.
            if (shortcutHash != null && !shortcutCorrect)
                throw new IOException($"A shortcut with a different target or working directory already exists: {shortcut}. It was not overwritten; move or rename it before retrying.");
            Directory.CreateDirectory(cache);
            string desiredShortcutHash;
            if (shortcutCorrect) desiredShortcutHash = shortcutHash!;
            else
            {
                temporaryShortcut = FileOps.SafePath(cache, "launcher-shortcut-" + Guid.NewGuid().ToString("N") + ".tmp");
                shortcuts.Write(temporaryShortcut, installed, root);
                if (!shortcuts.Matches(temporaryShortcut, installed, root))
                    throw new InvalidDataException("The generated launcher shortcut did not pass validation.");
                // COM Save has closed its handle; flush the generated file before publishing its ownership.
                using (var file = new FileStream(temporaryShortcut, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
                desiredShortcutHash = FileOps.Hash(temporaryShortcut);
            }

            var pending = new LauncherInstallationOwnership
            {
                ExecutableSha256 = installedHash,
                ShortcutSha256 = shortcutHash,
                PendingExecutableSha256 = sourceHash,
                PendingShortcutSha256 = desiredShortcutHash
            };
            FileOps.WriteJson(ownershipPath, pending);
            checkpoint?.Invoke("installation-prepared");
            EnsureNoPendingSession(cache);
            EnsureUnchanged(shortcut, shortcutHash);
            if (!shortcutCorrect)
            {
                File.Move(temporaryShortcut!, shortcut, overwrite: shortcutHash != null);
                temporaryShortcut = null;
            }
            EnsureUnchanged(shortcut, desiredShortcutHash);
            if (!shortcuts.Matches(shortcut, installed, root)) throw new InvalidDataException("The installed launcher shortcut is invalid.");
            checkpoint?.Invoke("shortcut-installed");

            EnsureNoPendingSession(cache);
            EnsureUnchanged(source, sourceHash);
            EnsureUnchanged(installed, installedHash);
            if (fromRoot)
            {
                // A same-volume rename works for a running Windows apphost and avoids an updater/helper process.
                File.Move(source, installed, overwrite: installedHash != null);
                log?.Invoke($"Moved the launcher to {PrivateDirectory}/{ExecutableName}; use {ShortcutName} for future modded sessions.");
            }
            EnsureUnchanged(installed, sourceHash);
            checkpoint?.Invoke("executable-relocated");
            FileOps.WriteJson(ownershipPath, new LauncherInstallationOwnership
            {
                ExecutableSha256 = sourceHash,
                ShortcutSha256 = desiredShortcutHash
            });
            checkpoint?.Invoke("installation-complete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or COMException or PlatformNotSupportedException)
        {
            throw new IOException("Launcher relocation or shortcut setup did not finish. " + ex.Message +
                $" Rerun the available {ExecutableName} to retry. If {ExecutableName} remains in the game root, SL-AC may reject vanilla startup until it is moved into {PrivateDirectory}.", ex);
        }
        finally
        {
            if (temporaryShortcut != null && File.Exists(temporaryShortcut))
            {
                try { File.Delete(temporaryShortcut); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void EnsureNoPendingSession(string cache)
    {
        string journal = FileOps.SafePath(cache, "session.json");
        if (File.Exists(journal) || Directory.Exists(journal))
            throw new IOException("A recovery journal still exists; restore the interrupted game session before relocating the launcher.");
    }

    private static void ValidateOwnership(LauncherInstallationOwnership ownership)
    {
        if (ownership.SchemaVersion != 1 || new[] { ownership.ExecutableSha256, ownership.ShortcutSha256,
                ownership.PendingExecutableSha256, ownership.PendingShortcutSha256 }.Any(hash => hash != null &&
                (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))))
            throw new InvalidDataException("Invalid launcher installation ownership record.");
    }

    private static void RequireRegularFileOrMissing(string path)
    {
        FileOps.RejectLink(path);
        if (Directory.Exists(path)) throw new IOException($"A directory occupies a required launcher file path: {path}");
    }

    private static string? HashIfPresent(string path) => File.Exists(path) ? FileOps.Hash(path) : null;
    private static bool MatchesEither(string actual, string? current, string? pending) =>
        (current != null && FileOps.HashEquals(actual, current)) || (pending != null && FileOps.HashEquals(actual, pending));
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void EnsureUnchanged(string path, string? expected)
    {
        RequireRegularFileOrMissing(path);
        if (!FileOps.HashEquals(HashIfPresent(path), expected)) throw new IOException($"A launcher installation file changed unexpectedly: {path}. It was not overwritten.");
    }
}

internal sealed class LauncherInstallationOwnership
{
    public int SchemaVersion { get; set; } = 1;
    public string? ExecutableSha256 { get; set; }
    public string? ShortcutSha256 { get; set; }
    public string? PendingExecutableSha256 { get; set; }
    public string? PendingShortcutSha256 { get; set; }
}

internal interface ILauncherShortcutStore
{
    void Write(string path, string executable, string workingDirectory);
    bool Matches(string path, string executable, string workingDirectory);
}

internal sealed class WindowsLauncherShortcutStore : ILauncherShortcutStore
{
    public void Write(string path, string executable, string workingDirectory)
    {
        WithLink(link =>
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(workingDirectory);
            link.SetArguments("");
            link.SetDescription("Launch SCP:SL with SCPSL-ModLaunch and restore vanilla files when it closes.");
            link.SetIconLocation(executable, 0);
            link.SetShowCmd(1);
            ((IPersistFile)link).Save(path, true);
            return true;
        });
    }

    public bool Matches(string path, string executable, string workingDirectory) => WithLink(link =>
    {
        ((IPersistFile)link).Load(path, 0);
        var target = new StringBuilder(32768);
        var working = new StringBuilder(32768);
        link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH: do not resolve or search for targets.
        link.GetWorkingDirectory(working, working.Capacity);
        return SamePath(target.ToString(), executable) && SamePath(working.ToString(), workingDirectory);
    });

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static T WithLink<T>(Func<IShellLinkW, T> action)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Shortcut creation requires Windows or Proton.");
        object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), throwOnError: true)!)!;
        try { return action((IShellLinkW)instance); }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maximum, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int maximum);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximum);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximum);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maximum, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
