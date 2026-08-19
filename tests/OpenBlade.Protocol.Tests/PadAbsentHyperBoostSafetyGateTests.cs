using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PadAbsentHyperBoostSafetyGateTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData("1111", (int)AdapterPowerState.FullPower280W)]
    [InlineData("1211", (int)AdapterPowerState.FullPower330W)]
    public async Task ValidateAsync_RequiresAdmittedFullPowerThenFinalPhysicalAbsence(
        string responseHex,
        int expectedStateValue)
    {
        var presence = new ScriptedPresence(
            CoolingPadPhysicalPresenceObservation.Absent);
        var session = new ScriptedSession(Convert.FromHexString(responseHex));
        var gate = new PadAbsentHyperBoostSafetyGate(presence, ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            session,
            default);

        Assert.True(result.Success);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.Equal((AdapterPowerState)expectedStateValue, result.AdapterPowerState);
        Assert.True(result.CoolingPadAbsentConfirmed);
        Assert.Equal(PerformanceFanValidationSafetyFailure.None, result.Failure);
        Assert.Equal(1, presence.Calls);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x07, 0x8C), (command.ClassId, command.CommandId));
    }

    [Fact]
    public async Task ValidateAsync_BlocksNonFullPowerBeforePresenceCheck()
    {
        var presence = new ScriptedPresence(
            CoolingPadPhysicalPresenceObservation.Absent);
        var gate = new PadAbsentHyperBoostSafetyGate(
            presence,
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new ScriptedSession(Convert.FromHexString("0711")),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired,
            result.Failure);
        Assert.Equal(0, presence.Calls);
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    public async Task ValidateAsync_FailsClosedWhenFinalAbsenceIsNotProven(
        int observationValue,
        int expectedFailureValue)
    {
        CoolingPadPhysicalPresenceObservation observation =
            (CoolingPadPhysicalPresenceObservation)observationValue;
        PerformanceFanValidationSafetyFailure expectedFailure =
            (PerformanceFanValidationSafetyFailure)expectedFailureValue;
        var gate = new PadAbsentHyperBoostSafetyGate(
            new ScriptedPresence(observation),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new ScriptedSession(Convert.FromHexString("1111")),
            default);

        Assert.False(result.Success);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.False(result.CoolingPadAbsentConfirmed);
        Assert.Equal(expectedFailure, result.Failure);
    }

    [Fact]
    public async Task ValidateAsync_MapsAdapterFailureWithoutCheckingPresence()
    {
        var presence = new ScriptedPresence(
            CoolingPadPhysicalPresenceObservation.Absent);
        var gate = new PadAbsentHyperBoostSafetyGate(presence, ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new ScriptedSession(HidSessionFailureKind.Timeout),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed,
            result.Failure);
        Assert.Equal(0, presence.Calls);
    }

    private sealed class ScriptedPresence(
        CoolingPadPhysicalPresenceObservation observation)
        : ICoolingPadPhysicalPresenceSource
    {
        public int Calls { get; private set; }

        public CoolingPadPhysicalPresenceObservation Observe()
        {
            Calls++;
            return observation;
        }
    }

    private sealed class ScriptedSession : IRazerCommandSession
    {
        private readonly HidSessionResult<ReadOnlyMemory<byte>> result;

        internal ScriptedSession(byte[] response)
        {
            result = new(response, null);
        }

        internal ScriptedSession(HidSessionFailureKind failure)
        {
            result = new(default, failure);
        }

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            _ = assignTransactionId();
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
