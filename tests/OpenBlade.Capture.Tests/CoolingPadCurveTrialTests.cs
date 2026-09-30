using OpenBlade.Capture;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadCurveTrialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FiniteSequenceReservesNineteenIds_AndSuppressesOnlyUnchangedAcknowledgedDemand(bool changes)
    {
        var h = new Harness();
        h.Source.Read = (_, index) => ValueTask.FromResult(new FanTemperatureReadings(
            changes && index > 2 ? 40 + (index - 2) * 4 : 40, null, true));
        var result = await h.Run();
        Assert.True(result.Success);
        Assert.False(result.ManualRecoveryRequired || result.SubmissionStateUnknown);
        Assert.Equal(12, result.Samples.Count);
        Assert.Equal(12, h.Source.Reads);
        Assert.Equal(10, h.DelayCount);
        Assert.Equal(1, h.FactoryCalls);
        Assert.Equal(1, h.Session.Disposals);
        Assert.Equal(1, h.Source.Disposals);
        Assert.True(result.TemperatureSourceDisposalConfirmed);
        Assert.Equal(changes ? 19 : 9, h.Session.Commands.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, h.Session.Transactions.Take(5));
        Assert.Equal(new byte[] { 16, 17, 18, 19 }, h.Session.Transactions.TakeLast(4));
        Assert.Equal(new[] { 5, changes ? 15 : 5, changes ? 16 : 6, changes ? 17 : 7, changes ? 19 : 9 }, h.PhysicalCounts);
        Assert.Equal("0D:10:000600", Describe(h.Session.Commands[3]));
        Assert.Equal("0D:01:010514", Describe(h.Session.Commands[4]));
        Assert.All(h.Session.Commands.Where(c => c.CommandId == 0x01 && c.ClassId == 0x0D), c => Assert.NotNull(c.SubmissionAllowed));
        Assert.NotNull(h.Session.Commands[3].SubmissionAllowed);
        Assert.False(result.Samples[0].SubmissionAttempted);
        Assert.True(result.Samples[1].SubmissionAttempted && result.Samples[1].Acknowledged);
        Assert.All(result.Samples.Skip(2), s => Assert.Equal(!changes, s.SuppressedUnchanged));
        Assert.All(result.Samples, s => Assert.InRange(s.PlannedConfiguredDemandRpm!.Value, 1000, 2200));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task AgeExpiringInsideControllerGuardBlocksNativeSet(int exchange)
    {
        var h = new Harness();
        h.Source.Read = (_, index) => ValueTask.FromResult(new FanTemperatureReadings(index >= 3 ? 60 : 40, null, true));
        h.Session.BeforeAdmission = index => { if (index == exchange) { h.Clock.Advance(TimeSpan.FromSeconds(5)); } };
        var result = await h.Run();
        Assert.Equal("ConflictBlocked", result.Failure);
        Assert.Equal(exchange, h.Session.Commands.Count);
        Assert.Equal(exchange - 1, h.Session.NativeSetCount);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.FixedAttempted || result.AutoAttempted || result.ModeRestoreAttempted);
        Assert.Equal(1, h.Session.Disposals);
    }

    [Fact]
    public async Task EntryAcknowledgementAgingSampleDoesNotRefreshOrSubmitFirstDemand()
    {
        var h = new Harness();
        h.Session.AfterResponse = count => { if (count == 4) { h.Clock.Advance(TimeSpan.FromSeconds(5)); } };
        var result = await h.Run();
        Assert.Equal("StaleSample", result.Failure);
        Assert.True(result.DemandEntryAcknowledged);
        Assert.False(result.Samples[1].SubmissionAttempted);
        Assert.Equal(2, h.Source.Reads);
        Assert.Equal(4, h.Session.Commands.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingTelemetryStopsBeforeAnyFurtherWrite(int failedRead)
    {
        var h = new Harness();
        h.Source.Read = (_, count) => ValueTask.FromResult(count == failedRead
            ? new FanTemperatureReadings(null, null) : new FanTemperatureReadings(40, null, true));
        var result = await h.Run();
        Assert.Equal("MissingCpu", result.Failure);
        Assert.Equal(failedRead == 1 ? 0 : 1, h.FactoryCalls);
        Assert.Equal(failedRead == 1 ? 0 : failedRead == 2 ? 3 : 5, h.Session.Commands.Count);
        Assert.Equal(failedRead != 1, result.ManualRecoveryRequired);
        Assert.False(result.FixedAttempted || result.AutoAttempted);
    }

    [Fact]
    public async Task SlowPreflightCannotOpenActor()
    {
        var h = new Harness();
        h.Source.Read = (_, _) =>
        { h.Clock.Advance(TimeSpan.FromSeconds(5)); return ValueTask.FromResult(new FanTemperatureReadings(40, null, true)); };
        var result = await h.Run();
        Assert.Equal("StaleSample", result.Failure);
        Assert.Equal(0, h.FactoryCalls);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task AmbiguousDemandResponseStopsWithoutSuppressionOrRestoration(int index)
    {
        var h = new Harness();
        h.Source.Read = (_, count) => ValueTask.FromResult(new FanTemperatureReadings(count >= 3 ? 60 : 40, null, true));
        h.Session.BadEchoAt = index;
        var result = await h.Run();
        Assert.Equal("UnexpectedPayload", result.Failure);
        Assert.Equal(index, h.Session.Commands.Count);
        Assert.True(result.SubmissionStateUnknown && result.ManualRecoveryRequired);
        Assert.True(result.Samples[^1].SubmissionAttempted);
        Assert.False(result.Samples[^1].Acknowledged || result.Samples[^1].SuppressedUnchanged);
        Assert.False(result.FixedAttempted || result.AutoAttempted);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 6)]
    [InlineData(4, 7)]
    [InlineData(5, 9)]
    public async Task RefusedPhysicalGateStopsFirmwareWrites(int rejected, int commands)
    {
        var h = new Harness { RejectPhysical = rejected };
        var result = await h.Run();
        Assert.Equal("OperatorRejected", result.Failure);
        Assert.Equal(commands, h.Session.Commands.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task CanceledBlockedTelemetryReleasesActorBeforeTelemetry_AndCannotResume()
    {
        var h = new Harness();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<FanTemperatureReadings>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Source.Read = (_, count) =>
        {
            if (count != 3) { return ValueTask.FromResult(new FanTemperatureReadings(40, null, true)); }
            entered.SetResult();
            return new ValueTask<FanTemperatureReadings>(late.Task);
        };
        var run = h.Run(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await h.Session.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.Source.Disposals);
        late.SetResult(new(90, null, true));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Canceled", result.Failure);
        Assert.Equal(1, h.Source.Disposals);
        Assert.Equal(5, h.Session.Commands.Count);
        Assert.Equal(2, result.Samples.Count);
        Assert.True(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task WrongHostCannotConstructTemperatureSourceOrActor()
    {
        int sourceCalls = 0;
        var runner = new CoolingPadCurveTrialRunner(() => new(null, HidSessionFailureKind.IdentityBlocked),
            () => { sourceCalls++; throw new Exception(); }, () => throw new Exception(),
            () => new ValidationRunContext(), TimeProvider.System);
        var result = await runner.RunAsync(Yes, Yes, Yes, Yes, Yes, default);
        Assert.Equal("HostIdentityBlocked", result.Failure);
        Assert.Equal(0, sourceCalls);
        Assert.False(result.ManualRecoveryRequired);
    }

    private static Task<bool> Yes(CancellationToken _) => Task.FromResult(true);
    private static string Describe(RazerCommandSpec c) => $"{c.ClassId:X2}:{c.CommandId:X2}:{Convert.ToHexString(c.Arguments.Span)}";
    private sealed class Harness
    {
        internal Clock Clock { get; } = new();
        internal Session Session { get; } = new();
        internal Source Source { get; } = new();
        internal int FactoryCalls;
        internal int DelayCount;
        internal int RejectPhysical;
        internal List<int> PhysicalCounts { get; } = [];
        internal Task<CoolingPadCurveTrialResult> Run(CancellationToken token = default)
        {
            var runner = new CoolingPadCurveTrialRunner(
                () => new(new("Razer", "Blade 16 - RZ09-0528", "RZ09-05289EN4", "future"), null),
                () => Source,
                () => { Assert.Equal(1, Source.Reads); FactoryCalls++; return new Factory(Session); },
                () => new ValidationRunContext(), Clock, delay: (duration, cancellation) =>
                { Assert.Equal(TimeSpan.FromSeconds(2), duration); DelayCount++; Clock.Advance(duration); return Task.CompletedTask; });
            return runner.RunAsync(Observe, Observe, Observe, Observe, Observe, token);
        }
        private Task<bool> Observe(CancellationToken token)
        {
            PhysicalCounts.Add(Session.Commands.Count);
            // A long human pause must not reuse the previous sample.
            Clock.Advance(TimeSpan.FromSeconds(30));
            return Task.FromResult(PhysicalCounts.Count != RejectPhysical);
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }
    private sealed class Source : IFanTemperatureSource, IDisposable
    {
        internal Func<CancellationToken, int, ValueTask<FanTemperatureReadings>> Read { get; set; } =
            (_, _) => ValueTask.FromResult(new FanTemperatureReadings(40, null, true));
        internal int Reads;
        internal int Disposals;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token) => Read(token, Interlocked.Increment(ref Reads));
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private sealed class Factory(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken token) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal List<RazerCommandSpec> Commands { get; } = [];
        internal List<byte> Transactions { get; } = [];
        internal Action<int>? BeforeAdmission;
        internal Action<int>? AfterResponse;
        internal int NativeSetCount;
        internal int BadEchoAt;
        internal int Disposals;
        private byte raw;
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> transaction, TimeSpan timeout, CancellationToken token)
        {
            Commands.Add(command);
            Transactions.Add(transaction().TransactionId);
            BeforeAdmission?.Invoke(Commands.Count);
            if (command.SubmissionAllowed?.Invoke() == false)
            { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked)); }
            NativeSetCount++;
            if (command.ClassId == 0 && command.CommandId == 4) { raw = command.Arguments.Span[0]; }
            byte[] response = command.ClassId == 0 && command.CommandId == 0x84 ? [raw, 0] : command.Arguments.ToArray();
            Assert.True(command.PayloadValidator!(response));
            AfterResponse?.Invoke(Commands.Count);
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(Commands.Count == BadEchoAt ? new byte[] { 255 } : response, null));
        }
        public ValueTask DisposeAsync() { Disposals++; Disposed.TrySetResult(); return ValueTask.CompletedTask; }
    }
}
