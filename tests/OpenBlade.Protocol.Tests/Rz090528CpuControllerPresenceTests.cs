using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuControllerPresenceTests
{
    [Fact]
    public void StoppedControllerAndUnrelatedRunningServicePermitBoundedAbsence()
    {
        int reads = 0;
        Assert.True(Rz090528CpuControllerPresence.ControllersAbsent(() => { reads++; return ["ordinary"]; },
            () => [new("Razer", "Razer controller", 0x10, 1, ""), new("Other", "Other", 0xE0, 4, "")]));
        Assert.Equal(2, reads);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void EveryNonStoppedControllerStateDeniesIncludingDisplayAlias(int state) => Assert.False(
        Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Ryzen agent", 0xD0, (uint)state, "")]));
    [Fact]
    public void ControllerAppearingDuringServiceScanDenies()
    {
        int reads = 0;
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => ++reads == 1 ? [] : ["Razer"], () => []));
    }
    [Fact]
    public void FailedPartialOrUnknownInventoryNeverBecomesAbsence()
    {
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => throw new IOException(), () => []));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => throw new IOException()));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Other", 0, 1, "")]));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [], () => [new("Other", "Other", 0x10, 0, "")]));
        Assert.False(Rz090528CpuControllerPresence.ControllersAbsent(() => [""], () => []));
    }
}
