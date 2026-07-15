using Microsoft.Extensions.DependencyInjection;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceCompositionTests
{
    [Fact]
    public void AddVerifiedBladeDeviceProvider_ResolvesWindowsProviderOnly()
    {
        var services = new ServiceCollection();

        services.AddVerifiedBladeDeviceProvider();

        using ServiceProvider provider = services.BuildServiceProvider();
        IVerifiedBladeDeviceProvider[] registrations =
            provider.GetServices<IVerifiedBladeDeviceProvider>().ToArray();
        Assert.Collection(
            registrations,
            registration => Assert.IsType<WindowsVerifiedBladeDeviceProvider>(registration));
        Assert.IsType<WindowsVerifiedBladeDeviceProvider>(
            provider.GetRequiredService<IVerifiedBladeDeviceProvider>());
        Assert.DoesNotContain(
            registrations,
            registration => registration is UnavailableBladeDeviceProvider);
    }
}
