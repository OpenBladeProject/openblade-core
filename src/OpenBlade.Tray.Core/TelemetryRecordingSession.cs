using System.Threading.Channels;

namespace OpenBlade.Tray;

public sealed record TelemetryRecordingLimits(
    TimeSpan MaximumDuration,
    TelemetryCsvLimits Csv,
    int PendingBatchCapacity)
{
    public static TelemetryRecordingLimits Default { get; } = new(
        TimeSpan.FromHours(24),
        TelemetryCsvLimits.Default,
        256);

    internal void Validate()
    {
        if (MaximumDuration <= TimeSpan.Zero || MaximumDuration > TimeSpan.FromHours(24)
            || PendingBatchCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TelemetryRecordingLimits));
        }
        Csv.Validate();
    }
}

internal interface ITelemetryRecordingSession : IAsyncDisposable
{
    Task Completion { get; }

    string? AutomaticCompletionReason { get; }

    bool IsActive { get; }

    bool RequiresSampling { get; }

    string DestinationPath { get; }

    string PartialPath { get; }

    TelemetrySessionMetadata Metadata { get; }

    ValueTask AppendAsync(
        TelemetrySample sample,
        IReadOnlyList<TelemetryEvent>? markers = null,
        CancellationToken cancellationToken = default);

    Task<string> StopAsync(CancellationToken cancellationToken = default);
}

public sealed class TelemetryRecordingSession : ITelemetryRecordingSession
{
    private readonly string destinationPath;
    private readonly string partialPath;
    private readonly TelemetrySessionMetadata metadata;
    private readonly TelemetryRecordingLimits limits;
    private readonly TimeProvider timeProvider;
    private readonly FileStream stream;
    private readonly TelemetryCsvWriter writer;
    private readonly Channel<TelemetryRecordBatch> pending;
    private readonly Task writerTask;
    private readonly object stopSync = new();
    private DateTimeOffset? lastSampleAt;
    private Task<string>? stopTask;
    private int accepting = 1;
    private int finalized;
    private string? automaticCompletionReason;

    internal TelemetryRecordingSession(
        string destinationPath,
        string partialPath,
        TelemetrySessionMetadata metadata,
        TelemetryRecordingLimits limits,
        TimeProvider timeProvider,
        FileStream stream,
        TelemetryCsvWriter writer)
    {
        this.destinationPath = destinationPath;
        this.partialPath = partialPath;
        this.metadata = metadata;
        this.limits = limits;
        this.timeProvider = timeProvider;
        this.stream = stream;
        this.writer = writer;
        pending = Channel.CreateBounded<TelemetryRecordBatch>(new BoundedChannelOptions(
            limits.PendingBatchCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        writerTask = RunWriterAsync();
    }

    public bool IsActive => Volatile.Read(ref accepting) != 0 && !writerTask.IsCompleted;

    Task ITelemetryRecordingSession.Completion => writerTask;

    string? ITelemetryRecordingSession.AutomaticCompletionReason =>
        Volatile.Read(ref automaticCompletionReason);

    public bool RequiresSampling => IsActive;

    public string DestinationPath => destinationPath;

    public string PartialPath => partialPath;

    public TelemetrySessionMetadata Metadata => metadata;

    public static async Task<TelemetryRecordingSession> StartAsync(
        string destinationPath,
        TelemetrySessionMetadata? metadata = null,
        TelemetryRecordingLimits? limits = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        TelemetrySessionMetadata effectiveMetadata = metadata
            ?? TelemetrySessionMetadata.CreateLive(clock.GetUtcNow());
        TelemetryValidation.ValidateMetadata(effectiveMetadata);
        if (effectiveMetadata.Source != TelemetrySource.LiveService
            || effectiveMetadata.EndedAtUtc is not null)
        {
            throw new InvalidDataException("A recording must start with live, open session metadata.");
        }
        TelemetryRecordingLimits effectiveLimits = limits ?? TelemetryRecordingLimits.Default;
        effectiveLimits.Validate();

        string fullDestination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(fullDestination);
        if (directory is null)
        {
            throw new ArgumentException("A telemetry recording directory is required.", nameof(destinationPath));
        }
        Directory.CreateDirectory(directory);
        string partial = fullDestination + ".partial";
        FileStream? stream = null;
        TelemetryCsvWriter? writer = null;
        bool createdPartial = false;
        try
        {
            stream = new FileStream(
                partial,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
            createdPartial = true;
            writer = await TelemetryCsv.CreateWriterAsync(
                stream,
                effectiveMetadata,
                effectiveLimits.Csv,
                leaveOpen: true,
                cancellationToken).ConfigureAwait(false);
            return new TelemetryRecordingSession(
                fullDestination,
                partial,
                effectiveMetadata,
                effectiveLimits,
                clock,
                stream,
                writer);
        }
        catch
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            try
            {
                if (createdPartial)
                {
                    File.Delete(partial);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            throw;
        }
    }

    public async ValueTask AppendAsync(
        TelemetrySample sample,
        IReadOnlyList<TelemetryEvent>? markers = null,
        CancellationToken cancellationToken = default)
    {
        TelemetryValidation.ValidateSample(sample);
        if (sample.Source != TelemetrySource.LiveService)
        {
            throw new InvalidDataException("Only live telemetry can be recorded.");
        }
        DateTimeOffset maximumEnd = metadata.StartedAtUtc + limits.MaximumDuration;
        if (sample.TimestampUtc < metadata.StartedAtUtc)
        {
            throw new InvalidDataException("The telemetry sample is outside the recording duration.");
        }
        if (sample.TimestampUtc > maximumEnd)
        {
            BeginAutomaticCompletion("The configured duration limit was reached.");
            return;
        }
        TelemetryEvent[] copiedMarkers = markers?.ToArray() ?? [];
        foreach (TelemetryEvent marker in copiedMarkers)
        {
            TelemetryValidation.ValidateEvent(marker);
            if (marker.TimestampUtc < metadata.StartedAtUtc || marker.TimestampUtc > maximumEnd)
            {
                throw new InvalidDataException("A telemetry event is outside the recording duration.");
            }
        }
        if (Volatile.Read(ref accepting) == 0)
        {
            if (Volatile.Read(ref automaticCompletionReason) is not null)
            {
                return;
            }
            throw new InvalidOperationException("The telemetry recording is stopping.");
        }
        if (writerTask.IsCompleted)
        {
            if (Volatile.Read(ref automaticCompletionReason) is not null)
            {
                return;
            }
            await writerTask.ConfigureAwait(false);
            throw new InvalidOperationException("The telemetry recording writer has stopped.");
        }
        try
        {
            await pending.Writer.WriteAsync(
                new TelemetryRecordBatch(sample, copiedMarkers),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException exception)
        {
            if (Volatile.Read(ref automaticCompletionReason) is not null)
            {
                return;
            }
            throw new InvalidOperationException("The telemetry recording is stopping.", exception);
        }
    }

    public async Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<string> stopping;
        lock (stopSync)
        {
            if (stopTask is null)
            {
                Volatile.Write(ref accepting, 0);
                pending.Writer.TryComplete();
                stopTask = StopCoreAsync();
            }
            stopping = stopTask;
        }
        return await stopping.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref finalized) != 0)
        {
            return;
        }
        await StopAsync().ConfigureAwait(false);
    }

    private async Task<string> StopCoreAsync()
    {
        try
        {
            await writerTask.ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        File.Move(partialPath, destinationPath, overwrite: true);
        Volatile.Write(ref finalized, 1);
        return destinationPath;
    }

    private async Task RunWriterAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (TelemetryRecordBatch batch in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await writer.WriteSampleAsync(batch.Sample, CancellationToken.None).ConfigureAwait(false);
                    lastSampleAt = batch.Sample.TimestampUtc;
                    foreach (TelemetryEvent marker in batch.Markers)
                    {
                        await writer.WriteEventAsync(marker, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (TelemetryCapacityReachedException exception)
                {
                    BeginAutomaticCompletion(exception.Message);
                    break;
                }
            }

            DateTimeOffset maximumEnd = metadata.StartedAtUtc + limits.MaximumDuration;
            DateTimeOffset endedAt = timeProvider.GetUtcNow();
            if (endedAt > maximumEnd)
            {
                endedAt = maximumEnd;
            }
            if (lastSampleAt > endedAt)
            {
                endedAt = lastSampleAt.Value;
            }
            await writer.CompleteAsync(endedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            // Stop drains accepted appends before acquiring finalization. A failed
            // reader must release blocked producers itself; its completion observer
            // may be waiting behind Stop's gate and cannot close this channel for it.
            Volatile.Write(ref accepting, 0);
            pending.Writer.TryComplete(failure);
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void BeginAutomaticCompletion(string reason)
    {
        Interlocked.CompareExchange(ref automaticCompletionReason, reason, null);
        Volatile.Write(ref accepting, 0);
        pending.Writer.TryComplete();
    }

    private sealed record TelemetryRecordBatch(
        TelemetrySample Sample,
        IReadOnlyList<TelemetryEvent> Markers);
}
