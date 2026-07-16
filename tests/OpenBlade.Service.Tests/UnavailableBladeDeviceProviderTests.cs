using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class UnavailableBladeDeviceProviderTests
{
    [Fact]
    public void TryGetVerifiedDevice_AlwaysFailsClosedWithoutDevice()
    {
        var provider = new UnavailableBladeDeviceProvider();

        bool available = provider.TryGetVerifiedDevice(out IBladeDevice device);

        Assert.False(available);
        Assert.Null(device);
    }
}
