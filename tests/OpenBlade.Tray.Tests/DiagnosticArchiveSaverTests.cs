using System.ComponentModel;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveAsync_OneUnreadableLogPreservesHealthyLogsAndExplainsOmission(bool oversized)
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        string badLog = Path.Combine(logs, "OpenBlade.Bad.log");
        await File.WriteAllTextAsync(badLog, oversized ? new string('x', 1024 * 1024 + 1) : "locked");
        await File.WriteAllTextAsync(Path.Combine(logs, "OpenBlade.Tray.log"), "healthy");
        string path = Path.Combine(directory, "diagnostics.zip");
        try
        {
            using FileStream? locked = oversized ? null : new(badLog, FileMode.Open, FileAccess.Read, FileShare.None);
            var saver = new DiagnosticArchiveSaver(new StubDestination(path), logs);
            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);
            using ZipArchive archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("versions.json"));
            Assert.NotNull(archive.GetEntry("user-mode-logs/OpenBlade.Tray.log"));
            if (oversized)
            {
                Assert.Equal(1024 * 1024, archive.GetEntry("user-mode-logs/OpenBlade.Bad.log")?.Length);
            }
            else
            {
                Assert.Null(archive.GetEntry("user-mode-logs/OpenBlade.Bad.log"));
            }
            Assert.NotNull(archive.GetEntry("diagnostic-inventory.json"));
            using JsonDocument manifest = await JsonDocument.ParseAsync(archive.GetEntry("diagnostic-export.json")!.Open());
            JsonElement section = manifest.RootElement.EnumerateArray().Single(value =>
                value.GetProperty("name").GetString() == "UserModeLogs");
            Assert.Equal("Partial", section.GetProperty("status").GetString());
            Assert.Equal(oversized ? 2 : 1, section.GetProperty("includedItems").GetInt32());
            Assert.Equal(oversized ? "LogTailTruncated" : "LogReadFailed",
                section.GetProperty("failures")[0].GetProperty("reason").GetString());
            Assert.DoesNotContain(directory, manifest.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ExportsSnapTapFailureAndSiblingCaptureFailureWithoutPrivateText()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "diagnostics.zip");
        var failure = DiagnosticFailure.FromException("SnapTap", "Driver", "HookSetup", "HookWriteFailed",
            new Win32Exception(5, @"C:\Users\private-owner\driver.sys"), mayHaveSubmitted: true);
        var report = new SnapTapDiagnosticReport(DateTimeOffset.UtcNow,
            new(SnapTapRuntimeState.Failed, DateTimeOffset.UtcNow, Failure: failure), null);
        try
        {
            var saver = new DiagnosticArchiveSaver(new StubDestination(path), Path.Combine(directory, "absent"),
                new ThrowingThxAudioDiagnosticProvider(), () => report);
            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);
            using ZipArchive archive = ZipFile.OpenRead(path);
            using JsonDocument snapTap = await JsonDocument.ParseAsync(archive.GetEntry("snap-tap.json")!.Open());
            JsonElement root = snapTap.RootElement;
            Assert.Equal("Failed", root.GetProperty("status").GetProperty("state").GetString());
            Assert.Equal("HookSetup", root.GetProperty("status").GetProperty("failure").GetProperty("stage").GetString());
            Assert.Equal(5, root.GetProperty("status").GetProperty("failure").GetProperty("nativeErrorCode").GetInt32());
            Assert.True(root.GetProperty("status").GetProperty("failure").GetProperty("mayHaveSubmitted").GetBoolean());
            Assert.DoesNotContain("private", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
            using JsonDocument thx = await JsonDocument.ParseAsync(archive.GetEntry("thx-audio.json")!.Open());
            Assert.Equal("InvalidOperationException", thx.RootElement.GetProperty("failure").GetProperty("exceptionType").GetString());
            Assert.DoesNotContain("secret", thx.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(archive.GetEntry("diagnostic-export.json"));
            using JsonDocument inventory = await JsonDocument.ParseAsync(archive.GetEntry("diagnostic-inventory.json")!.Open());
            Assert.False(inventory.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal(2, inventory.RootElement.GetProperty("failureDetailsVersion").GetInt32());
            Assert.Contains(inventory.RootElement.GetProperty("entries").EnumerateArray(),
                entry => entry.GetProperty("fullName").GetString() == "snap-tap.json");
            using JsonDocument manifest = await JsonDocument.ParseAsync(archive.GetEntry("diagnostic-export.json")!.Open());
            Assert.Contains(manifest.RootElement.EnumerateArray(),
                section => section.GetProperty("name").GetString() == "thx-audio.json"
                    && section.GetProperty("status").GetString() == "Partial");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_RecordsSnapTapCaptureFailureAndStillExportsLogs()
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "diagnostics.zip");
        try
        {
            var saver = new DiagnosticArchiveSaver(new StubDestination(path), directory, null,
                () => throw new IOException(@"C:\Users\private-owner\driver.sys", new Win32Exception(5)));
            await saver.SaveAsync(CreateValidArchive(), CancellationToken.None);
            using ZipArchive archive = ZipFile.OpenRead(path);
            using JsonDocument report = await JsonDocument.ParseAsync(archive.GetEntry("snap-tap.json")!.Open());
            Assert.Equal("CaptureFailed", report.RootElement.GetProperty("captureFailure").GetProperty("reason").GetString());
            Assert.Equal(5, report.RootElement.GetProperty("captureFailure").GetProperty("cause").GetProperty("nativeErrorCode").GetInt32());
            Assert.DoesNotContain("private", report.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
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
    public async Task SaveAsync_IncludesAnotherProcessWriterFailure()
    {
        string directory = CreateTemporaryDirectory();
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(Path.Combine(logs, "health"));
        string path = Path.Combine(directory, "diagnostics.zip");
        int otherProcess = Environment.ProcessId + 100;
        var writer = new DiagnosticLogWriterHealth("OpenBlade.SessionAgent", 1, 0,
            DiagnosticFailure.FromException("Diagnostics", "PersistentLog", "Append", "LogWriteFailed",
                new IOException("The runtime log could not be opened.")))
        {
            ProcessId = otherProcess,
            Version = "0.42.2-test",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(Path.Combine(logs, "health", "OpenBlade.SessionAgent.health.json"),
            JsonSerializer.Serialize(writer));
        try
        {
            await new DiagnosticArchiveSaver(new StubDestination(path), logs)
                .SaveAsync(CreateValidArchive(), CancellationToken.None);
            using ZipArchive archive = ZipFile.OpenRead(path);
            using JsonDocument health = await JsonDocument.ParseAsync(archive.GetEntry("log-writer-health.json")!.Open());
            JsonElement value = Assert.Single(health.RootElement.EnumerateArray());
            Assert.Equal(otherProcess, value.GetProperty("processId").GetInt32());
            Assert.Equal(1, value.GetProperty("failedWrites").GetInt64());
            Assert.Contains("could not be opened", value.GetProperty("lastFailure").GetProperty("message").GetString());
            using JsonDocument inventory = await JsonDocument.ParseAsync(archive.GetEntry("diagnostic-inventory.json")!.Open());
            Assert.False(inventory.RootElement.GetProperty("isComplete").GetBoolean());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{bad")]
    [InlineData("[]")]
    public async Task SaveAsync_MalformedServiceHistoryDoesNotDiscardTheArchive(string invalidHistory)
    {
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "diagnostics.zip");
        try
        {
            using var payload = new MemoryStream();
            payload.Write(CreateValidArchive().ZipBytes);
            payload.Position = 0;
            using (var source = new ZipArchive(payload, ZipArchiveMode.Update, leaveOpen: true))
            {
                using var writer = new StreamWriter(source.CreateEntry("service-history.json").Open());
                writer.Write(invalidHistory);
            }
            await new DiagnosticArchiveSaver(new StubDestination(path), Path.Combine(directory, "absent"))
                .SaveAsync(new("diagnostics.zip", payload.ToArray()), CancellationToken.None);
            using ZipArchive archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry("versions.json"));
            using JsonDocument manifest = await JsonDocument.ParseAsync(archive.GetEntry("diagnostic-export.json")!.Open());
            Assert.Contains(manifest.RootElement.EnumerateArray(),
                section => section.GetProperty("name").GetString() == "diagnostic-inventory.json"
                    && section.GetProperty("status").GetString() == "Failed");
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
            throw new InvalidOperationException("C:\\Users\\Private\\component.dll password=private-secret-value");
    }
}
