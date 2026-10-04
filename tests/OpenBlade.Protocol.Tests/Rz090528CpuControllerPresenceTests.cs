using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090528CpuControllerPresenceTests
{
    [Theory]
    [InlineData("Razer")]
    [InlineData("Ryzen")]
    [InlineData("RGS")]
    public void GetterActivityRechecksBothPoliciesAndRevokesPermanently(string controller)
    {
        string[] processes = []; int full = 0, checks = 0;
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => processes,
            () => { full++; return []; }, _ => { checks++; return []; });
        Assert.NotNull(current);
        for (int i = 0; i < 164; i++) { Assert.True(current()); }
        Assert.Equal(1, full); Assert.Equal(164, checks);
        processes = [controller]; Assert.False(current());
        processes = []; Assert.False(current());
    }

    [Fact]
    public void GetterRejectsControllerServiceAppearingWithoutAProcess()
    {
        var service = new CpuServiceInventoryEntry("RGS", "RGS", 0x10, 1, "");
        var current = Rz090528CpuControllerPresence.CaptureGetterContinuity(() => [],
            () => [service], _ => [service]);
        Assert.NotNull(current); Assert.True(current());
        service = service with { State = 4 }; Assert.False(current());
    }

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
