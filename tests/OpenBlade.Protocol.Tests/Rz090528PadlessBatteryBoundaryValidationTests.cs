namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528PadlessBatteryBoundaryValidationTests
{
    [Theory]
    [InlineData(2, true)]
    [InlineData(7, false)]
    public async Task CoherentBatteryAndStableAcSelectNoOpOrExistingRestore(int returnMode, bool noOp)
    {
        var f = new Fixture(Snapshot(3, false), Snapshot(returnMode), Snapshot(returnMode));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.True(result.TransitionPassed);
        Assert.Equal(noOp, result.AlreadyPerformance);
        Assert.Equal(!noOp, result.PermitRestore);
        Assert.Equal(new[] { Rz090528BatteryBoundarySignal.ReadyToUnplug, Rz090528BatteryBoundarySignal.BatteryObservedReconnectNow }, f.Signals);
        Assert.True(f.Run.Result!.AutomaticReversionObserved);
        Assert.False(f.Source.Poisoned);
        Assert.True(f.Run.Result.Observations[^1].ElapsedMilliseconds - f.Run.Result.Observations[^2].ElapsedMilliseconds >= 1000);
    }

    [Fact]
    public async Task MissingBatteryGpuStillSignalsReconnectAndCanRestoreWithFreshWindowsPower()
    {
        var f = new Fixture(Snapshot(3, false), Snapshot(7), Snapshot(7)) { MissingBatteryGpu = true };
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.TransitionPassed);
        Assert.True(result.PermitRestore);
        Assert.Contains(Rz090528BatteryBoundarySignal.BatteryObservedReconnectNow, f.Signals);
        Assert.False(f.Source.Poisoned);
    }

    [Fact]
    public async Task ChangingFullAcModesNeverAuthorizesCleanup()
    {
        var f = new Fixture(Snapshot(7, false), Snapshot(7), Snapshot(2));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.False(result.AlreadyPerformance);
        Assert.True(f.Source.Poisoned);
        Assert.Equal("FullAcModeChanged", f.Run.Result!.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task FullAcUnsupportedModePoisonsWithoutFurtherRead(int mode)
    {
        var f = new Fixture(Snapshot(mode));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.True(f.Source.Poisoned);
        Assert.Equal(3, f.Source.Reads);
    }

    [Fact]
    public async Task PlugEventInsideBracketIsDiscardedWithoutPoisonAndResetsStableAcWindow()
    {
        var f = new Fixture(Snapshot(7) with { PowerAfter = "0011" }, Snapshot(3, false),
            Snapshot(7) with { PowerBefore = "0011" }, Snapshot(7), Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.True(result.TransitionPassed);
        Assert.True(result.PermitRestore);
        Assert.False(f.Source.Poisoned);
        Assert.Equal(7, f.Source.Reads);
        Assert.Equal(Rz090528PadlessBatteryBoundaryValidation.Transitional, f.Run.Result!.Observations[2].Failure);
        Assert.Equal(Rz090528PadlessBatteryBoundaryValidation.Transitional, f.Run.Result.Observations[4].Failure);
    }

    [Fact]
    public async Task PersistentMixedPairNeverAuthorizesCleanupAndDoesNotPoison()
    {
        var f = new Fixture(Snapshot(7) with { Pair = new("01010700", "01020300") });
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.False(result.AlreadyPerformance);
        Assert.False(f.Source.Poisoned);
        Assert.True(f.Run.Result!.ManualRecoveryRequired);
        Assert.Equal("NoPowerTransition", f.Run.Result.Failure);
    }

    [Fact]
    public async Task WindowsAndFirmwarePowerDisagreementIsTransitional()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.WindowsOverride = false;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.False(f.Source.Poisoned);
        Assert.All(f.Run.Result!.Observations.Skip(2),
            value => Assert.Equal(Rz090528PadlessBatteryBoundaryValidation.Transitional, value.Failure));
    }

    [Fact]
    public async Task TripwireOnTransitionalSampleExitsFromNextFullAcSample()
    {
        var f = new Fixture(Snapshot(7) with { PowerAfter = "0011" }, Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.Cpu = 75;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.True(result.PermitRestore);
        Assert.False(result.TransitionPassed);
        Assert.Equal("ExperimentalTemperatureTripwire", f.Run.Result!.Failure);
        Assert.Equal(6, f.Source.Reads);
        Assert.Equal(new[] { Rz090528BatteryBoundarySignal.ReadyToUnplug, Rz090528BatteryBoundarySignal.AbortReconnectAc }, f.Signals);
    }

    [Fact]
    public async Task ModeZeroChannelPoisonsEvenInsidePlugEventBracket()
    {
        var f = new Fixture(Snapshot(7) with { PowerAfter = "0011", Pair = new("01010000", "01020700") }, Snapshot(7), Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.True(f.Source.Poisoned);
        Assert.Equal("PowerThermalOrExternalStateConflict", f.Run.Result!.Failure);
        Assert.Contains(Rz090528BatteryBoundarySignal.AbortReconnectAc, f.Signals);
    }

    [Fact]
    public async Task TripwireReasonIsPreservedThroughLaterDeadline()
    {
        var f = new Fixture(Snapshot(3, false));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.Cpu = 80;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.Equal("ExperimentalTemperatureTripwire", f.Run.Result!.Failure);
        Assert.Single(f.Signals, Rz090528BatteryBoundarySignal.AbortReconnectAc);
    }

    [Fact]
    public async Task BatteryModeZeroStillPoisons()
    {
        var f = new Fixture(Snapshot(0, false));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        Assert.False((await f.Run.RunAsync(f.Source, default)).PermitRestore);
        Assert.True(f.Source.Poisoned);
    }

    [Fact]
    public async Task NoUnplugFailsButUsesTwoFreshAcSnapshotsForCleanup()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.TransitionPassed);
        Assert.True(result.PermitRestore);
        Assert.Equal("NoPowerTransition", f.Run.Result!.Failure);
        Assert.False(f.Run.Result.BatteryObserved);
        Assert.False(f.Run.Result.AutomaticReversionObserved);
        Assert.True(f.Run.Result.Observations[^1].ElapsedMilliseconds - f.Run.Result.Observations[^2].ElapsedMilliseconds >= 1000);
    }

    [Fact]
    public async Task CanceledBoundaryUsesIndependentTwoSampleCleanup()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await f.Run.RunAsync(f.Source, cancel.Token);
        Assert.True(result.PermitRestore);
        Assert.False(result.TransitionPassed);
        Assert.Equal(4, f.Source.Reads);
    }

    [Fact]
    public async Task ThermalTripwireOnAcImmediatelyEntersIndependentCleanup()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.Cpu = 75;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.True(result.PermitRestore);
        Assert.False(result.TransitionPassed);
        Assert.Equal("ExperimentalTemperatureTripwire", f.Run.Result!.Failure);
        Assert.Equal(5, f.Source.Reads);
        Assert.Equal(1000, f.Run.Result.Observations[^1].ElapsedMilliseconds);
    }

    [Fact]
    public async Task ActualExternalWorkerStillRunningPreventsAnyBoundaryReadOrCleanup()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.ExternalQuiesced = false;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.Equal(2, f.Source.Reads);
        Assert.False(f.Run.Result!.ExternalReadQuiesced);
    }

    [Fact]
    public async Task DeadlineAfterExternalReadDoesNotDispatchNativeUntilSeparateCleanup()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        f.OnExternal = () => f.Clock.Advance(TimeSpan.FromSeconds(30));
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.True(result.PermitRestore);
        Assert.Equal(4, f.Source.Reads); // two entry + two independent cleanup; no boundary native read
        Assert.Equal("BoundaryDeadlineExceeded", f.Run.Result!.Failure);
    }

    [Fact]
    public async Task PendingNativeTimeoutPoisonsAndLateCompletionCannotUpgradeDecision()
    {
        var f = new Fixture(Snapshot(7));
        Assert.True(await f.Run.PrepareAsync(f.Source, default));
        var pending = new TaskCompletionSource<Rz090528BatteryBoundarySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.Pending = pending.Task;
        var result = await f.Run.RunAsync(f.Source, default);
        Assert.False(result.PermitRestore);
        Assert.True(f.Source.Poisoned);
        Assert.False(f.Run.Result!.SnapshotQuiesced);
        pending.SetResult(Snapshot(7));
        await pending.Task;
        Assert.False(result.PermitRestore);
        Assert.Equal(3, f.Source.Reads);
    }

    [Theory]
    [InlineData(71, 60, 0)]
    [InlineData(60, 73, 0)]
    [InlineData(60, 60, 11)]
    public async Task EntryExperimentalEnvelopeRejectsBeforeApply(double cpu, int gpu, uint utilization)
    {
        var f = new Fixture(Snapshot(7)) { Cpu = cpu, Gpu = gpu, Utilization = utilization };
        Assert.False(await f.Run.PrepareAsync(f.Source, default));
        Assert.Empty(f.Signals);
    }

    private static Rz090528BatteryBoundarySnapshot Snapshot(int mode, bool ac = true) =>
        new(ac ? "1111" : "0011", new($"0101{mode:X2}00", $"0102{mode:X2}00"), ac ? "1111" : "0011", true, null);

    private sealed class Fixture
    {
        internal readonly Rz090528PadlessNoLoadValidationTests.Clock Clock = new();
        internal readonly Source Source;
        internal readonly List<Rz090528BatteryBoundarySignal> Signals = [];
        internal readonly Rz090528PadlessBatteryBoundaryValidation Run;
        internal bool ExternalQuiesced = true, MissingBatteryGpu;
        internal double Cpu = 60;
        internal int Gpu = 60;
        internal uint Utilization;
        internal Action? OnExternal;
        internal bool? WindowsOverride;
        private int externalReads;
        internal Fixture(params Rz090528BatteryBoundarySnapshot[] samples)
        {
            Source = new([Snapshot(2), Snapshot(2), .. samples]);
            Run = new(Read, Signals.Add, Clock, Clock.Wait, () => ExternalQuiesced,
                () => WindowsOverride ?? Source.Peek.PowerBefore == "1111");
        }
        private ValueTask<Rz090528BatteryBoundaryExternal> Read(CancellationToken token)
        {
            OnExternal?.Invoke(); OnExternal = null;
            bool ac = (externalReads++ % 2 == 0 ? Source.Peek : Source.Last).PowerBefore == "1111";
            bool missing = MissingBatteryGpu && !ac;
            return ValueTask.FromResult(new Rz090528BatteryBoundaryExternal(Clock.GetUtcNow(), 0, Cpu,
                missing ? null : Gpu, Utilization, WindowsOverride ?? ac, true, !missing, missing ? "GpuUnavailable" : null));
        }
    }
    private sealed class Source(Rz090528BatteryBoundarySnapshot[] samples) : IRz090528BatteryBoundarySource
    {
        internal int Reads;
        internal bool Poisoned;
        internal Task<Rz090528BatteryBoundarySnapshot>? Pending;
        internal Rz090528BatteryBoundarySnapshot Peek => samples[Math.Min(Reads, samples.Length - 1)];
        internal Rz090528BatteryBoundarySnapshot Last => samples[Math.Min(Math.Max(0, Reads - 1), samples.Length - 1)];
        public Task<Rz090528BatteryBoundarySnapshot> ReadBatteryBoundaryAsync(CancellationToken token)
        {
            var sample = Peek; Reads++; return Pending ?? Task.FromResult(sample);
        }
        public void MarkSnapshotUncertain() => Poisoned = true;
    }
}
