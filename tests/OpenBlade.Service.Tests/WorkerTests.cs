using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task ListenerEstablishmentFailuresUseCappedCancellableBackoff()
    {
        using var cancellation = new CancellationTokenSource();
        var delays = new List<double>();
        int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker.RunPipeListenerAsync(
            _ =>
            {
                attempts++;
                throw new PipeListenerEstablishmentException(new IOException("busy"));
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            new Worker.PipeListenerErrorReporter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                TimeProvider.System),
            cancellation.Token,
            (delay, token) =>
            {
                delays.Add(delay.TotalMilliseconds);
                if (delays.Count == 8)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }
                return Task.CompletedTask;
            }));

        Assert.Equal([100d, 200d, 400d, 800d, 1600d, 2000d, 2000d, 2000d], delays);
        Assert.Equal(8, attempts);
    }

    [Fact]
    public async Task ListenerFailureDoesNotPreventSiblingStartupAndSupervision()
    {
        bool siblingStarted = false;
        var expected = new InvalidOperationException("sibling failed");
        Task running = ServiceBackgroundTaskGroup.RunAsync(
            [
                token => Worker.RunPipeListenerAsync(
                    _ => throw new PipeListenerEstablishmentException(new IOException("busy")),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    new Worker.PipeListenerErrorReporter(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                        TimeProvider.System),
                    token,
                    Task.Delay),
                _ =>
                {
                    siblingStarted = true;
                    return Task.FromException(expected);
                },
            ],
            CancellationToken.None);

        Exception failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, failure);
        Assert.True(siblingStarted);
    }

    [Fact]
    public void ListenerFailureReportingUsesMonotonicWindowAndSummarizesSuppressedFailures()
    {
        var logger = new RecordingLogger();
        var time = new ManualTimestampProvider();
        var reporter = new Worker.PipeListenerErrorReporter(logger, time);
        var failure = new PipeListenerEstablishmentException(new IOException("busy"));
        TimeSpan retryDelay = TimeSpan.FromSeconds(2);

        reporter.Report(failure, retryDelay);
        Assert.Single(logger.Entries);
        time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        reporter.Report(failure, retryDelay);
        reporter.Report(failure, retryDelay);
        Assert.Single(logger.Entries);

        time.Advance(TimeSpan.FromTicks(1));
        reporter.Report(failure, retryDelay);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(2L, logger.Entries[1].Properties["SuppressedFailures"]);
        time.Advance(TimeSpan.FromSeconds(30));
        reporter.Report(failure, retryDelay);
        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(0L, logger.Entries[2].Properties["SuppressedFailures"]);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Error, entry.Level));
    }

    [Fact]
    public async Task SuccessfulConnectionResetsRetryDelayWithoutDelayingHealthyClients()
    {
        using var cancellation = new CancellationTokenSource();
        var delays = new List<double>();
        int attempts = 0;
        ILogger logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Worker.RunPipeListenerAsync(
            _ => ++attempts is 3 or 4
                ? Task.CompletedTask
                : throw new PipeListenerEstablishmentException(new IOException("busy")),
            logger,
            new Worker.PipeListenerErrorReporter(logger, TimeProvider.System),
            cancellation.Token,
            (delay, token) =>
            {
                delays.Add(delay.TotalMilliseconds);
                if (delays.Count == 3)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }

                return Task.CompletedTask;
            }));

        Assert.Equal([100d, 200d, 100d], delays);
        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task StartupGate_AwaitsConfigurationBeforeBackgroundLoops()
    {
        var configurationLoaded = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int backgroundStarts = 0;

        Task running = Worker.RunAfterSuccessfulStartupAsync(
            _ => configurationLoaded.Task,
            _ =>
            {
                Interlocked.Increment(ref backgroundStarts);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref backgroundStarts));
        configurationLoaded.SetResult(true);
        await running;
        Assert.Equal(1, Volatile.Read(ref backgroundStarts));
    }

    [Fact]
    public async Task ShutdownCleanup_UsesFreshTokenAfterHostTokenIsCancelled()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        bool cleanupRan = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker.RunWithShutdownCleanupAsync(
                token => Task.FromCanceled(token),
                (intentionalStop, token) =>
                {
                    Assert.True(intentionalStop);
                    Assert.False(token.IsCancellationRequested);
                    cleanupRan = true;
                    return Task.CompletedTask;
                },
                stopping.Token));

        Assert.True(cleanupRan);
    }

    [Fact]
    public async Task UnexpectedBackgroundFailure_IsReportedToCleanup()
    {
        bool? intentionalStop = null;

        await Assert.ThrowsAsync<IOException>(() =>
            Worker.RunWithShutdownCleanupAsync(
                _ => Task.FromException(new IOException("background failed")),
                (intentional, _) =>
                {
                    intentionalStop = intentional;
                    return Task.CompletedTask;
                },
                CancellationToken.None));

        Assert.False(intentionalStop);
    }

    [Fact]
    public async Task UnexpectedBackgroundFailure_AttemptsCleanupButRetainsRunMarker()
    {
        int cleanupCalls = 0;

        bool? clearRunMarker = await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
            startupCompleted: true,
            intentionalStop: false,
            _ =>
            {
                cleanupCalls++;
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Hardware cleanup completed."));
            },
            new RecordingLogger(),
            CancellationToken.None);

        Assert.Equal(1, cleanupCalls);
        Assert.False(clearRunMarker);
    }

    [Fact]
    public async Task StartupFailure_SkipsCleanupWithoutHardwareWarning()
    {
        bool cleanupCalled = false;
        var logger = new RecordingLogger();

        bool? clearRunMarker = await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
            startupCompleted: false,
            intentionalStop: false,
            _ =>
            {
                cleanupCalled = true;
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Hardware cleanup completed."));
            },
            logger,
            CancellationToken.None);

        Assert.Null(clearRunMarker);
        Assert.False(cleanupCalled);
        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(
            "startup did not complete",
            entry.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShutdownCleanup_StoredSkipLogsReasonOnceWithoutWarning()
    {
        const string reason = "Razer Synapse is active, so cleanup was skipped.";
        var logger = new RecordingLogger();

        Worker.LogShutdownCleanupResult(
            logger,
            new ControlOperationResult(ControlOperationStatus.Stored, reason));

        LogEntry entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(
            ControlOperationStatus.Stored,
            Assert.IsType<ControlOperationStatus>(entry.Properties["Status"]));
        Assert.Equal(reason, entry.Properties["Message"]);
    }

    [Fact]
    public void ShutdownCleanup_FailedResultLogsInformationAndWarning()
    {
        const string reason = "Automatic fan control could not be confirmed.";
        var logger = new RecordingLogger();

        Worker.LogShutdownCleanupResult(
            logger,
            new ControlOperationResult(ControlOperationStatus.Failed, reason));

        LogEntry information = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Information);
        Assert.Equal(
            ControlOperationStatus.Failed,
            Assert.IsType<ControlOperationStatus>(information.Properties["Status"]));
        Assert.Equal(reason, information.Properties["Message"]);
        LogEntry warning = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning);
        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Applied, true)]
    [InlineData(ControlOperationStatus.Stored, true)]
    [InlineData(ControlOperationStatus.Blocked, false)]
    [InlineData(ControlOperationStatus.Failed, false)]
    public async Task RunMarker_IsClearedOnlyAfterConfirmedOrDeliberatelySkippedCleanup(
        ControlOperationStatus status,
        bool expected)
    {
        bool? clearRunMarker = await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
            startupCompleted: true,
            intentionalStop: true,
            _ => Task.FromResult(new ControlOperationResult(status, "result")),
            new RecordingLogger(),
            CancellationToken.None);

        Assert.Equal(
            expected,
            clearRunMarker);
    }

    private sealed class ManualTimestampProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException(
            "Listener error reporting must use monotonic time.");

        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, object?> properties = state is
                IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);
}
