using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ReactiveKeyPulseTests
{
    [Fact]
    public void PulseCarriesOnlyAnAnonymousMatrixSlot()
    {
        var pulse = new ReactiveKeyPulse(77);

        Assert.Equal(77, pulse.MatrixSlot);
        Assert.Equal(
            [nameof(ReactiveKeyPulse.MatrixSlot)],
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
