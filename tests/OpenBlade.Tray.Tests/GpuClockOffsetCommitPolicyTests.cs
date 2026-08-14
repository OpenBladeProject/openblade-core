using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GpuClockOffsetCommitPolicyTests
{
    private static readonly GpuClockOffsetCapabilities Capabilities = new(
        "Test GPU",
        new GpuClockOffsetRange(-100, 100, 0),
        new GpuClockOffsetRange(-200, 200, 0));

    [Fact]
    public void ValidEditableRequestCanCommit()
    {
        Assert.True(GpuClockOffsetCommitPolicy.CanCommit(
            canEdit: true,
            Capabilities,
            new GpuClockOffsetSettings(50, 100)));
    }

    [Fact]
    public void EditabilityLossRejectsPreviouslyValidRequest()
    {
        Assert.False(GpuClockOffsetCommitPolicy.CanCommit(
            canEdit: false,
            Capabilities,
            new GpuClockOffsetSettings(50, 100)));
    }

    [Theory]
    [InlineData(-101, 0)]
    [InlineData(101, 0)]
    [InlineData(0, -201)]
    [InlineData(0, 201)]
    public void CapabilityChangeRejectsStaleRequest(
        int coreMegahertz,
        int memoryMegahertz)
    {
        Assert.False(GpuClockOffsetCommitPolicy.CanCommit(
            canEdit: true,
            Capabilities,
            new GpuClockOffsetSettings(coreMegahertz, memoryMegahertz)));
    }

    [Fact]
    public void MissingCapabilitiesRejectRequest()
    {
        Assert.False(GpuClockOffsetCommitPolicy.CanCommit(
            canEdit: true,
            capabilities: null,
            GpuClockOffsetSettings.Default));
    }
}
