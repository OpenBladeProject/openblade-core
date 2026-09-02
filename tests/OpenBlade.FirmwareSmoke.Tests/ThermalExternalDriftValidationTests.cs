using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class ThermalExternalDriftValidationTests
{
    private static readonly DateTimeOffset Start = new(
        2026,
        9,
        1,
        0,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_ConfirmsBackgroundConvergenceWithoutObserveOrReplay()
    {
        ThermalExternalDriftValidationBuildManifest manifest = ValidManifest();
        ServiceStatusSnapshot baseline = Status(PerformancePreset.Balanced);
        ServiceStatusSnapshot drifted = Status(
            PerformancePreset.Silent,
            external: true,
            convergenceState: ThermalConvergenceState.External,
            convergenceReason: ThermalConvergenceReason.None);
        ServiceStatusSnapshot restored = Status(PerformancePreset.Balanced);
        var client = new FakeClient(baseline)
        {
            OnValidate = fake => fake.Enqueue(drifted, restored),
        };
        var clock = new ManualTimeProvider(Start);
        HyperBoostLifecycleServiceProcess process = StableProcess();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics =
            SuccessfulDiagnostics(manifest.ValidationRunId);

        ThermalExternalDriftValidationResult result = await Runner(
            client,
            clock,
            () => process,
            () => diagnostics).RunAsync(manifest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.InjectionAttempted);
        Assert.True(result.InjectionConfirmed);
        Assert.True(result.SameServiceProcess);
        Assert.True(result.MaintainAttemptObserved);
        Assert.True(result.ConvergenceConfirmed);
        Assert.True(result.NoReplayObserved);
        Assert.False(result.RecoveryAttempted);
        Assert.True(result.FinalRestorationConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(PerformancePreset.Balanced, result.BaselinePerformance);
        Assert.Equal(PerformancePreset.Silent, result.InjectedPerformance);
        Assert.Equal(PerformancePreset.Balanced, result.FinalPerformance);
        Assert.Equal(FanMode.Automatic, result.FinalFans);
        Assert.Equal(1, result.MaintainAttemptCount);
        Assert.Equal(1, result.SyntheticWriteAttemptCount);
        Assert.Equal("OpenBlade:verified-device", result.DeviceFingerprint);
        Assert.Equal(
            Guid.Parse("EFB30F48-9FC8-43C4-B9A2-54D808CEBA5B"),
            result.MaintainTransitionAttemptId);
        Assert.Equal(1, client.ValidationCalls);
        Assert.Equal(0, client.ObserveCalls);
        Assert.Equal(0, client.ReapplyCalls);
        Assert.Equal(2, client.DiagnosticsCalls);
        Assert.True(client.GetStatusCalls > 2);
    }

    [Fact]
    public async Task RunAsync_WaitsForPublishedDriftBeforeAcceptingRestoredBaseline()
    {
        ThermalExternalDriftValidationBuildManifest manifest = ValidManifest();
        ServiceStatusSnapshot baseline = Status(PerformancePreset.Balanced);
        ServiceStatusSnapshot drifted = Status(
            PerformancePreset.Silent,
            external: true,
            convergenceState: ThermalConvergenceState.External,
            convergenceReason: ThermalConvergenceReason.None);
        ServiceStatusSnapshot restored = Status(PerformancePreset.Balanced);
        // The first poll after injection still publishes the pre-injection
        // state; the Service ledger only shows the Maintain attempt once the
        // drifted and restored snapshots have been consumed.
        var client = new FakeClient(baseline)
        {
            OnValidate = fake => fake.Enqueue(baseline, drifted, restored),
        };
        var clock = new ManualTimeProvider(Start);
        HyperBoostLifecycleServiceProcess process = StableProcess();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> ledger =
            SuccessfulDiagnostics(manifest.ValidationRunId);
        ThermalExternalDriftValidationResult result = await Runner(
            client,
            clock,
            () => process,
            () => client.QueuedCount == 0 ? ledger : [InjectionDiagnostic(manifest.ValidationRunId)])
            .RunAsync(manifest, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ConvergenceConfirmed);
        Assert.True(result.MaintainAttemptObserved);
        Assert.True(result.NoReplayObserved);
        Assert.Equal(0, client.QueuedCount);
        Assert.Equal(PerformancePreset.Balanced, result.FinalPerformance);
    }

    [Fact]
    public async Task RunAsync_RejectsServiceProcessChangeAsStartupReconciliation()
    {
        ThermalExternalDriftValidationBuildManifest manifest = ValidManifest();
        ServiceStatusSnapshot baseline = Status(PerformancePreset.Balanced);
        var client = new FakeClient(baseline);
        var clock = new ManualTimeProvider(Start);
        HyperBoostLifecycleServiceProcess original = StableProcess();
        HyperBoostLifecycleServiceProcess replacement = original with
        {
            StartUtcTicks = original.StartUtcTicks + 1,
        };
        int processReads = 0;

        ThermalExternalDriftValidationResult result = await Runner(
            client,
            clock,
            () => ++processReads == 1 ? original : replacement,
            static () => []).RunAsync(manifest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InjectionAttempted);
        Assert.False(result.SameServiceProcess);
        Assert.False(result.ConvergenceConfirmed);
        Assert.False(result.RecoveryAttempted);
        Assert.Contains(
            "startup reconciliation",
            result.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, client.ValidationCalls);
        Assert.Equal(0, client.ObserveCalls);
        Assert.Equal(0, client.ReapplyCalls);
    }

    [Fact]
    public async Task RunAsync_TimeoutUsesOrdinaryReapplyOnlyForSafeConfirmedDrift()
    {
        ThermalExternalDriftValidationBuildManifest manifest = ValidManifest();
        ServiceStatusSnapshot baseline = Status(PerformancePreset.Balanced);
        ServiceStatusSnapshot drifted = Status(
            PerformancePreset.Silent,
            external: true,
            convergenceState: ThermalConvergenceState.External,
            convergenceReason: ThermalConvergenceReason.None);
        var client = new FakeClient(baseline)
        {
            OnValidate = fake => fake.Enqueue(drifted),
            OnReapply = fake => fake.SetCurrent(baseline),
        };
        var clock = new ManualTimeProvider(Start);
        HyperBoostLifecycleServiceProcess process = StableProcess();

        ThermalExternalDriftValidationResult result = await Runner(
            client,
            clock,
            () => process,
            () =>
            [
                InjectionDiagnostic(manifest.ValidationRunId),
            ]).RunAsync(manifest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.InjectionConfirmed);
        Assert.False(result.ConvergenceConfirmed);
        Assert.True(result.RecoveryAttempted);
        Assert.True(result.FinalRestorationConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(1, client.ReapplyCalls);
        Assert.Equal(0, client.ObserveCalls);
        Assert.Contains("ordinary explicit Reapply", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnconfirmedDriftNeverReappliesAndRequiresManualRecovery()
    {
        ThermalExternalDriftValidationBuildManifest manifest = ValidManifest();
        ServiceStatusSnapshot baseline = Status(PerformancePreset.Balanced);
        ServiceStatusSnapshot ambiguous = Status(
            PerformancePreset.Silent,
            external: true,
            unconfirmed: true,
            convergenceState: ThermalConvergenceState.AwaitingObservation,
            convergenceReason: ThermalConvergenceReason.PendingReconciliation);
        var client = new FakeClient(baseline)
        {
            OnValidate = fake => fake.Enqueue(ambiguous),
        };
        var clock = new ManualTimeProvider(Start);
        HyperBoostLifecycleServiceProcess process = StableProcess();

        ThermalExternalDriftValidationResult result = await Runner(
            client,
            clock,
            () => process,
            () =>
            [
                InjectionDiagnostic(manifest.ValidationRunId),
            ]).RunAsync(manifest, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ConvergenceConfirmed);
        Assert.False(result.RecoveryAttempted);
        Assert.False(result.FinalRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(0, client.ReapplyCalls);
        Assert.Equal(0, client.ObserveCalls);
        Assert.Contains("Do not issue another raw write", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectsMalformedManifests()
    {
        ThermalExternalDriftValidationBuildManifest[] malformedManifests =
        {
            ValidManifest() with { SchemaVersion = 2 },
            ValidManifest() with { ValidationRunId = Guid.Empty },
            ValidManifest() with { SourceRevision = new string('A', 39) },
            ValidManifest() with { SourceRevision = new string('Z', 40) },
            ValidManifest() with { LocalDevVersion = "0.20.0" },
            ValidManifest() with { LocalDevVersion = "invalid" },
            ValidManifest() with { InstallerSha256 = new string('A', 63) },
            ValidManifest() with { FirmwareSmokeSha256 = new string('A', 65) },
            ValidManifest() with { ServiceAssemblySha256 = new string('A', 63) },
            ValidManifest() with { ProtocolAssemblySha256 = new string('A', 65) },
            ValidManifest() with { ProductionFixtureSha256 = new string('Z', 64) },
        };

        foreach (ThermalExternalDriftValidationBuildManifest manifest in
            malformedManifests)
        {
            var client = new FakeClient(Status(PerformancePreset.Balanced));
            var clock = new ManualTimeProvider(Start);
            var runner = Runner(
                client,
                clock,
                StableProcess,
                static () => []);

            InvalidDataException error =
                await Assert.ThrowsAsync<InvalidDataException>(
                    () => runner.RunAsync(manifest, CancellationToken.None));

            Assert.Contains(
                "manifest is malformed",
                error.Message,
                StringComparison.Ordinal);
            Assert.Equal(0, client.GetStatusCalls);
            Assert.Equal(0, client.ValidationCalls);
        }
    }

    [Fact]
    public void Manifest_ConfirmsExactFirmwareSmokeBytes()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            string hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    File.ReadAllBytes(path)));
            ThermalExternalDriftValidationBuildManifest manifest =
                ValidManifest() with { FirmwareSmokeSha256 = hash };

            Assert.True(manifest.MatchesFirmwareSmoke(path));
            Assert.False(
                (manifest with { FirmwareSmokeSha256 = new string('A', 64) })
                    .MatchesFirmwareSmoke(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ThermalExternalDriftValidationRunner Runner(
        FakeClient client,
        ManualTimeProvider clock,
        Func<HyperBoostLifecycleServiceProcess?> process,
        Func<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>> diagnostics) =>
        new(
            client,
            client.GetStatusAsync,
            process,
            _ => client.ReadDiagnosticsAsync(diagnostics()),
            clock.DelayAsync,
            clock);

    private static ThermalExternalDriftValidationBuildManifest ValidManifest() => new(
        SchemaVersion: 1,
        ValidationRunId: Guid.Parse("5B409034-47C6-41BA-BA6F-0046570C1A5C"),
        SourceRevision: new string('A', 40),
        LocalDevVersion: "65535.1.2.3",
        InstallerSha256: new string('E', 64),
        FirmwareSmokeSha256: new string('F', 64),
        ServiceAssemblySha256: new string('B', 64),
        ProtocolAssemblySha256: new string('C', 64),
        ProductionFixtureSha256: new string('D', 64));

    private static HyperBoostLifecycleServiceProcess StableProcess() => new(
        ProcessId: 1234,
        StartUtcTicks: Start.UtcTicks);

    private static IReadOnlyList<HyperBoostLifecycleDiagnosticResult>
        SuccessfulDiagnostics(Guid validationRunId) =>
    [
        InjectionDiagnostic(validationRunId),
        new(
            Start.AddSeconds(2),
            "ThermalConvergence",
            "Reconcile",
            "Applied",
            "Maintain thermal convergence attempt efb30f48-9fc8-43c4-b9a2-54d808ceba5b: components=Performance; stage=Confirmed."),
    ];

    private static HyperBoostLifecycleDiagnosticResult InjectionDiagnostic(
        Guid validationRunId) => new(
        Start.AddSeconds(1),
        "ThermalExternalDriftValidation",
        "Inject",
        "Applied",
        "Thermal validation confirmed one Service-owned synthetic performance mutation; deviceFingerprint=OpenBlade:verified-device; desired intent unchanged.",
        validationRunId);

    private static ServiceStatusSnapshot Status(
        PerformancePreset confirmedPerformance,
        bool external = false,
        bool unconfirmed = false,
        ThermalConvergenceState convergenceState = ThermalConvergenceState.Matched,
        ThermalConvergenceReason convergenceReason = ThermalConvergenceReason.None)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: external ? [DeviceSubsystem.Performance] : [],
            Message: "Verified",
            Capabilities: DeviceCapabilities.SafeTargetDefaults(),
            ConfirmedState: new ConfirmedDeviceState(
                confirmedPerformance,
                FanMode.Automatic,
                new ConfirmedBatteryState(true, 80, false),
                Lighting: null),
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    FanMode.Automatic,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    LightingSettingsFactory.SpectrumCycling(50),
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(BatteryPolicy.Create(80), true, false))
            {
                FanControl = FanControlSettings.Default,
            },
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: new FirmwareVersions(
                "4.00",
                "1.00",
                "1.00",
                "1.00",
                "RZ09-0581"),
            ServiceVersion: "test")
        {
            UnconfirmedWrites = unconfirmed ? [DeviceSubsystem.Performance] : [],
            ThermalConvergence = new ThermalConvergenceStatus(
                ThermalControlPolicy.MaintainDesiredState,
                ThermalPolicyUpgradeDisclosureState.Acknowledged,
                convergenceState,
                external ? [ThermalComponent.Performance] : [],
                convergenceReason,
                ExplicitReapplyRequired: false),
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => current;

        public override long GetTimestamp() => timestamp;

        internal Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current += duration;
            timestamp += duration.Ticks;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClient(ServiceStatusSnapshot initial) : IFirmwareSmokeClient
    {
        private readonly Queue<ServiceStatusSnapshot> queuedStatuses = [];
        private ServiceStatusSnapshot current = initial;

        internal Action<FakeClient>? OnValidate { get; init; }

        internal Action<FakeClient>? OnReapply { get; init; }

        internal int GetStatusCalls { get; private set; }

        internal int ObserveCalls { get; private set; }

        internal int DiagnosticsCalls { get; private set; }

        internal int ValidationCalls { get; private set; }

        internal int ReapplyCalls { get; private set; }
        internal int QueuedCount => queuedStatuses.Count;

        internal void Enqueue(params ServiceStatusSnapshot[] statuses)
        {
            foreach (ServiceStatusSnapshot status in statuses)
            {
                queuedStatuses.Enqueue(status);
            }
        }

        internal void SetCurrent(ServiceStatusSnapshot status) => current = status;

        internal Task<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>
            ReadDiagnosticsAsync(
            IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics)
        {
            DiagnosticsCalls++;
            return Task.FromResult(diagnostics);
        }

        public Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetStatusCalls++;
            if (queuedStatuses.TryDequeue(out ServiceStatusSnapshot? queued))
            {
                current = queued;
            }
            return Task.FromResult(current);
        }

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors sensors,
            CancellationToken cancellationToken)
        {
            ObserveCalls++;
            return Task.FromResult(current);
        }

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken)
        {
            DiagnosticsCalls++;
            throw new InvalidOperationException(
                "The injected diagnostic reader must be used by these tests.");
        }

        public Task<ControlOperationResult> ValidateThermalExternalDriftAsync(
            Guid validationRunId,
            string expectedSourceRevision,
            string expectedServiceAssemblySha256,
            string expectedProtocolAssemblySha256,
            string expectedProductionFixtureSha256,
            CancellationToken cancellationToken)
        {
            ValidationCalls++;
            OnValidate?.Invoke(this);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

        public Task<ControlOperationResult> ReapplyChangedSettingsAsync(
            CancellationToken cancellationToken)
        {
            ReapplyCalls++;
            OnReapply?.Invoke(this);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            PerformanceOverride? value,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public Task<ControlOperationResult> SetHyperBoostAsync(
            bool enabled,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            FanControlSettings? settings,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public Task<ControlOperationResult> ApplyLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => ThrowUnsupported();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<ControlOperationResult> ThrowUnsupported() =>
            throw new InvalidOperationException("Unexpected mutation in validation test.");
    }
}
