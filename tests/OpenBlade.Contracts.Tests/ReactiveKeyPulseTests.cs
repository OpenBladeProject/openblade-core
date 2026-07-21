using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ReactiveKeyPulseTests
{
    [Fact]
    public void PulseCarriesOnlyAnonymousMatrixSlotAndMonotonicTiming()
    {
        var pulse = new ReactiveKeyPulse(77, observedTimestamp: 12345);

        Assert.Equal(77, pulse.MatrixSlot);
        Assert.Equal(12345, pulse.ObservedTimestamp);
        Assert.Equal(
            [
                nameof(ReactiveKeyPulse.MatrixSlot),
                nameof(ReactiveKeyPulse.ObservedTimestamp),
            ],
            typeof(ReactiveKeyPulse)
                .GetProperties()
                .Where(property => property.GetMethod?.IsStatic == false)
                .Select(property => property.Name)
                .ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void PulseAcceptsCapturedMatrixBounds(byte slot)
    {
        Assert.Equal(slot, new ReactiveKeyPulse(slot).MatrixSlot);
    }
}
