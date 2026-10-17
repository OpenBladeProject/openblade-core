using System.IO.Compression;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class DiagnosticArchiveSaverTests
{
    private static readonly byte[] ArchiveBytes =
    [
        0x50, 0x4B, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04,
    ];

    [Fact]
    public async Task SaveAsync_WhenDestinationIsCanceled_DoesNotWriteAFile()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var destination = new StubDestination(null);
            var saver = new DiagnosticArchiveSaver(destination);

            DiagnosticArchiveSaveResult result = await saver.SaveAsync(
                CreateArchive(),
                CancellationToken.None);

            Assert.Equal(DiagnosticArchiveSaveStatus.Canceled, result.Status);
            Assert.Null(result.DestinationPath);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_CancellationDuringDestinationSelection_WritesNothing()
    {
        using var source = new CancellationTokenSource();
        var destination = new BlockingDestination();
        var saver = new DiagnosticArchiveSaver(destination);
        Task<DiagnosticArchiveSaveResult> saving = saver.SaveAsync(CreateArchive(), source.Token);

        await destination.SelectionStarted.Task;
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saving);
    }

    [Fact]
    public async Task SaveAsync_CancellationAtSelectionWriteBoundary_CreatesNoFiles()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "chosen-name.zip");
        using var source = new CancellationTokenSource();
        var destination = new CancelingDestination(path, source);
        var saver = new DiagnosticArchiveSaver(destination);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saver.SaveAsync(
                CreateArchive(),
                source.Token));

            Assert.False(File.Exists(path));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ReplacesExistingDestinationAndLeavesNoTemporarySibling()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "chosen-name.zip");
        await File.WriteAllTextAsync(path, "old contents");
        try
        {
            var destination = new StubDestination(path);
            var saver = new DiagnosticArchiveSaver(destination);

            DiagnosticArchiveSaveResult result = await saver.SaveAsync(
                CreateArchive(),
                CancellationToken.None);

            Assert.Equal(DiagnosticArchiveSaveStatus.Saved, result.Status);
            Assert.Equal(path, result.DestinationPath);
            Assert.Equal(ArchiveBytes, await File.ReadAllBytesAsync(path));
            Assert.Equal([path], Directory.EnumerateFiles(directory));
            Assert.Equal("OpenBlade-diagnostics.zip", destination.SuggestedFileName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WhenReplacementFails_CleansTemporarySibling()
    {
        string directory = CreateTemporaryDirectory();
        string destinationDirectory = Path.Combine(directory, "existing.zip");
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            var saver = new DiagnosticArchiveSaver(
                new StubDestination(destinationDirectory));

            Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => saver.SaveAsync(
                CreateArchive(),
                CancellationToken.None));

            Assert.True(failure is IOException or UnauthorizedAccessException);
            Assert.Equal([destinationDirectory], Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_AppendsBoundedUserModeLogsToValidArchive()
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "OpenBlade.Tray.log"),
            "redacted tray warning");
        string path = Path.Combine(directory, "diagnostics.zip");
        try
        {
            var saver = new DiagnosticArchiveSaver(
                new StubDestination(path),
                logs);

            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);

            using ZipArchive archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("versions.json"));
            ZipArchiveEntry log = Assert.Single(
                archive.Entries,
                entry => entry.FullName == "user-mode-logs/OpenBlade.Tray.log");
            using var reader = new StreamReader(log.Open());
            Assert.Equal("redacted tray warning", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_AppendsReadOnlyThxAdmissionDiagnostics()
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        string path = Path.Combine(directory, "diagnostics.zip");
        Directory.CreateDirectory(logs);
        var diagnostics = new StubThxAudioDiagnosticProvider(new(
            ThxAudioDiagnosticCaptureStatus.Succeeded,
            ThxAudioOperationStatus.Unavailable,
            ThxAudioAvailability.ComponentsUnavailable,
            "The pinned THX component was not found."));
        try
        {
            var saver = new DiagnosticArchiveSaver(
                new StubDestination(path),
                logs,
                diagnostics);

            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);

            Assert.Equal(1, diagnostics.CaptureCount);
            using ZipArchive archive = ZipFile.OpenRead(path);
            ZipArchiveEntry entry = Assert.Single(
                archive.Entries,
                value => value.FullName == "thx-audio.json");
            using var document = await JsonDocument.ParseAsync(entry.Open());
            JsonElement root = document.RootElement;
            Assert.Equal("Succeeded", root.GetProperty("captureStatus").GetString());
            Assert.Equal("Unavailable", root.GetProperty("operationStatus").GetString());
            Assert.Equal("ComponentsUnavailable", root.GetProperty("availability").GetString());
            Assert.Equal(
                "The pinned THX component was not found.",
                root.GetProperty("error").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_WhenThxDiagnosticProviderFails_ExportsBoundedFailure()
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        string path = Path.Combine(directory, "diagnostics.zip");
        Directory.CreateDirectory(logs);
        try
        {
            var saver = new DiagnosticArchiveSaver(
                new StubDestination(path),
                logs,
                new ThrowingThxAudioDiagnosticProvider());

            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);

            using ZipArchive archive = ZipFile.OpenRead(path);
            ZipArchiveEntry entry = Assert.Single(
                archive.Entries,
                value => value.FullName == "thx-audio.json");
            using var document = await JsonDocument.ParseAsync(entry.Open());
            JsonElement root = document.RootElement;
            Assert.Equal("Failed", root.GetProperty("captureStatus").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("operationStatus").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("availability").ValueKind);
            Assert.Equal(
                "The Session Agent THX admission query failed: InvalidOperationException.",
                root.GetProperty("error").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveExactAsync_WritesInputBytesWithoutAppendingUserModeLogs()
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(
            Path.Combine(logs, "OpenBlade.Tray.log"),
            "redacted tray warning");
        string path = Path.Combine(directory, "report.zip");
        try
        {
            var saver = new DiagnosticArchiveSaver(
                new StubDestination(path),
                logs);
            DiagnosticArchive input = CreateValidArchive();

            DiagnosticArchiveSaveResult result = await saver.SaveExactAsync(
                input,
                CancellationToken.None);

            Assert.Equal(DiagnosticArchiveSaveStatus.Saved, result.Status);
            Assert.Equal(path, result.DestinationPath);
            Assert.Equal(input.ZipBytes, await File.ReadAllBytesAsync(path));
            using ZipArchive archive = ZipFile.OpenRead(path);
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.StartsWith("user-mode-logs/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DiagnosticArchive CreateArchive() =>
        new("OpenBlade-diagnostics.zip", ArchiveBytes);

    private static DiagnosticArchive CreateValidArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("versions.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }

        return new DiagnosticArchive("OpenBlade-diagnostics.zip", output.ToArray());
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"OpenBlade.Tray.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubDestination(string? path) : IDiagnosticArchiveDestination
    {
        public string? SuggestedFileName { get; private set; }

        public Task<string?> ChooseDestinationAsync(
            string suggestedFileName,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SuggestedFileName = suggestedFileName;
            return Task.FromResult(path);
        }
    }

    private sealed class BlockingDestination : IDiagnosticArchiveDestination
    {
        public TaskCompletionSource SelectionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> ChooseDestinationAsync(
            string suggestedFileName,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            SelectionStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }
    }

    private sealed class CancelingDestination(
        string path,
        CancellationTokenSource cancellation) : IDiagnosticArchiveDestination
    {
        public Task<string?> ChooseDestinationAsync(
            string suggestedFileName,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            cancellation.Cancel();
            return Task.FromResult<string?>(path);
        }
    }

    private sealed class StubThxAudioDiagnosticProvider(ThxAudioDiagnosticReport report)
        : IThxAudioDiagnosticProvider
    {
        public int CaptureCount { get; private set; }

        public Task<ThxAudioDiagnosticReport> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return Task.FromResult(report);
        }
    }

    private sealed class ThrowingThxAudioDiagnosticProvider : IThxAudioDiagnosticProvider
    {
        public Task<ThxAudioDiagnosticReport> CaptureAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("C:\\Users\\Private\\secret.txt");
    }
}
