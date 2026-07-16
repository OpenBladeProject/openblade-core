using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class FanRpmQuerySessionTests
{
    [Fact]
    public async Task RunAsync_PerformsOneClosedTypedQueryAndDisposes()
    {
        var port = new Port(new(new FanRpmReadings(3200, 3000), null));
        var factory = new Factory(new(port, null));
        var session = new FanRpmQuerySession(factory);

        FanRpmQueryResult result = await session.RunAsync(default);

        Assert.True(result.Success);
        Assert.Equal(3200, result.Readings!.Selector1Rpm);
        Assert.Equal(3000, result.Readings.Selector2Rpm);
        Assert.Equal(3200, result.Readings.MaxRpm);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_MapsExactIdentityFailureWithoutQuerying()
    {
        var factory = new Factory(new(null, HidSessionFailureKind.IdentityBlocked));
        var session = new FanRpmQuerySession(factory);

        FanRpmQueryResult result = await session.RunAsync(default);

        Assert.False(result.Success);
        Assert.Null(result.Readings);
        Assert.Equal(FanRpmQueryFailure.WrongTarget, result.Failure);
        Assert.Equal(1, factory.OpenCount);
    }

    private sealed class Factory(HidSessionResult<IFanRpmQueryPort> result)
        : IFanRpmQueryRunFactory
    {
        internal int OpenCount { get; private set; }

        public Task<HidSessionResult<IFanRpmQueryPort>> OpenAsync(CancellationToken token)
        {
            OpenCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class Port(HidSessionResult<FanRpmReadings> result) : IFanRpmQueryPort
    {
        internal int QueryCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public Task<HidSessionResult<FanRpmReadings>> QueryAsync(CancellationToken token)
        {
            QueryCount++;
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
