using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.Protocol;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class GuardianFanStopBackendTests
{
    private static readonly FanControlSettings Normal = FanControlSettings.FromLegacy(FanMode.Maximum);

    [Fact]
    public async Task AutonomousAutoBeforeFirstPollPublishesHistoryAndReleasesWithoutAnotherWrite()
    {
        var client = new Client();
        client.Start = (_, _) => { client.Complete(); return Task.FromResult(FanStopSubmission.Submitted); };
        var lease = new Lease();
        var controller = Controller(client, lease);
        Assert.Equal(ControlOperationStatus.Applied, (await Stop(controller)).Status);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.Equal(1, controller.ConfirmedStopGeneration);
        Assert.Equal(0, client.AutoCalls);
        Assert.Equal(1, lease.Disposals);
        Assert.False(controller.BlocksOrdinaryHardware);
    }

    [Fact]
    public async Task ProvenAsyncRefusalReleasesWithoutClaimingAutoOrHistoricalStop()
    {
        var client = new Client();
        client.Start = (_, _) =>
        {
            client.Set(client.Snapshot with
            {
                Revision = 2,
                Phase = FanStopGuardianPhase.Refused,
                ConfirmedState = FanStopObservedState.Ordinary,
                StopConfirmed = false,
                NativeReleased = true,
                OwnerExited = true,
                KnownNotSubmitted = true,
                Reason = "Thermal entry refused before command dispatch.",
            });
            return Task.FromResult(FanStopSubmission.Submitted);
        };
        var lease = new Lease();
        var controller = Controller(client, lease);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        Assert.Equal(FanStopState.Inactive, controller.GetStatus(true).State);
        Assert.Equal(0, controller.ConfirmedStopGeneration);
        Assert.Equal(0, client.AutoCalls);
        Assert.Equal(1, lease.Disposals);
        Assert.False(controller.BlocksOrdinaryHardware);
    }

    [Fact]
    public async Task WrongOperationFencesWithoutAutoOrReleasingHardware()
    {
        var client = new Client();
        var lease = new Lease();
        var controller = Controller(client, lease);
        await Stop(controller);
        client.Set(client.Snapshot with { OperationId = Guid.NewGuid(), Revision = 2 });
        await controller.ReconcileAsync(false, () => true, default);
        Assert.True(controller.RequiresRecovery);
        Assert.True(controller.BlocksOrdinaryHardware);
        Assert.Equal(0, lease.Disposals);
        Assert.Equal(0, client.AutoCalls);
    }

    [Fact]
    public async Task SameRevisionCannotChangeEvidence()
    {
        var client = new Client();
        var controller = Controller(client, new());
        await Stop(controller);
        client.Set(client.Snapshot with { Reason = "Changed without revision" });
        await controller.ReconcileAsync(false, () => true, default);
        Assert.True(controller.RequiresRecovery);
    }

    [Fact]
    public async Task AutoObservationWithoutReleaseKeepsFenceAndRefusesNewStart()
    {
        var client = new Client();
        var lease = new Lease();
        var controller = Controller(client, lease);
        await Stop(controller);
        client.Complete(nativeReleased: false);
        await controller.ReconcileAsync(false, () => true, default);
        Assert.Equal(FanStopState.ReturningToAutomatic, controller.GetStatus(true).State);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        Assert.Equal(1, client.StartCalls);
        Assert.Equal(0, lease.Disposals);
        client.Complete();
        await controller.ReconcileAsync(false, () => true, default);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task CanceledResolvedDispatchUsesIndependentAutoAndPropagatesCancellation()
    {
        using var canceled = new CancellationTokenSource();
        var client = new Client();
        client.Start = (_, _) =>
        {
            canceled.Cancel();
            throw new OperationCanceledException(canceled.Token);
        };
        var lease = new Lease();
        var controller = Controller(client, lease);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stop(controller, canceled.Token));
        Assert.Equal(1, client.AutoCalls);
        Assert.False(client.AutoTokenWasCanceled);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.Equal(1, controller.ConfirmedStopGeneration);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task CanceledDrainDoesNotDispatchOrInventHardwareOwnership()
    {
        using var canceled = new CancellationTokenSource();
        var client = new Client();
        var backend = new GuardianFanStopBackend(client);
        backend.ConfigureHardwareDelegation(token => { canceled.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult<IDisposable>(new Lease()); });
        var controller = new FanStopController(backend);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stop(controller, canceled.Token));
        Assert.Equal(0, client.StartCalls);
        Assert.Equal(0, client.AutoCalls);
        Assert.False(controller.BlocksOrdinaryHardware);
        Assert.Equal(FanStopState.Inactive, controller.GetStatus(true).State);
    }

    [Fact]
    public async Task CallerAuthorityLossAfterDispatchCannotSkipMandatoryConfirmation()
    {
        bool caller = true;
        var client = new Client { Start = (_, _) => { caller = false; return Task.FromResult(FanStopSubmission.Submitted); } };
        var controller = Controller(client, new());
        await controller.SetModeAsync(FanStopMode.Stop, true, Normal, () => true, default,
            () => true, () => caller);
        Assert.True(controller.OwnsStop);
        Assert.Equal(1, controller.ConfirmedStopGeneration);
    }

    [Fact]
    public async Task SeparateSafetyAdmissionPreventsLeaseAndStart()
    {
        var client = new Client { Admission = Client.SafeAdmission with { TemperatureSafe = false } };
        var lease = new Lease();
        var controller = Controller(client, lease);
        Assert.Equal(ControlOperationStatus.Blocked, (await Stop(controller)).Status);
        Assert.Equal(0, client.StartCalls);
        Assert.Equal(0, lease.Acquisitions);
    }

    [Fact]
    public async Task AutonomousCompletionRacingAutoDispatchUsesTerminalProofWithoutFenceOrReplay()
    {
        var client = new Client { AutoSubmission = FanStopSubmission.NotSubmitted };
        var lease = new Lease();
        var controller = Controller(client, lease);
        await Stop(controller);
        ControlOperationResult? result = await controller.ReconcileAsync(true, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, result!.Status);
        Assert.Equal(FanStopState.Automatic, controller.GetStatus(true).State);
        Assert.False(controller.RequiresRecovery);
        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task SameRunAutoSignalIsIdempotentWhileWaitingForRelease()
    {
        var client = new Client { CompleteOnAuto = false };
        var backend = new GuardianFanStopBackend(client);
        backend.ConfigureHardwareDelegation(_ => Task.FromResult<IDisposable>(new Lease()));
        await backend.SubmitAsync(FanStopMode.Stop, default);
        await backend.SubmitAsync(FanStopMode.Automatic, default);
        await backend.SubmitAsync(FanStopMode.Automatic, default);
        Assert.Equal(1, client.AutoCalls);
        Assert.True(backend.BlocksOrdinaryHardware);
    }

    [Fact]
    public async Task ProviderDrainsExistingOperationAndRejectsQueuedAndNewHidDuringDelegation()
    {
        FakeQueryPort first = FakeQueryPort.Blocked(BatteryValidationSetting.Limit80);
        FakeQueryPort after = FakeQueryPort.Success(BatteryValidationSetting.Limit80);
        var factory = new FakeQueryFactory(first, after);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready), Adapt(factory), UnavailableFanRpmFactory());
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Task<ConfirmedDeviceState> active = device.ReadStateAsync(default);
        await first.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ConfirmedDeviceState> queued = device.ReadStateAsync(default);
        Task<IDisposable> acquire = provider.AcquireFanStopHardwareLeaseAsync(default);
        Assert.False(acquire.IsCompleted);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => device.ReadStateAsync(default));
        first.Release();
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => queued);
        IDisposable lease = await acquire.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.OpenCount);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => device.ReadStateAsync(default));
        lease.Dispose();
        await device.ReadStateAsync(default);
        Assert.Equal(2, factory.OpenCount);
    }

    private static FanStopController Controller(Client client, Lease lease)
    {
        var backend = new GuardianFanStopBackend(client);
        backend.ConfigureHardwareDelegation(_ => { lease.Acquisitions++; return Task.FromResult<IDisposable>(lease); });
        return new(backend);
    }
    private static Task<ControlOperationResult> Stop(FanStopController controller, CancellationToken token = default) =>
        controller.SetModeAsync(FanStopMode.Stop, true, Normal, () => true, token);

    internal sealed class Lease : IDisposable
    {
        public int Acquisitions;
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    internal sealed class Client : IFanStopGuardianClient
    {
        public static FanStopAdmission SafeAdmission => new(true, true, true, true, true, true, true, true, true, true)
        { ObservedPowerSource = PowerSource.UsbC };
        public FanStopAdmission Admission = SafeAdmission;
        public Func<Guid, CancellationToken, Task<FanStopSubmission>>? Start;
        public bool CompleteOnAuto = true;
        public FanStopSubmission AutoSubmission = FanStopSubmission.Submitted;
        public int StartCalls;
        public int AutoCalls;
        public bool AutoTokenWasCanceled;
        public FanStopUnavailableReason UnavailableReason => FanStopUnavailableReason.None;
        public FanStopStartupDisposition StartupDisposition { get; set; } = FanStopStartupDisposition.Clear;
        public FanStopGuardianSnapshot Snapshot { get; private set; } = new(Guid.Empty, 0, FanStopGuardianPhase.Idle);
        public event Action? SnapshotChanged;
        public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(Admission); }
        public Task<FanStopSubmission> StartAsync(Guid operationId, CancellationToken token)
        {
            StartCalls++;
            Snapshot = new(operationId, 1, FanStopGuardianPhase.Stopped, FanStopObservedState.Stopped, StopConfirmed: true);
            return Start?.Invoke(operationId, token) ?? Task.FromResult(FanStopSubmission.Submitted);
        }
        public Task<FanStopSubmission> RequestAutoAsync(Guid operationId, CancellationToken token)
        {
            Assert.Equal(Snapshot.OperationId, operationId);
            AutoCalls++;
            AutoTokenWasCanceled = token.IsCancellationRequested;
            if (CompleteOnAuto) { Complete(); }
            return Task.FromResult(AutoSubmission);
        }
        public void Complete(bool nativeReleased = true) => Snapshot = Snapshot with
        {
            Revision = Snapshot.Revision + 1,
            Phase = FanStopGuardianPhase.Completed,
            ConfirmedState = FanStopObservedState.Automatic,
            AutoConfirmed = true,
            CleanupConfirmed = true,
            JournalComplete = true,
            NativeReleased = nativeReleased,
            OwnerExited = true,
        };
        public void Set(FanStopGuardianSnapshot snapshot, bool notify = false)
        { Snapshot = snapshot; if (notify) { SnapshotChanged?.Invoke(); } }
        public void Notify() => SnapshotChanged?.Invoke();
    }
}
