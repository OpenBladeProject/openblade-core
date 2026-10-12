namespace OpenBlade.Contracts.Tests;

public sealed class CoolingPadFanCapabilitiesTests
{
    [Fact]
    public void PadObservations_DoNotGrantWriteOrReadbackAdmission()
    {
        CoolingPadFanCapabilities observed = CoolingPadFanCapabilities.Observed;
        Assert.Equal((ushort)0x1532, observed.VendorId);
        Assert.Equal((ushort)0x0F43, observed.ProductId);
        Assert.Equal((ushort)0x0200, observed.Revision);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, observed.HostDemandAccess);
        Assert.Equal(new DeviceNumericRange(500, 3200, 50), observed.VendorConfiguredDemandRpm);
        Assert.False(observed.FullDemandRangePhysicallyValidated);
        Assert.False(observed.MeasuredRpmReadbackSupported);
        Assert.False(observed.ActiveFanStateReadbackSupported);
        Assert.False(observed.OwnershipReadbackSupported);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(3200)]
    public void Demand_AcceptsVendorRangeEndpoints(int rpm)
    {
        var demand = new CoolingPadConfiguredRpmDemand(rpm);
        Assert.Equal(rpm, demand.Rpm);
        Assert.True(demand.IsWellFormed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(450)]
    [InlineData(3250)]
    [InlineData(501)]
    [InlineData(2225)]
    public void Demand_RejectsOutOfRangeOrOffStep(int rpm) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoolingPadConfiguredRpmDemand(rpm));
}
