using Microsoft.Extensions.DependencyInjection;

namespace OpenBlade.Service;

internal static class ServiceComposition
{
    internal static IServiceCollection AddVerifiedBladeDeviceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVerifiedBladeDeviceProvider, WindowsVerifiedBladeDeviceProvider>();
        return services;
    }
}
