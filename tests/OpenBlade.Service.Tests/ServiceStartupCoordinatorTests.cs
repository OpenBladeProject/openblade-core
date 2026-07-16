using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceStartupCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.ServiceStartupCoordinatorTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ControlOperationStatus.Stored)]
    [InlineData(ControlOperationStatus.Applied)]
    public async Task InitializeAsync_LoadsStoresThenObservesExactlyOnce(
        ControlOperationStatus profileStatus)
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path, profileStatus);
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        bool started = await coordinator.InitializeAsync(CancellationToken.None);

        Assert.True(started);
        Assert.Equal(["UpdateProfiles", "Observe"], runtime.Calls);
        Assert.Equal(1, runtime.ObserveCount);
        Assert.True(runtime.ProfileFileExistedDuringUpdate);
    }

    [Theory]
    [InlineData(ControlOperationStatus.Blocked)]
    [InlineData(ControlOperationStatus.Failed)]
    public async Task InitializeAsync_FailedProfileUpdateLogsCriticalAndSkipsObserve(
        ControlOperationStatus profileStatus)
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path, profileStatus);
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger);

        bool started = await coordinator.InitializeAsync(CancellationToken.None);

        Assert.False(started);
        Assert.Equal(["UpdateProfiles"], runtime.Calls);
        Assert.Equal(0, runtime.ObserveCount);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Critical);
    }

    [Fact]
    public async Task InitializeAsync_ObservationFailureLogsErrorAndAllowsIpcStartup()
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path, ControlOperationStatus.Stored)
        {
            ObserveFailure = new IOException("device unavailable"),
        };
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger);

        bool started = await coordinator.InitializeAsync(CancellationToken.None);

        Assert.True(started);
        Assert.Equal(1, runtime.ObserveCount);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error
                && ReferenceEquals(entry.Exception, runtime.ObserveFailure));
    }

    [Fact]
    public async Task InitializeAsync_ObservationCancellationPropagates()
    {
        string path = Path.Combine(directory, "profiles.json");
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime(path, ControlOperationStatus.Stored)
        {
            ObserveFailure = new OperationCanceledException(cancellation.Token),
            UpdateCompleted = cancellation.Cancel,
        };
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.InitializeAsync(cancellation.Token));

        Assert.Equal(["UpdateProfiles", "Observe"], runtime.Calls);
        Assert.Equal(1, runtime.ObserveCount);
    }

    [Fact]
    public async Task InitializeAsync_RecoveredProfilesLogWarning()
    {
        string path = Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{invalid-json");
        var runtime = new RecordingRuntime(path, ControlOperationStatus.Stored);
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger);

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WorkerGate_DoesNotStartBackgroundWorkBeforeStartupSucceeds()
    {
        var startupRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int backgroundStarts = 0;

        Task running = Worker.RunAfterSuccessfulStartupAsync(
            _ => startupRelease.Task,
            _ =>
            {
                Interlocked.Increment(ref backgroundStarts);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref backgroundStarts));
        startupRelease.SetResult(true);
        await running;
        Assert.Equal(1, Volatile.Read(ref backgroundStarts));
    }

    [Fact]
    public async Task WorkerGate_FailedStartupSkipsAllBackgroundWork()
    {
        int backgroundStarts = 0;

        await Worker.RunAfterSuccessfulStartupAsync(
            _ => Task.FromResult(false),
            _ =>
            {
                Interlocked.Increment(ref backgroundStarts);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref backgroundStarts));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceStartupCoordinator CreateCoordinator(
        string path,
        IServiceControlRuntime runtime,
        ILogger logger) => new(
            new MachineProfileBootstrapper(new ProfileStore(path)),
            runtime,
            logger);

    private sealed class RecordingRuntime(
        string profilePath,
        ControlOperationStatus profileStatus) : IServiceControlRuntime
    {
        public List<string> Calls { get; } = [];
        public Exception? ObserveFailure { get; init; }
        public Action? UpdateCompleted { get; init; }
        public int ObserveCount { get; private set; }
        public bool ProfileFileExistedDuringUpdate { get; private set; }

        public Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token)
        {
            Calls.Add("UpdateProfiles");
            ProfileFileExistedDuringUpdate = File.Exists(profilePath);
            UpdateCompleted?.Invoke();
            return Task.FromResult(new ControlOperationResult(profileStatus, profileStatus.ToString()));
        }

        public Task ObserveAsync(CancellationToken cancellationToken)
        {
            Calls.Add("Observe");
            ObserveCount++;
            return ObserveFailure is null
                ? Task.CompletedTask
                : Task.FromException(ObserveFailure);
        }

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
