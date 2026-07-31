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

    [Fact]
    public async Task InitializeAsync_LoadsThenInitializesThenObservesExactlyOnce()
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path);
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        bool started = await coordinator.InitializeAsync(CancellationToken.None);

        Assert.True(started);
        Assert.Equal(
            [
                "Initialize",
                "Observe",
                "MaintainDriverMode",
                "ReconcileStartupProfile",
                "RestoreLighting",
            ],
            runtime.Calls);
        Assert.Equal(1, runtime.ObserveCount);
        Assert.True(runtime.ProfileFileExistedDuringInitialize);
        Assert.Equal(PowerSource.PluggedIn, runtime.InitialPowerSource);
    }

    [Fact]
    public async Task InitializeAsync_ObservationFailureLogsErrorAndAllowsIpcStartup()
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path)
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
        var runtime = new RecordingRuntime(path)
        {
            ObserveFailure = new OperationCanceledException(cancellation.Token),
            Initialized = cancellation.Cancel,
        };
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.InitializeAsync(cancellation.Token));

        Assert.Equal(["Initialize", "Observe"], runtime.Calls);
        Assert.Equal(1, runtime.ObserveCount);
    }

    [Fact]
    public async Task InitializeAsync_RecoveredConfigurationLogsWarning()
    {
        string path = Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{invalid-json");
        var runtime = new RecordingRuntime(path);
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger);

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task InitializeAsync_LoadsPersistentOverridesIntoRuntime()
    {
        string path = Path.Combine(directory, "profiles.json");
        var expected = new PersistentOverrideState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            null,
            FanControl: FanControlSettings.FromLegacy(FanMode.Maximum));
        await new PersistentOverrideStore(Path.Combine(directory, "manual-overrides.json"))
            .SaveAsync(expected, CancellationToken.None);
        var runtime = new RecordingRuntime(path);
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));

        Assert.Equal(expected, runtime.PersistentOverrides);
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
        ILogger logger)
    {
        string directory = Path.GetDirectoryName(path)!;
        var store = new BatteryConfigurationStore(
            path,
            Path.Combine(directory, "battery-preferences.json"),
            Path.Combine(directory, "battery-configuration.commit.json"));
        return new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(store),
            new PersistentOverrideStore(Path.Combine(directory, "manual-overrides.json")),
            runtime,
            new FixedPowerStateProvider(),
            logger);
    }

    private sealed class RecordingRuntime(string profilePath) : IServiceControlRuntime
    {
        public List<string> Calls { get; } = [];
        public Exception? ObserveFailure { get; init; }
        public Action? Initialized { get; init; }
        public int ObserveCount { get; private set; }
        public bool ProfileFileExistedDuringInitialize { get; private set; }
        public PowerSource? InitialPowerSource { get; private set; }
        public PersistentOverrideState? PersistentOverrides { get; private set; }

        public void Initialize(BatteryConfiguration configuration, PowerSource powerSource)
        {
            Calls.Add("Initialize");
            ProfileFileExistedDuringInitialize = File.Exists(profilePath);
            InitialPowerSource = powerSource;
            Initialized?.Invoke();
        }

        public void Initialize(
            BatteryConfiguration configuration,
            PowerSource powerSource,
            PersistentOverrideState persistentOverrides)
        {
            PersistentOverrides = persistentOverrides;
            Initialize(configuration, powerSource);
        }

        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken)
        {
            Calls.Add("Observe");
            ObserveCount++;
            return ObserveFailure is null
                ? Task.CompletedTask
                : Task.FromException(ObserveFailure);
        }

        public Task RestoreLightingRendererAsync(CancellationToken cancellationToken)
        {
            Calls.Add("RestoreLighting");
            return Task.CompletedTask;
        }

        public Task MaintainDriverModeAsync(CancellationToken cancellationToken)
        {
            Calls.Add("MaintainDriverMode");
            return Task.CompletedTask;
        }

        public Task ReconcileStartupProfileAsync(CancellationToken cancellationToken)
        {
            Calls.Add("ReconcileStartupProfile");
            return Task.CompletedTask;
        }

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedPowerStateProvider : IPowerStateProvider
    {
        public PowerSource CapturePowerSource() => PowerSource.PluggedIn;

        public int? CaptureBatteryPowerFlowMilliwatts() => null;
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
