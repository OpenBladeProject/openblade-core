using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBlade.Protocol;

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
        services.AddSingleton<IPowerStateProvider>(provider =>
            provider.GetService<ILogger<WindowsPowerStateProvider>>() is { } logger
                ? new WindowsPowerStateProvider(logger)
                : new WindowsPowerStateProvider());
        return services;
    }

    internal static IServiceCollection AddVerifiedBladeDeviceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IProductionBatteryPortFactory>(_ =>
            CreateProductionBatteryFactory());
        services.AddSingleton<IProductionLightingPortFactory>(_ =>
            CreateProductionLightingFactory());
        services.AddSingleton(provider =>
            CreateProfileSetValidator(
                provider.GetRequiredService<IProductionLightingPortFactory>()));
        services.AddSingleton<IFanRpmQueryRunFactory>(_ =>
            BatteryValidationFactory.CreateWindowsFanRpmForService());
        services.AddSingleton<IGpuClockOffsetController>(_ =>
            new NvidiaGpuClockOffsetController());
        services.AddSingleton<IVerifiedBladeDeviceProvider>(provider =>
            new WindowsVerifiedBladeDeviceProvider(
                WindowsVerifiedBladeDeviceProvider.ProductionProbeFactory,
                provider.GetRequiredService<IProductionBatteryPortFactory>(),
                provider.GetRequiredService<IFanRpmQueryRunFactory>(),
                provider.GetRequiredService<IProductionLightingPortFactory>(),
                provider.GetRequiredService<IGpuClockOffsetController>(),
                provider.GetService<ILogger<WindowsVerifiedBladeDeviceProvider>>()));
        return services;
    }

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory() =>
        CreateProductionBatteryFactory(
            BatteryValidationFactory.CreateWindowsProductionBatteryForService,
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

    internal static IProductionLightingPortFactory CreateProductionLightingFactory() =>
        CreateProductionLightingFactory(
            BatteryValidationFactory.CreateWindowsProductionLightingForService,
            ProductionLightingFactory.CreateWindowsReadOnly);

    internal static IProductionLightingPortFactory CreateProductionLightingFactory(
        Func<IProductionLightingPortFactory> createAdmitted,
        Func<IProductionLightingPortFactory> createReadOnly)
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

    internal static ProfileSetValidator CreateProfileSetValidator(
        IProductionLightingPortFactory lightingFactory)
    {
        ArgumentNullException.ThrowIfNull(lightingFactory);
        return ProfileSetValidator.CreateForLightingCapabilities(
            lightingFactory.Capabilities);
    }
}
