using System.Diagnostics;
using System.Security.Cryptography;

namespace OpenBlade.Setup;

public interface ISetupPayloadFileSystem
{
    bool DirectoryExists(string path);
    FileAttributes GetAttributes(string path);
    IEnumerable<string> EnumerateFiles(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination);
    long GetFileLength(string path);
    Stream OpenRead(string path);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path);
}

public sealed class WindowsSetupPayloadFileSystem : ISetupPayloadFileSystem
{
    private const int CopyBufferSize = 128 * 1024;

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string destination)
    {
        using FileStream sourceStream = new(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.SequentialScan);
        using FileStream destinationStream = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.WriteThrough);
        sourceStream.CopyTo(destinationStream, CopyBufferSize);
        destinationStream.Flush(flushToDisk: true);
    }
    public long GetFileLength(string path) => new FileInfo(path).Length;
    public Stream OpenRead(string path) => File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}

public sealed class SetupPayloadTransaction : ISetupPayloadTransaction
{
    private const int AtomicMoveMaximumAttempts = 25;
    private const string ForbiddenCaptureToolFileNamePrefix = "OpenBlade.Capture";
    private static readonly TimeSpan AtomicMoveRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly string[] RequiredRelativePaths =
    [
        "OpenBlade.Service.exe",
        "OpenBlade.SessionAgent.exe",
        "OpenBlade.SessionAgent.dll",
        "OpenBlade.SessionAgent.deps.json",
        "OpenBlade.SessionAgent.runtimeconfig.json",
        "OpenBlade.Tray.exe",
        "OpenBlade.Tray.dll",
        "OpenBlade.Tray.deps.json",
        "OpenBlade.Tray.runtimeconfig.json",
        "OpenBlade.Tray.pri",
        "App.xbf",
        "MainWindow.xbf",
        "Microsoft.WindowsAppRuntime.dll",
        "Microsoft.WindowsAppRuntime.pri",
        "Microsoft.ui.xaml.dll",
        "Microsoft.UI.Xaml.Controls.dll",
        "Microsoft.UI.Xaml.Controls.pri",
        @"Microsoft.UI.Xaml\Assets\NoiseAsset_256x256_PNG.png",
    ];

    private readonly SetupPaths paths;
    private readonly ISetupPayloadFileSystem files;
    private readonly string stagingDirectory;
    private readonly string backupDirectory;
    private readonly string failedDirectory;
    private TransactionState state = TransactionState.Staged;

    private SetupPayloadTransaction(
        SetupPaths paths,
        ISetupPayloadFileSystem files,
        string stagingDirectory,
        string backupDirectory,
        string failedDirectory,
        bool hadExistingInstall)
    {
        this.paths = paths;
        this.files = files;
        this.stagingDirectory = stagingDirectory;
        this.backupDirectory = backupDirectory;
        this.failedDirectory = failedDirectory;
        HadExistingInstall = hadExistingInstall;
    }

    public bool HadExistingInstall { get; }

    public bool IsActivated => state is TransactionState.BackupMoved
        or TransactionState.Active
        or TransactionState.RestoreFailed;

    public string? CommitWarning { get; private set; }

    public static async Task<ISetupPayloadTransaction> StageAsync(
        SetupPaths paths,
        string payloadDirectory,
        ISetupPayloadFileSystem files,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        ArgumentNullException.ThrowIfNull(files);

        string payloadRoot = NormalizeRoot(payloadDirectory);
        IReadOnlyDictionary<string, PayloadEntry> sourceManifest =
            BuildManifest(payloadRoot, files, cancellationToken);
        ValidateProductionPayload(payloadRoot, sourceManifest, files);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureNoAmbiguousTransactionSiblings(paths, files);

        string transactionId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(paths.ProgramFilesRoot, $"OpenBlade.installing.{transactionId}");
        string backup = Path.Combine(paths.ProgramFilesRoot, $"OpenBlade.rollback.{transactionId}");
        string failed = Path.Combine(paths.ProgramFilesRoot, $"OpenBlade.failed.{transactionId}");
        EnsureTransactionPaths(paths, staging, backup, failed);

        bool stagingCreated = false;
        try
        {
            files.CreateDirectory(staging);
            stagingCreated = true;

            foreach (PayloadEntry entry in sourceManifest.Values.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = ResolveContainedPath(payloadRoot, entry.RelativePath);
                string destination = ResolveContainedPath(staging, entry.RelativePath);
                files.CreateDirectory(Path.GetDirectoryName(destination)!);
                files.CopyFile(source, destination);
                cancellationToken.ThrowIfCancellationRequested();

                PayloadEntry currentSource = ReadEntry(payloadRoot, source, files);
                if (!entry.ContentEquals(currentSource))
                {
                    throw new InvalidDataException(
                        $"Payload file '{entry.RelativePath}' changed while staging.");
                }
            }

            IReadOnlyDictionary<string, PayloadEntry> stagedManifest =
                BuildManifest(staging, files, cancellationToken);
            RequireExactManifest(sourceManifest, stagedManifest);

            return new SetupPayloadTransaction(
                paths,
                files,
                staging,
                backup,
                failed,
                files.DirectoryExists(paths.InstallDirectory));
        }
        catch (Exception failure)
        {
            if (!stagingCreated || !files.DirectoryExists(staging))
            {
                throw;
            }

            try
            {
                DeleteTransactionDirectory(files, paths, staging);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(failure, cleanupFailure);
            }

            throw;
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (state != TransactionState.Staged)
        {
            throw new InvalidOperationException("The payload transaction can only be activated once.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureTransactionPaths(paths, stagingDirectory, backupDirectory, failedDirectory);
        bool liveExists = files.DirectoryExists(paths.InstallDirectory);
        if (liveExists != HadExistingInstall)
        {
            throw new InvalidOperationException("The installed payload changed while setup was staging; no files were replaced.");
        }

        if (HadExistingInstall)
        {
            await MoveAtomicDirectoryWithRetryAsync(
                paths.InstallDirectory,
                backupDirectory,
                cancellationToken);
            state = TransactionState.BackupMoved;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.MoveDirectory(stagingDirectory, paths.InstallDirectory);
                state = TransactionState.Active;
            }
            catch (Exception activationFailure)
            {
                try
                {
                    await MoveAtomicDirectoryWithRetryAsync(
                        backupDirectory,
                        paths.InstallDirectory,
                        CancellationToken.None);
                    state = TransactionState.Staged;
                }
                catch (Exception restoreFailure)
                {
                    state = TransactionState.RestoreFailed;
                    throw new AggregateException(
                        $"Payload activation and restoration failed. The prior installation is retained at '{backupDirectory}'.",
                        activationFailure,
                        restoreFailure);
                }

                throw;
            }
        }
        else
        {
            files.MoveDirectory(stagingDirectory, paths.InstallDirectory);
            state = TransactionState.Active;
        }

    }

    private async Task MoveAtomicDirectoryWithRetryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                files.MoveDirectory(source, destination);
                return;
            }
            catch (Exception exception) when (
                attempt < AtomicMoveMaximumAttempts
                && IsTransientMoveFailure(exception)
                && files.DirectoryExists(source)
                && !files.DirectoryExists(destination))
            {
                await Task.Delay(AtomicMoveRetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsTransientMoveFailure(Exception exception) =>
        exception is UnauthorizedAccessException or IOException;

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        if (state == TransactionState.Committed)
        {
            return Task.CompletedTask;
        }

        if (state != TransactionState.Active)
        {
            throw new InvalidOperationException("The payload transaction must be activated before it can be committed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        state = TransactionState.Committed;
        if (HadExistingInstall && files.DirectoryExists(backupDirectory))
        {
            try
            {
                DeleteTransactionDirectory(files, paths, backupDirectory);
            }
            catch (Exception exception)
            {
                CommitWarning = CreateBoundedCommitWarning(backupDirectory);
                TraceWarningNoThrow(
                    "{0} Cleanup detail: {1}",
                    CommitWarning,
                    exception.Message);
            }
        }
        return Task.CompletedTask;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (state == TransactionState.RolledBack)
        {
            return;
        }

        if (state == TransactionState.Committed)
        {
            throw new InvalidOperationException("A committed payload transaction cannot be rolled back.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        switch (state)
        {
            case TransactionState.Staged:
                if (files.DirectoryExists(stagingDirectory))
                {
                    DeleteTransactionDirectory(files, paths, stagingDirectory);
                }
                state = TransactionState.RolledBack;
                break;

            case TransactionState.BackupMoved:
            case TransactionState.RestoreFailed:
                await RestoreStrandedBackupAsync(cancellationToken);
                state = TransactionState.RolledBack;
                break;

            case TransactionState.Active:
                if (HadExistingInstall)
                {
                    await RollbackUpdateAsync(cancellationToken);
                }
                else
                {
                    DeleteLiveDirectory(files, paths);
                }
                state = TransactionState.RolledBack;
                break;
        }

    }

    public ValueTask DisposeAsync()
    {
        if (state == TransactionState.Staged && files.DirectoryExists(stagingDirectory))
        {
            try
            {
                DeleteTransactionDirectory(files, paths, stagingDirectory);
                state = TransactionState.RolledBack;
            }
            catch (Exception exception)
            {
                TraceWarningNoThrow(
                    "OpenBlade setup retained guarded staging directory '{0}' after best-effort disposal cleanup failed: {1}",
                    stagingDirectory,
                    exception.Message);
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task RollbackUpdateAsync(CancellationToken cancellationToken)
    {
        files.MoveDirectory(paths.InstallDirectory, failedDirectory);
        try
        {
            await MoveAtomicDirectoryWithRetryAsync(
                backupDirectory,
                paths.InstallDirectory,
                cancellationToken);
        }
        catch (Exception restoreFailure)
        {
            try
            {
                files.MoveDirectory(failedDirectory, paths.InstallDirectory);
            }
            catch (Exception reverseFailure)
            {
                state = TransactionState.RestoreFailed;
                throw new AggregateException(
                    $"Payload rollback failed. The prior installation is retained at '{backupDirectory}'.",
                    restoreFailure,
                    reverseFailure);
            }

            throw;
        }

        state = TransactionState.RolledBack;
        CleanupRestoredTransactionDirectories(failedDirectory);
    }

    private async Task RestoreStrandedBackupAsync(CancellationToken cancellationToken)
    {
        if (files.DirectoryExists(paths.InstallDirectory))
        {
            throw new InvalidOperationException(
                $"Cannot restore '{backupDirectory}' because the live install path is occupied.");
        }

        await MoveAtomicDirectoryWithRetryAsync(
            backupDirectory,
            paths.InstallDirectory,
            cancellationToken);
        state = TransactionState.RolledBack;
        CleanupRestoredTransactionDirectories(stagingDirectory, failedDirectory);
    }

    private void CleanupRestoredTransactionDirectories(params string[] directories)
    {
        List<Exception> failures = [];
        foreach (string directory in directories)
        {
            if (!files.DirectoryExists(directory))
            {
                continue;
            }

            try
            {
                DeleteTransactionDirectory(files, paths, directory);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException(
                    $"The prior OpenBlade installation was restored, but guarded transaction cleanup failed for '{directory}'.",
                    exception));
            }
        }

        if (failures.Count == 1)
        {
            throw failures[0];
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "The prior OpenBlade installation was restored, but transaction cleanup failed.",
                failures);
        }
    }

    private static IReadOnlyDictionary<string, PayloadEntry> BuildManifest(
        string root,
        ISetupPayloadFileSystem files,
        CancellationToken cancellationToken)
    {
        if (!files.DirectoryExists(root))
        {
            throw new InvalidOperationException($"The setup payload directory '{root}' is missing or empty.");
        }

        RejectReparsePoint(root, files);
        Dictionary<string, PayloadEntry> manifest = new(StringComparer.OrdinalIgnoreCase);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            RejectReparsePoint(directory, files);

            foreach (string childDirectory in files.EnumerateDirectories(directory))
            {
                RejectReparsePoint(childDirectory, files);
                _ = GetSafeRelativePath(root, childDirectory);
                pending.Push(childDirectory);
            }

            foreach (string file in files.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(file, files);
                PayloadEntry entry = ReadEntry(root, file, files);
                if (!manifest.TryAdd(entry.RelativePath, entry))
                {
                    throw new InvalidDataException(
                        $"The payload contains duplicate path '{entry.RelativePath}' when compared case-insensitively.");
                }
            }
        }

        if (manifest.Count == 0)
        {
            throw new InvalidOperationException($"The setup payload directory '{root}' is missing or empty.");
        }

        return manifest;
    }

    private static PayloadEntry ReadEntry(string root, string file, ISetupPayloadFileSystem files)
    {
        string relative = GetSafeRelativePath(root, file);
        long length = files.GetFileLength(file);
        using Stream content = files.OpenRead(file);
        string hash = Convert.ToHexString(SHA256.HashData(content));
        return new(relative, length, hash);
    }

    private static void ValidateProductionPayload(
        string payloadRoot,
        IReadOnlyDictionary<string, PayloadEntry> manifest,
        ISetupPayloadFileSystem files)
    {
        string? forbiddenCaptureTool = manifest.Keys.FirstOrDefault(relativePath =>
            Path.GetFileName(relativePath).StartsWith(
                ForbiddenCaptureToolFileNamePrefix,
                StringComparison.OrdinalIgnoreCase));
        if (forbiddenCaptureTool is not null)
        {
            throw new InvalidOperationException(
                $"The production setup payload must not include the developer tool '{forbiddenCaptureTool}'.");
        }

        foreach (string required in RequiredRelativePaths)
        {
            string normalized = NormalizeRelativePath(required);
            if (!manifest.ContainsKey(normalized))
            {
                throw new InvalidOperationException(
                    $"The setup payload is incomplete: '{required}' is missing.");
            }
        }

        DynamicLightingIdentityRegistration.ValidatePayloadArtifacts(
            payloadRoot,
            manifest.Keys,
            files);
    }

    private static void RequireExactManifest(
        IReadOnlyDictionary<string, PayloadEntry> expected,
        IReadOnlyDictionary<string, PayloadEntry> actual)
    {
        if (expected.Count != actual.Count)
        {
            throw new InvalidDataException("The staged payload file set does not match the source payload.");
        }

        foreach ((string relativePath, PayloadEntry expectedEntry) in expected)
        {
            if (!actual.TryGetValue(relativePath, out PayloadEntry? actualEntry)
                || !expectedEntry.ContentEquals(actualEntry))
            {
                throw new InvalidDataException(
                    $"The staged payload does not match source file '{relativePath}'.");
            }
        }
    }

    private static void EnsureNoAmbiguousTransactionSiblings(
        SetupPaths paths,
        ISetupPayloadFileSystem files)
    {
        foreach (string directory in files.EnumerateDirectories(paths.ProgramFilesRoot))
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (name.StartsWith("OpenBlade.installing.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OpenBlade.rollback.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OpenBlade.failed.", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"An existing OpenBlade setup transaction directory requires manual inspection: '{directory}'.");
            }
        }
    }

    private static void EnsureTransactionPaths(SetupPaths paths, params string[] transactionPaths)
    {
        if (!SetupPaths.IsExactOpenBladeDirectory(paths.InstallDirectory, paths.ProgramFilesRoot))
        {
            throw new InvalidOperationException($"Unexpected live install path '{paths.InstallDirectory}'.");
        }

        foreach (string path in transactionPaths)
        {
            if (!SetupPaths.IsExactTransactionSiblingDirectory(path, paths.ProgramFilesRoot))
            {
                throw new InvalidOperationException($"Unexpected setup transaction path '{path}'.");
            }
        }
    }

    private static void DeleteTransactionDirectory(
        ISetupPayloadFileSystem files,
        SetupPaths paths,
        string directory)
    {
        if (!SetupPaths.IsExactTransactionSiblingDirectory(directory, paths.ProgramFilesRoot))
        {
            throw new InvalidOperationException(
                $"Refusing to recursively delete unexpected transaction path '{directory}'.");
        }

        files.DeleteDirectory(directory);
    }

    private static void DeleteLiveDirectory(ISetupPayloadFileSystem files, SetupPaths paths)
    {
        if (!SetupPaths.IsExactOpenBladeDirectory(paths.InstallDirectory, paths.ProgramFilesRoot))
        {
            throw new InvalidOperationException(
                $"Refusing to recursively delete unexpected install path '{paths.InstallDirectory}'.");
        }

        if (files.DirectoryExists(paths.InstallDirectory))
        {
            files.DeleteDirectory(paths.InstallDirectory);
        }
    }

    private static void RejectReparsePoint(string path, ISetupPayloadFileSystem files)
    {
        if ((files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Payload reparse points are not allowed: '{path}'.");
        }
    }

    private static string GetSafeRelativePath(string root, string path)
    {
        string resolved = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, resolved);
        if (Path.IsPathRooted(relative)
            || relative == "."
            || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Payload path escapes its root: '{path}'.");
        }

        _ = ResolveContainedPath(root, relative);
        return NormalizeRelativePath(relative);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Rooted payload-relative path is not allowed: '{relativePath}'.");
        }

        string normalizedRoot = NormalizeRoot(root);
        string resolved = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Payload path escapes its root: '{relativePath}'.");
        }

        return resolved;
    }

    private static string NormalizeRoot(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static string CreateBoundedCommitWarning(string retainedBackup)
    {
        string warning =
            $"OpenBlade was updated successfully, but backup cleanup failed. "
            + $"The guarded rollback directory was retained at '{retainedBackup}'.";
        return warning.Length <= 512
            ? warning
            : "OpenBlade was updated successfully, but backup cleanup failed. "
                + "A guarded OpenBlade.rollback directory was retained under Program Files for manual inspection.";
    }

    private static void TraceWarningNoThrow(string format, params object?[] arguments)
    {
        try
        {
            Trace.TraceWarning(format, arguments);
        }
        catch
        {
            // Cleanup and committed-state reporting must never replace the primary setup result.
        }
    }

    private sealed record PayloadEntry(string RelativePath, long Length, string Sha256)
    {
        public bool ContentEquals(PayloadEntry other) =>
            Length == other.Length && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
    }

    private enum TransactionState
    {
        Staged,
        BackupMoved,
        Active,
        RestoreFailed,
        Committed,
        RolledBack,
    }
}
