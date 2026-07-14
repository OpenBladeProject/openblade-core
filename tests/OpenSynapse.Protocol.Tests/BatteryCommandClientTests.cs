using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class BatteryCommandClientTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);

    public static TheoryData<BatteryValidationSetting, byte> ClosedSettings => new()
    {
        { BatteryValidationSetting.ProtectionOff, 0x50 },
        { BatteryValidationSetting.Limit50, 0xB2 },
        { BatteryValidationSetting.Limit55, 0xB7 },
        { BatteryValidationSetting.Limit60, 0xBC },
        { BatteryValidationSetting.Limit65, 0xC1 },
        { BatteryValidationSetting.Limit70, 0xC6 },
        { BatteryValidationSetting.Limit75, 0xCB },
        { BatteryValidationSetting.Limit80, 0xD0 },
    };

    [Fact]
    public async Task QueryAsync_UsesExactCommandAndForwardsExecutionInputs()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new ScriptedSession(Success([0xD0]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        HidSessionResult<BatteryValidationSetting> result =
            await client.QueryAsync(0x31, cancellation.Token);

        Assert.Null(result.Failure);
        Assert.Equal(BatteryValidationSetting.Limit80, result.Value);
        Call call = Assert.Single(session.Calls);
        Assert.Equal(0x07, call.Command.ClassId);
        Assert.Equal(0x92, call.Command.CommandId);
        Assert.Equal([0x00], call.Command.Arguments.ToArray());
        Assert.Equal(1, call.Command.ExpectedPayloadLength);
        Assert.Equal(0x31, call.AssignTransactionId().TransactionId);
        Assert.Equal(ExchangeTimeout, call.ExchangeTimeout);
        Assert.Equal(cancellation.Token, call.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(ClosedSettings))]
    public async Task SetAsync_UsesExactClosedPayloadAndValidatesEcho(
        BatteryValidationSetting setting,
        byte payload)
    {
        var session = new ScriptedSession(Success([payload]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        HidSessionResult<HidUnit> result = await client.SetAsync(0x42, setting, default);

        Assert.Null(result.Failure);
        Assert.Equal(new HidUnit(), result.Value);
        Call call = Assert.Single(session.Calls);
        Assert.Equal(0x07, call.Command.ClassId);
        Assert.Equal(0x12, call.Command.CommandId);
        Assert.Equal([payload], call.Command.Arguments.ToArray());
        Assert.Equal(1, call.Command.ExpectedPayloadLength);
        Assert.Equal(0x42, call.AssignTransactionId().TransactionId);
    }

    [Theory]
    [InlineData(0)]
    public void QueryAsync_RejectsZeroTransactionBeforeCallingSession(byte transactionId)
    {
        var session = new ScriptedSession(Success([0x50]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        void Act() => _ = client.QueryAsync(transactionId, default);

        Assert.Throws<ArgumentOutOfRangeException>(Act);
        Assert.Empty(session.Calls);
    }

    [Fact]
    public void SetAsync_RejectsZeroTransactionBeforeCallingSession()
    {
        var session = new ScriptedSession(Success([0x50]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        void Act() => _ = client.SetAsync(0, BatteryValidationSetting.ProtectionOff, default);

        Assert.Throws<ArgumentOutOfRangeException>(Act);
        Assert.Empty(session.Calls);
    }

    [Fact]
    public void SetAsync_RejectsUndefinedSettingBeforeCallingSession()
    {
        var session = new ScriptedSession(Success([0x50]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        void Act() => _ = client.SetAsync(1, (BatteryValidationSetting)999, default);

        Assert.Throws<ArgumentOutOfRangeException>(Act);
        Assert.Empty(session.Calls);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xB2, 0x00 })]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0xB3 })]
    [InlineData(new byte[] { 0xFF })]
    public async Task QueryAsync_ReturnsProtocolFailureForInvalidPayload(byte[] payload)
    {
        var client = new BatteryCommandClient(new ScriptedSession(Success(payload)), ExchangeTimeout);

        HidSessionResult<BatteryValidationSetting> result = await client.QueryAsync(1, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(default, result.Value);
        Assert.Equal(
            new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.ProtocolValidation,
                HidSessionFailureKind.ProtocolFailure,
                NativeWin32Error: null,
                TransferredByteCount: null),
            result.Diagnostic);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xB2, 0x00 })]
    [InlineData(new byte[] { 0xB3 })]
    public async Task SetAsync_ReturnsProtocolFailureForIncorrectAck(byte[] payload)
    {
        var client = new BatteryCommandClient(new ScriptedSession(Success(payload)), ExchangeTimeout);

        HidSessionResult<HidUnit> result =
            await client.SetAsync(1, BatteryValidationSetting.Limit50, default);

        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public async Task SetAsync_AcceptsEchoAsAckWithoutReadback()
    {
        var session = new ScriptedSession(Success([0xC6]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        HidSessionResult<HidUnit> result =
            await client.SetAsync(1, BatteryValidationSetting.Limit70, default);

        Assert.Null(result.Failure);
        Assert.Equal(new HidUnit(), result.Value);
        Assert.Single(session.Calls);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.IdentityBlocked)]
    [InlineData(HidSessionFailureKind.Canceled)]
    [InlineData(HidSessionFailureKind.NativeIoFailure)]
    public async Task QueryAsync_PropagatesSessionFailure(HidSessionFailureKind failure)
    {
        var client = new BatteryCommandClient(new ScriptedSession(Failed(failure)), ExchangeTimeout);

        HidSessionResult<BatteryValidationSetting> result = await client.QueryAsync(1, default);

        Assert.Equal(failure, result.Failure);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public async Task QueryAsync_PropagatesSessionDiagnostic()
    {
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 31,
            TransferredByteCount: 12);
        HidSessionResult<ReadOnlyMemory<byte>> failed = Failed(
            HidSessionFailureKind.NativeIoFailure) with
        {
            Diagnostic = diagnostic,
        };
        var client = new BatteryCommandClient(new ScriptedSession(failed), ExchangeTimeout);

        HidSessionResult<BatteryValidationSetting> result = await client.QueryAsync(1, default);

        Assert.Equal(diagnostic, result.Diagnostic);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.Timeout)]
    [InlineData(HidSessionFailureKind.Poisoned)]
    [InlineData(HidSessionFailureKind.Disposed)]
    public async Task SetAsync_PropagatesSessionFailure(HidSessionFailureKind failure)
    {
        var client = new BatteryCommandClient(new ScriptedSession(Failed(failure)), ExchangeTimeout);

        HidSessionResult<HidUnit> result =
            await client.SetAsync(1, BatteryValidationSetting.Limit50, default);

        Assert.Equal(failure, result.Failure);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public async Task SetAsync_ForwardsTimeoutAndCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new ScriptedSession(Success([0xB2]));
        var client = new BatteryCommandClient(session, ExchangeTimeout);

        await client.SetAsync(1, BatteryValidationSetting.Limit50, cancellation.Token);

        Call call = Assert.Single(session.Calls);
        Assert.Equal(ExchangeTimeout, call.ExchangeTimeout);
        Assert.Equal(cancellation.Token, call.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(InvalidTimeouts))]
    public void Constructor_RejectsTimeoutOutsideActorRange(TimeSpan timeout)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BatteryCommandClient(new ScriptedSession(Success([0x50])), timeout));
    }

    public static TheoryData<TimeSpan> InvalidTimeouts => new()
    {
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(-2),
        Timeout.InfiniteTimeSpan,
        TimeSpan.FromMilliseconds(uint.MaxValue),
    };

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(byte[] payload) =>
        new(payload, null);

    private static HidSessionResult<ReadOnlyMemory<byte>> Failed(HidSessionFailureKind failure) =>
        new(new byte[] { 0xD0 }, failure);

    private sealed class ScriptedSession(params HidSessionResult<ReadOnlyMemory<byte>>[] results)
        : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results = new(results);

        internal List<Call> Calls { get; } = [];

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(new(command, assignTransactionId, exchangeTimeout, cancellationToken));
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed record Call(
        RazerCommandSpec Command,
        Func<TransactionIdAssignment> AssignTransactionId,
        TimeSpan ExchangeTimeout,
        CancellationToken CancellationToken);
}
