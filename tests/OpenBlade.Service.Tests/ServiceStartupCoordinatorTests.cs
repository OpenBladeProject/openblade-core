using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Core.Native;
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
    public async Task InitializeAsync_UnclearedRunMarkerUsesOwnedRecoverySequence()
    {
        string path = Path.Combine(directory, "profiles.json");
        var runMarker = new ServiceRunMarkerStore(
            Path.Combine(directory, "service-running.json"));
        Assert.Equal(
            ServiceStartupKind.Normal,
            await runMarker.BeginRunAsync(CancellationToken.None));
        var runtime = new RecordingRuntime(path);
        var coordinator = CreateCoordinator(
            path,
            runtime,
            new RecordingLogger(),
            runMarker);

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));

        Assert.True(runtime.RecoveringUnexpectedExit);
        Assert.Equal(
            [
                "Initialize",
                "Observe",
                "MaintainDriverMode",
                "ReconcileUnexpectedExit",
                "PrepareManualFanCurveRecovery",
                "MaintainFanCurve",
                "RestoreLighting",
            ],
            runtime.Calls);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_NativeReconciliationFailureReachesWorkerBeforeBackgroundStartup(bool recovery)
    {
        string path = Path.Combine(directory, "profiles.json");
        var marker = new ServiceRunMarkerStore(Path.Combine(directory, "service-running.json"));
        if (recovery)
        {
            await marker.BeginRunAsync(CancellationToken.None);
        }
        var failure = new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
        var runtime = new RecordingRuntime(path) { ReconcileFailure = failure };
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger, marker);
        var events = new List<string>();
        bool backgroundStarted = false;

        NativeComponentException actual = await Assert.ThrowsAsync<NativeComponentException>(() =>
            Worker.RunWithShutdownCleanupAsync(
                token => Worker.RunAfterSuccessfulStartupAsync(coordinator.InitializeAsync,
                    _ => { backgroundStarted = true; return Task.CompletedTask; }, token),
                async (intentional, token) =>
                {
                    Assert.False(intentional);
                    Assert.False(token.IsCancellationRequested);
                    bool? clearMarker = await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
                        coordinator.RuntimeInitialized,
                        intentional,
                        _ =>
                        {
                            events.Add("cleanup");
                            return Task.FromResult(new ControlOperationResult(
                                ControlOperationStatus.Applied, "Guarded cleanup completed."));
                        },
                        logger,
                        token);
                    Assert.False(clearMarker);
                },
                CancellationToken.None,
                error => { Assert.Same(failure, error); events.Add("report"); }));

        Assert.Same(failure, actual);
        Assert.False(backgroundStarted);
        Assert.True(coordinator.RuntimeInitialized);
        Assert.Equal(["report", "cleanup"], events);
        Assert.Equal(["Initialize", "Observe", "MaintainDriverMode",
            recovery ? "ReconcileUnexpectedExit" : "ReconcileStartupProfile"], runtime.Calls);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task InitializeAsync_RuntimeInitializationFailureDoesNotPermitHardwareCleanup()
    {
        string path = Path.Combine(directory, "profiles.json");
        var failure = new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
        var runtime = new RecordingRuntime(path) { Initialized = () => throw failure };
        var logger = new RecordingLogger();
        var coordinator = CreateCoordinator(path, runtime, logger);

        Assert.Same(failure, await Assert.ThrowsAsync<NativeComponentException>(
            () => coordinator.InitializeAsync(CancellationToken.None)));
        Assert.False(coordinator.RuntimeInitialized);
        bool? clearMarker = await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
            coordinator.RuntimeInitialized, intentionalStop: false,
            _ => throw new InvalidOperationException("Uninitialized runtime must not clean hardware."),
            logger, CancellationToken.None);
        Assert.Null(clearMarker);
        Assert.Equal(["Initialize"], runtime.Calls);
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
        Assert.Equal(
            ConfigurationLoadDisposition.Quarantined,
            runtime.Configuration?.Disposition);
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

    [Fact]
    public async Task InitializeAsync_RecoveryRequiredRefusesBeforeRuntimeOrBackgroundWork()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profiles.json");
        string markerPath = Path.Combine(directory, "battery-configuration.commit.json");
        var store = new BatteryConfigurationStore(
            path, Path.Combine(directory, "battery-preferences.json"), markerPath);
        await store.LoadAsync(CancellationToken.None);
        string recoveryPath = markerPath + ".recovery-required";
        await File.WriteAllTextAsync(recoveryPath, "unresolved machine transaction");
        var prior = Directory.GetFiles(directory).ToDictionary(
            file => file, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var runtime = new RecordingRuntime(path);
        var logger = new RecordingLogger();
        ServiceStartupCoordinator coordinator = CreateCoordinator(path, runtime, logger);
        bool? initialized = null;
        bool backgroundStarted = false;

        await Worker.RunAfterSuccessfulStartupAsync(async token =>
        {
            initialized = await coordinator.InitializeAsync(token);
            return initialized.Value;
        }, _ =>
        {
            backgroundStarted = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(initialized);
        Assert.False(coordinator.RuntimeInitialized);
        Assert.False(backgroundStarted);
        Assert.Empty(runtime.Calls);
        Assert.Null(runtime.Configuration);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error
            && entry.Exception is ConfigurationQuarantinedException
            && entry.Exception.Message.Contains(recoveryPath, StringComparison.Ordinal));
        Assert.Equal(prior.Keys.Order(), Directory.GetFiles(directory).Order());
        foreach ((string file, byte[] contents) in prior)
        {
            Assert.Equal(contents, await File.ReadAllBytesAsync(file));
        }
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
        ILogger logger,
        ServiceRunMarkerStore? runMarkerStore = null)
    {
        string directory = Path.GetDirectoryName(path)!;
        var store = new BatteryConfigurationStore(
            path,
            Path.Combine(directory, "battery-preferences.json"),
            Path.Combine(directory, "battery-configuration.commit.json"));
        return new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(store),
            new PersistentOverrideStore(Path.Combine(directory, "manual-overrides.json")),
            runMarkerStore ?? new ServiceRunMarkerStore(
                Path.Combine(directory, "service-running.json")),
            runtime,
            new FixedPowerStateProvider(),
            logger);
    }

    private sealed class RecordingRuntime(string profilePath) : IServiceControlRuntime
    {
        public List<string> Calls { get; } = [];
        public Exception? ObserveFailure { get; init; }
        public Exception? ReconcileFailure { get; init; }
        public Action? Initialized { get; init; }
        public int ObserveCount { get; private set; }
        public bool ProfileFileExistedDuringInitialize { get; private set; }
        public PowerSource? InitialPowerSource { get; private set; }
        public PersistentOverrideState? PersistentOverrides { get; private set; }
        public bool RecoveringUnexpectedExit { get; private set; }
        public BatteryConfiguration? Configuration { get; private set; }

        public void Initialize(BatteryConfiguration configuration, PowerSource powerSource)
        {
            Calls.Add("Initialize");
            Configuration = configuration;
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

        public void Initialize(
            BatteryConfiguration configuration,
            PowerSource powerSource,
            PersistentOverrideState persistentOverrides,
            bool recoveringUnexpectedExit)
        {
            RecoveringUnexpectedExit = recoveringUnexpectedExit;
            Initialize(configuration, powerSource, persistentOverrides);
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
            return ReconcileFailure is null ? Task.CompletedTask : Task.FromException(ReconcileFailure);
        }

        public Task ReconcileUnexpectedExitAsync(CancellationToken cancellationToken)
        {
            Calls.Add("ReconcileUnexpectedExit");
            return ReconcileFailure is null ? Task.CompletedTask : Task.FromException(ReconcileFailure);
        }

        public Task MaintainFanCurveAsync(CancellationToken cancellationToken)
        {
            Calls.Add("MaintainFanCurve");
            return Task.CompletedTask;
        }

        public Task PrepareManualFanCurveRecoveryAsync(
            CancellationToken cancellationToken)
        {
            Calls.Add("PrepareManualFanCurveRecovery");
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
