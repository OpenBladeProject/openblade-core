using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

internal static class ServiceComposition
{
    internal static IServiceCollection AddServiceControlRuntime(
        this IServiceCollection services,
        string machineDataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        string journalPath = Path.Combine(
            string.IsNullOrWhiteSpace(machineDataDirectory)
                ? throw new ArgumentException(
                    "A machine-data directory is required.",
                    nameof(machineDataDirectory))
                : Path.GetFullPath(machineDataDirectory),
            "hyperboost-ownership.json");

        services.AddSingleton<IHyperBoostOwnershipJournalStore>(
            _ => new HyperBoostOwnershipJournalStore(journalPath));
        services.AddSingleton<ICpuTuningOwnershipJournalStore>(_ => new CpuTuningOwnershipJournalStore(
            Path.Combine(Path.GetDirectoryName(journalPath)!, "cpu-tuning-ownership.json")));
        services.AddSingleton(provider => new CpuTuningController(new WindowsCpuTuningObservationSource(),
            provider.GetRequiredService<ICpuTuningOwnershipJournalStore>()));
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
                hyperBoostOwnershipJournalStore:
                    provider.GetRequiredService<IHyperBoostOwnershipJournalStore>(),
                cpuTuningController: provider.GetRequiredService<CpuTuningController>()));
        services.AddSingleton<ServiceSessionPowerTransitionCoordinator>();
        return services;
    }

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
                PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent().Enabled,
                BatteryValidationFactory
                    .CreateWindowsProductionBatteryForPadlessHyperBoostLifecycleValidation,
                () => BatteryValidationFactory.CreateWindowsProductionBatteryForService(
                    provider.GetRequiredService<FirmwareCompatibilityPolicy>()),
                () => ProductionBatteryFactory.CreateWindowsReadOnly(
                    provider.GetRequiredService<FirmwareCompatibilityPolicy>())));
        services.AddSingleton<IProductionLightingPortFactory>(provider =>
            CreateProductionLightingFactory(
                provider.GetRequiredService<FirmwareCompatibilityPolicy>()));
        services.AddSingleton(provider =>
            CreateProfileSetValidator(
                provider.GetRequiredService<IProductionBatteryPortFactory>(),
                provider.GetRequiredService<IProductionLightingPortFactory>()));
        services.AddSingleton<IFanRpmQueryRunFactory>(provider =>
            BatteryValidationFactory.CreateWindowsFanRpmForService(
                provider.GetRequiredService<FirmwareCompatibilityPolicy>()));
        services.AddSingleton<IGpuClockOffsetController>(_ =>
            new NvidiaGpuClockOffsetController());
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
                gpuActivitySource: new WindowsGpuActivitySource(),
                lowPowerGate: provider.GetRequiredService<LowPowerGate>());
        });
        return services;
    }

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory() =>
        CreateProductionBatteryFactory(
            PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent().Enabled
                ? BatteryValidationFactory
                    .CreateWindowsProductionBatteryForPadlessHyperBoostLifecycleValidation
                : BatteryValidationFactory.CreateWindowsProductionBatteryForService,
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

    internal static IProductionBatteryPortFactory CreateProductionBatteryFactory(
        bool validationBuildEnabled,
        Func<IProductionBatteryPortFactory> createValidation,
        Func<IProductionBatteryPortFactory> createAdmitted,
        Func<IProductionBatteryPortFactory> createReadOnly)
    {
        ArgumentNullException.ThrowIfNull(createValidation);
        ArgumentNullException.ThrowIfNull(createAdmitted);
        if (!validationBuildEnabled)
        {
            return CreateProductionBatteryFactory(createAdmitted, createReadOnly);
        }

        try
        {
            return createValidation();
        }
        catch (Exception exception) when (exception is
            UnvalidatedCommandException or JsonException)
        {
            return CreateProductionBatteryFactory(createAdmitted, createReadOnly);
        }
    }

    internal static IProductionLightingPortFactory CreateProductionLightingFactory() =>
        CreateProductionLightingFactory(
            BatteryValidationFactory.CreateWindowsProductionLightingForService,
            ProductionLightingFactory.CreateWindowsReadOnly);

    internal static IProductionLightingPortFactory CreateProductionLightingFactory(
        FirmwareCompatibilityPolicy compatibilityPolicy)
    {
        ArgumentNullException.ThrowIfNull(compatibilityPolicy);
        return CreateProductionLightingFactory(
            () => BatteryValidationFactory.CreateWindowsProductionLightingForService(
                compatibilityPolicy),
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
