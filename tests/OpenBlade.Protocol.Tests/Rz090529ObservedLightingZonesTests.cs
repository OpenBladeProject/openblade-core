using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Rz090529ObservedLightingZonesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rz090529_ReportsOwnerObservedZonesWithoutGrantingWrites(bool enabled)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(
            item => item.ProductId == 0x02C7);

        DeviceCapabilities capabilities = ExperimentalBladePort.CreateCapabilities(model, enabled);

        Assert.Equal(
            new HashSet<ObservedLightingZone>
            {
                ObservedLightingZone.Touchpad,
                ObservedLightingZone.UnderGlass,
            },
            capabilities.ObservedLightingZones);
    }

    [Theory]
    [InlineData(0x02A0)]
    [InlineData(0x02B8)]
    public void OtherModels_DoNotInheritRz090529ObservedZones(int productId)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(
            item => item.ProductId == productId);

        Assert.Empty(ExperimentalBladePort.CreateCapabilities(model, enabled: true).ObservedLightingZones);
    }
}
