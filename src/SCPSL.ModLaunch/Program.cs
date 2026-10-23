using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SCPSL.ModLaunch.Core;
using SCPSL.ModLaunch.Core.Provisioning;

namespace SCPSL.ModLaunch;

internal static class Program
{
    private static readonly object LogGate = new();
    private static StreamWriter? logFile;

    public static async Task<int> Main(string[] args)
    {
        SessionTransaction? transaction = null;
        SessionJournal? journal = null;
        StreamWriter? writer = null;
        DirectoryLock? directoryLock = null;
        LauncherUpdater? updater = null;
        Task? updateCheck = null;
        FileVerificationCache? verification = null;
        var startup = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        bool gameStarted = false;
        bool restoreAfterGame = true;
        bool finishingInstallation = false;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (gameStarted) Log(restoreAfterGame ? "Close SCP:SL to finish the session and restore its files." : "Close SCP:SL to finish the session; modded files will remain active.");
            else cancellation.Cancel();
        };
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run the Windows executable directly or through Steam Proton.");
            string processPath = Environment.ProcessPath ?? throw new IOException("Cannot locate SCPSL-ModLaunch.exe.");
            // Both the initial drop-in EXE and its permanent private location identify the same game.
            string root = LauncherInstallation.ResolveGameDirectory(processPath, requireGameFiles: false);
            directoryLock = new DirectoryLock(root);
            string cache = FileOps.SafePath(root, ".modlaunch");
            string logPath = FileOps.SafePath(cache, $"logs/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            writer = new StreamWriter(logPath) { AutoFlush = true };
            lock (LogGate) logFile = writer;
            Version launcherVersion = Assembly.GetExecutingAssembly().GetName().Version!;
            Log($"SCPSL-ModLaunch {launcherVersion.ToString(launcherVersion.Revision > 0 ? 4 : 3)} — reversible modded session");
            try { updater = new LauncherUpdater(root, processPath, launcherVersion, Log); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log("Launcher update check unavailable: " + ex.Message);
            }
            // Finish a download left by a terminated launcher before starting another session.
            if (updater?.TryApplyPending() == true)
            {
                string updatedPath = updater.AppliedExecutablePath!;
                updater.Dispose();
                updater = null;
                directoryLock.Dispose();
                directoryLock = null;
                var restart = new ProcessStartInfo(updatedPath) { WorkingDirectory = root, UseShellExecute = false };
                foreach (string argument in args) restart.ArgumentList.Add(argument);
                using var updated = Process.Start(restart) ?? throw new IOException("Could not start the updated launcher. Run SCPSL-ModLaunch again.");
                await updated.WaitForExitAsync();
                return updated.ExitCode;
            }
            // Independent of game cancellation: even broken arguments, files or recovery must not skip updates.
            updateCheck = updater?.CheckAsync();
            LaunchArguments arguments = GameArguments.Parse(args);
            restoreAfterGame = !arguments.NoRestore;
            _ = LauncherInstallation.ResolveGameDirectory(processPath);
            GameProcessGuard.EnsureStopped(root);
            using (StartupPhase.Start(Log, "metadata cache load")) verification = new FileVerificationCache(root, Log);
            transaction = new SessionTransaction(root, Log, verification: verification);
            try { journal = transaction.LoadPending(); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new RestorationException(["Recovery journal could not be validated: " + ex.Message]);
            }
            bool reuseActiveSession = false;
            string? dotnetRoot = null;
            if (journal != null && arguments.NoRestore)
            {
                try
                {
                    using (StartupPhase.Start(Log, "active session verification")) transaction.VerifyActive(journal);
                    dotnetRoot = string.IsNullOrWhiteSpace(journal.DotnetRoot)
                        ? HostRuntime.Resolve().DotnetRoot : HostRuntime.ResolveFromRoot(journal.DotnetRoot).DotnetRoot;
                    reuseActiveSession = true;
                    Log("Reusing the active modded files and retained vanilla backups.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Log("The pending session cannot be reused; restoring it first: " + ex.Message);
                }
            }
            if (journal != null && !reuseActiveSession)
            {
                Log("Restoring the pending session before doing anything else.");
                RestoreResult recovered;
                using (StartupPhase.Start(Log, "interrupted session restoration")) recovered = transaction.Restore(journal);
                if (!recovered.Success) throw new RestorationException(recovered.Failures);
                journal = null;
                RuntimeInstallation.CaptureGeneratedFiles(root, Log, verification);
                verification.Save();
                using (StartupPhase.Start(Log, "cache maintenance")) CacheMaintenance.BeforePreparation(root, Log);
                finishingInstallation = true;
                LauncherInstallation.Complete(root, processPath, Log);
                finishingInstallation = false;
                Dialog("SCPSL-ModLaunch recovery", "Original game files restored. You can now launch vanilla, or run SCPSL-ModLaunch again for a modded session.", false);
                return 0;
            }
            if (!reuseActiveSession)
            {
                using (StartupPhase.Start(Log, "preflight and cache maintenance"))
                {
                    RuntimeInstallation.Preflight(root, arguments.NoMelonLoader);
                    CacheMaintenance.BeforePreparation(root, Log);
                }
                if (arguments.RecoverOnly)
                {
                    Log("No unfinished session exists; loader entry points are inactive.");
                    finishingInstallation = true;
                    LauncherInstallation.Complete(root, processPath, Log);
                    finishingInstallation = false;
                    return 0;
                }
                string settingsPath = FileOps.SafePath(cache, "settings.json");
                ModLaunchSettings settings = File.Exists(settingsPath) ? FileOps.ReadJson<ModLaunchSettings>(settingsPath) : new ModLaunchSettings();
                if (!File.Exists(settingsPath)) FileOps.WriteJson(settingsPath, settings);
                using (StartupPhase.Start(Log, "bundled adapter verification")) ExtractBundledAdapter(cache, verification);
                var preparation = new PreparationService(Log);
                PreparedSession prepared;
                using (StartupPhase.Start(Log, "preparation total"))
                    prepared = await preparation.PrepareAsync(root, cache, settings, cancellation.Token, verification, arguments.NoMelonLoader);
                cancellation.Token.ThrowIfCancellationRequested();
                GameProcessGuard.EnsureStopped(root);
                RuntimeInstallation.Preflight(root, arguments.NoMelonLoader);
                if (!prepared.SkipMelonLoader)
                {
                    using (StartupPhase.Start(Log, "installed runtime validation"))
                        RuntimeInstallation.InstallPersistentFiles(root, prepared, Log, verification);
                }
                using (StartupPhase.Start(Log, "successful preparation maintenance")) CacheMaintenance.AfterSuccessfulPreparation(root, prepared, Log);
                if (!prepared.SkipMelonLoader)
                {
                    using (StartupPhase.Start(Log, "bundled native bootstrap selection")) SelectBundledNativeBootstrap(cache, prepared);
                    LoaderConfig.Update(FileOps.SafePath(root, "UserData/Loader.cfg"), prepared.DotnetRoot);
                }
                dotnetRoot = prepared.DotnetRoot;
                Log("Backing up original files and activating the modded session...");
                using (StartupPhase.Start(Log, "original verification and backups")) journal = transaction.Create(prepared);
                using (StartupPhase.Start(Log, "verification metadata persistence")) verification.Save();
            }
            SessionJournal activeJournal = journal ?? throw new InvalidOperationException("The modded session journal is missing.");
            bool retainFiles = false;
            try
            {
                if (!reuseActiveSession)
                    using (StartupPhase.Start(Log, "session activation")) transaction.Activate(activeJournal);
                verification.LogSummary();
                cancellation.Token.ThrowIfCancellationRequested();
                if (arguments.NoStart)
                {
                    retainFiles = true;
                    Log("Modded files are ready. SCP:SL was not started.");
                    return 0;
                }
                using var game = new Process { StartInfo = GameArguments.StartInfo(root, dotnetRoot!, arguments.GameArguments) };
                using (StartupPhase.Start(Log, "SCP:SL process creation"))
                    if (!game.Start()) throw new IOException("SCP:SL could not be started.");
                gameStarted = true;
                startup.Stop();
                Log($"Startup total to SCP:SL process start: {startup.Elapsed.TotalMilliseconds:F1} ms.");
                // If recording the PID fails, we still must wait before touching the running game's files.
                try { transaction.RecordGameProcess(activeJournal, game); }
                catch (Exception ex) { Log("Could not record game PID; waiting for exit before recovery: " + ex.Message); }
                Log(arguments.NoRestore ? "SCP:SL is running. Modded files will remain active after it closes." : "SCP:SL is running. Close the game normally to restore vanilla files.");
                await game.WaitForExitAsync(CancellationToken.None);
                Log($"SCP:SL exited with code {game.ExitCode}.");
                gameStarted = false;
                retainFiles = arguments.NoRestore;
            }
            finally
            {
                GameProcessGuard.EnsureStopped(root);
                if (retainFiles)
                {
                    verification.Save();
                    Log("Modded files remain active; vanilla backups and the recovery journal have been retained. Run without --modlaunch-no-restore to restore vanilla and exit.");
                }
                else
                {
                    RestoreResult restored;
                    using (StartupPhase.Start(Log, "session restoration")) restored = transaction.Restore(activeJournal);
                    if (!restored.Success) throw new RestorationException(restored.Failures);
                    journal = null;
                    using (StartupPhase.Start(Log, "generated file capture")) RuntimeInstallation.CaptureGeneratedFiles(root, Log, verification);
                    verification.Save();
                    CacheMaintenance.CleanCompletedSessions(root, Log);
                }
            }
            if (retainFiles) return 0;
            finishingInstallation = true;
            LauncherInstallation.Complete(root, processPath, Log);
            finishingInstallation = false;
            return 0;
        }
        catch (Exception ex)
        {
            string message = ex is OperationCanceledException ? "Preparation cancelled." : ex.Message;
            if (journal != null && ex is not RestorationException)
                message += "\n\nAn unfinished session remains. Close SCP:SL and rerun SCPSL-ModLaunch to recover before launching vanilla.";
            if (ex is RestorationException)
                message += "\n\nVerify SCP:SL files in Steam: Properties > Installed Files > Verify integrity. Rerun SCPSL-ModLaunch for recovery. Steam verification may not remove an extra version.dll; do not launch vanilla while that loader entry point remains. Backups and the recovery journal have been retained.";
            if (finishingInstallation)
                message += "\n\nThe game files are restored, but launcher relocation or shortcut setup is incomplete. A remaining SCPSL-ModLaunch.exe in the game root can block vanilla startup. Resolve the reported problem, then run the available launcher with --modlaunch-recover-only to finish setup.";
            Log(message);
            Dialog("SCPSL-ModLaunch", message, true);
            return ex is RestorationException ? 2 : 1;
        }
        finally
        {
            try
            {
                if (updateCheck != null) await updateCheck;
                // Runs after both success and failure. Only launcher files are replaced here;
                // a game's pending recovery journal is preserved for the next invocation.
                updater?.TryApplyPending();
            }
            finally
            {
                updater?.Dispose();
                directoryLock?.Dispose();
                lock (LogGate) { logFile = null; writer?.Dispose(); }
            }
        }
    }

    private static void SelectBundledNativeBootstrap(string cache, PreparedSession prepared)
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("native-bootstrap-pr13.zip")
            ?? throw new InvalidDataException("The packaged PR13 native bootstrap is missing. Use a complete SCPSL-ModLaunch release package.");
        BundledNativeBootstrap.InstallAndSelect(cache, prepared, resource, Log);
    }

    private static void ExtractBundledAdapter(string cache, FileVerificationCache verification)
    {
        using Stream? resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("adapter-bundled.zip");
        if (resource == null) return;
        BundledAdapterInstallation.Install(cache, resource, verification: verification);
    }

    private static void Log(string message)
    {
        lock (LogGate)
        {
            Console.WriteLine(message);
            try { logFile?.WriteLine($"{DateTime.UtcNow:O} {message}"); }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }
    }

    private static void Dialog(string title, string message, bool error)
    {
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("MODLAUNCH_NO_DIALOG") != "1")
            _ = MessageBoxW(IntPtr.Zero, message, title, error ? 0x10u : 0x40u);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr parent, string text, string caption, uint type);

    private sealed class RestorationException(IEnumerable<string> failures) : IOException("Some game files could not be restored:\n" + string.Join("\n", failures));
}
