using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadControlRuntimeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetainedMutationEvidenceClearsOnlyAfterCompleteAttendedRestoration(bool finalConfirmed)
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        Assert.False(h.Runtime.HasRetainedMutationEvidence);
        await h.Runtime.ResumeAsync(default);
        Assert.True(h.Runtime.HasRetainedMutationEvidence);
        var restored = await h.Runtime.RestoreForDeveloperValidationAsync((stage, _) =>
        {
            Assert.True(h.Runtime.HasRetainedMutationEvidence);
            return Task.FromResult(stage != "finalPhysicalConfirmation" || finalConfirmed);
        }, default);
        Assert.Equal(finalConfirmed, restored.Completed);
        Assert.Equal(!finalConfirmed, h.Runtime.HasRetainedMutationEvidence);
        await h.Runtime.StopAsync(default);
        Assert.Equal(!finalConfirmed, h.Runtime.HasRetainedMutationEvidence);
    }

    [Fact]
    public async Task StopWhileAdmissionPredicateWaitsPreventsLateSet()
    {
        var h = new Harness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool atSet = false;
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default, () =>
        {
            if (atSet) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            return true;
        });
        h.Source.Value = new(45, 44);
        h.Port.BeforeUpdate = () => { atSet = true; return Task.CompletedTask; };
        Task tick = Task.Run(() => h.Runtime.TickAsync(default));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task stop = h.Runtime.StopAsync(default);
        release.Set();
        await Task.WhenAll(tick, stop).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("demand:1350", h.Port.Log);
        Assert.Equal(CoolingPadControlState.Stopped, h.Runtime.GetStatus().State);
        Assert.True(h.Port.Disposed);
    }

    private static CoolingPadControlSettings Fixed => new(CoolingPadControlMode.FixedDemand, new(1500));
    private static CoolingPadControlSettings Curve => new(CoolingPadControlMode.HostCurve,
        cpuCurve: new CoolingPadDemandCurve([new(40, new(1000)), new(90, new(2200))]),
        gpuCurve: new CoolingPadDemandCurve([new(30, new(1000)), new(80, new(2200))]));

    [Fact]
    public async Task DefaultClosedFactoryStagesIntentWithoutNativeOrTelemetry()
    {
        var source = new Source();
        var runtime = new CoolingPadControlRuntime(temperatures: source);
        var staged = await runtime.ApplySettingsAsync(Curve, default);
        Assert.True(staged.ResumeRequired);
        Assert.Equal(CoolingPadControlPauseReason.AdmissionClosed, (await runtime.ResumeAsync(default)).PauseReason);
        Assert.Equal(0, source.Reads);
        Assert.False(runtime.HasRetainedMutationEvidence);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, runtime.GetStatus().Access);
    }

    [Fact]
    public async Task FreshEntryChangedDemandAndSuppressionKeepAcknowledgementSeparate()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        Assert.Empty(h.Factory.Ports);
        var running = await h.Runtime.ResumeAsync(default);
        Assert.Equal(CoolingPadControlState.Running, running.State);
        Assert.Equal(1600, running.LastAcknowledgedDemand!.Value.Rpm);
        Assert.Equal(new[] { "query", "acquire", "query", "entry", "demand:1600" }, h.Port.Log);
        h.Source.Value = new(45, 44);
        await h.Runtime.TickAsync(default);
        await h.Runtime.TickAsync(default);
        Assert.Equal(1350, h.Runtime.GetStatus().LastAcknowledgedDemand!.Value.Rpm);
        Assert.Single(h.Port.Log, x => x == "demand:1350");
        Assert.Equal(4, h.Source.Reads);
        var stopped = await h.Runtime.StopAsync(default);
        Assert.True(h.Port.Disposed);
        Assert.Equal(CoolingPadControlState.Stopped, stopped.State);
        Assert.Equal(1350, stopped.LastAcknowledgedDemand!.Value.Rpm);
        Assert.DoesNotContain(h.Port.Log, x => x is "quiet" or "restore");
        await h.Runtime.TickAsync(default);
        Assert.Equal(4, h.Source.Reads);
    }

    [Theory]
    [InlineData(CoolingPadPortFault.Disconnected, CoolingPadControlPauseReason.DeviceUnavailable)]
    [InlineData(CoolingPadPortFault.ControllerConflict, CoolingPadControlPauseReason.ControllerConflict)]
    [InlineData(CoolingPadPortFault.SubmissionUnknown, CoolingPadControlPauseReason.SubmissionUnknown)]
    public async Task SessionFaultDoesNotReplayAndRequiresFreshRawZeroOnExplicitResume(CoolingPadPortFault fault, CoolingPadControlPauseReason reason)
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default);
        h.Source.Value = new(45, 44);
        h.Port.NextWrite = new(fault == CoolingPadPortFault.SubmissionUnknown, false, fault);
        var paused = await h.Runtime.TickAsync(default);
        Assert.Equal(reason, paused.PauseReason);
        Assert.Equal(1600, paused.LastAcknowledgedDemand!.Value.Rpm);
        Assert.Equal(1350, paused.PlannedDemand!.Value.Rpm);
        Assert.True(paused.ResumeRequired);
        Assert.Equal(fault == CoolingPadPortFault.SubmissionUnknown, paused.SubmissionStateUnknown);
        int reads = h.Source.Reads;
        await h.Runtime.TickAsync(default);
        Assert.Single(h.Factory.Ports);
        Assert.Equal(reads, h.Source.Reads);
        h.Factory.InitialRaw = CoolingPadRawControlState.Raw0300;
        Assert.Equal(CoolingPadControlPauseReason.SessionChanged, (await h.Runtime.ResumeAsync(default)).PauseReason);
        Assert.Equal(new[] { "query" }, h.Port.Log);
        h.Factory.InitialRaw = CoolingPadRawControlState.Raw0000;
        Assert.Equal(CoolingPadControlState.Running, (await h.Runtime.ResumeAsync(default)).State);
        Assert.Equal(3, h.Factory.Ports.Count);
    }

    [Theory]
    [InlineData(false, CoolingPadControlPauseReason.TelemetryUnavailable)]
    [InlineData(true, CoolingPadControlPauseReason.None)]
    public async Task InactiveHeuristicRequiresExplicitQualityAttestation(bool trustworthy, CoolingPadControlPauseReason expected)
    {
        var h = new Harness(trustworthy);
        h.Source.Value = new(45, null, true);
        await h.Runtime.ApplySettingsAsync(Curve, default);
        var result = await h.Runtime.ResumeAsync(default);
        Assert.Equal(expected, result.PauseReason);
        Assert.Equal(trustworthy, h.Port.Log.Contains("acquire"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrStaleTelemetryStopsAllFutureUpdates(bool stale)
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default);
        h.Source.Value = new(null, 44);
        if (stale) { h.Source.OnRead = () => h.Clock.Advance(TimeSpan.FromSeconds(5)); }
        var paused = await h.Runtime.TickAsync(default);
        Assert.Equal(stale ? CoolingPadControlPauseReason.StaleTelemetry : CoolingPadControlPauseReason.TelemetryUnavailable, paused.PauseReason);
        Assert.Equal(5, h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
        await h.Runtime.TickAsync(default);
        Assert.Single(h.Factory.Ports);
    }

    [Fact]
    public async Task NativeAdmissionHookRejectsAgeThatExpiredAfterEntryAcknowledgement()
    {
        var h = new Harness();
        h.Factory.Configure = port => port.AfterEntry = () => h.Clock.Advance(TimeSpan.FromSeconds(5));
        await h.Runtime.ApplySettingsAsync(Curve, default);
        var result = await h.Runtime.ResumeAsync(default);
        Assert.Equal(CoolingPadControlPauseReason.StaleTelemetry, result.PauseReason);
        Assert.Equal(new[] { "query", "acquire", "query", "entry" }, h.Port.Log);
        Assert.Null(result.LastAcknowledgedDemand);
    }

    [Fact]
    public async Task StopRevokesAlreadyQueuedSetAndSendsNoCleanup()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default);
        h.Source.Value = new(45, 44);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Port.BeforeUpdate = async () => { reached.SetResult(); await release.Task; };
        Task tick = h.Runtime.TickAsync(default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task stop = h.Runtime.StopAsync(default);
        release.SetResult();
        await Task.WhenAll(tick, stop);
        Assert.Equal(5, h.Port.Log.Count);
        Assert.Equal(CoolingPadControlState.Stopped, h.Runtime.GetStatus().State);
        Assert.True(h.Port.Disposed);
    }

    [Fact]
    public async Task CancellationClosesActorBeforeLateTelemetryAndCannotResumeUntilReadFinishes()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Pending = () => { reached.SetResult(); return late.Task; };
        using var cancel = new CancellationTokenSource();
        Task tick = h.Runtime.TickAsync(cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await tick.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(h.Port.Disposed);
        await h.Runtime.ResumeAsync(default);
        Assert.Single(h.Factory.Ports);
        late.SetResult(new(90, 80));
        await h.Runtime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5, h.Port.Log.Count);
    }

    [Fact]
    public async Task FixedDemandDoesNotReadTelemetryOrClaimMeasuredSpeed()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        var status = await h.Runtime.ResumeAsync(default);
        Assert.Equal(1500, status.LastAcknowledgedDemand!.Value.Rpm);
        Assert.False(status.MeasuredRpmReadbackSupported);
        Assert.Equal(0, h.Source.Reads);
        await h.Runtime.TickAsync(default);
        Assert.Equal(5, h.Port.Log.Count);
    }

    [Fact]
    public async Task ActiveFixedToCurveEditUsesExistingSessionButPausedEditCannotReopen()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        await h.Runtime.ResumeAsync(default);
        await h.Runtime.ApplySettingsAsync(Curve, default);
        Assert.Equal(CoolingPadControlState.Running, h.Runtime.GetStatus().State);
        Assert.Null(h.Runtime.GetStatus().PlannedDemand);
        Assert.Equal(1500, h.Runtime.GetStatus().LastAcknowledgedDemand!.Value.Rpm);
        Assert.Equal(0, h.Source.Reads);
        await h.Runtime.TickAsync(default);
        Assert.Single(h.Factory.Ports);
        Assert.Single(h.Port.Log, x => x == "acquire");
        Assert.Equal(1600, h.Runtime.GetStatus().LastAcknowledgedDemand!.Value.Rpm);
        await h.Runtime.PauseAsync(CoolingPadControlPauseReason.ControllerConflict, default);
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        await h.Runtime.TickAsync(default);
        Assert.Single(h.Factory.Ports);
        Assert.True(h.Runtime.GetStatus().ResumeRequired);
        Assert.Equal(CoolingPadControlState.Paused, h.Runtime.GetStatus().State);
    }

    [Fact]
    public async Task CanceledLateOpenCannotEnterOrAllowConcurrentReplacement()
    {
        var h = new Harness();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Factory.BeforeOpen = async () => { reached.SetResult(); await release.Task; };
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        using var cancel = new CancellationTokenSource();
        Task resume = h.Runtime.ResumeAsync(cancel.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await resume.WaitAsync(TimeSpan.FromSeconds(2));
        await h.Runtime.ResumeAsync(default);
        Assert.Equal(1, h.Factory.Opens);
        release.SetResult();
        await h.Runtime.DrainAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(h.Port.Disposed);
        Assert.Empty(h.Port.Log);
    }

    [Fact]
    public async Task FailedPortDisposalCannotAuthorizeAnotherSession()
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Fixed, default);
        await h.Runtime.ResumeAsync(default);
        h.Port.DisposeFails = true;
        await h.Runtime.PauseAsync(CoolingPadControlPauseReason.SessionChanged, default);
        Assert.Equal(CoolingPadControlPauseReason.DeviceUnavailable, (await h.Runtime.ResumeAsync(default)).PauseReason);
        Assert.Single(h.Factory.Ports);
    }

    [Fact]
    public async Task NewSessionAuthorizationCannotAuthorizeHeldOldSessionSet()
    {
        var h = new Harness();
        bool originalSessionActive = true;
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default, () => originalSessionActive);
        Port oldPort = h.Port;
        h.Source.Value = new(45, 44);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        oldPort.BeforeUpdate = async () => { reached.SetResult(); await release.Task; };
        Task tick = h.Runtime.TickAsync(default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        originalSessionActive = false;
        // Before Resume has revoked anything, the old callback already rejects the lost session.
        Assert.False(oldPort.PendingAdmission!());
        Task resume = h.Runtime.ResumeAsync(default, () => true);
        release.SetResult();
        await Task.WhenAll(tick, resume);
        Assert.Equal(5, oldPort.Log.Count);
        Assert.True(oldPort.Disposed);
        Assert.Equal(CoolingPadControlPauseReason.ExplicitResumeRequired, h.Runtime.GetStatus().PauseReason);
        // Failure of the old generation also revokes the queued Resume; a fresh request is required.
        await h.Runtime.ResumeAsync(default, () => true);
        Assert.Equal(CoolingPadControlState.Running, h.Runtime.GetStatus().State);
        Assert.Equal(1350, h.Runtime.GetStatus().LastAcknowledgedDemand!.Value.Rpm);
        Assert.Equal(5, oldPort.Log.Count);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    [InlineData("pause")]
    public async Task CanceledQueuedRevocationPublishesPausedOnNextTick(string action)
    {
        var h = new Harness();
        await h.Runtime.ApplySettingsAsync(Curve, default);
        await h.Runtime.ResumeAsync(default);
        h.Source.Value = new(45, 44);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Port.AfterUpdate = async () => { acknowledged.SetResult(); await release.Task; };
        Task tick = h.Runtime.TickAsync(default);
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action switch
        {
            "stop" => h.Runtime.StopAsync(cancel.Token),
            "resume" => h.Runtime.ResumeAsync(cancel.Token),
            _ => h.Runtime.PauseAsync(CoolingPadControlPauseReason.SessionChanged, cancel.Token),
        });
        release.SetResult();
        await tick;
        int readCount = h.Source.Reads;
        int commands = h.Port.Log.Count;
        var paused = await h.Runtime.TickAsync(default);
        Assert.Equal(CoolingPadControlState.Paused, paused.State);
        Assert.Equal(CoolingPadControlPauseReason.ExplicitResumeRequired, paused.PauseReason);
        Assert.True(paused.ResumeRequired);
        Assert.Equal(readCount, h.Source.Reads);
        Assert.Equal(commands, h.Port.Log.Count);
        Assert.True(h.Port.Disposed);
    }

    private sealed class Harness
    {
        internal readonly Source Source = new();
        internal readonly Factory Factory = new();
        internal readonly Clock Clock = new();
        internal CoolingPadControlRuntime Runtime { get; }
        internal Port Port => Factory.Ports[^1];
        internal Harness(bool trustedInactive = false) => Runtime = new(Factory, Source, Clock, _ => trustedInactive);
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }
    private sealed class Source : IFanTemperatureSource
    {
        internal FanTemperatureReadings Value = new(45, 54);
        internal int Reads;
        internal Action? OnRead;
        internal Func<Task<FanTemperatureReadings>>? Pending;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            OnRead?.Invoke();
            return Pending is null ? new(Value) : new(Pending());
        }
    }
    private sealed class Factory : ICoolingPadControlPortFactory
    {
        internal readonly List<Port> Ports = [];
        internal CoolingPadRawControlState InitialRaw;
        internal Action<Port>? Configure;
        internal Func<Task>? BeforeOpen;
        internal int Opens;
        public async Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Opens);
            if (BeforeOpen is not null) { await BeforeOpen(); }
            var port = new Port { Raw = InitialRaw };
            Configure?.Invoke(port);
            Ports.Add(port);
            return new CoolingPadPortOpenResult(port);
        }
    }
    private sealed class Port : ICoolingPadControlPort, ICoolingPadDeveloperRestorationPort
    {
        internal readonly List<string> Log = [];
        internal CoolingPadRawControlState Raw;
        internal bool Disposed;
        internal bool DisposeFails;
        internal Action? AfterEntry;
        internal Func<Task>? BeforeUpdate;
        internal Func<Task>? AfterUpdate;
        internal Func<bool>? PendingAdmission;
        internal CoolingPadWriteResult? NextWrite;
        private static CoolingPadWriteResult Ack => new(true, true);
        private static CoolingPadWriteResult Denied => new(false, false, CoolingPadPortFault.Rejected);
        public Task<CoolingPadQueryResult> QueryControlStateAsync(CancellationToken token)
        { Log.Add("query"); return Task.FromResult(new CoolingPadQueryResult(Raw)); }
        public Task<CoolingPadWriteResult> AcquireAsync(Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { return Task.FromResult(Denied); }
            Log.Add("acquire"); Raw = CoolingPadRawControlState.Raw0300; return Task.FromResult(Ack);
        }
        public Task<CoolingPadEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token)
        {
            if (!allowed()) { return Task.FromResult(new CoolingPadEntryResult(Denied, Denied)); }
            Log.Add("entry"); AfterEntry?.Invoke();
            if (!allowed()) { return Task.FromResult(new CoolingPadEntryResult(Ack, Denied)); }
            Log.Add($"demand:{demand.Rpm}"); return Task.FromResult(new CoolingPadEntryResult(Ack, Ack));
        }
        public async Task<CoolingPadWriteResult> SetDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token)
        {
            PendingAdmission = allowed;
            if (BeforeUpdate is not null) { await BeforeUpdate(); }
            if (!allowed()) { return Denied; }
            Log.Add($"demand:{demand.Rpm}");
            if (AfterUpdate is not null) { await AfterUpdate(); }
            return NextWrite ?? Ack;
        }
        public ValueTask DisposeAsync()
        {
            if (DisposeFails) { throw new IOException(); }
            Disposed = true;
            return ValueTask.CompletedTask;
        }
        public Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> allowed, CancellationToken token)
        { Assert.True(allowed()); Log.Add("medium"); return Task.FromResult(Ack); }
        public Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> allowed, CancellationToken token)
        { Assert.True(allowed()); Log.Add("quiet"); return Task.FromResult(Ack); }
        public Task<CoolingPadWriteResult> RestoreRawAsync(Func<bool> allowed, CancellationToken token)
        { Assert.True(allowed()); Log.Add("restore"); Raw = CoolingPadRawControlState.Raw0000; return Task.FromResult(Ack); }
    }
}
