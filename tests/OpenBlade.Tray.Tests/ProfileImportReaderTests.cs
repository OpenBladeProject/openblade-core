using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ProfileImportReaderTests
{
    [Fact]
    public async Task ReadsNormalExportAndLeavesCallerStreamOpen()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(), "1.0.0", DateTimeOffset.UtcNow);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ProfileSet actual = await ProfileImportReader.ReadAsync(stream);
        Assert.Equal(ProfileExportSerializer.Deserialize(json), actual);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task RejectsOversizedStreamBeforeReadingEntireFile()
    {
        using var stream = new MemoryStream(
            new byte[ProfileExportSerializer.MaximumFileSizeBytes * 2]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ProfileImportReader.ReadAsync(stream));
        Assert.Equal(ProfileExportSerializer.MaximumFileSizeBytes + 1, stream.Position);
    }

    [Fact]
    public void RejectsOversizedCharacterInputBeforeParsing()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(
                new string('x', ProfileExportSerializer.MaximumFileSizeBytes + 1)));
        Assert.Contains("1 MiB", error.Message);
    }

    [Fact]
    public void RejectsOversizedUtf8InputBelowCharacterLimit()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(
                new string('\u20ac', ProfileExportSerializer.MaximumFileSizeBytes / 2)));
        Assert.Contains("1 MiB", error.Message);
    }

    [Fact]
    public async Task CanceledImportDoesNotReadInput()
    {
        using var stream = new MemoryStream(new byte[100]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProfileImportReader.ReadAsync(stream, cancellation.Token));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task CancellationInterruptsPendingRead()
    {
        using var stream = new PendingReadStream();
        using var cancellation = new CancellationTokenSource();
        Task<ProfileSet> import = ProfileImportReader.ReadAsync(stream, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import);
    }

    [Fact]
    public async Task SynchronouslyReadableInputRunsOutsideCallerContext()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(), "1.0.0", DateTimeOffset.UtcNow);
        using var stream = new ContextRecordingStream(Encoding.UTF8.GetBytes(json));
        SynchronizationContext? previous = SynchronizationContext.Current;
        var caller = new SynchronizationContext();
        Task<ProfileSet> import;
        try
        {
            SynchronizationContext.SetSynchronizationContext(caller);
            import = ProfileImportReader.ReadAsync(stream);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await import;
        Assert.Null(stream.ReadContext);
        Assert.True(stream.WasRead);
    }

    private sealed class ContextRecordingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public SynchronizationContext? ReadContext { get; private set; }
        public bool WasRead { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            ReadContext = SynchronizationContext.Current;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class PendingReadStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
