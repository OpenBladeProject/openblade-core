using Microsoft.Extensions.DependencyInjection;

namespace OpenBlade.Service;

internal static class ServiceComposition
{
    internal static IServiceCollection AddWindowsTelemetry(this IServiceCollection services) =>
        AddWindowsTelemetry(services, static () => new WindowsNvmlApi());

    internal static IServiceCollection AddWindowsTelemetry(
        this IServiceCollection services,
        Func<INvmlApi> createNvmlApi)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createNvmlApi);

        services.AddSingleton<IGpuTemperatureSource>(_ =>
            new NvmlGpuTemperatureSource(createNvmlApi()));
        services.AddSingleton<IPowerTelemetryProvider>(provider =>
            new WindowsPowerTelemetryProvider(
                provider.GetRequiredService<IGpuTemperatureSource>()));
        return services;
    }

    internal static IServiceCollection AddVerifiedBladeDeviceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVerifiedBladeDeviceProvider, WindowsVerifiedBladeDeviceProvider>();
        return services;
    }
}
