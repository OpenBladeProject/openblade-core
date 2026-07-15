using OpenBlade.Contracts;
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

    private static DiagnosticArchive CreateArchive() =>
        new("OpenBlade-diagnostics.zip", ArchiveBytes);

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
}
