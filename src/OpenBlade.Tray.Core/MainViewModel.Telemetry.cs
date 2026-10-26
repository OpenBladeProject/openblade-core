using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private readonly TelemetryHistory telemetryHistory = new();
    private readonly SemaphoreSlim telemetryEnabledGate = new(1, 1);
    private readonly SemaphoreSlim telemetryRecordingGate = new(1, 1);
    private readonly object telemetryAppendSync = new();
    private ITelemetryRecordingSession? telemetryRecording;
    private Func<string, TelemetrySessionMetadata, TimeProvider, CancellationToken,
        Task<ITelemetryRecordingSession>> telemetryRecordingFactory = DefaultTelemetryRecordingFactoryAsync;
    private Func<string, TimeProvider, CancellationToken, Task<TelemetrySessionDocument>>
        telemetryReplayReader = DefaultTelemetryReplayReaderAsync;
    private TelemetrySessionDocument? telemetryReplaySession;
    private TelemetryHistory? telemetryReplayHistory;
    private TelemetrySample? latestLiveTelemetrySample;
    private bool telemetryPresentationActive;
    private DateTimeOffset? lastTelemetryTimestampUtc;
    private string telemetryErrorMessage = string.Empty;
    private int pendingTelemetryAppends;
    private bool telemetryRecordingAccepting;
    private TaskCompletionSource? telemetryAppendsDrained;
    private bool telemetryEnabled;
    private long telemetryEnableGeneration;

    public bool TelemetryEnabled
    {
        get => telemetryEnabled;
        private set
        {
            if (telemetryEnabled == value)
            {
                return;
            }
            SetField(ref telemetryEnabled, value);
            telemetryEnableGeneration++;
            Notify(nameof(RequiresTelemetryObservation));
        }
    }

    public bool IsTelemetryPresentationActive => telemetryPresentationActive;

    public bool IsTelemetryRecording
    {
        get
        {
            lock (telemetryAppendSync)
            {
                return telemetryRecordingAccepting
                    && telemetryRecording?.RequiresSampling == true;
            }
        }
    }

    public bool RequiresTelemetryObservation =>
        TelemetryEnabled
        && (IsTelemetryRecording
            || IsTelemetryPresentationActive && TelemetryReplaySession is null);

    public string? TelemetryRecordingPath => telemetryRecording?.DestinationPath;

    public string TelemetryErrorMessage
    {
        get => telemetryErrorMessage;
        private set => SetField(ref telemetryErrorMessage, value);
    }

    public TelemetrySessionDocument? TelemetryReplaySession => telemetryReplaySession;

    public TelemetrySessionMetadata? DisplayedTelemetrySessionMetadata =>
        TelemetryReplaySession?.Metadata ?? telemetryRecording?.Metadata;

    // Recording changes the CSV destination, while the live data source stays the same.
    public Guid? DisplayedTelemetrySourceSessionId => TelemetryReplaySession?.Metadata.SessionId;

    public TelemetrySource DisplayedTelemetrySource =>
        TelemetryReplaySession is null
            ? TelemetrySource.LiveService
            : TelemetrySource.LocalCsvReplay;

    public TelemetrySample? LatestTelemetrySample => TelemetryReplaySession is { } replay
        ? replay.Samples.LastOrDefault() : latestLiveTelemetrySample;

    public TelemetryFreshness? LatestTelemetryFreshness => LatestTelemetrySample is { } sample
        ? sample.GetFreshness(timeProvider.GetUtcNow(), TelemetryFreshnessInterval)
        : null;

    public TelemetryHistorySnapshot TelemetryHistorySnapshot => GetTelemetrySnapshot();

    public TimeSpan TelemetryExpectedSampleInterval => LiveReadingsObservationInterval > TimeSpan.Zero
        && LiveReadingsObservationInterval != Timeout.InfiniteTimeSpan
            ? LiveReadingsObservationInterval
            : TimeSpan.FromSeconds(2);

    private TimeSpan TelemetryFreshnessInterval => TelemetryExpectedSampleInterval;

    public async Task SetTelemetryEnabledAsync(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await telemetryEnabledGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (enabled)
            {
                TelemetryEnabled = true;
                return;
            }

            TelemetryEnabled = false;
            CloseTelemetryReplay();

            // Disabling telemetry is a committed user action. Reuse the normal
            // recording stop path so already accepted appends drain and the CSV
            // is finalized atomically; cancellation must not strand a partial
            // session or reopen append admission after the feature is off.
            await StopTelemetryRecordingAsync(CancellationToken.None);
        }
        finally
        {
            telemetryEnabledGate.Release();
        }
    }

    public void SetTelemetryPresentationActive(bool active)
    {
        if (telemetryPresentationActive == active)
        {
            return;
        }
        telemetryPresentationActive = active;
        Notify(nameof(IsTelemetryPresentationActive));
        Notify(nameof(RequiresTelemetryObservation));
    }

    public TelemetryHistorySnapshot GetTelemetrySnapshot(
        DateTimeOffset? rangeStartUtc = null,
        DateTimeOffset? rangeEndUtc = null,
        int maximumChartPoints = TelemetryHistory.DefaultMaximumChartPoints)
    {
        TelemetryHistorySnapshot snapshot = (telemetryReplayHistory ?? telemetryHistory).Snapshot(
            rangeStartUtc,
            rangeEndUtc,
            maximumChartPoints,
            TelemetryExpectedSampleInterval);
        return telemetryReplaySession is { } replay ? snapshot with
        {
            Events = replay.Events.Where(marker =>
                (rangeStartUtc is null || marker.TimestampUtc >= rangeStartUtc)
                && (rangeEndUtc is null || marker.TimestampUtc <= rangeEndUtc)).ToArray(),
        } : snapshot;
    }

    public async Task StartTelemetryRecordingAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await telemetryRecordingGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!TelemetryEnabled)
            {
                throw new InvalidOperationException(TrayText.Get("Telemetry is disabled."));
            }
            if (telemetryRecording is not null)
            {
                throw new InvalidOperationException("Telemetry recording is already active.");
            }
            ITelemetryRecordingSession created = await telemetryRecordingFactory(
                destinationPath,
                TelemetrySessionMetadata.CreateLive(timeProvider.GetUtcNow()),
                timeProvider,
                cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            }
            catch
            {
                try
                {
                    await created.DisposeAsync();
                }
                catch (Exception exception)
                {
                    // Preserve the disposal admission failure. The partial
                    // recording remains available if its writer cannot close.
                    DiagnosticTrace.TraceException(
                        "OpenBlade.Tray.MainViewModel.Telemetry",
                        "DisposeRejectedRecording",
                        exception);
                }
                throw;
            }
            lock (telemetryAppendSync)
            {
                telemetryRecording = created;
                telemetryRecordingAccepting = true;
            }
            _ = ObserveTelemetryRecordingCompletionAsync(created);
            TelemetryErrorMessage = string.Empty;
            NotifyTelemetryRecordingStateChanged();
        }
        finally
        {
            telemetryRecordingGate.Release();
        }
    }

    public async Task<string?> StopTelemetryRecordingAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await telemetryRecordingGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            ITelemetryRecordingSession? recording = telemetryRecording;
            if (recording is null)
            {
                return null;
            }
            Task pendingAppends;
            lock (telemetryAppendSync)
            {
                telemetryRecordingAccepting = false;
                pendingAppends = PendingTelemetryAppendsDrainedTaskLocked();
            }
            NotifyTelemetryRecordingStateChanged();
            try
            {
                await pendingAppends.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (telemetryAppendSync)
                {
                    if (ReferenceEquals(telemetryRecording, recording)
                        && recording.RequiresSampling)
                    {
                        telemetryRecordingAccepting = true;
                    }
                }
                NotifyTelemetryRecordingStateChanged();
                throw;
            }
            try
            {
                // Once admission is closed and every accepted append is drained,
                // finalization owns the partial file through its atomic move. Do
                // not abandon that finite operation and strand an inactive session.
                string path = await recording.StopAsync(CancellationToken.None);
                lock (telemetryAppendSync)
                {
                    if (ReferenceEquals(telemetryRecording, recording))
                    {
                        telemetryRecording = null;
                    }
                }
                NotifyTelemetryRecordingStateChanged();
                return path;
            }
            catch (Exception exception)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.MainViewModel.Telemetry",
                    "FinalizeRecording",
                    exception);
                lock (telemetryAppendSync)
                {
                    if (ReferenceEquals(telemetryRecording, recording))
                    {
                        telemetryRecording = null;
                    }
                }
                TelemetryErrorMessage = TrayText.Format(
                    "The telemetry recording could not be finalized. The partial recording remains at {0}. {1}",
                    recording.PartialPath, exception.Message);
                NotifyTelemetryRecordingStateChanged();
                throw;
            }
        }
        finally
        {
            telemetryRecordingGate.Release();
        }
    }

    public async Task OpenTelemetryReplayAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!TelemetryEnabled)
        {
            throw new InvalidOperationException(TrayText.Get("Telemetry is disabled."));
        }
        long enableGeneration = telemetryEnableGeneration;
        TelemetrySessionDocument document = await telemetryReplayReader(
            path,
            timeProvider,
            cancellationToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        TelemetryHistory history = await Task.Run(
            () => BuildReplayHistory(document, cancellationToken),
            cancellationToken);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!TelemetryEnabled || telemetryEnableGeneration != enableGeneration)
        {
            throw new InvalidOperationException(TrayText.Get("Telemetry is disabled."));
        }
        telemetryReplaySession = document;
        telemetryReplayHistory = history;
        TelemetryErrorMessage = string.Empty;
        NotifyTelemetryViewChanged();
        Notify(nameof(RequiresTelemetryObservation));
    }

    public void CloseTelemetryReplay()
    {
        if (telemetryReplaySession is null)
        {
            return;
        }
        telemetryReplaySession = null;
        telemetryReplayHistory = null;
        NotifyTelemetryViewChanged();
        Notify(nameof(RequiresTelemetryObservation));
    }

    public void RefreshTelemetryFreshness() =>
        Notify(nameof(LatestTelemetryFreshness));

    private TelemetryRecordBatch? CaptureTelemetryObservation(ServiceStatusSnapshot status)
    {
        if (!TelemetryEnabled)
        {
            return null;
        }
        ITelemetryRecordingSession? recording = null;
        lock (telemetryAppendSync)
        {
            if (telemetryRecordingAccepting
                && telemetryRecording?.RequiresSampling == true)
            {
                recording = telemetryRecording;
                pendingTelemetryAppends++;
            }
            else if (!telemetryPresentationActive || telemetryReplaySession is not null)
            {
                return null;
            }
        }
        try
        {
            DateTimeOffset timestamp = timeProvider.GetUtcNow();
            if (lastTelemetryTimestampUtc is { } previous && timestamp <= previous)
            {
                timestamp = previous.AddTicks(1);
            }
            TelemetrySample sample = TelemetrySample.Project(status, timestamp);
            IReadOnlyList<TelemetryEvent> markers = telemetryHistory.AddSample(sample);
            lastTelemetryTimestampUtc = timestamp;
            latestLiveTelemetrySample = sample;
            Notify(nameof(LatestTelemetrySample));
            Notify(nameof(LatestTelemetryFreshness));
            Notify(nameof(TelemetryHistorySnapshot));
            return new TelemetryRecordBatch(sample, markers, recording);
        }
        catch
        {
            if (recording is not null)
            {
                CompleteTelemetryAppend();
            }
            throw;
        }
    }

    private async Task AppendTelemetryRecordingAsync(
        TelemetryRecordBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.Recording is null)
        {
            return;
        }
        Exception? failure = null;
        try
        {
            await batch.Recording.AppendAsync(
                batch.Sample,
                batch.Markers,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            CompleteTelemetryAppend();
        }
        if (failure is not null)
        {
            await HandleTelemetryRecordingFailureAsync(batch.Recording, failure)
                .ConfigureAwait(false);
        }
    }

    private async Task DisposeTelemetryAsync()
    {
        await telemetryRecordingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ITelemetryRecordingSession? recording;
            Task pendingAppends;
            lock (telemetryAppendSync)
            {
                recording = telemetryRecording;
                telemetryRecordingAccepting = false;
                pendingAppends = PendingTelemetryAppendsDrainedTaskLocked();
            }
            await pendingAppends.ConfigureAwait(false);
            lock (telemetryAppendSync)
            {
                if (ReferenceEquals(telemetryRecording, recording))
                {
                    telemetryRecording = null;
                }
            }
            if (recording is not null)
            {
                try
                {
                    await recording.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException)
                {
                    // A recording writer fault must not prevent the remaining
                    // view-model resources from being released during shutdown.
                    DiagnosticTrace.TraceException(
                        "OpenBlade.Tray.MainViewModel.Telemetry",
                        "DisposeRecordingDuringShutdown",
                        disposeException);
                }
            }
        }
        finally
        {
            telemetryRecordingGate.Release();
        }
    }

    private void NotifyTelemetryRecordingStateChanged()
    {
        Notify(nameof(IsTelemetryRecording));
        Notify(nameof(RequiresTelemetryObservation));
        Notify(nameof(TelemetryRecordingPath));
        Notify(nameof(DisplayedTelemetrySessionMetadata));
    }

    private void NotifyTelemetryViewChanged()
    {
        Notify(nameof(TelemetryReplaySession));
        Notify(nameof(DisplayedTelemetrySourceSessionId));
        Notify(nameof(DisplayedTelemetrySessionMetadata));
        Notify(nameof(DisplayedTelemetrySource));
        Notify(nameof(LatestTelemetrySample));
        Notify(nameof(LatestTelemetryFreshness));
        Notify(nameof(TelemetryHistorySnapshot));
    }

    private sealed record TelemetryRecordBatch(
        TelemetrySample Sample,
        IReadOnlyList<TelemetryEvent> Markers,
        ITelemetryRecordingSession? Recording);

    internal void SetTelemetryRecordingFactoryForTests(
        Func<string, TelemetrySessionMetadata, TimeProvider, CancellationToken,
            Task<ITelemetryRecordingSession>> factory) =>
        telemetryRecordingFactory = factory ?? throw new ArgumentNullException(nameof(factory));

    internal void SetTelemetryReplayReaderForTests(
        Func<string, TimeProvider, CancellationToken, Task<TelemetrySessionDocument>> reader) =>
        telemetryReplayReader = reader ?? throw new ArgumentNullException(nameof(reader));

    private static async Task<ITelemetryRecordingSession> DefaultTelemetryRecordingFactoryAsync(
        string path,
        TelemetrySessionMetadata metadata,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        await TelemetryRecordingSession.StartAsync(
            path,
            metadata,
            timeProvider: clock,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private static Task<TelemetrySessionDocument> DefaultTelemetryReplayReaderAsync(
        string path,
        TimeProvider clock,
        CancellationToken cancellationToken) =>
        TelemetryCsv.ReadAsync(
            path,
            timeProvider: clock,
            cancellationToken: cancellationToken);

    private static TelemetryHistory BuildReplayHistory(
        TelemetrySessionDocument document,
        CancellationToken cancellationToken)
    {
        var history = new TelemetryHistory(Math.Max(1, document.Samples.Count));
        foreach (TelemetrySample sample in document.Samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            history.AddSample(sample);
        }
        return history;
    }

    private Task PendingTelemetryAppendsDrainedTaskLocked()
    {
        if (pendingTelemetryAppends == 0)
        {
            return Task.CompletedTask;
        }
        telemetryAppendsDrained ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return telemetryAppendsDrained.Task;
    }

    private void CompleteTelemetryAppend()
    {
        TaskCompletionSource? drained = null;
        lock (telemetryAppendSync)
        {
            pendingTelemetryAppends--;
            if (pendingTelemetryAppends == 0)
            {
                drained = telemetryAppendsDrained;
                telemetryAppendsDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private async Task HandleTelemetryRecordingFailureAsync(
        ITelemetryRecordingSession recording,
        Exception exception)
    {
        DiagnosticTrace.TraceException(
            "OpenBlade.Tray.MainViewModel.Telemetry",
            "RecordTelemetry",
            exception);
        await telemetryRecordingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (telemetryAppendSync)
            {
                if (!ReferenceEquals(telemetryRecording, recording))
                {
                    return;
                }
                telemetryRecordingAccepting = false;
            }
            await dispatcher.InvokeAsync(
                () =>
                {
                    lock (telemetryAppendSync)
                    {
                        if (ReferenceEquals(telemetryRecording, recording))
                        {
                            telemetryRecording = null;
                        }
                    }
                    TelemetryErrorMessage = TrayText.Format(
                        "Telemetry recording stopped. The partial recording remains at {0}. {1}",
                        recording.PartialPath, exception.Message);
                    NotifyTelemetryRecordingStateChanged();
                },
                CancellationToken.None).ConfigureAwait(false);
            try
            {
                await recording.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException)
            {
                // The partial recording remains available for recovery.
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.MainViewModel.Telemetry",
                    "DisposeFailedRecording",
                    disposeException);
            }
        }
        finally
        {
            telemetryRecordingGate.Release();
        }
    }

    private async Task ObserveTelemetryRecordingCompletionAsync(
        ITelemetryRecordingSession recording)
    {
        try
        {
            await recording.Completion.ConfigureAwait(false);
            if (recording.AutomaticCompletionReason is not null)
            {
                await FinalizeAutomaticallyCompletedTelemetryRecordingAsync(recording)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await HandleTelemetryRecordingFailureAsync(recording, exception)
                .ConfigureAwait(false);
        }
    }

    private async Task FinalizeAutomaticallyCompletedTelemetryRecordingAsync(
        ITelemetryRecordingSession recording)
    {
        await telemetryRecordingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (telemetryAppendSync)
            {
                if (!ReferenceEquals(telemetryRecording, recording))
                {
                    return;
                }
                telemetryRecordingAccepting = false;
            }

            try
            {
                string path = await recording.StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        lock (telemetryAppendSync)
                        {
                            if (ReferenceEquals(telemetryRecording, recording))
                            {
                                telemetryRecording = null;
                            }
                        }
                        TelemetryErrorMessage = TrayText.Format(
                            "Telemetry recording reached its limit and was saved to {0}.",
                            path);
                        NotifyTelemetryRecordingStateChanged();
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                DiagnosticTrace.TraceException(
                    "OpenBlade.Tray.MainViewModel.Telemetry",
                    "FinalizeAutomaticRecording",
                    exception);
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        lock (telemetryAppendSync)
                        {
                            if (ReferenceEquals(telemetryRecording, recording))
                            {
                                telemetryRecording = null;
                            }
                        }
                        TelemetryErrorMessage = TrayText.Format(
                            "The telemetry recording could not be finalized. The partial recording remains at {0}. {1}",
                            recording.PartialPath, exception.Message);
                        NotifyTelemetryRecordingStateChanged();
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            telemetryRecordingGate.Release();
        }
    }
}
