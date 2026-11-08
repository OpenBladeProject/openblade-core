using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x02A0, true, true)]
    [InlineData(0x02A0, false, false)]
    [InlineData(0x029F, true, false)]
    [InlineData(0x029D, true, false)]
    public void FunctionRowPrimaryIsExactBlade18HostIntentOnly(int productId, bool enabled, bool expected)
    {
        DeviceCapabilities caps = ExperimentalBladePort.CreateCapabilities(Model((ushort)productId), enabled);
        Assert.Equal(expected, caps.HostManagedLaptopControls.Contains(OneShotLaptopControl.FunctionKeyPrimary));
        Assert.DoesNotContain(OneShotLaptopControl.FunctionKeyPrimary, caps.OneShotLaptopControls);

    }
}
