using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PadPresentHyperBoostSafetyGateTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task ValidateAsync_RequiresFullPowerExactPadAndFreshOpaqueZero()
    {
        var discovery = new Discovery(CoolingPadConnectionState.PresentExact);
        var port = new Port(new(new CoolingPadFanObservation(0), null));
        var factory = new Factory(port);
        var gate = new PadPresentHyperBoostSafetyGate(
            discovery,
            factory,
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new AdapterSession(Convert.FromHexString("1111")),
            default);

        Assert.True(result.Success);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.True(result.CoolingPadPresentConfirmed);
        Assert.True(result.CoolingPadOpaqueZeroConfirmed);
        Assert.Equal(1, discovery.Calls);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.ObserveCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ValidateAsync_BlocksNonFullPowerBeforePadAccess()
    {
        var discovery = new Discovery(CoolingPadConnectionState.PresentExact);
        var factory = new Factory(new Port(new(new CoolingPadFanObservation(0), null)));
        var gate = new PadPresentHyperBoostSafetyGate(
            discovery,
            factory,
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new AdapterSession(Convert.FromHexString("0711")),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired,
            result.Failure);
        Assert.Equal(0, discovery.Calls);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent, 3)]
    [InlineData(CoolingPadConnectionState.Ambiguous, 4)]
    [InlineData(CoolingPadConnectionState.ObservationFailed, 5)]
    public async Task ValidateAsync_FailsClosedUnlessOneExactPadIsPresent(
        CoolingPadConnectionState connection,
        int expectedFailureValue)
    {
        var factory = new Factory(new Port(new(new CoolingPadFanObservation(0), null)));
        var gate = new PadPresentHyperBoostSafetyGate(
            new Discovery(connection),
            factory,
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new AdapterSession(Convert.FromHexString("1111")),
            default);

        Assert.False(result.Success);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.Equal(
            (PerformanceFanValidationSafetyFailure)expectedFailureValue,
            result.Failure);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ValidateAsync_NonzeroOpaqueObservationIsOnlyAVeto()
    {
        var port = new Port(new(new CoolingPadFanObservation(0x2C), null));
        var gate = new PadPresentHyperBoostSafetyGate(
            new Discovery(CoolingPadConnectionState.PresentExact),
            new Factory(port),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new AdapterSession(Convert.FromHexString("1111")),
            default);

        Assert.False(result.Success);
        Assert.True(result.CoolingPadPresentConfirmed);
        Assert.False(result.CoolingPadOpaqueZeroConfirmed);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.CoolingPadOpaqueZeroRequired,
            result.Failure);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ValidateAsync_QueryFailureDoesNotBecomeCompatibilityEvidence()
    {
        var port = new Port(new(default, HidSessionFailureKind.Timeout));
        var gate = new PadPresentHyperBoostSafetyGate(
            new Discovery(CoolingPadConnectionState.PresentExact),
            new Factory(port),
            ExchangeTimeout);

        PerformanceFanValidationSafetyGateResult result = await gate.ValidateAsync(
            new ValidationRunContext(),
            new AdapterSession(Convert.FromHexString("1111")),
            default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.CoolingPadQueryFailed,
            result.Failure);
        Assert.Equal(1, port.DisposeCount);
    }

    private sealed class Discovery(CoolingPadConnectionState state)
        : ICoolingPadDiscoverySource
    {
        internal int Calls { get; private set; }

        public CoolingPadConnectionState Observe()
        {
            Calls++;
            return state;
        }
    }

    private sealed class Factory(IProductionCoolingPadPort port)
        : IProductionCoolingPadPortFactory
    {
        internal int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionCoolingPadPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult(
                new HidSessionResult<IProductionCoolingPadPort>(port, null));
        }
    }

    private sealed class Port(HidSessionResult<CoolingPadFanObservation> result)
        : IProductionCoolingPadPort
    {
        internal int ObserveCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public Task<HidSessionResult<CoolingPadFanObservation>> ObserveFanAsync(
            CancellationToken cancellationToken)
        {
            ObserveCount++;
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdapterSession(byte[] response) : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Assert.Equal((0x07, 0x8C), (command.ClassId, command.CommandId));
            _ = assignTransactionId();
            return Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
