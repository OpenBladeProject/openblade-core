using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

internal sealed class FailingTelemetryRecordingFixture : IAsyncDisposable
{
    private readonly GatedFailureStream failureStream;

    private FailingTelemetryRecordingFixture(
        TelemetryRecordingSession recording,
        GatedFailureStream failureStream)
    {
        Recording = recording;
        this.failureStream = failureStream;
    }

    public TelemetryRecordingSession Recording { get; }

    public Task WriteStarted => failureStream.Started.Task;

    public static async Task<FailingTelemetryRecordingFixture> CreateAsync(
        string path,
        TelemetrySessionMetadata metadata,
        TimeProvider clock)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(
            path + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        var failureStream = new GatedFailureStream(stream);
        try
        {
            TelemetryCsvWriter writer = await TelemetryCsv.CreateWriterAsync(
                failureStream, metadata, TelemetryCsvLimits.Default,
                leaveOpen: true, CancellationToken.None);
            var recording = new TelemetryRecordingSession(
                path, path + ".partial", metadata,
                TelemetryRecordingLimits.Default with { PendingBatchCapacity = 1 },
                clock, stream, writer);
            failureStream.Armed = true;
            return new FailingTelemetryRecordingFixture(recording, failureStream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    public void FailWrite() => failureStream.Release.TrySetResult();

    public async ValueTask DisposeAsync()
    {
        FailWrite();
        try
        {
            await Recording.DisposeAsync();
        }
        catch (IOException)
        {
            // The test deliberately fails this writer; its partial is retained.
        }
    }

    private sealed class GatedFailureStream(Stream inner) : Stream
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                throw new IOException("Injected telemetry disk failure.");
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
