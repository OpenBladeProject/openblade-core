using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class TemporaryFullChargeQuerySessionTests
{
    [Fact]
    public async Task RunAsync_PerformsExactlyOneTypedQueryAndDisposes()
    {
        var port = new Port(new(false, null));
        var session = new TemporaryFullChargeQuerySession(new Factory(new(port, null)));

        TemporaryFullChargeQueryResult result = await session.RunAsync(default);

        Assert.True(result.Success);
        Assert.False(result.Active);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_MapsOpenAndQueryFailuresWithoutRawData()
    {
        var failedOpen = new TemporaryFullChargeQuerySession(
            new Factory(new(null, HidSessionFailureKind.ConflictBlocked)));

        TemporaryFullChargeQueryResult result = await failedOpen.RunAsync(default);

        Assert.Equal(TemporaryFullChargeQueryFailure.Conflict, result.Failure);
        Assert.Null(result.Active);
    }

    private sealed class Factory(HidSessionResult<ITemporaryFullChargeQueryPort> result)
        : ITemporaryFullChargeQueryRunFactory
    {
        public Task<HidSessionResult<ITemporaryFullChargeQueryPort>> OpenAsync(CancellationToken token) => Task.FromResult(result);
    }

    private sealed class Port(HidSessionResult<bool> result) : ITemporaryFullChargeQueryPort
    {
        internal int QueryCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<bool>> QueryAsync(CancellationToken token) { QueryCount++; return Task.FromResult(result); }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
