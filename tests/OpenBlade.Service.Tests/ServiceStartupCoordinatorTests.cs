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
        Assert.True(runtime.RecoveringManagedRun);
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
    public async Task InitializeAsync_NativeHandbackUsesOwnedRecoverySequenceWithoutRunMarker()
    {
        string path = Path.Combine(directory, "profiles.json");
        var runtime = new RecordingRuntime(path) { NativeHandbackRecoveryPending = true };
        var coordinator = CreateCoordinator(path, runtime, new RecordingLogger());

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));

        // A crashed native generation left unconfirmed writes: no profile apply.
        // Without a managed run marker, managed owned no fan state to recover.
        Assert.True(runtime.RecoveringUnexpectedExit);
        Assert.False(runtime.RecoveringManagedRun);
        Assert.DoesNotContain("ReconcileStartupProfile", runtime.Calls);
        Assert.Contains("ReconcileUnexpectedExit", runtime.Calls);
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
                            var result = new ControlOperationResult(
                                ControlOperationStatus.Applied, "Guarded cleanup completed.");
                            return Task.FromResult(new ServiceStopCleanupResult(result, result.Status));
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

    [Fact]
    public async Task InitializeAsync_OverrideContentionAfterBootstrapRefusesStartup()
    {
        var writer = new ScratchConfigurationWriter(directory);
        IDisposable? competingLease = null;
        int acquisitions = 0;
        var binding = new MachineConfigurationWriterBinding(directory, (root, token) =>
        {
            if (++acquisitions == 2)
            {
                competingLease = writer.Acquire(root, token);
            }
            return writer.Acquire(root, token);
        });
        await File.WriteAllTextAsync(binding.OverridePath, MachineConfigurationWriterTests.LegacyOverride);
        byte[] before = await File.ReadAllBytesAsync(binding.OverridePath);
        var runtime = new RecordingRuntime(binding.ProfilePath)
        {
            Initialized = () => { using IDisposable lease = writer.Acquire(); },
        };
        var logger = new RecordingLogger();
        string markerPath = Path.Combine(directory, "service-running.json");
        var coordinator = new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(new BatteryConfigurationStore(binding)),
            new PersistentOverrideStore(binding),
            new ServiceRunMarkerStore(markerPath),
            runtime,
            new FixedPowerStateProvider(),
            logger);

        try
        {
            Assert.False(await coordinator.InitializeAsync(CancellationToken.None));
            Assert.False(coordinator.RuntimeInitialized);
            Assert.Empty(runtime.Calls);
            Assert.True(File.Exists(binding.MarkerPath));
            Assert.False(File.Exists(markerPath));
            Assert.Equal(before, await File.ReadAllBytesAsync(binding.OverridePath));
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error
                && entry.Exception is IOException);
        }
        finally
        {
            competingLease?.Dispose();
        }

        Assert.True(await coordinator.InitializeAsync(CancellationToken.None));
        Assert.Equal(PerformancePreset.Performance, runtime.PersistentOverrides?.Performance);
        Assert.Equal(4, acquisitions);
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("stale-receipt")]
    [InlineData("recovery")]
    [InlineData("marker")]
    [InlineData("marker-temp")]
    public async Task InitializeAsync_ExplicitTransitionRefusalPreservesEveryFile(string evidence)
    {
        var writer = new ScratchConfigurationWriter(directory);
        var binding = new MachineConfigurationWriterBinding(directory, writer.Acquire);
        Guid outgoing = Guid.NewGuid();
        string receiptPath = Path.Combine(directory, "retirement-receipt");
        if (evidence != "missing-receipt")
        {
            await File.WriteAllTextAsync(receiptPath,
                (evidence == "stale-receipt" ? Guid.NewGuid() : outgoing).ToString("N"));
        }
        await File.WriteAllTextAsync(binding.OverridePath, MachineConfigurationWriterTests.LegacyOverride);
        if (evidence == "recovery")
        {
            await File.WriteAllTextAsync(binding.MarkerPath + ".recovery-required", "unresolved transaction");
        }
        else if (evidence is "marker" or "marker-temp")
        {
            await File.WriteAllTextAsync(
                binding.RunMarkerPath + (evidence == "marker-temp" ? ".tmp" : ""), "outgoing marker evidence");
        }

        var before = Directory.GetFiles(directory).ToDictionary(file => file, File.ReadAllBytes);
        int retirementChecks = 0;
        var marker = new ServiceRunMarkerStore(binding.RunMarkerPath, () => DateTimeOffset.UtcNow,
            () => TimeSpan.FromHours(1), writer.Acquire, () =>
            {
                retirementChecks++;
                if (!File.Exists(receiptPath) || File.ReadAllText(receiptPath) != outgoing.ToString("N"))
                {
                    throw new InvalidOperationException("No receipt for the outgoing generation.");
                }
            });
        var runtime = new RecordingRuntime(binding.ProfilePath);
        var coordinator = new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(new BatteryConfigurationStore(binding)),
            new PersistentOverrideStore(binding), marker, runtime, new FixedPowerStateProvider(), new RecordingLogger());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        if (evidence == "recovery")
        {
            Assert.False(await coordinator.InitializeAsync(deadline.Token));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync(deadline.Token));
        }

        Assert.Equal(evidence == "marker-temp" ? 0 : 1, retirementChecks);
        Assert.False(coordinator.RuntimeInitialized);
        Assert.Empty(runtime.Calls);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(directory).Order());
        foreach ((string file, byte[] bytes) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        }
        using IDisposable released = writer.Acquire();
    }

    [Theory]
    [InlineData("different-root")]
    [InlineData("different-provider")]
    [InlineData("wrong-marker-leaf")]
    [InlineData("different-bootstrap-provider")]
    public async Task InitializeAsync_ExplicitTransitionRejectsMismatchedEnrollment(string mismatch)
    {
        var writer = new ScratchConfigurationWriter(directory);
        var binding = new MachineConfigurationWriterBinding(directory, writer.Acquire);
        await File.WriteAllTextAsync(binding.OverridePath, MachineConfigurationWriterTests.LegacyOverride);
        string markerPath = mismatch switch
        {
            "different-root" => Path.Combine(directory, "other", "service-running.json"),
            "wrong-marker-leaf" => binding.ProfilePath,
            _ => binding.RunMarkerPath,
        };
        Func<string, CancellationToken, IDisposable> markerAcquire = mismatch == "different-provider"
            ? (root, token) => writer.Acquire(root, token)
            : writer.Acquire;
        var marker = new ServiceRunMarkerStore(markerPath, () => DateTimeOffset.UtcNow, () => TimeSpan.Zero,
            markerAcquire, () => Assert.Equal("different-bootstrap-provider", mismatch));
        var bootstrapBinding = mismatch == "different-bootstrap-provider"
            ? new MachineConfigurationWriterBinding(directory, (root, token) => writer.Acquire(root, token))
            : binding;
        var runtime = new RecordingRuntime(binding.ProfilePath);
        var coordinator = new ServiceStartupCoordinator(
            new MachineProfileBootstrapper(new BatteryConfigurationStore(bootstrapBinding)),
            new PersistentOverrideStore(binding), marker, runtime, new FixedPowerStateProvider(), new RecordingLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.InitializeAsync(CancellationToken.None));
        Assert.Empty(runtime.Calls);
        Assert.False(File.Exists(binding.ProfilePath));
        Assert.False(File.Exists(binding.RunMarkerPath));
        Assert.Equal(MachineConfigurationWriterTests.LegacyOverride, await File.ReadAllTextAsync(binding.OverridePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_ExplicitTransitionWritesAfterProofAndReleasesBeforeRuntime(bool failAfterAdmission)
    {
        var writer = new ScratchConfigurationWriter(directory);
        int acquisitions = 0;
        IDisposable Acquire(string root, CancellationToken token)
        {
            acquisitions++;
            return writer.Acquire(root, token);
        }

        var binding = new MachineConfigurationWriterBinding(directory, Acquire);
        await File.WriteAllTextAsync(binding.OverridePath, MachineConfigurationWriterTests.LegacyOverride);
        Guid outgoing = Guid.NewGuid();
        string receiptPath = Path.Combine(directory, "retirement-receipt");
        await File.WriteAllTextAsync(receiptPath, outgoing.ToString("N"));
        bool verified = false;
        var marker = new ServiceRunMarkerStore(binding.RunMarkerPath, () => DateTimeOffset.UtcNow,
            () => TimeSpan.FromHours(1), Acquire, () =>
            {
                Assert.False(File.Exists(binding.ProfilePath));
                Assert.False(File.Exists(binding.PreferencesPath));
                Assert.False(File.Exists(binding.MarkerPath));
                Assert.False(File.Exists(binding.RunMarkerPath));
                Assert.Equal(MachineConfigurationWriterTests.LegacyOverride, File.ReadAllText(binding.OverridePath));
                Assert.Equal(32, Assert.Throws<System.ComponentModel.Win32Exception>(() => writer.Acquire()).NativeErrorCode);
                Assert.Equal(outgoing.ToString("N"), File.ReadAllText(receiptPath));
                verified = true;
            });
        var battery = new BatteryConfigurationStore(binding, transactionBoundary: (_, _) =>
        {
            Assert.True(verified);
            Assert.Equal(1, acquisitions);
            return Task.CompletedTask;
        });
        var failure = new NativeComponentException((int)NativeBoundaryErrorKind.InvalidNativeResult);
        var runtime = new RecordingRuntime(binding.ProfilePath)
        {
            ReconcileFailure = failAfterAdmission ? failure : null,
            Initialized = () =>
            {
                Assert.True(verified);
                Assert.Equal(1, acquisitions);
                using IDisposable lease = writer.Acquire();
                Assert.True(File.Exists(binding.RunMarkerPath));
                Assert.True(File.Exists(binding.MarkerPath));
                Assert.NotEqual(MachineConfigurationWriterTests.LegacyOverride, File.ReadAllText(binding.OverridePath));
            },
        };
        var logger = new RecordingLogger();
        var coordinator = new ServiceStartupCoordinator(new MachineProfileBootstrapper(battery),
            new PersistentOverrideStore(binding), marker, runtime, new FixedPowerStateProvider(), logger);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (failAfterAdmission)
        {
            Assert.Same(failure, await Assert.ThrowsAsync<NativeComponentException>(
                () => coordinator.InitializeAsync(deadline.Token)));
            byte[] interruptedMarker = await File.ReadAllBytesAsync(binding.RunMarkerPath);
            Assert.False(await Worker.PrepareHardwareAndShouldClearRunMarkerAsync(
                coordinator.RuntimeInitialized, intentionalStop: false,
                _ => Task.FromResult(new ServiceStopCleanupResult(
                    new ControlOperationResult(ControlOperationStatus.Applied, "Fake cleanup completed."),
                    ControlOperationStatus.Applied)),
                logger, deadline.Token));
            Assert.Equal(interruptedMarker, await File.ReadAllBytesAsync(binding.RunMarkerPath));

            // An ordinary same-boot restart still classifies the surviving marker
            // and runs the existing owned-state recovery sequence.
            var recovery = new RecordingRuntime(binding.ProfilePath);
            var recoveryMarker = new ServiceRunMarkerStore(binding.RunMarkerPath, () => DateTimeOffset.UtcNow,
                () => TimeSpan.FromHours(1), writer.Acquire);
            var recoveryCoordinator = new ServiceStartupCoordinator(new MachineProfileBootstrapper(
                new BatteryConfigurationStore(binding)), new PersistentOverrideStore(binding),
                recoveryMarker, recovery, new FixedPowerStateProvider(), logger);
            Assert.True(await recoveryCoordinator.InitializeAsync(deadline.Token));
            Assert.True(recovery.RecoveringUnexpectedExit);
            Assert.Contains("ReconcileUnexpectedExit", recovery.Calls);
        }
        else
        {
            Assert.True(await coordinator.InitializeAsync(deadline.Token));
        }

        Assert.True(coordinator.RuntimeInitialized);
        Assert.False(runtime.RecoveringUnexpectedExit);
        Assert.Equal(PerformancePreset.Performance, runtime.PersistentOverrides?.Performance);
        Assert.Equal(outgoing.ToString("N"), await File.ReadAllTextAsync(receiptPath));
    }

    [Fact]
    public async Task InitializeAsync_ExplicitTransitionRetainsFailedRollbackEvidence()
    {
        var writer = new ScratchConfigurationWriter(directory);
        var binding = new MachineConfigurationWriterBinding(directory, writer.Acquire);
        await File.WriteAllTextAsync(binding.ProfilePath, BatteryConfigurationStoreTests.LegacyJson);
        await File.WriteAllTextAsync(binding.OverridePath, MachineConfigurationWriterTests.LegacyOverride);
        Guid outgoing = Guid.NewGuid();
        string receiptPath = Path.Combine(directory, "retirement-receipt");
        await File.WriteAllTextAsync(receiptPath, outgoing.ToString("N"));
        var marker = new ServiceRunMarkerStore(binding.RunMarkerPath, () => DateTimeOffset.UtcNow,
            () => TimeSpan.FromHours(1), writer.Acquire,
            () => Assert.Equal(outgoing.ToString("N"), File.ReadAllText(receiptPath)));
        FileStream? denyRollback = null;
        var battery = new BatteryConfigurationStore(binding, transactionBoundary: (boundary, _) =>
        {
            if (boundary == BatteryConfigurationCommitBoundary.AfterProfileReplacement)
            {
                denyRollback = File.Open(binding.ProfilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                throw new IOException("Injected failure after profile replacement.");
            }
            return Task.CompletedTask;
        });
        var runtime = new RecordingRuntime(binding.ProfilePath);
        var coordinator = new ServiceStartupCoordinator(new MachineProfileBootstrapper(battery),
            new PersistentOverrideStore(binding), marker, runtime, new FixedPowerStateProvider(), new RecordingLogger());

        try
        {
            IOException failure = await Assert.ThrowsAsync<IOException>(
                () => coordinator.InitializeAsync(CancellationToken.None));
            Assert.Contains("commit and rollback both failed", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            denyRollback?.Dispose();
        }

        Assert.False(coordinator.RuntimeInitialized);
        Assert.Empty(runtime.Calls);
        Assert.True(File.Exists(binding.MarkerPath + ".recovery-required"));
        Assert.False(File.Exists(binding.RunMarkerPath));
        Assert.Equal(MachineConfigurationWriterTests.LegacyOverride, await File.ReadAllTextAsync(binding.OverridePath));
        var beforeRetry = Directory.GetFiles(directory).ToDictionary(file => file, File.ReadAllBytes);

        Assert.False(await coordinator.InitializeAsync(CancellationToken.None));
        Assert.Equal(beforeRetry.Keys.Order(), Directory.GetFiles(directory).Order());
        foreach ((string file, byte[] bytes) in beforeRetry)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
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
        public bool? RecoveringManagedRun { get; private set; }
        public bool NativeHandbackRecoveryPending { get; init; }
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

        public void Initialize(
            BatteryConfiguration configuration,
            PowerSource powerSource,
            PersistentOverrideState persistentOverrides,
            bool recoveringUnexpectedExit,
            bool recoveringManagedRun)
        {
            RecoveringManagedRun = recoveringManagedRun;
            Initialize(configuration, powerSource, persistentOverrides, recoveringUnexpectedExit);
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
