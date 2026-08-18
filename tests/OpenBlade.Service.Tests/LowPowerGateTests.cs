using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class LowPowerGateTests
{
    [Fact]
    public void OperationPermit_IsInvalidatedByLowPowerTransition()
    {
        var gate = new LowPowerGate();

        Assert.True(gate.TryAcquireOperation(out LowPowerOperationPermit permit));
        Assert.True(gate.IsCurrent(permit));

        Assert.True(gate.Enter());
        Assert.False(gate.IsCurrent(permit));
        Assert.False(gate.TryAcquireOperation(out _));

        Assert.True(gate.Exit());
        Assert.False(gate.IsCurrent(permit));
        Assert.True(gate.TryAcquireOperation(out LowPowerOperationPermit resumed));
        Assert.True(gate.IsCurrent(resumed));
    }
}
