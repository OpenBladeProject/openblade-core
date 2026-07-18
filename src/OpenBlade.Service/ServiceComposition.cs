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
        services.AddSingleton<IFirmwareVersionSource, WindowsFirmwareVersionSource>();
        services.AddSingleton<IAuthorizedUserTokenSource, WindowsAuthorizedUserTokenSource>();
        services.AddSingleton<IDiagnosticMetadataSource, WindowsDiagnosticMetadataSource>();
        services.AddSingleton<IDiagnosticArchiveProvider, DiagnosticArchiveProvider>();
        return services;
    }

    internal static IServiceCollection AddWindowsPowerState(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPowerStateProvider>(_ => new WindowsPowerStateProvider());
        return services;
    }

    internal static IServiceCollection AddVerifiedBladeDeviceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProductionBatteryPortFactory>(_ =>
            CreateProductionBatteryFactory());
        services.AddSingleton<IFanRpmQueryRunFactory>(_ =>
            BatteryValidationFactory.CreateWindowsFanRpmQuery());
        services.AddSingleton<IVerifiedBladeDeviceProvider>(provider =>
            new WindowsVerifiedBladeDeviceProvider(
                WindowsVerifiedBladeDeviceProvider.ProductionProbeFactory,
                provider.GetRequiredService<IProductionBatteryPortFactory>(),
                provider.GetRequiredService<IFanRpmQueryRunFactory>()));
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
