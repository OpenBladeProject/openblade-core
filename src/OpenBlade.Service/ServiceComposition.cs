using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBlade.Protocol;
using System.Text.Json;

namespace OpenBlade.Service;

internal static class ServiceComposition
{
    internal static IServiceCollection AddDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DiagnosticHistory>();
        services.AddSingleton<ILoggerProvider>(provider =>
            provider.GetRequiredService<DiagnosticHistory>());
        services.AddSingleton<IAuthorizedUserTokenSource, WindowsAuthorizedUserTokenSource>();
        services.AddSingleton<IDiagnosticMetadataSource, WindowsDiagnosticMetadataSource>();
        services.AddSingleton<IDiagnosticArchiveProvider, DiagnosticArchiveProvider>();
        return services;
    }

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

        services.AddSingleton<IProductionBatteryPortFactory>(_ =>
            CreateProductionBatteryFactory());
        services.AddSingleton<IVerifiedBladeDeviceProvider>(provider =>
            new WindowsVerifiedBladeDeviceProvider(
                WindowsVerifiedBladeDeviceProvider.ProductionProbeFactory,
                provider.GetRequiredService<IProductionBatteryPortFactory>()));
        return services;
    }

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory() =>
        CreateProductionBatteryFactory(
            ProductionBatteryFactory.CreateWindows,
            ProductionBatteryFactory.CreateWindowsReadOnly);

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory(
        Func<IProductionBatteryPortFactory> createAdmitted,
        Func<IProductionBatteryPortFactory> createReadOnly)
    {
        ArgumentNullException.ThrowIfNull(createAdmitted);
        ArgumentNullException.ThrowIfNull(createReadOnly);
        try
        {
            return createAdmitted();
        }
        catch (UnvalidatedCommandException)
        {
            return createReadOnly();
        }
        catch (JsonException)
        {
            return createReadOnly();
        }
    }
}
