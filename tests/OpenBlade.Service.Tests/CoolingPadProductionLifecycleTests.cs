using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadProductionLifecycleTests
{
    private static readonly CoolingPadControlSettings Fixed = new(CoolingPadControlMode.FixedDemand, new(2200));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledUnsafePauseOfParkedPortCannotBeOverwrittenByResumeOrRestore(bool restore)
    {
        var h = new Harness();
        await h.Start();
        await h.Runtime.StopAsync(default);
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runtime.PauseAsync(CoolingPadControlPauseReason.PowerTransition, canceled.Token));
        int writes = h.Port.Log.Count;
        if (restore)
        { Assert.Equal(ControlOperationStatus.Blocked, (await h.Restore(Guid.Empty, CoolingPadRestorationStage.Begin)).Status); }
        else
        { Assert.NotEqual(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State); }
        Assert.True(h.Port.Disposed);
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.Equal(1, h.Factory.Opens);
        await h.Runtime.DrainAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAdmissionFalseThenTrueCannotParkOrReviveOldEpoch(bool sessionPredicate)
    {
        bool admitted = true;
        var h = new Harness(writeAdmission: sessionPredicate ? null : () => admitted);
        await h.Start(sessionPredicate ? () => admitted : null);
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        int writes = h.Port.Log.Count;
        h.Port.BeforeUpdate = allowed =>
        {
            admitted = false;
            Assert.False(allowed());
            admitted = true;
            Assert.False(allowed());
            return Task.CompletedTask;
        };
        await h.Runtime.TickAsync(default);
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
        Assert.False(h.Runtime.GetStatus().QuietRestorationAvailable);
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task FreshnessOnlyNativeBoundaryRejectionParksHealthySession()
    {
        var source = new WaitingSource();
        source.Release.SetResult(new(45, 44));
        var h = new Harness(temperatures: source);
        await h.Start();
        var curve = new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]);
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.HostCurve, cpuCurve: curve, gpuCurve: curve), default);
        int writes = h.Port.Log.Count;
        h.Port.BeforeUpdate = _ => { h.Clock.Advance(TimeSpan.FromSeconds(5)); return Task.CompletedTask; };
        await h.Runtime.TickAsync(default);
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.False(h.Port.Disposed);
        Assert.True(h.Runtime.GetStatus().QuietRestorationAvailable);
        h.Port.BeforeUpdate = null;
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
        Assert.Equal(1, h.Factory.Opens);
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task CanceledUnsafePauseStillDisposesAfterKnownUnsubmittedRejection()
    {
        var h = new Harness();
        await h.Start();
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Port.BeforeUpdate = async _ => { entered.SetResult(); await release.Task; };
        int writes = h.Port.Log.Count;
        Task tick = h.Runtime.TickAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runtime.PauseAsync(CoolingPadControlPauseReason.SessionChanged, canceled.Token));
        release.SetResult();
        await tick;
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
        Assert.False(h.Runtime.GetStatus().RetainedSessionResumable);
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task ExplicitHyperBoostParkingIntentPrecedesOuterEpochLossAndQueuedTick()
    {
        bool admitted = true;
        var h = new Harness(writeAdmission: () => admitted);
        await h.Start();
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Port.AfterUpdate = async () => { submitted.SetResult(); await release.Task; };
        Task tick = h.Runtime.TickAsync(default);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task queued = h.Runtime.TickAsync(default);
        h.Runtime.RequestHyperBoostEntryPark();
        admitted = false; // Outer HB epoch changes only after the explicit intent.
        Task park = h.Runtime.ParkForHyperBoostEntryAsync(default);
        release.SetResult();
        await Task.WhenAll(tick, queued, park);
        Assert.False(h.Port.Disposed);
        Assert.True(h.Runtime.GetStatus().QuietRestorationAvailable);
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task QueuedTickBeforeStopPreservesAcknowledgedEnteredPort()
    {
        var h = new Harness();
        await h.Start();
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Port.AfterUpdate = async () => { submitted.SetResult(); await release.Task; };
        Task inFlight = h.Runtime.TickAsync(default);
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task queuedTick = h.Runtime.TickAsync(default);
        Task stop = h.Runtime.StopAsync(default);
        int writes = h.Port.Log.Count;
        release.SetResult();
        await Task.WhenAll(inFlight, queuedTick, stop);
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.False(h.Port.Disposed);
        Assert.True(h.Runtime.GetStatus().RetainedSessionResumable);
        Assert.True(h.Runtime.GetStatus().QuietRestorationAvailable);
        h.Port.AfterUpdate = null;
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
        Assert.Equal(1, h.Factory.Opens);
        Assert.Equal(1, h.Port.Log.Count(c => c == "acquire"));
        await h.Runtime.DrainAsync();
        Assert.True(h.Port.Disposed);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("drain")]
    [InlineData("pauseThenStop")]
    public async Task PredispatchPlanningRevocationParksOnlyExplicitStop(string action)
    {
        var source = new WaitingSource();
        var h = new Harness(temperatures: source);
        await h.Start();
        var curve = new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]);
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.HostCurve, cpuCurve: curve, gpuCurve: curve), default);
        int writes = h.Port.Log.Count;
        Task tick = h.Runtime.TickAsync(default);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task transition = action switch
        {
            "stop" => h.Runtime.StopAsync(default),
            "drain" => h.Runtime.DrainAsync(),
            _ => h.Runtime.PauseAsync(CoolingPadControlPauseReason.PowerTransition, default)
        };
        Task? laterStop = action == "pauseThenStop" ? h.Runtime.StopAsync(default) : null;
        source.Release.SetResult(new(45, 44));
        await Task.WhenAll(tick, transition, laterStop ?? Task.CompletedTask);
        Assert.Equal(writes, h.Port.Log.Count);
        Assert.Equal(action != "stop", h.Port.Disposed);
        Assert.Equal(action == "stop", h.Runtime.GetStatus().QuietRestorationAvailable);
        if (action == "stop")
        {
            await h.Runtime.ApplySettingsAsync(Fixed, default);
            Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
            Assert.Equal(1, h.Factory.Opens);
        }
        await h.Runtime.DrainAsync();
    }

    private sealed class WaitingSource : IFanTemperatureSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FanTemperatureReadings> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        { Started.TrySetResult(); return new(Release.Task); }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public async Task CheckpointFailureStopsDispatchOrLeavesInterruptedOutcomeUnknown(int phaseValue, bool submitted)
    {
        var store = new FailingStore();
        var h = new Harness(store);
        await h.Start();
        int count = h.Port.Log.Count;
        store.Fail = (CoolingPadMutationPhase)phaseValue;
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        await h.Runtime.TickAsync(default);
        Assert.Equal(count + (submitted ? 1 : 0), h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
        Assert.True((await store.LoadAsync(default)).BlocksAdmission);
        var restarted = new Harness(store);
        await restarted.Runtime.RefreshProductionStateAsync(default);
        Assert.Equal(submitted, restarted.Runtime.GetStatus().SubmissionStateUnknown);
        Assert.Equal(0, restarted.Factory.Opens);
    }

    [Fact]
    public async Task MissingTelemetryParksHealthyPortAndExplicitFreshResumeDoesNotReacquire()
    {
        var h = new Harness();
        await h.Start();
        var curve = new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]);
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.HostCurve, cpuCurve: curve, gpuCurve: curve), default);
        Assert.Equal(CoolingPadControlPauseReason.TelemetryUnavailable, (await h.Runtime.TickAsync(default)).PauseReason);
        Assert.False(h.Port.Disposed);
        Assert.True(h.Runtime.GetStatus().RetainedSessionResumable);
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
        Assert.Equal(1, h.Factory.Opens);
        Assert.Equal(1, h.Port.Log.Count(c => c == "acquire"));
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task HealthyStopParksAndExplicitResumeUpdatesSamePortWithoutEntryOrAcquire()
    {
        var h = new Harness();
        await h.Start();
        Assert.Equal(2200, (await h.Store.LoadAsync(default)).Evidence!.LastAcknowledgedDemandRpm);
        await h.Runtime.StopAsync(default);
        Assert.False(h.Port.Disposed);
        Assert.True(h.Runtime.GetStatus().RetainedSessionResumable);
        Assert.Equal(CoolingPadMutationPhase.Parked, (await h.Store.LoadAsync(default)).Evidence!.Phase);
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
        Assert.Equal(1, h.Factory.Opens);
        Assert.Equal(new[] { "query", "acquire", "query", "entry", "demand:2200", "query", "demand:1000" }, h.Port.Log);
        await h.Runtime.DrainAsync();
        Assert.True(h.Port.Disposed);
        Assert.True((await h.Store.LoadAsync(default)).BlocksAdmission);
    }

    [Fact]
    public async Task NoStartupOrStagedIntentWrites_AndRestartCannotReplayCleanRetainedState()
    {
        var h = new Harness();
        await h.Runtime.RefreshProductionStateAsync(default);
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.Empty(h.Port.Log);
        Assert.Null((await h.Store.LoadAsync(default)).Evidence);
        await h.Start();
        await h.Runtime.StopAsync(default);
        var restarted = new Harness(h.Store);
        await restarted.Runtime.RefreshProductionStateAsync(default);
        Assert.True(restarted.Runtime.GetStatus().RetainedEffect);
        Assert.False(restarted.Runtime.GetStatus().SubmissionStateUnknown);
        await restarted.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.NotEqual(CoolingPadControlState.Running, (await restarted.Runtime.ResumeAsync(default)).State);
        Assert.Equal(0, restarted.Factory.Opens);
        await h.Runtime.DrainAsync();
    }

    [Fact]
    public async Task RestorationRequiresFreshOwnedStagesAndOnlyFinalPhysicalConfirmationResolves()
    {
        var h = new Harness();
        await h.Start();
        await h.Runtime.StopAsync(default);
        Assert.Equal(ControlOperationStatus.Applied, (await h.Restore(Guid.Empty, CoolingPadRestorationStage.Begin)).Status);
        var medium = h.Runtime.GetStatus().Restoration!;
        int calls = h.Port.Log.Count;
        Assert.Equal(ControlOperationStatus.Blocked, (await h.Runtime.RestoreQuietAsync("another-user", medium.OperationId, medium.Stage, true, () => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await h.Restore(medium.OperationId, CoolingPadRestorationStage.Quiet)).Status);
        Assert.Equal(calls, h.Port.Log.Count);
        await h.Restore(medium.OperationId, medium.Stage);
        Assert.Equal(CoolingPadRestorationStage.Quiet, h.Runtime.GetStatus().Restoration!.Stage);
        Assert.Equal(ControlOperationStatus.Blocked, (await h.Restore(medium.OperationId, medium.Stage)).Status);
        await h.Restore(medium.OperationId, CoolingPadRestorationStage.Quiet);
        Assert.True((await h.Store.LoadAsync(default)).BlocksAdmission);
        Assert.Equal(CoolingPadRestorationStage.Final, h.Runtime.GetStatus().Restoration!.Stage);
        await h.Restore(medium.OperationId, CoolingPadRestorationStage.Final);
        Assert.False((await h.Store.LoadAsync(default)).BlocksAdmission);
        Assert.False(await h.Runtime.HasBlockingMutationEvidenceAsync(default));
        Assert.True(h.Port.Disposed);
        Assert.Equal(new[] { "medium", "quiet", "restore" }, h.Port.Log.TakeLast(3));
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("expired")]
    [InlineData("session")]
    [InlineData("generation")]
    public async Task RestorationFailuresCannotDispatchNextStageOrClearEvidence(string fault)
    {
        var h = new Harness();
        await h.Start();
        await h.Restore(Guid.Empty, CoolingPadRestorationStage.Begin);
        var prompt = h.Runtime.GetStatus().Restoration!;
        int calls = h.Port.Log.Count;
        if (fault == "expired") { h.Clock.Advance(TimeSpan.FromMinutes(10)); }
        if (fault == "generation")
        {
            var current = await h.Store.LoadAsync(default);
            await h.Store.CheckpointAsync(current, CoolingPadMutationPhase.RestorationPending, null, default);
        }
        await h.Runtime.RestoreQuietAsync("owner", prompt.OperationId, prompt.Stage, fault != "refused", () => fault != "session", default);
        Assert.Equal(calls, h.Port.Log.Count);
        Assert.True((await h.Store.LoadAsync(default)).BlocksAdmission);
        Assert.True(h.Port.Disposed);
        Assert.Null(h.Runtime.GetStatus().Restoration);
    }

    [Fact]
    public async Task AmbiguousUpdateLeavesPendingAcrossRestartAndNeverRestores()
    {
        var h = new Harness();
        await h.Start();
        h.Port.FailDemand = true;
        await h.Runtime.ApplySettingsAsync(new(CoolingPadControlMode.FixedDemand, new(1000)), default);
        Assert.True((await h.Runtime.TickAsync(default)).SubmissionStateUnknown);
        Assert.Equal(CoolingPadMutationPhase.Pending, (await h.Store.LoadAsync(default)).Evidence!.Phase);
        Assert.Equal(ControlOperationStatus.Blocked, (await h.Restore(Guid.Empty, CoolingPadRestorationStage.Begin)).Status);
        var restarted = new Harness(h.Store);
        await restarted.Runtime.RefreshProductionStateAsync(default);
        Assert.True(restarted.Runtime.GetStatus().SubmissionStateUnknown);
        Assert.DoesNotContain("medium", h.Port.Log);
    }

    [Fact]
    public async Task ExpiredRestorationMaintenanceOnlyClosesResources()
    {
        var h = new Harness();
        await h.Start();
        await h.Restore(Guid.Empty, CoolingPadRestorationStage.Begin);
        int count = h.Port.Log.Count;
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        await h.Runtime.RefreshProductionStateAsync(default);
        Assert.Equal(count, h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
        Assert.True((await h.Store.LoadAsync(default)).BlocksAdmission);
    }

    private sealed class FailingStore : ICoolingPadMutationEvidenceStore
    {
        private readonly InMemoryCoolingPadMutationEvidenceStore inner = new();
        internal CoolingPadMutationPhase? Fail;
        public Task<CoolingPadMutationEvidenceSnapshot> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public Task<CoolingPadMutationEvidenceSnapshot> ArmAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => inner.ArmAsync(expected, token);
        public Task<CoolingPadMutationEvidenceSnapshot> ResolveAsync(CoolingPadMutationEvidenceSnapshot expected, CancellationToken token) => inner.ResolveAsync(expected, token);
        public Task<CoolingPadMutationEvidenceSnapshot> CheckpointAsync(CoolingPadMutationEvidenceSnapshot expected, CoolingPadMutationPhase phase, int? rpm, CancellationToken token) =>
            phase == Fail ? Task.FromException<CoolingPadMutationEvidenceSnapshot>(new IOException("Injected checkpoint failure")) : inner.CheckpointAsync(expected, phase, rpm, token);
    }

    private sealed class Harness
    {
        internal readonly Port Port = new();
        internal readonly Clock Clock = new();
        internal ICoolingPadMutationEvidenceStore Store { get; }
        internal Factory Factory { get; }
        internal CoolingPadControlRuntime Runtime { get; }
        internal Harness(ICoolingPadMutationEvidenceStore? store = null, IFanTemperatureSource? temperatures = null, Func<bool>? writeAdmission = null)
        {
            Store = store ?? new InMemoryCoolingPadMutationEvidenceStore(); Factory = new(Port);
            Runtime = new(Store, Factory, temperatures, clock: Clock, writeAdmission: writeAdmission, productionLifecycle: true);
        }
        internal async Task Start(Func<bool>? sessionAdmission = null)
        {
            await Runtime.ApplySettingsAsync(Fixed, default);
            Assert.Equal(CoolingPadControlState.Running, (await Runtime.ResumeAsync(default, sessionAdmission)).State);
        }
        internal Task<ControlOperationResult> Restore(Guid id, CoolingPadRestorationStage stage) =>
            Runtime.RestoreQuietAsync("owner", id, stage, true, () => true, default);
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan time) => timestamp += time.Ticks;
    }
    private sealed class Factory(Port port) : ICoolingPadControlPortFactory
    {
        internal int Opens;
        public Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token)
        { Opens++; return Task.FromResult(new CoolingPadPortOpenResult(port)); }
    }
    private sealed class Port : ICoolingPadControlPort, ICoolingPadDeveloperRestorationPort
    {
        internal List<string> Log { get; } = [];
        internal bool Disposed, FailDemand;
        internal Func<Task>? AfterUpdate;
        internal Func<Func<bool>, Task>? BeforeUpdate;
        private CoolingPadRawControlState raw;
        public Task<CoolingPadQueryResult> QueryControlStateAsync(CancellationToken token)
        { Log.Add("query"); return Task.FromResult(new CoolingPadQueryResult(raw)); }
        public Task<CoolingPadWriteResult> AcquireAsync(Func<bool> allowed, CancellationToken token)
        { raw = CoolingPadRawControlState.Raw0300; return Write("acquire", allowed); }
        public async Task<CoolingPadEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token) =>
            new(await Write("entry", allowed), await Write($"demand:{demand.Rpm}", allowed));
        public async Task<CoolingPadWriteResult> SetDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token)
        {
            if (BeforeUpdate is { } before) { await before(allowed); }
            if (!allowed()) { return new(false, false, CoolingPadPortFault.AdmissionRevoked); }
            var result = await Write($"demand:{demand.Rpm}", allowed, FailDemand);
            if (AfterUpdate is { } callback) { await callback(); }
            return result;
        }
        public Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> allowed, CancellationToken token) => Write("medium", allowed);
        public Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> allowed, CancellationToken token) => Write("quiet", allowed);
        public Task<CoolingPadWriteResult> RestoreRawAsync(Func<bool> allowed, CancellationToken token)
        { raw = CoolingPadRawControlState.Raw0000; return Write("restore", allowed); }
        private Task<CoolingPadWriteResult> Write(string command, Func<bool> allowed, bool fail = false)
        {
            if (!allowed()) { return Task.FromResult(new CoolingPadWriteResult(false, false, CoolingPadPortFault.Rejected)); }
            Log.Add(command);
            return Task.FromResult(new CoolingPadWriteResult(true, !fail, fail ? CoolingPadPortFault.SubmissionUnknown : null));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
