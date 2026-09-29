using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade.CpuTuning.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("inactive")]
    [InlineData("low-power")]
    [InlineData("unverified")]
    [InlineData("unsupported")]
    [InlineData("no-recent-device")]
    public async Task ObservationAdmissionFailureSkipsCpuSourceAndPublication(string failure)
    {
        TestContext context = await CreateContextAsync(supported: failure != "unsupported",
            verified: failure != "unverified");
        if (failure == "low-power") { context.Gate.Enter(); }
        if (failure == "no-recent-device") { context.Provider.Available = false; }
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => failure != "inactive", CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionOrLowPowerChangeAfterObservationPreventsPublication(bool suspend)
    {
        TestContext context = await CreateContextAsync();
        bool active = true;
        context.Source.AfterInspection = () =>
        {
            if (suspend) { context.Gate.Enter(); }
            else { active = false; }
        };
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int publications = context.Hub.Published.Count;

        ControlOperationResult result = await context.Runtime.ObserveCpuTuningAsync(true,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(3, context.Source.Calls);
        Assert.Equal(before, context.Runtime.GetStatus());
        Assert.Equal(publications, context.Hub.Published.Count);
    }

    [Fact]
    public async Task PassiveObservationPublishesStatusWithoutAdmittingWritesOrRestoration()
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult observation = await context.Runtime.ObserveCpuTuningAsync(true,
            () => true, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, observation.Status);
        Assert.Equal(observation.CpuTuning, context.Runtime.GetStatus().CpuTuning);
        CpuTuningStatus status = Assert.IsType<CpuTuningStatus>(observation.CpuTuning);
        Assert.Null(status.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Unknown, status.PendingBoot);
        Assert.False(status.RestorePending);
        Assert.False(status.Firmware.CanWrite);
        Assert.False(status.Live.CanWrite);
        int calls = context.Source.Calls;

        foreach (CpuTuningTarget target in Enum.GetValues<CpuTuningTarget>())
        {
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.SetCpuTuningAsync(target, new(65, new(false, -17)),
                    () => true, CancellationToken.None)).Status);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await context.Runtime.RestoreCpuTuningAsync(target,
                    () => true, CancellationToken.None)).Status);
        }
        Assert.Equal(calls, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
    }

    [Theory]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, true)]
    [InlineData(CpuTuningTarget.FirmwareNextBoot, false)]
    [InlineData(CpuTuningTarget.LiveNow, true)]
    [InlineData(CpuTuningTarget.LiveNow, false)]
    public async Task SetAndRestoreRemainBlockedWithoutCallingCpuSource(CpuTuningTarget target, bool active)
    {
        TestContext context = await CreateContextAsync();

        ControlOperationResult set = await context.Runtime.SetCpuTuningAsync(target,
            new(65, new(true, -1)), () => active, CancellationToken.None);
        ControlOperationResult restore = await context.Runtime.RestoreCpuTuningAsync(target,
            () => active, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, set.Status);
        Assert.Equal(ControlOperationStatus.Blocked, restore.Status);
        Assert.Equal(0, context.Source.Calls);
        Assert.Empty(context.Device.BatterySetterCalls);
        Assert.Null(context.Runtime.GetStatus().CpuTuning);
    }

    [Fact]
    public async Task AdmittedFakeSavedWorkflowPublishesOwnershipAndRestoresOriginalBytes()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult saved = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -10)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, saved.Status);
        Assert.True(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(CpuTuningPendingBoot.Requested, saved.CpuTuning!.PendingBoot);
        Assert.Null(saved.CpuTuning.LiveState);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(context.Runtime.GetStatus().CpuTuning!.RestorePending);
        Assert.Equal(baseline, context.Source.Bytes);
        Assert.Equal(2, context.Source.Submissions);
    }

    [Fact]
    public async Task CurveOnlyRecoveryIgnoresUnownedObservedOnlyPowerLimitAndCannotRestoreLive()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true, powerAdmitted: false);
        byte[] baseline = (byte[])context.Source.Bytes!.Clone();
        ControlOperationResult applied = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(true, -12)), () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(new(CpuTuningRecoveryState.RestoreReady, CpuTuningFeatures.CurveOptimizer), applied.CpuTuning!.FirmwareRecovery);
        Assert.Equal(ControlOperationStatus.Blocked, (await context.Runtime.RestoreCpuTuningAsync(
            CpuTuningTarget.LiveNow, () => true, CancellationToken.None)).Status);
        Assert.Equal(1, context.Source.Submissions);
        ControlOperationResult restored = await context.Runtime.RestoreCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(2, context.Source.Submissions);
        Assert.Equal(baseline, context.Source.Bytes);
    }

    [Fact]
    public async Task SessionLossAfterInitialGateStopsFakeSavedSubmission()
    {
        TestContext context = await CreateContextAsync(admittedSavedBackend: true);
        bool active = true;
        context.Source.BeforeIdentity = () => active = false;
        ControlOperationResult result = await context.Runtime.SetCpuTuningAsync(CpuTuningTarget.FirmwareNextBoot,
            new(null, new(false, -1)), () => active, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Source.Submissions);
        Assert.False(result.CpuTuning!.RestorePending);
    }

    private async Task<TestContext> CreateContextAsync(bool supported = true, bool verified = true,
        bool admittedSavedBackend = false, bool powerAdmitted = true)
    {
        var store = new BatteryConfigurationStore(Path.Combine(directory, "profiles.json"),
            Path.Combine(directory, "preferences.json"), Path.Combine(directory, "commit.json"));
        BatteryConfiguration configuration = await store.LoadAsync(CancellationToken.None);
        var device = new BatteryTestDevice();
        device.Capabilities = device.Capabilities with
        {
            CpuPowerLimit = supported ? new(new(45, 95, 1), admittedSavedBackend && powerAdmitted
                ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly) : null,
            CpuVoltageOptimizer = admittedSavedBackend ? new(new(-30, -1, 1), DeviceFeatureAccess.ReadWrite) : null,
        };
        var provider = new FakeProvider(device) { Available = verified };
        var source = new FakeObservationSource();
        if (admittedSavedBackend) { source.Bytes = CpuSavedTuningTransactionTests.Baseline(); }
        var gate = new LowPowerGate();
        var hub = new FakeHub();
        var runtime = new ServiceControlRuntime(store,
            new PersistentOverrideStore(Path.Combine(directory, "overrides.json")),
            new FakePowerState { Power = admittedSavedBackend ? PowerSource.PluggedIn : PowerSource.Battery },
            hub, provider, new DiagnosticHistory(), gate,
            new FakePresenceMonitor(), new FakeFirmwareSource(),
            static (_, _) => Task.CompletedTask, ProfileSetValidator.CreateUnverifiedSafeDefaults(),
            cpuTuningController: admittedSavedBackend
                ? new CpuTuningController(source, new CpuTuningOwnershipJournalStore(Path.Combine(directory, "cpu.json")),
                    safety => new FakeSavedBackend(source, safety))
                : new CpuTuningController(source));
        runtime.Initialize(configuration, admittedSavedBackend ? PowerSource.PluggedIn : PowerSource.Battery);
        await runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(verified, runtime.GetStatus().DeviceVerified);
        return new(runtime, device, provider, source, gate, hub);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
    }

    private sealed record TestContext(ServiceControlRuntime Runtime, BatteryTestDevice Device,
        FakeProvider Provider, FakeObservationSource Source, LowPowerGate Gate, FakeHub Hub);

    private sealed class FakeObservationSource : ICpuTuningObservationSource
    {
        public int Calls { get; private set; }
        public Action? AfterInspection { get; set; }
        internal Action? BeforeIdentity;
        internal byte[]? Bytes;
        internal int Submissions;
        public DeviceIdentity? ReadIdentity()
        {
            Calls++;
            BeforeIdentity?.Invoke();
            return new("RZ09-0528", 0x1532, 0x02C6, "2.02");
        }
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity)
        {
            Calls++;
            if (Bytes is not null)
            {
                return new(SavedCpuFirmwareReadStatus.Observed, Rz090528CpuFirmwareCodec.DecodeSaved(Bytes),
                    Rz090528CpuFirmwareCodec.DecodeSaved(Bytes), 0, 0);
            }
            return new(SavedCpuFirmwareReadStatus.Unavailable, null, null, 5, 5);
        }
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend)
        {
            Calls++;
            AfterInspection?.Invoke();
            return new(DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
        }
    }

    private sealed class FakeProvider(IBladeDevice device) : IVerifiedBladeDeviceProvider
    {
        public bool Available { get; set; } = true;
        public FirmwareCompatibilityStatus FirmwareCompatibility => FirmwareCompatibilityStatus.Tested;
        public bool RequiresUntestedFirmwareOverride => false;
        public void SetAllowUntestedFirmware(bool enabled) { }
        public bool TryGetVerifiedDevice(out IBladeDevice result)
        {
            result = device;
            return Available;
        }
    }

    private sealed class FakePowerState : IPowerStateProvider
    {
        internal PowerSource Power = PowerSource.Battery;
        public PowerSource CapturePowerSource() => Power;
        public int? CaptureBatteryPowerFlowMilliwatts() => null;
    }

    private sealed class FakeSavedBackend(FakeObservationSource source,
        Func<DeviceIdentity, CpuSavedTuningCommand, bool> safety) : ICpuSavedTuningBackend
    {
        public Task<bool> CheckWriteAdmissionAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token) =>
            Task.FromResult(safety(identity, command));
        public Task<SavedCpuFirmwareReadResult> ReadAsync(DeviceIdentity identity, CancellationToken token) =>
            Task.FromResult(source.ReadSaved(identity));
        public Task<CpuSavedTuningSubmission> SendAsync(DeviceIdentity identity, CpuSavedTuningCommand command, CancellationToken token)
        {
            if (!safety(identity, command)) { return Task.FromResult(CpuSavedTuningSubmission.NotSubmitted); }
            source.Bytes = CpuSavedTuningPlan.Simulate(source.Bytes!, command);
            source.Submissions++;
            return Task.FromResult(CpuSavedTuningSubmission.Acknowledged);
        }
    }

    private sealed class FakePresenceMonitor : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Observe() => new([], null);
    }

    private sealed class FakeFirmwareSource : IFirmwareVersionSource
    {
        public FirmwareVersions Capture() => FirmwareVersions.Unavailable();
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        public List<ServiceStatusSnapshot> Published { get; } = [];
        public void Publish(ServiceStatusSnapshot snapshot) => Published.Add(snapshot);
        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
