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

    internal static IServiceCollection AddMultiUserSessionSupport(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IInteractiveSessionAuthority, WindowsInteractiveSessionAuthority>();
        services.AddSingleton<IServiceClientContextResolver, WindowsServiceClientContextResolver>();
        return services;
    }

    internal static IServiceCollection AddVerifiedBladeDeviceProvider(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<FirmwareCompatibilityPolicy>();
        services.AddSingleton<IProductionBatteryPortFactory>(provider =>
            CreateProductionBatteryFactory(
                provider.GetRequiredService<FirmwareCompatibilityPolicy>()));
        services.AddSingleton<IProductionCoolingPadPortFactory>(_ =>
            ProductionCoolingPadFactory.CreateWindows());
        services.AddSingleton<WindowsVerifiedCoolingPadProvider>(provider =>
            new WindowsVerifiedCoolingPadProvider(
                provider.GetRequiredService<IProductionCoolingPadPortFactory>(),
                CoolingPadDiscoveryFactory.CreateWindows(),
                WindowsHostIdentitySource.IsRz090581WindowsHost(
                    allowUntestedFirmware: true),
                TimeProvider.System,
                WindowsVerifiedCoolingPadProvider.DefaultObservationInterval,
                provider.GetService<LowPowerGate>()));
        services.AddSingleton<IVerifiedCoolingPadProvider>(provider =>
            provider.GetRequiredService<WindowsVerifiedCoolingPadProvider>());
        services.AddSingleton<ICoolingPadLightingSessionOwner>(provider =>
            provider.GetRequiredService<WindowsVerifiedCoolingPadProvider>()
                .CreateLightingSessionOwner());
        services.AddSingleton<ICoolingPadLightingSink>(provider =>
            new ProviderCoolingPadLightingSink(
                provider.GetRequiredService<ICoolingPadLightingSessionOwner>()));
        services.AddSingleton<ICoolingPadLightingCoordinator>(provider =>
            CoolingPadLightingCoordinatorFactory.CreateWindows(
                provider.GetRequiredService<ICoolingPadLightingSink>()));
        services.AddSingleton<IKeyboardLightingFrameObserver>(provider =>
            provider.GetRequiredService<ICoolingPadLightingCoordinator>());
        services.AddSingleton<ICoolingPadLightingRuntimeCoordinator>(provider =>
            new ProtocolCoolingPadLightingRuntimeCoordinator(
                provider.GetRequiredService<ICoolingPadLightingCoordinator>()));
        services.AddSingleton<IProductionLightingPortFactory>(provider =>
            CreateProductionLightingFactory(
                provider.GetRequiredService<FirmwareCompatibilityPolicy>(),
                provider.GetRequiredService<IKeyboardLightingFrameObserver>()));
        services.AddSingleton(provider =>
            CreateProfileSetValidator(
                provider.GetRequiredService<IProductionBatteryPortFactory>(),
                provider.GetRequiredService<IProductionLightingPortFactory>()));
        services.AddSingleton<IFanRpmQueryRunFactory>(provider =>
            BatteryValidationFactory.CreateWindowsFanRpmForService(
                provider.GetRequiredService<FirmwareCompatibilityPolicy>()));
        services.AddSingleton<IGpuClockOffsetController>(_ =>
            new NvidiaGpuClockOffsetController());
        services.AddSingleton<ICoolingPadFanRuntimeCoordinator>(provider =>
            CreateCoolingPadFanRuntimeCoordinator(
                provider.GetRequiredService<WindowsVerifiedCoolingPadProvider>(),
                provider.GetService<ICoolingPadFanOwnershipJournalStore>()
                    ?? UnavailableCoolingPadFanOwnershipJournalStore.Instance));
        services.AddSingleton<IVerifiedBladeDeviceProvider>(provider =>
        {
            FirmwareCompatibilityPolicy compatibilityPolicy =
                provider.GetRequiredService<FirmwareCompatibilityPolicy>();
            return new WindowsVerifiedBladeDeviceProvider(
                () => BatteryValidationFactory.ProbeWindowsForService(
                    compatibilityPolicy),
                provider.GetRequiredService<IProductionBatteryPortFactory>(),
                provider.GetRequiredService<IFanRpmQueryRunFactory>(),
                provider.GetRequiredService<IProductionLightingPortFactory>(),
                provider.GetRequiredService<IGpuClockOffsetController>(),
                provider.GetService<ILogger<WindowsVerifiedBladeDeviceProvider>>(),
                compatibilityPolicy: compatibilityPolicy,
                gpuActivitySource: new WindowsGpuActivitySource());
        });
        return services;
    }

    private static ICoolingPadFanRuntimeCoordinator
        CreateCoolingPadFanRuntimeCoordinator(
            WindowsVerifiedCoolingPadProvider provider,
            ICoolingPadFanOwnershipJournalStore journalStore)
    {
        CoolingPadFanControlEvidenceGates evidence =
            CoolingPadFanControlEvidenceGates.Current;
        CoolingPadFanControlImplementationGates implementation =
            CoolingPadFanControlImplementationGates.Current;
        return evidence.WriteAdmitted && implementation.WriteAvailable
            ? AdmittedCoolingPadFanRuntimeCoordinator.CreateForProduction(
                provider,
                journalStore)
            : UnavailableCoolingPadFanRuntimeCoordinator.Instance;
    }

    internal static IServiceCollection AddServiceControlRuntime(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IServiceControlRuntime>(provider =>
            new ServiceControlRuntime(
                provider.GetRequiredService<BatteryConfigurationStore>(),
                provider.GetRequiredService<PersistentOverrideStore>(),
                provider.GetRequiredService<IPowerStateProvider>(),
                provider.GetRequiredService<IStatusSubscriptionHub>(),
                provider.GetRequiredService<IVerifiedBladeDeviceProvider>(),
                provider.GetRequiredService<DiagnosticHistory>(),
                provider.GetRequiredService<LowPowerGate>(),
                provider.GetRequiredService<IControllerPresenceMonitor>(),
                provider.GetRequiredService<IFirmwareVersionSource>(),
                static (delay, token) => Task.Delay(delay, token),
                provider.GetRequiredService<ProfileSetValidator>(),
                userConfigurationRepository:
                    provider.GetRequiredService<IUserConfigurationRepository>(),
                displayBrightnessHidController:
                    provider.GetRequiredService<IDisplayBrightnessHidController>(),
                coolingPadProvider:
                    provider.GetRequiredService<IVerifiedCoolingPadProvider>(),
                coolingPadLightingCoordinator:
                    provider.GetRequiredService<ICoolingPadLightingRuntimeCoordinator>(),
                coolingPadFanCoordinator:
                    provider.GetRequiredService<ICoolingPadFanRuntimeCoordinator>(),
                hyperBoostOwnershipJournalStore:
                    provider.GetService<IHyperBoostOwnershipJournalStore>()
                        ?? UnavailableHyperBoostOwnershipJournalStore.Instance));
        return services;
    }

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory() =>
        CreateProductionBatteryFactory(
            BatteryValidationFactory.CreateWindowsProductionBatteryForService,
            ProductionBatteryFactory.CreateWindowsReadOnly);

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateProductionBatteryFactory(
            () => BatteryValidationFactory.CreateWindowsProductionBatteryForService(
                compatibilityPolicy),
            () => ProductionBatteryFactory.CreateWindowsReadOnly(
                compatibilityPolicy));
    }

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
        FirmwareCompatibilityPolicy compatibilityPolicy)
        => CreateProductionLightingFactory(
            compatibilityPolicy,
            UnavailableKeyboardLightingFrameObserver.Instance);

    internal static IProductionLightingPortFactory CreateProductionLightingFactory(
        FirmwareCompatibilityPolicy compatibilityPolicy,
        IKeyboardLightingFrameObserver frameObserver)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        ArgumentNullException.ThrowIfNull(frameObserver);
        return CreateProductionLightingFactory(
            () => BatteryValidationFactory.CreateWindowsProductionLightingForService(
                compatibilityPolicy,
                frameObserver),
            () => ProductionLightingFactory.CreateWindowsReadOnly(
                compatibilityPolicy));
    }

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
        IProductionBatteryPortFactory batteryFactory,
        IProductionLightingPortFactory lightingFactory)
    {
        ArgumentNullException.ThrowIfNull(batteryFactory);
        ArgumentNullException.ThrowIfNull(lightingFactory);
        return ProfileSetValidator.CreateForDeviceCapabilities(
            batteryFactory.Capabilities,
            lightingFactory.Capabilities);
    }
}
