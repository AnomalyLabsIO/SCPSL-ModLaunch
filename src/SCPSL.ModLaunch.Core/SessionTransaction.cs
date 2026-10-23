using System.IO.Compression;

namespace SCPSL.ModLaunch.Core;

public enum FileAction { Observe, Replace, Park }

public sealed class JournalEntry
{
    public string RelativePath { get; set; } = "";
    public FileAction Action { get; set; }
    public string? OriginalHash { get; set; }
    public string? PreparedHash { get; set; }
    public string? PreparedSource { get; set; }
    public string? BackupPath { get; set; }
    public string? ParkedPath { get; set; }
}

public sealed class SessionJournal
{
    // The default preserves recovery of journals written before neutral bootstrap staging.
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string GameDirectory { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int? GameProcessId { get; set; }
    public DateTime? GameProcessStartedUtc { get; set; }
    public List<JournalEntry> Entries { get; set; } = [];
}

public sealed class RestoreResult
{
    public bool Success => Failures.Count == 0;
    public List<string> Failures { get; } = [];
}

/// <summary>Write-ahead, hash-guarded reversible changes. The caller must hold DirectoryLock throughout.</summary>
public sealed class SessionTransaction(string gameDirectory, Action<string>? log = null, Action<string>? checkpoint = null,
    FileVerificationCache? verification = null)
{
    public const string RetailExecutable = "SCPSL.exe";
    public const string RuntimeMetadataPath = "SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat";
    private readonly string root = Path.GetFullPath(gameDirectory);
    private string Cache => FileOps.SafePath(root, ".modlaunch");
    private string JournalPath => FileOps.SafePath(Cache, "session.json");
    private static readonly string[] ProtectedDirectories = ["Mods", "Plugins", "UserData", "UserLibs", "MelonLoader", ".modlaunch"];

    public SessionJournal? LoadPending()
    {
        if (!File.Exists(JournalPath)) return null;
        var journal = FileOps.ReadJson<SessionJournal>(JournalPath);
        Validate(journal);
        return journal;
    }

    public SessionJournal Create(PreparedSession prepared)
    {
        if (File.Exists(JournalPath)) throw new InvalidOperationException("An unfinished session must be restored first.");
        if (prepared.LayoutVersion is not (2 or 3)) throw new InvalidDataException("The prepared cache uses an obsolete layout; prepare the game again.");
        if (prepared.Inputs.Count == 0) throw new InvalidDataException("Preparation supplied no original file identities.");
        var journal = new SessionJournal { SchemaVersion = 3, Id = Guid.NewGuid().ToString("N"), GameDirectory = root, Fingerprint = prepared.Fingerprint };
        var entries = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in prepared.Inputs)
        {
            string rel = NormalizeTarget(input.RelativePath);
            if (rel.Equals("version.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"A modded entry point cannot be a game original: {rel}");
            if (!entries.TryAdd(rel, new JournalEntry { RelativePath = rel, OriginalHash = input.Sha256 }))
                throw new InvalidDataException($"Duplicate input: {rel}");
            CheckHash(FileOps.SafePath(root, rel), input.Sha256);
        }
        foreach (var file in prepared.Files)
        {
            string rel = NormalizeTarget(file.RelativePath);
            if (rel.Equals(RuntimeMetadataPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime metadata must remain unchanged; a parser copy cannot be activated.");
            if (rel.Equals("version.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An adapter cannot supply the loader bootstrap.");
            string source = FileOps.SafePath(prepared.PreparedDirectory, file.SourcePath ?? file.RelativePath);
            CheckHash(source, file.Sha256);
            if (!entries.TryGetValue(rel, out var entry))
            {
                EnsureAbsent(rel);
                entries.Add(rel, entry = new JournalEntry { RelativePath = rel });
            }
            if (entry.Action != FileAction.Observe) throw new InvalidDataException($"Duplicate prepared output: {rel}");
            entry.Action = FileAction.Replace;
            entry.PreparedHash = file.Sha256;
            entry.PreparedSource = Path.GetRelativePath(Cache, source).Replace('\\', '/');
            _ = FileOps.SafePath(Cache, entry.PreparedSource);
        }
        if (!entries.ContainsKey(RetailExecutable) || !entries.ContainsKey("GameAssembly.dll"))
            throw new InvalidDataException("Preparation does not identify required game originals.");
        if (!entries.TryGetValue("SL-AC.dll", out var ac)) throw new InvalidDataException("Preparation must record whether SL-AC.dll exists.");
        if (ac.Action != FileAction.Observe) throw new InvalidDataException("The adapter must not replace SL-AC.dll.");
        ac.Action = FileAction.Park;
        ac.ParkedPath = $"{CacheLayout.SessionFolder(journal.Id)}/SL-AC.dll.original";
        EnsureAbsent("version.dll");
        string bootstrapSource = $"{CacheLayout.SessionFolder(journal.Id)}/version.dll.modded";
        ExtractBootstrap(prepared, FileOps.SafePath(Cache, bootstrapSource));
        entries.Add("version.dll", new JournalEntry { RelativePath = "version.dll", Action = FileAction.Replace, PreparedHash = prepared.BootstrapHash, PreparedSource = bootstrapSource });

        foreach (var entry in entries.Values.Where(e => e.OriginalHash != null))
        {
            entry.BackupPath = CacheLayout.OriginalFile(entry.RelativePath, entry.OriginalHash!);
            string backup = FileOps.SafePath(Cache, entry.BackupPath);
            if (File.Exists(backup)) CheckHash(backup, entry.OriginalHash);
            else
                FileOps.CopyVerified(FileOps.SafePath(root, entry.RelativePath), backup, entry.OriginalHash!);
        }
        journal.Entries = entries.Values.ToList();
        Validate(journal);
        FileOps.WriteJson(JournalPath, journal);
        checkpoint?.Invoke("journal-created");
        return journal;
    }

    public void Activate(SessionJournal journal)
    {
        Validate(journal);
        if (journal.SchemaVersion != 3) throw new InvalidDataException("Legacy sessions support recovery only.");
        // Keep the original executable available until every other modded file is installed.
        foreach (var entry in journal.Entries.OrderBy(e => Is(e, RetailExecutable) ? 2 : Is(e, "version.dll") ? 1 : 0))
            ActivateEntry(journal, entry);
    }

    private void ActivateEntry(SessionJournal journal, JournalEntry entry)
    {
        checkpoint?.Invoke("before-activate:" + entry.RelativePath);
        string destination = FileOps.SafePath(root, entry.RelativePath);
        CheckHash(destination, entry.OriginalHash);
        // Transactional checks still read all bytes. Retire the old stamp before writes/moves.
        verification?.Invalidate(destination);
        if (entry.Action == FileAction.Replace)
            FileOps.CopyVerified(FileOps.SafePath(Cache, entry.PreparedSource!), destination, entry.PreparedHash!);
        else if (entry.Action == FileAction.Park && entry.OriginalHash != null)
        {
            string parked = FileOps.SafePath(Cache, entry.ParkedPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
            File.Move(destination, parked);
            CheckHash(parked, entry.OriginalHash);
        }
        checkpoint?.Invoke("after-activate:" + entry.RelativePath);
    }

    public void RecordGameProcess(SessionJournal journal, System.Diagnostics.Process process)
    {
        journal.GameProcessId = process.Id;
        journal.GameProcessStartedUtc = process.StartTime.ToUniversalTime();
        FileOps.WriteJson(JournalPath, journal);
    }

    public RestoreResult Restore(SessionJournal journal)
    {
        Validate(journal);
        var result = new RestoreResult();
        // The loader goes dormant first. SCPSL.exe is restored only after every other original verifies.
        RestoreOne(journal, Find(journal, "version.dll"), result);
        foreach (var entry in journal.Entries.Where(e => !Is(e, "version.dll") && !Is(e, RetailExecutable)))
            RestoreOne(journal, entry, result);

        // Do not reinstate the retail executable over a directory whose other files failed restoration.
        if (result.Success)
        {
            VerifyOriginals(journal, result, includeRetail: false);
            if (result.Success) RestoreOne(journal, Find(journal, RetailExecutable), result);
        }
        if (result.Success)
        {
            VerifyOriginals(journal, result, includeRetail: true);
            if (result.Success)
            {
                try { File.Delete(JournalPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { result.Failures.Add("Recovery journal could not be cleared: " + ex.Message); }
                if (result.Success)
                {
                    checkpoint?.Invoke("journal-cleared");
                    // Final full verification recorded stamps for the restored originals,
                    // including files recreated through temporary copies and atomic moves.
                    verification?.Save();
                    log?.Invoke("Original game files restored and verified.");
                }
            }
        }
        return result;
    }

    private void RestoreOne(SessionJournal journal, JournalEntry entry, RestoreResult result)
    {
        checkpoint?.Invoke("before-restore:" + entry.RelativePath);
        try
        {
            RestoreWithRetry(journal, entry);
            checkpoint?.Invoke("after-restore:" + entry.RelativePath);
        }
        catch (Exception ex) when (IsFileError(ex)) { result.Failures.Add($"{entry.RelativePath}: {ex.Message}"); }
    }

    private void VerifyOriginals(SessionJournal journal, RestoreResult result, bool includeRetail)
    {
        foreach (var entry in journal.Entries.Where(e => includeRetail || !Is(e, RetailExecutable)))
        {
            try { CheckHash(FileOps.SafePath(root, entry.RelativePath), entry.OriginalHash); }
            catch (Exception ex) when (IsFileError(ex)) { result.Failures.Add($"{entry.RelativePath}: final verification failed: {ex.Message}"); }
        }
    }

    private void RestoreWithRetry(SessionJournal journal, JournalEntry entry)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool reported = false;
        while (true)
        {
            try { RestoreEntry(journal, entry); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                (ex.HResult & 0xffff) is 5 or 32 or 33 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (!reported) log?.Invoke($"Waiting for Windows to release {entry.RelativePath}...");
                reported = true;
                Thread.Sleep(200);
            }
        }
    }

    private void RestoreEntry(SessionJournal journal, JournalEntry entry)
    {
        string destination = FileOps.SafePath(root, entry.RelativePath);
        if (Directory.Exists(destination)) throw new InvalidDataException("A directory occupies a managed file path.");
        string? current = File.Exists(destination) ? FileOps.Hash(destination) : null;
        if (FileOps.HashEquals(current, entry.OriginalHash)) return;
        if (entry.Action == FileAction.Observe)
            throw new InvalidDataException("An unchanged input was replaced externally; refusing to overwrite it.");
        string? expectedActive = entry.Action == FileAction.Park ? null : entry.PreparedHash;
        if (!FileOps.HashEquals(current, expectedActive))
            throw new InvalidDataException("Unexpected file contents (possibly a Steam update); refusing to overwrite them.");
        verification?.Invalidate(destination);
        if (entry.OriginalHash == null)
        {
            if (Is(entry, "version.dll"))
            {
                // Image sections can prevent deletion after exit. A neutral name is dormant immediately.
                string dormant = FileOps.SafePath(Cache, journal.SchemaVersion >= 3
                    ? $"{CacheLayout.SessionFolder(journal.Id)}/version.dll.disabled"
                    : $"sessions/{journal.Id}/parked/bootstrap.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(dormant)!);
                File.Move(destination, dormant);
                try { File.Delete(dormant); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log?.Invoke("Deferred removal of dormant session payload: " + Path.GetFileName(dormant)); }
            }
            else if (File.Exists(destination)) File.Delete(destination);
        }
        else
        {
            string backup = FileOps.SafePath(Cache, entry.BackupPath!);
            CheckHash(backup, entry.OriginalHash);
            string? parked = entry.ParkedPath == null ? null : FileOps.SafePath(Cache, entry.ParkedPath);
            if (entry.Action == FileAction.Park && parked != null && File.Exists(parked))
            {
                CheckHash(parked, entry.OriginalHash);
                File.Move(parked, destination);
            }
            else FileOps.CopyVerified(backup, destination, entry.OriginalHash);
        }
        CheckHash(destination, entry.OriginalHash);
    }

    private void Validate(SessionJournal journal)
    {
        if (journal.SchemaVersion is not (1 or 2 or 3) || !Guid.TryParseExact(journal.Id, "N", out _) || string.IsNullOrWhiteSpace(journal.GameDirectory) || journal.Entries == null ||
            !string.Equals(Path.GetFullPath(journal.GameDirectory), root, StringComparison.OrdinalIgnoreCase) || journal.Entries.Count == 0)
            throw new InvalidDataException("Invalid recovery journal; retain backups and verify the game in Steam.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Entries)
        {
            if (entry == null) throw new InvalidDataException("Null recovery journal entry.");
            string rel = NormalizeTarget(entry.RelativePath);
            if (!paths.Add(rel) || !Enum.IsDefined(entry.Action)) throw new InvalidDataException("Duplicate or invalid journal entry.");
            _ = FileOps.SafePath(root, rel);
            ValidateHash(entry.OriginalHash);
            ValidateHash(entry.PreparedHash);
            if (entry.OriginalHash != null)
            {
                string expected = journal.SchemaVersion >= 3 ? CacheLayout.OriginalFile(rel, entry.OriginalHash)
                    : $"originals/{entry.OriginalHash.ToUpperInvariant()}.bin";
                if (entry.BackupPath != expected) throw new InvalidDataException("Invalid backup location.");
                _ = FileOps.SafePath(Cache, entry.BackupPath);
            }
            if (entry.Action == FileAction.Replace)
            {
                if (entry.PreparedHash == null || entry.PreparedSource == null) throw new InvalidDataException("Incomplete replacement record.");
                _ = FileOps.SafePath(Cache, entry.PreparedSource);
            }
            else if (entry.PreparedHash != null || entry.PreparedSource != null)
                throw new InvalidDataException("Only replacement records may identify prepared outputs.");
            if (entry.Action == FileAction.Park)
            {
                string? expected = !Is(entry, "SL-AC.dll") ? null : journal.SchemaVersion >= 3
                    ? $"{CacheLayout.SessionFolder(journal.Id)}/SL-AC.dll.original"
                    : journal.SchemaVersion == 1 ? $"sessions/{journal.Id}/parked/SL-AC.dll.disabled" : $"sessions/{journal.Id}/parked/anticheat.bin";
                if (expected == null || entry.ParkedPath != expected) throw new InvalidDataException("Invalid parked file record.");
                _ = FileOps.SafePath(Cache, expected);
            }
            if (Is(entry, "version.dll") && entry.OriginalHash != null)
                throw new InvalidDataException("An existing modded entry point cannot be adopted as a vanilla original.");
        }
        var mandatory = new List<(string Path, FileAction Action, bool NeedsOriginal)>
        {
            (RetailExecutable, FileAction.Replace, true), ("GameAssembly.dll", FileAction.Replace, true),
            ("UnityPlayer.dll", FileAction.Observe, true), ("SCPSL_Data/il2cpp_data/Metadata/global-metadata.dat", FileAction.Observe, true),
            ("SL-AC.dll", FileAction.Park, false), ("version.dll", FileAction.Replace, false)
        };
        if (journal.SchemaVersion >= 2)
        {
            mandatory.Add((UnityBundleVersion.RelativePath, FileAction.Replace, true));
        }
        foreach (var (path, action, needsOriginal) in mandatory)
        {
            var entry = journal.Entries.SingleOrDefault(e => e.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Action != action || (needsOriginal && entry.OriginalHash == null))
                throw new InvalidDataException($"Recovery journal lacks a mandatory file/action: {path}");
            if (action == FileAction.Replace && needsOriginal && path != UnityBundleVersion.RelativePath && FileOps.HashEquals(entry.OriginalHash, entry.PreparedHash))
                throw new InvalidDataException($"The supposed vanilla original already equals the modded output: {path}. Verify your game files first.");
        }
    }

    private void ExtractBootstrap(PreparedSession prepared, string destination)
    {
        ValidateHash(prepared.BootstrapHash);
        if (prepared.BootstrapHash == null) throw new InvalidDataException("The prepared bootstrap identity is missing.");
        bool selectedArchive = prepared.BootstrapArchive != null || prepared.BootstrapArchiveSha256 != null;
        if (selectedArchive && (string.IsNullOrWhiteSpace(prepared.BootstrapArchive) || string.IsNullOrWhiteSpace(prepared.BootstrapArchiveSha256)))
            throw new InvalidDataException("The selected native bootstrap archive and SHA-256 must be supplied together.");
        string archivePath = FileOps.SafePath(Cache, Path.GetRelativePath(Cache, selectedArchive ? prepared.BootstrapArchive! : prepared.MelonLoaderArchive));
        if (selectedArchive)
        {
            ValidateHash(prepared.BootstrapArchiveSha256);
            // The selected native archive is checked fully at every transaction, independently
            // of the official runtime archive and the file verification cache.
            if (!FileOps.HashEquals(FileOps.Hash(archivePath), prepared.BootstrapArchiveSha256))
                throw new InvalidDataException("The selected native bootstrap archive does not match its SHA-256.");
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var matches = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals("version.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].Length <= 0 || matches[0].Length > 64 * 1024 * 1024)
            throw new InvalidDataException("The MelonLoader archive has a missing, duplicate, or invalid bootstrap.");
        using var source = matches[0].Open();
        using var output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > matches[0].Length) throw new InvalidDataException("The MelonLoader bootstrap exceeds its declared size.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != matches[0].Length) throw new InvalidDataException("The MelonLoader bootstrap is truncated.");
        byte[] bytes = output.ToArray();
        if (!FileOps.HashEquals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), prepared.BootstrapHash))
            throw new InvalidDataException("The cached MelonLoader bootstrap does not match its prepared identity.");
        FileOps.WriteBytes(destination, bytes);
    }

    private void EnsureAbsent(string relative)
    {
        string path = FileOps.SafePath(root, relative);
        if (File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException($"An existing path conflicts with SCPSL-ModLaunch: {relative}");
    }

    private static JournalEntry Find(SessionJournal journal, string path) => journal.Entries.Single(e => Is(e, path));
    private static bool Is(JournalEntry entry, string path) => entry.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase);
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException;

    private static void ValidateHash(string? hash)
    {
        if (hash != null && (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))) throw new InvalidDataException("Invalid SHA-256 in journal.");
    }

    private static string NormalizeTarget(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("A managed file path is missing.");
        string value = relative.Replace('\\', '/');
        string first = value.Split('/')[0];
        if (ProtectedDirectories.Contains(first, StringComparer.OrdinalIgnoreCase) || first.StartsWith("SCPSL-ModLaunch", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"User/launcher data cannot be part of the game transaction: {relative}");
        return value;
    }

    private void CheckHash(string path, string? expected)
    {
        if (Directory.Exists(path)) throw new InvalidDataException($"A directory occupies a managed file path: {path}");
        // Never use metadata reuse for backup, activation, restoration or deletion decisions.
        string? actual = File.Exists(path) ? verification?.HashFresh(path) ?? FileOps.Hash(path) : null;
        if (actual is null) verification?.Invalidate(path);
        if (!FileOps.HashEquals(actual, expected)) throw new InvalidDataException($"File identity changed or is missing: {path}");
    }
}
