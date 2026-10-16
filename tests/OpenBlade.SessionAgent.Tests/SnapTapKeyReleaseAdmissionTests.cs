using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SnapTapKeyReleaseAdmissionTests
{
    [Fact]
    public void AzertyHeldPhysicalPosition_BlocksEvenWhenVirtualAIsReleased()
    {
        var observed = new List<int>();
        bool released = SnapTapKeyReleaseAdmission.AreReleased(
            scan => scan == 0x1E ? 0x51u : 0x44u,
            key =>
            {
                observed.Add(key);
                return key == 0x51 ? unchecked((short)0x8000) : (short)0;
            });
        Assert.False(released);
        Assert.Contains(0x51, observed);
        Assert.DoesNotContain(0x41, observed);
    }

    [Theory]
    [InlineData(0u, 0x44u)]
    [InlineData(0x41u, 0u)]
    [InlineData(256u, 0x44u)]
    [InlineData(0x41u, 0x41u)]
    public void UnresolvedOrAliasedMapping_DoesNotReadOrArm(uint first, uint second)
    {
        Assert.False(SnapTapKeyReleaseAdmission.AreReleased(
            scan => scan == 0x1E ? first : second,
            _ => throw new InvalidOperationException("Invalid mappings must not query state.")));
    }

    [Fact]
    public void BothResolvedPositionsReleased_AdmitsRegardlessOfUnrelatedHeldKey()
    {
        Assert.True(SnapTapKeyReleaseAdmission.AreReleased(
            scan => scan == 0x1E ? 0x51u : 0x44u,
            key => key == 0x41 ? unchecked((short)0x8000) : (short)0));
    }
}
