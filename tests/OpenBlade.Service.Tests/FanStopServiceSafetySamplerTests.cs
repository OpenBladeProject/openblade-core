using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopServiceSafetySamplerTests
{
    private static FanStopSettings Custom => FanStopSettings.Default with
    {
        EndMode = FanStopEndMode.Custom,
        CpuLimitCelsius = 55,
        GpuLimitCelsius = 55,
        CpuImmediateCutoffCelsius = 80,
        GpuImmediateCutoffCelsius = 80,
        SecondsAboveLimit = 5,
    };

    [Theory]
    [InlineData("coast wait and native verification", 4)]
    [InlineData("held Stop typed power query", 5)]
    [InlineData("power transition native verification", 3)]
    public async Task NativeOwnerPauseDoesNotInterruptRealSafetyObservations(string operation, int seconds)
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        var releaseNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task nativeOwner = Task.Run(async () => await releaseNative.Task);
        try
        {
            int before = fixture.Source.Reads;
            for (int index = 0; index < seconds; index++)
            {
                await fixture.TickAsync(TimeSpan.FromSeconds(1));
                Assert.True(fixture.Sampler.ReadFresh().Admitted, operation);
                Assert.False(nativeOwner.IsCompleted);
            }
            Assert.Equal(before + seconds, fixture.Source.Reads);
            Assert.Equal(1, fixture.Source.MaximumConcurrentReads);
            Assert.True(fixture.AdmissionReads >= before + seconds);
        }
        finally { releaseNative.TrySetResult(); await nativeOwner; }
    }

    [Fact]
    public async Task DefaultCadenceKeepsProofFreshAcrossNonzeroAcquisitionTime()
    {
        var source = new Source
        {
            ReadOverride = async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700));
                return new(50, null, true, true);
            },
        };
        // Use the production cadence and clock: an extra one-second wait after
        // each 700ms read leaves a 400ms expired-proof interval on every cycle.
        using var sampler = new So6120ServiceSafetySampler(source, Custom, TimeProvider.System, () => new(true));
        try
        {
            Assert.True((await sampler.WaitForInitialAsync()).Admitted);
            var owned = await sampler.BeginOwnedStopAsync();
            Assert.True(owned.Admitted);
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (sampler.Current.Revision < owned.Revision + 3)
            {
                Assert.True(sampler.ReadFresh().Admitted);
                await Task.Delay(20, bound.Token);
            }
            Assert.True(sampler.ReadFresh().Admitted);
            Assert.Equal(1, source.MaximumConcurrentReads);
        }
        finally
        {
            sampler.Dispose();
            await sampler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.False(source.DisposedDuringRead);
    }

    [Fact]
    public async Task EntryAndRepeatedCommandProofReadsCannotCreditTheOwnedAverage()
    {
        using var fixture = new Fixture(Custom with { CpuLimitCelsius = 45 });
        Assert.True((await fixture.Sampler.WaitForInitialAsync()).Admitted);
        for (int index = 0; index < 8; index++) { await fixture.TickAsync(TimeSpan.FromSeconds(1)); }
        await fixture.StartOwnedAsync();
        for (int index = 0; index < 4; index++)
        {
            await fixture.TickAsync(TimeSpan.FromSeconds(1));
            for (int check = 0; check < 50; check++) { Assert.True(fixture.Sampler.ReadFresh().Admitted); }
        }
        await fixture.TickAsync(TimeSpan.FromSeconds(1));
        var ended = fixture.Sampler.ReadFresh();
        Assert.Equal(FanStopEndReason.ThermalThreshold, ended.EndReason);
        Assert.Equal("RollingAverage", ended.ThermalDecision?.Rule);
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, ended.ThermalDecision?.CoverageTicks);
        Assert.Equal(50m, ended.ThermalDecision?.Cpu?.Celsius);
        fixture.Sampler.End(FanStopEndReason.PowerChanged, "Later power change");
        Assert.Same(ended, fixture.Sampler.ReadFresh());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImmediateCutoffLatchesDuringNativePauseAndRetainsTheActualSensor(bool cpu)
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        var blockedNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            fixture.Source.Reading = new(cpu ? 81 : 50, cpu ? 50 : 81, false, false);
            await fixture.TickAsync(TimeSpan.FromMilliseconds(500));
            var ended = fixture.Sampler.ReadFresh();
            Assert.False(blockedNative.Task.IsCompleted);
            Assert.Equal(FanStopEndReason.ThermalThreshold, ended.EndReason);
            Assert.Equal("ImmediateCutoff", ended.ThermalDecision?.Rule);
            Assert.Equal(81m, (cpu ? ended.ThermalDecision?.Cpu : ended.ThermalDecision?.Gpu)?.Celsius);
            fixture.Source.Reading = new(40, null, true, true);
            Assert.Same(ended, fixture.Sampler.ReadFresh());
        }
        finally { blockedNative.TrySetResult(); }
    }

    [Fact]
    public async Task GpuWakeBuildsItsOwnCompleteWindowWhileCpuCoverageContinues()
    {
        using var fixture = new Fixture(Custom with { GpuLimitCelsius = 45 });
        await fixture.StartOwnedAsync();
        for (int index = 0; index < 3; index++) { await fixture.TickAsync(TimeSpan.FromSeconds(1)); }
        fixture.Source.Reading = new(50, 60, false, false);
        await fixture.TickAsync(TimeSpan.FromSeconds(1));
        for (int index = 0; index < 4; index++)
        {
            await fixture.TickAsync(TimeSpan.FromSeconds(1));
            Assert.True(fixture.Sampler.ReadFresh().Admitted);
        }
        await fixture.TickAsync(TimeSpan.FromSeconds(1));
        var ended = fixture.Sampler.ReadFresh();
        Assert.Equal("RollingAverage", ended.ThermalDecision?.Rule);
        Assert.Null(ended.ThermalDecision?.Cpu);
        Assert.Equal(60m, ended.ThermalDecision?.Gpu?.Celsius);
    }

    [Fact]
    public async Task GenuineUnsampledGapEndsStopEvenWhenTheNextSampleIsFreshAndHot()
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        fixture.Source.Reading = new(81, null, true, true);
        await fixture.TickAsync(TimeSpan.FromMilliseconds(2001));
        var ended = fixture.Sampler.ReadFresh();
        Assert.Equal(FanStopEndReason.SafetyCondition, ended.EndReason);
        Assert.Null(ended.ThermalDecision);
    }

    [Fact]
    public async Task ExpiredCommandProofCannotBeRevivedByAnInFlightObservation()
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(2001));
        var ended = fixture.Sampler.ReadFresh();
        Assert.False(ended.Admitted);
        Assert.Equal(FanStopEndReason.SafetyCondition, ended.EndReason);
        fixture.Wake();
        await fixture.Sampler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Same(ended, fixture.Sampler.ReadFresh());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstOwnedSampleFailureReturnsAFalseProofForGuardedAutoRatherThanThrowing(bool thermal)
    {
        using var fixture = new Fixture(Custom);
        Assert.True((await fixture.Sampler.WaitForInitialAsync()).Admitted);
        if (thermal) { fixture.Source.Reading = new(81, null, true, true); }
        else { fixture.Source.Failure = new IOException("Missing owned sample"); }
        Task<So6120ServiceSafetySnapshot> beginning = fixture.Sampler.BeginOwnedStopAsync();
        fixture.Wake();
        var result = await beginning.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(result.Admitted);
        Assert.Equal(thermal ? FanStopEndReason.ThermalThreshold : FanStopEndReason.SafetyCondition, result.EndReason);
        // This is the same false hook consumed by ObserveStoppedCoast before
        // native verification; it requests AutomaticRequired, not an exception.
        Assert.False(fixture.Sampler.ReadFresh().Admitted);
    }

    [Fact]
    public async Task EntryCutoffRefusalIsLatchedBeforeAnyOwnedWindowExists()
    {
        using var fixture = new Fixture(Custom, new(81, null, true, true));
        var refused = await fixture.Sampler.WaitForInitialAsync();
        Assert.False(refused.Owned);
        Assert.False(refused.Admitted);
        Assert.Equal("ImmediateCutoff", refused.ThermalDecision?.Rule);
        fixture.Source.Reading = new(40, null, true, true);
        Assert.Same(refused, await fixture.Sampler.BeginOwnedStopAsync());
    }

    [Fact]
    public async Task VolatileSessionFailureIsLatchedIndependentlyOfNativeOwner()
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        int previousReads = fixture.Source.Reads;
        fixture.Admission = new(false, FanStopEndReason.SessionChanged, "Bound console session changed");
        await fixture.TickAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(FanStopEndReason.SessionChanged, fixture.Sampler.ReadFresh().EndReason);
        Assert.Equal(previousReads, fixture.Source.Reads);
    }

    [Fact]
    public async Task BoundedStopNeverDisposesOrRestartsAnOutstandingSensorRead()
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.ReadOverride = () => { readStarted.TrySetResult(); return new(finishRead.Task); };
        fixture.Wake();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int reads = fixture.Source.Reads;
        fixture.Sampler.Dispose();
        Assert.False(fixture.Source.Disposed);
        Assert.False(fixture.Sampler.Completion.IsCompleted);
        Assert.False(fixture.Sampler.ReadFresh().Admitted);
        finishRead.SetResult(new(40, null, true, true));
        await fixture.Sampler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(fixture.Source.Disposed);
        Assert.False(fixture.Source.DisposedDuringRead);
        Assert.Equal(reads, fixture.Source.Reads);
        Assert.Equal(1, fixture.Source.MaximumConcurrentReads);
    }

    [Fact]
    public async Task SensorDeadlineEndsAdmissionWithoutDisposingOrRetryingAnUncooperativeRead()
    {
        using var fixture = new Fixture(Custom);
        await fixture.StartOwnedAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Source.ReadOverride = () => { started.TrySetResult(); return new(finish.Task); };
        fixture.Wake();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        int reads = fixture.Source.Reads;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            while (fixture.Sampler.Current.EndReason == FanStopEndReason.None)
            { await Task.Delay(10, deadline.Token); }
            Assert.Equal(FanStopEndReason.SafetyCondition, fixture.Sampler.Current.EndReason);
            Assert.False(fixture.Source.Disposed);
            Assert.False(fixture.Sampler.Completion.IsCompleted);
            Assert.Equal(reads, fixture.Source.Reads);
        }
        finally
        {
            finish.TrySetResult(new(40, null, true, true));
            await fixture.Sampler.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(fixture.Source.Disposed);
        Assert.False(fixture.Source.DisposedDuringRead);
        Assert.Equal(1, fixture.Source.MaximumConcurrentReads);
    }

    [Fact]
    public async Task FailedPreparationDisposalWaitsForTheActualPreparationToEnd()
    {
        var source = new Source();
        var preparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task cleanup = So6120ServiceSafetySampler.DisposeSourceAfterAsync(source, preparation.Task);
        Assert.False(source.Disposed);
        preparation.SetException(new IOException("Late preparation failure"));
        await cleanup;
        Assert.True(source.Disposed);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>(new() { SingleReader = true });
        internal readonly Clock Clock = new();
        internal readonly Source Source;
        internal readonly So6120ServiceSafetySampler Sampler;
        internal So6120ServiceVolatileAdmission Admission = new(true);
        internal int AdmissionReads;

        internal Fixture(FanStopSettings settings, FanTemperatureReadings? reading = null)
        {
            Source = new() { Reading = reading ?? new(50, null, true, true) };
            Sampler = new(Source, settings, Clock, () =>
            {
                Interlocked.Increment(ref AdmissionReads);
                return Admission;
            }, async token => { await ticks.Reader.ReadAsync(token); });
        }

        internal void Wake() => Assert.True(ticks.Writer.TryWrite(true));

        internal async Task StartOwnedAsync()
        {
            Assert.True((await Sampler.WaitForInitialAsync()).Admitted);
            Task<So6120ServiceSafetySnapshot> beginning = Sampler.BeginOwnedStopAsync();
            Wake();
            var result = await beginning.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(result.Admitted);
            Assert.True(result.Owned);
        }

        internal async Task TickAsync(TimeSpan elapsed)
        {
            long previous = Sampler.Current.Revision;
            Clock.Advance(elapsed);
            Wake();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Sampler.Current.Revision <= previous && Sampler.Current.EndReason == FanStopEndReason.None)
            { await Task.Delay(1, deadline.Token); }
        }

        public void Dispose() => Sampler.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class Source : IFanTemperatureSource, IDisposable
    {
        internal FanTemperatureReadings Reading = new(50, null, true, true);
        internal Func<ValueTask<FanTemperatureReadings>>? ReadOverride;
        internal Exception? Failure;
        internal int Reads;
        internal int MaximumConcurrentReads;
        private int active;
        internal bool Disposed;
        internal bool DisposedDuringRead;

        public async ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken)
        {
            int concurrent = Interlocked.Increment(ref active);
            MaximumConcurrentReads = Math.Max(MaximumConcurrentReads, concurrent);
            Interlocked.Increment(ref Reads);
            try
            {
                if (Failure is not null) { throw Failure; }
                return ReadOverride is { } read ? await read() : Reading;
            }
            finally { Interlocked.Decrement(ref active); }
        }

        public void Dispose()
        {
            DisposedDuringRead |= Volatile.Read(ref active) != 0;
            Disposed = true;
        }
    }
}
