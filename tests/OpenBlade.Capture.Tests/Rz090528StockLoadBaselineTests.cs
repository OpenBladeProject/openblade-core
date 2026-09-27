namespace OpenBlade.Capture.Tests;

public sealed class Rz090528StockLoadBaselineTests
{
    [Fact]
    public async Task ThreeLoadedSamplesQualifyThenCollectThirtySecondsWithoutWrites()
    {
        var clock = new Clock();
        int reads = 0;
        var collector = Create(clock, new Workload(), () => { reads++; return Healthy; });
        bool armed = false;
        var result = await collector.RunAsync(_ => armed = true, CancellationToken.None);
        Assert.True(armed);
        Assert.True(result.Completed);
        Assert.Equal(4000, result.ArmingElapsedMilliseconds);
        Assert.Equal(30000, result.CollectionElapsedMilliseconds);
        Assert.Equal(15, reads);
    }

    [Fact]
    public async Task WaitingForOperatorDoesNotConsumeCollectionBudget()
    {
        var clock = new Clock();
        var result = await Create(clock, new Workload { AbsentReads = 50 }, () => Healthy)
            .RunAsync(_ => { }, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(104000, result.ArmingElapsedMilliseconds);
        Assert.Equal(30000, result.CollectionElapsedMilliseconds);
    }

    [Fact]
    public async Task ArmingTimeoutNeverOpensQueryActor()
    {
        var clock = new Clock();
        var result = await Create(clock, new Workload { AbsentReads = 1000 },
            () => throw new InvalidOperationException("Must not open"))
            .RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal("ArmingTimedOut", result.Failure);
        Assert.Empty(result.Samples);
    }

    [Theory]
    [InlineData("PreexistingWorkloadRefused")]
    [InlineData("WorkloadIdentityMismatch")]
    public async Task ArmingFailureNeverReadsHardware(string failure)
    {
        var result = await Create(new Clock(), new Workload { ArmFailure = failure },
            () => throw new InvalidOperationException("Must not open"))
            .RunAsync(_ => throw new InvalidOperationException("Must not arm"), CancellationToken.None);
        Assert.Equal(failure, result.Failure);
        Assert.Empty(result.Samples);
    }

    [Theory]
    [InlineData(0, 45, 2000, 2000)]
    [InlineData(45, 0, 2000, 2000)]
    [InlineData(45, 45, 0, 2000)]
    [InlineData(45, 45, 2000, 0)]
    public async Task MissingPositiveSensorEndsCollection(int cpu, int gpu, int fan1, int fan2)
    {
        var result = await Create(new Clock(), new Workload(), () => new(cpu, gpu, fan1, fan2, null))
            .RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal("PositiveActiveTemperaturesAndFansRequired", result.Failure);
        Assert.False(result.Completed);
    }

    [Theory]
    [InlineData("ThermalReadOrPairAmbiguous")]
    [InlineData("FullPowerNotConfirmed")]
    [InlineData("PadAbsenceLost")]
    public async Task SnapshotFailuresAreRetainedWithoutSuccess(string failure)
    {
        var result = await Create(new Clock(), new Workload(), () => Healthy with { Failure = failure })
            .RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public async Task FallingLoadIsNotSuccessfulBaseline()
    {
        var result = await Create(new Clock(), new Workload { LowLoadAfter = 4 }, () => Healthy)
            .RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal("QualifiedLoadLost", result.Failure);
    }

    [Fact]
    public async Task LostEligibilityStopsBeforeNextQuery()
    {
        var clock = new Clock();
        int checks = 0, reads = 0;
        var collector = new Rz090528StockLoadBaseline(new Workload(), _ => { reads++; return Task.FromResult(Healthy); },
            () => ++checks < 6, clock, clock.Wait);
        var result = await collector.RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal("ExactHostIsolationOrGpuZeroNotConfirmed", result.Failure);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(251, false)]
    [InlineData(5000, false)]
    public async Task LateCompletionIsReportedAccurately(int late, bool success)
    {
        var clock = new Clock();
        int reads = 0;
        var collector = Create(clock, new Workload(), () => { if (++reads == 15) { clock.Extra = late; } return Healthy; });
        var result = await collector.RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal(success, result.Completed);
        Assert.Equal(30000 + late, result.CollectionElapsedMilliseconds);
    }

    [Fact]
    public async Task CancellationDoesNotStartQueries()
    {
        var result = await Create(new Clock(), new Workload(), () => throw new InvalidOperationException())
            .RunAsync(_ => { }, new CancellationToken(true));
        Assert.Equal("Canceled", result.Failure);
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void PlanAndCliArePinned()
    {
        Assert.NotEmpty(Rz090528StockLoadBaseline.ReadPlan());
        Assert.True(Rz090528StockLoadBaselineCommand.ValidArguments(["--confirm-target",
            Rz090528StockLoadBaselineCommand.Confirmation, "--plan-sha256", Rz090528StockLoadBaseline.PlanSha256,
            "--workload-exe", @"C:\Benchmarks\3DMarkSteelNomad.exe"]));
        Assert.False(Rz090528StockLoadBaselineCommand.ValidArguments(["--workload", "arbitrary.exe"]));
    }

    private static StockLoadReadings Healthy => new(60, 65, 2500, 2700, null)
    {
        PairedPerformanceAutomaticConfirmed = true,
        Full280WConfirmed = true,
        PadAbsentConfirmed = true,
        IndependentGpuZeroConfirmed = true,
    };
    private static Rz090528StockLoadBaseline Create(Clock clock, Workload workload, Func<StockLoadReadings> read)
    {
        bool preflight = true;
        return new(workload, _ =>
        {
            if (preflight) { preflight = false; return Task.FromResult(Healthy); }
            return Task.FromResult(read());
        }, () => true, clock, clock.Wait);
    }

    [Theory]
    [InlineData("ThermalReadOrPairAmbiguous")]
    [InlineData("FullPowerNotConfirmed")]
    public async Task FailedPreflightCannotEmitArmedMarker(string failure)
    {
        var clock = new Clock();
        bool armed = false;
        var run = new Rz090528StockLoadBaseline(new Workload(), _ => Task.FromResult(Healthy with { Failure = failure }),
            () => true, clock, clock.Wait);
        var result = await run.RunAsync(_ => armed = true, CancellationToken.None);
        Assert.False(armed);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public async Task PreflightPermitsSleepingGpuAndZeroIdleFansButCollectionDoesNot()
    {
        var clock = new Clock();
        bool armed = false;
        var run = new Rz090528StockLoadBaseline(new Workload(), _ => Task.FromResult(Healthy with
        { GpuCelsius = null, Fan1Rpm = 0, Fan2Rpm = 0 }), () => true, clock, clock.Wait);
        var result = await run.RunAsync(_ => armed = true, CancellationToken.None);
        Assert.True(armed);
        Assert.Equal("PositiveActiveTemperaturesAndFansRequired", result.Failure);
    }

    [Fact]
    public async Task SlowFirstObservationCannotQualifyCatchUpSamples()
    {
        var clock = new Clock();
        var workload = new Workload { OnObserve = count => { if (count == 1) { clock.Advance(3900); } } };
        var result = await Create(clock, workload, () => Healthy).RunAsync(_ => { }, CancellationToken.None);
        Assert.True(result.Completed);
        Assert.Equal(new double[] { 3900, 5900, 7900 }, result.ArmingSamples.Select(value => value.ElapsedMilliseconds));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(2001, false)]
    public async Task FinalChecksHaveSeparateRecordedBudget(int duration, bool success)
    {
        var clock = new Clock();
        var workload = new Workload { OnObserve = count => { if (count == 19) { clock.Advance(duration); } } };
        var result = await Create(clock, workload, () => Healthy).RunAsync(_ => { }, CancellationToken.None);
        Assert.Equal(success, result.Completed);
        Assert.Equal(30000, result.CollectionElapsedMilliseconds);
        Assert.Equal(duration, result.FinalVerificationElapsedMilliseconds);
    }

    [Fact]
    public async Task CancellationDuringPendingReadAwaitsCompletionBeforeDisposalCanProceed()
    {
        var clock = new Clock();
        var pending = new TaskCompletionSource<StockLoadReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        int reads = 0;
        var run = new Rz090528StockLoadBaseline(new Workload(), _ => ++reads == 1 ? Task.FromResult(Healthy) : pending.Task,
            () => true, clock, clock.Wait);
        Task<StockLoadBaselineResult> resultTask = run.RunAsync(_ => { }, cancel.Token);
        Assert.Equal(2, reads);
        cancel.Cancel();
        Assert.False(resultTask.IsCompleted);
        pending.SetResult(Healthy);
        Assert.Equal("Canceled", (await resultTask).Failure);
    }

    [Fact]
    public async Task CancellationDuringFinalSynchronousChecksCannotReportSuccess()
    {
        var clock = new Clock();
        using var cancel = new CancellationTokenSource();
        var workload = new Workload { OnObserve = count => { if (count == 19) { cancel.Cancel(); } } };
        var result = await Create(clock, workload, () => Healthy).RunAsync(_ => { }, cancel.Token);
        Assert.Equal("Canceled", result.Failure);
    }

    private sealed class Workload : IStockLoadWorkload
    {
        internal string? ArmFailure;
        internal int AbsentReads, LowLoadAfter = int.MaxValue;
        internal Action<int>? OnObserve;
        private int reads;
        public string? Arm(DateTimeOffset armedAtUtc) => ArmFailure;
        public StockLoadWorkloadObservation Observe()
        {
            reads++;
            OnObserve?.Invoke(reads);
            return reads <= AbsentReads ? new(false, null, null)
                : new(true, reads > LowLoadAfter ? 10u : 95u, null);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long milliseconds;
        internal int Extra;
        internal void Advance(int value) => milliseconds += value;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(milliseconds);
        internal Task Wait(TimeSpan duration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            milliseconds += (long)duration.TotalMilliseconds + Extra;
            return Task.CompletedTask;
        }
    }
}
