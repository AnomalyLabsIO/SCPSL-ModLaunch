using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace SCPSL.ModLaunch.Core;

/// <summary>Moves the drop-in launcher out of the retail game's root after restoration or verified session retention.</summary>
public static class LauncherInstallation
{
    public const string ExecutableName = "SCPSL-ModLaunch.exe";
    public const string ShortcutName = "SCPSL ModLaunch.lnk";
    public const string GuiShortcutName = "SCPSL ModLaunch GUI.lnk";
    public const string LegacyShortcutName = "SCPSL-ModLaunch.lnk";
    public const string CliShortcutArguments = "--modlaunch-cli";
    public const string GuiShortcutArguments = "--modlaunch-gui";
    private const string PrivateDirectory = ".modlaunch";
    private const string OwnershipName = "launcher-installation.json";

    public static string ResolveGameDirectory(string executablePath, bool requireGameFiles = true)
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
        if (requireGameFiles && !File.Exists(FileOps.SafePath(root, "SCPSL.exe")) &&
            !File.Exists(FileOps.SafePath(root, PrivateDirectory + "/session.json")))
            throw new InvalidDataException($"Place {ExecutableName} beside SCPSL.exe or in that game's {PrivateDirectory} directory.");
        return root;
    }

    public static void Complete(string gameDirectory, string executablePath, Action<string>? log = null) =>
        Complete(gameDirectory, executablePath, new WindowsLauncherShortcutStore(), log);

    public static void CompleteRetainedSession(string gameDirectory, string executablePath, Action<string>? log = null) =>
        Complete(gameDirectory, executablePath, new WindowsLauncherShortcutStore(), log, retainActiveSession: true);

    // The caller holds the game's exclusive directory lock and has stopped the game/helpers.
    internal static void Complete(string gameDirectory, string executablePath, ILauncherShortcutStore shortcuts,
        Action<string>? log = null, Action<string>? checkpoint = null, bool retainActiveSession = false)
    {
        var temporaryShortcuts = new List<string>();
        try
        {
            string root = Path.GetFullPath(gameDirectory);
            string cache = FileOps.SafePath(root, PrivateDirectory);
            string source = Path.GetFullPath(executablePath);
            string rootExecutable = FileOps.SafePath(root, ExecutableName);
            string installed = FileOps.SafePath(cache, ExecutableName);
            string shortcut = FileOps.SafePath(root, ShortcutName);
            string guiShortcut = FileOps.SafePath(root, GuiShortcutName);
            string legacyShortcut = FileOps.SafePath(root, LegacyShortcutName);
            string ownershipPath = FileOps.SafePath(cache, OwnershipName);
            bool fromRoot = SamePath(source, rootExecutable);
            if (!fromRoot && !SamePath(source, installed))
                throw new InvalidDataException("The launcher is outside its supported root or private-directory location.");
            string journalPath = FileOps.SafePath(cache, "session.json");
            string? retainedJournalHash = null;
            if (retainActiveSession)
            {
                // Relocation changes launcher files only. Verify the retained game and its recovery
                // backups first, then keep the exact journal intact throughout the handoff.
                RequireRegularFileOrMissing(journalPath);
                retainedJournalHash = FileOps.Hash(journalPath);
                var transaction = new SessionTransaction(root);
                var retained = transaction.LoadPending() ?? throw new IOException("The retained session journal is missing.");
                transaction.VerifyActive(retained);
            }
            void CheckSession()
            {
                if (retainActiveSession) EnsureUnchanged(journalPath, retainedJournalHash);
                else EnsureNoPendingSession(cache);
            }
            CheckSession();
            foreach (string path in new[] { source, installed, shortcut, guiShortcut, legacyShortcut, ownershipPath, rootExecutable }) RequireRegularFileOrMissing(path);
            if (!File.Exists(source)) throw new FileNotFoundException("The running launcher's file is missing.", source);
            if (!fromRoot && File.Exists(rootExecutable))
                throw new IOException($"A launcher also exists at {rootExecutable}. Run that root copy to finish installing the replacement.");

            LauncherInstallationOwnership? ownership = File.Exists(ownershipPath)
                ? FileOps.ReadJson<LauncherInstallationOwnership>(ownershipPath) : null;
            if (ownership != null) ValidateOwnership(ownership);
            string sourceHash = FileOps.Hash(source);
            string? installedHash = HashIfPresent(installed);
            string? shortcutHash = HashIfPresent(shortcut);
            string? guiShortcutHash = HashIfPresent(guiShortcut);
            string? legacyShortcutHash = HashIfPresent(legacyShortcut);
            // A manually relocated, currently running launcher can establish its own ownership.
            // An existing destination encountered by a root copy needs prior recorded ownership.
            if (installedHash != null && (fromRoot || ownership != null) &&
                !MatchesEither(installedHash, ownership?.ExecutableSha256, ownership?.PendingExecutableSha256))
                throw new IOException($"The destination launcher is unrecognized or changed: {installed}. It was not overwritten.");
            // Explorer may update link metadata, and users may add arguments or customize its icon.
            // Keep such links byte-for-byte when their target and working directory still match.
            // A changed destination is never silently retargeted, even if its old bytes were owned.
            foreach (string path in new[] { shortcut, guiShortcut })
                if (File.Exists(path) && !shortcuts.Matches(path, installed, root))
                    throw new IOException($"A shortcut with a different target or working directory already exists: {path}. It was not overwritten; move or rename it before retrying.");
            string? ownedLegacyHash = null;
            if (legacyShortcutHash != null && ownership != null)
                ownedLegacyHash = ownership.SchemaVersion == 1
                    ? MatchesEither(legacyShortcutHash, ownership.ShortcutSha256, ownership.PendingShortcutSha256) ? legacyShortcutHash : null
                    : ownership.LegacyShortcutSha256;
            // Only retire the exact old default link we recorded. A customized or unowned link stays.
            bool retireLegacy = legacyShortcutHash != null && ownedLegacyHash != null &&
                FileOps.HashEquals(legacyShortcutHash, ownedLegacyHash) && shortcuts.Matches(legacyShortcut, installed, root) &&
                shortcuts.ReadArguments(legacyShortcut) == "";
            Directory.CreateDirectory(cache);
            (string? Temporary, string Hash) PrepareShortcut(string? existingHash, string arguments)
            {
                if (existingHash != null) return (null, existingHash);
                string temporary = FileOps.SafePath(cache, "launcher-shortcut-" + Guid.NewGuid().ToString("N") + ".tmp");
                temporaryShortcuts.Add(temporary);
                shortcuts.Write(temporary, installed, root, arguments);
                if (!shortcuts.Matches(temporary, installed, root) || shortcuts.ReadArguments(temporary) != arguments)
                    throw new InvalidDataException("The generated launcher shortcut did not pass validation.");
                // COM Save has closed its handle; flush the generated file before publishing its ownership.
                using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) file.Flush(true);
                return (temporary, FileOps.Hash(temporary));
            }
            var cli = PrepareShortcut(shortcutHash, CliShortcutArguments);
            var gui = PrepareShortcut(guiShortcutHash, GuiShortcutArguments);

            var pending = new LauncherInstallationOwnership
            {
                ExecutableSha256 = installedHash,
                ShortcutSha256 = shortcutHash,
                GuiShortcutSha256 = guiShortcutHash,
                LegacyShortcutSha256 = ownedLegacyHash,
                PendingExecutableSha256 = sourceHash,
                PendingShortcutSha256 = cli.Hash,
                PendingGuiShortcutSha256 = gui.Hash
            };
            FileOps.WriteJson(ownershipPath, pending);
            checkpoint?.Invoke("installation-prepared");
            CheckSession();
            EnsureUnchanged(shortcut, shortcutHash);
            if (cli.Temporary != null)
            {
                File.Move(cli.Temporary, shortcut);
                temporaryShortcuts.Remove(cli.Temporary);
            }
            EnsureUnchanged(shortcut, cli.Hash);
            if (!shortcuts.Matches(shortcut, installed, root)) throw new InvalidDataException("The installed launcher shortcut is invalid.");
            checkpoint?.Invoke("cli-shortcut-installed");
            CheckSession();
            EnsureUnchanged(guiShortcut, guiShortcutHash);
            if (gui.Temporary != null)
            {
                File.Move(gui.Temporary, guiShortcut);
                temporaryShortcuts.Remove(gui.Temporary);
            }
            EnsureUnchanged(guiShortcut, gui.Hash);
            if (!shortcuts.Matches(guiShortcut, installed, root)) throw new InvalidDataException("The installed GUI shortcut is invalid.");
            checkpoint?.Invoke("gui-shortcut-installed");
            checkpoint?.Invoke("shortcut-installed");

            CheckSession();
            EnsureUnchanged(shortcut, cli.Hash);
            EnsureUnchanged(guiShortcut, gui.Hash);
            EnsureUnchanged(source, sourceHash);
            EnsureUnchanged(installed, installedHash);
            if (fromRoot)
            {
                // A same-volume rename works for a running Windows apphost and avoids an updater/helper process.
                File.Move(source, installed, overwrite: installedHash != null);
                log?.Invoke($"Moved the launcher to {PrivateDirectory}/{ExecutableName}; use {ShortcutName} for a CLI session or {GuiShortcutName} for the GUI.");
            }
            EnsureUnchanged(installed, sourceHash);
            checkpoint?.Invoke("executable-relocated");
            if (retireLegacy)
            {
                CheckSession();
                EnsureUnchanged(legacyShortcut, legacyShortcutHash);
                File.Delete(legacyShortcut);
                ownedLegacyHash = null;
                checkpoint?.Invoke("legacy-shortcut-retired");
            }
            EnsureUnchanged(shortcut, cli.Hash);
            EnsureUnchanged(guiShortcut, gui.Hash);
            CheckSession();
            FileOps.WriteJson(ownershipPath, new LauncherInstallationOwnership
            {
                ExecutableSha256 = sourceHash,
                ShortcutSha256 = cli.Hash,
                GuiShortcutSha256 = gui.Hash,
                LegacyShortcutSha256 = ownedLegacyHash
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
            foreach (string temporaryShortcut in temporaryShortcuts.Where(File.Exists))
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

    // Updating launcher bytes does not change any game files, so this remains usable
    // when a failed game session needs a newer launcher's recovery code.
    internal static void RecordExecutableUpdate(string root, string executablePath, string oldHash, string newHash, bool completed)
    {
        if (!SamePath(executablePath, FileOps.SafePath(root, ".modlaunch/" + ExecutableName))) return;
        string path = FileOps.SafePath(root, ".modlaunch/" + OwnershipName);
        var owner = File.Exists(path) ? FileOps.ReadJson<LauncherInstallationOwnership>(path)
            : new LauncherInstallationOwnership { ExecutableSha256 = oldHash };
        ValidateOwnership(owner);
        if (!MatchesEither(oldHash, owner.ExecutableSha256, owner.PendingExecutableSha256) &&
            !(completed && MatchesEither(newHash, owner.ExecutableSha256, owner.PendingExecutableSha256)))
            throw new IOException("The installed launcher ownership changed; its update was not applied.");
        if (completed) { owner.ExecutableSha256 = newHash; owner.PendingExecutableSha256 = null; }
        else owner.PendingExecutableSha256 = newHash;
        FileOps.WriteJson(path, owner);
    }

    private static void ValidateOwnership(LauncherInstallationOwnership ownership)
    {
        if (ownership.SchemaVersion is not (1 or 2) || new[] { ownership.ExecutableSha256, ownership.ShortcutSha256,
                ownership.PendingExecutableSha256, ownership.PendingShortcutSha256, ownership.GuiShortcutSha256,
                ownership.PendingGuiShortcutSha256, ownership.LegacyShortcutSha256 }.Any(hash => hash != null &&
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
    public int SchemaVersion { get; set; } = 2;
    public string? ExecutableSha256 { get; set; }
    public string? ShortcutSha256 { get; set; }
    public string? PendingExecutableSha256 { get; set; }
    public string? PendingShortcutSha256 { get; set; }
    public string? GuiShortcutSha256 { get; set; }
    public string? PendingGuiShortcutSha256 { get; set; }
    public string? LegacyShortcutSha256 { get; set; }
}

internal interface ILauncherShortcutStore
{
    void Write(string path, string executable, string workingDirectory, string arguments);
    bool Matches(string path, string executable, string workingDirectory);
    string ReadArguments(string path);
}

internal sealed class WindowsLauncherShortcutStore : ILauncherShortcutStore
{
    public void Write(string path, string executable, string workingDirectory, string arguments)
    {
        WithLink(link =>
        {
            link.SetPath(executable);
            link.SetWorkingDirectory(workingDirectory);
            link.SetArguments(arguments);
            link.SetDescription(arguments == LauncherInstallation.GuiShortcutArguments
                ? "Open SCPSL-ModLaunch to inspect the game, manage mods, and choose a launch action."
                : "Launch SCP:SL with SCPSL-ModLaunch and restore vanilla files when it closes.");
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

    public string ReadArguments(string path) => WithLink(link =>
    {
        ((IPersistFile)link).Load(path, 0);
        var arguments = new StringBuilder(32768);
        link.GetArguments(arguments, arguments.Capacity);
        return arguments.ToString();
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
