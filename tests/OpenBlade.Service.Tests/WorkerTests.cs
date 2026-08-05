using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WorkerTests
{
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
                token =>
                {
                    Assert.False(token.IsCancellationRequested);
                    cleanupRan = true;
                    return Task.CompletedTask;
                },
                stopping.Token));

        Assert.True(cleanupRan);
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
