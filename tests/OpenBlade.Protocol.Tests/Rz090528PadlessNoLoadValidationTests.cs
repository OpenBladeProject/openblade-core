namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessNoLoadValidationTests
{
    [Fact]
    public async Task ThirtySamplesCoverSixtySecondsWithFrozenBaselineRelativeTripwire()
    {
        var clock = new Clock();
        var run = Create(clock, new Telemetry());
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source(), CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(55, result.CpuAbortCelsius);
        Assert.Null(result.GpuAbortCelsius);
        Assert.Equal(3, result.Baseline.Count);
        Assert.Equal(30, result.Samples.Count);
        Assert.Equal(60000, result.ElapsedMilliseconds);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Theory]
    [InlineData("cpu", "CpuExperimentalTripwire")]
    [InlineData("missing", "CpuTelemetryLost")]
    [InlineData("gpu", "GpuActivatedWithoutBaseline")]
    public async Task OrdinaryTelemetryAbortPermitsOnlyFreshGuardedRestoration(string fault, string failure)
    {
        var run = Create(new Clock(), new Telemetry { Fault = fault });
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source(), CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal(failure, result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Theory]
    [InlineData("ThermalReadOrPairAmbiguous", false)]
    [InlineData("CleanupTransactionReserve", true)]
    [InlineData("PadAbsenceLost", true)]
    public async Task ProtocolFailurePreservesItsRestorationClassification(string failure, bool restore)
    {
        var run = Create(new Clock(), new Telemetry());
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source { Failure = failure, Restore = restore }, CancellationToken.None);
        Assert.Equal(restore, result.PermitKnownStateRestore);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public async Task MissedSamplingDeadlineAbortsInsteadOfExtendingDwell()
    {
        var clock = new Clock();
        var run = Create(clock, new Telemetry());
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        clock.DelayExtra = TimeSpan.FromSeconds(5);
        var result = await run.RunAsync(new Source(), CancellationToken.None);
        Assert.False(result.Completed);
        Assert.Equal("SamplingDeadlineMissed", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public async Task CancellationAtObservationBoundaryAllowsIndependentKnownStateCleanup()
    {
        var run = Create(new Clock(), new Telemetry());
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source(), new CancellationToken(true));
        Assert.Equal("Canceled", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public async Task UnstableBaselineBlocksEntryAndPlanTamperingIsRejected()
    {
        var run = Create(new Clock(), new Telemetry { Unstable = true });
        Assert.False(await run.PrepareAsync(new Source(), CancellationToken.None));
        byte[] plan = Rz090528PadlessNoLoadValidation.ReadPlan();
        plan[0] ^= 1;
        Assert.Throws<InvalidOperationException>(() => Rz090528PadlessNoLoadValidation.ValidatePlan(plan));
    }

    [Fact]
    public async Task ActiveGpuUsesItsOwnBaselineAndStopsAtFrozenThreshold()
    {
        var run = Create(new Clock(), new Telemetry { BaselineGpu = 40, Fault = "gpuLimit" });
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source(), CancellationToken.None);
        Assert.Equal(50, result.GpuAbortCelsius);
        Assert.Equal("GpuExperimentalTripwire", result.Failure);
        Assert.True(result.PermitKnownStateRestore);
    }

    [Fact]
    public async Task CpuAboveEntryEnvelopeNeverPermitsApply()
    {
        var run = Create(new Clock(), new Telemetry { BaselineCpu = 51 });
        Assert.False(await run.PrepareAsync(new Source(), CancellationToken.None));
        Assert.False(run.Result!.PermitKnownStateRestore);
    }

    private static Rz090528PadlessNoLoadValidation Create(Clock clock, Telemetry source) => new(source, clock, clock.Wait);
    [Fact]
    public async Task SlowFinalBaselineSampleCannotAdmitEntry()
    {
        var clock = new Clock();
        var telemetry = new Telemetry { OnRead = count => { if (count == 3) { clock.Advance(TimeSpan.FromSeconds(3)); } } };
        var run = Create(clock, telemetry);
        Assert.False(await run.PrepareAsync(new Source(), CancellationToken.None));
        Assert.Equal("BaselineSamplingStale", run.Result!.Failure);
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(251, false)]
    [InlineData(5000, false)]
    public async Task FinalWakeMustRespectExplicitCompletionTolerance(int lateMilliseconds, bool completed)
    {
        var clock = new Clock();
        var telemetry = new Telemetry { OnRead = count => { if (count == 33) { clock.DelayExtra = TimeSpan.FromMilliseconds(lateMilliseconds); } } };
        var run = Create(clock, telemetry);
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        var result = await run.RunAsync(new Source(), CancellationToken.None);
        Assert.Equal(completed, result.Completed);
        Assert.Equal(60000 + lateMilliseconds, result.ElapsedMilliseconds);
        Assert.True(result.PermitKnownStateRestore);
        Assert.Equal(completed ? null : "SamplingDeadlineMissed", result.Failure);
    }

    [Fact]
    public async Task PendingReadCancellationCannotBecomeSuccessAfterLateCompletion()
    {
        var run = Create(new Clock(), new Telemetry());
        Assert.True(await run.PrepareAsync(new Source(), CancellationToken.None));
        using var cancel = new CancellationTokenSource();
        var source = new PendingSource();
        var pending = run.RunAsync(source, cancel.Token);
        await source.Started.Task;
        cancel.Cancel();
        var result = await pending;
        Assert.Equal("ProtocolSnapshotUncertain", result.Failure);
        Assert.False(result.PermitKnownStateRestore);
        source.Completion.SetResult(new(new(3000, 3000), null, true));
        Assert.Same(result, run.Result);
        Assert.False(run.Result!.Completed);
    }

    private sealed class PendingSource : IRz090528NoLoadSnapshotSource
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<Rz090528NoLoadSnapshot> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Rz090528NoLoadSnapshot> ReadAsync(bool baseline, CancellationToken token)
        {
            Started.SetResult();
            return Completion.Task;
        }
    }

    internal sealed class Clock : TimeProvider
    {
        private long ticks;
        internal TimeSpan DelayExtra;
        internal void Advance(TimeSpan duration) => ticks += duration.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal Task Wait(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ticks += duration.Ticks + DelayExtra.Ticks;
            return Task.CompletedTask;
        }
    }
    internal sealed class Telemetry : IHyperBoostThermalTelemetrySource
    {
        internal string? Fault;
        internal bool Unstable;
        internal int BaselineCpu = 45;
        internal int? BaselineGpu;
        internal Action<int>? OnRead;
        private int reads;
        public ValueTask<HyperBoostTemperatureObservation> ReadAsync(CancellationToken token)
        {
            reads++;
            OnRead?.Invoke(reads);
            var value = reads <= 3
                ? new HyperBoostTemperatureObservation(Unstable && reads == 2 ? 49 : BaselineCpu, BaselineGpu, BaselineGpu is null)
                : Fault switch
                {
                    "cpu" => new(55, null, true),
                    "missing" => new(null, null, true),
                    "gpu" => new(45, 40, false),
                    "gpuLimit" => new(45, 50, false),
                    _ => new(BaselineCpu, BaselineGpu, BaselineGpu is null),
                };
            return ValueTask.FromResult(value);
        }
    }
    private sealed class Source : IRz090528NoLoadSnapshotSource
    {
        internal string? Failure;
        internal bool Restore;
        public Task<Rz090528NoLoadSnapshot> ReadAsync(bool baseline, CancellationToken token) =>
            Task.FromResult(new Rz090528NoLoadSnapshot(new(3000, 3000), Failure, Failure is null || Restore));
    }
}
