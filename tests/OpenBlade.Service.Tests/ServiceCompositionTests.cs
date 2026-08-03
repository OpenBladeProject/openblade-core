using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceCompositionTests
{
    [Fact]
    public void ProductionBatteryFactoryFallbackCatchesAdmissionFailureOnlyAndReturnsEmptyCatalogFactory()
    {
        var fallback = new StubProductionBatteryFactory(DeviceCapabilities.Unavailable());

        IProductionBatteryPortFactory selected = ServiceComposition.CreateProductionBatteryFactory(
            () => throw new UnvalidatedCommandException("untrusted fixture"),
            () => fallback);

        Assert.Same(fallback, selected);
        Assert.Empty(selected.Capabilities.ChargeLimits);
    }

    [Fact]
    public void ProductionBatteryFactoryFallbackDoesNotHideUnexpectedCompositionFailure()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServiceComposition.CreateProductionBatteryFactory(
                () => throw new InvalidOperationException("programming defect"),
                () => new StubProductionBatteryFactory(DeviceCapabilities.Unavailable())));
    }

    [Fact]
    public void ProductionLightingFactoryFallbackCatchesAdmissionFailureOnly()
    {
        var fallback = new StubProductionLightingFactory(DeviceCapabilities.Unavailable());

        IProductionLightingPortFactory selected =
            ServiceComposition.CreateProductionLightingFactory(
                () => throw new UnvalidatedCommandException("untrusted lighting fixture"),
                () => fallback);

        Assert.Same(fallback, selected);
        Assert.Empty(selected.Capabilities.LightingEffects);
    }

    [Fact]
    public void ProductionLightingFactoryFallbackDoesNotHideUnexpectedFailure()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ServiceComposition.CreateProductionLightingFactory(
                () => throw new InvalidOperationException("programming defect"),
                () => new StubProductionLightingFactory(DeviceCapabilities.Unavailable())));
    }

    [Fact]
    public void ProfileValidatorUsesAdmittedLightingCapabilitiesAndAlwaysKeepsOffSafe()
    {
        var factory = new StubProductionLightingFactory(
            DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect> { LightingEffect.Static },
            });
        ProfileSetValidator validator = ServiceComposition.CreateProfileSetValidator(
            new StubProductionBatteryFactory(DeviceCapabilities.Unavailable()),
            factory);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        LightingSettings staticLighting = LightingSettingsFactory.Static(
            new RgbColor(12, 34, 56),
            brightness: 70);

        validator.Validate(defaults);
        validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = staticLighting },
            Battery = defaults.Battery with { Lighting = staticLighting },
            UsbC = defaults.UsbC with { Lighting = staticLighting },
        });
    }

    [Fact]
    public void ProfileValidatorRemainsOffOnlyWhenLightingAdmissionFallsBackReadOnly()
    {
        ProfileSetValidator validator = ServiceComposition.CreateProfileSetValidator(
            new StubProductionBatteryFactory(DeviceCapabilities.Unavailable()),
            new StubProductionLightingFactory(DeviceCapabilities.Unavailable()));
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        validator.Validate(defaults);

        Assert.Throws<InvalidDataException>(() => validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = LightingSettingsFactory.SpectrumCycling(50),
            },
        }));
    }

    [Fact]
    public void ProfileValidatorUsesBatteryCapabilitiesForFanAndCustomConstraints()
    {
        DeviceCapabilities batteryCapabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanSpeedRange = new FanSpeedRange(2000, 5000, 100),
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Medium),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
                    new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
                }),
        };
        ProfileSetValidator validator = ServiceComposition.CreateProfileSetValidator(
            new StubProductionBatteryFactory(batteryCapabilities),
            new StubProductionLightingFactory(DeviceCapabilities.Unavailable()));
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet unsupported = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Medium),
            },
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => validator.Validate(unsupported));

        Assert.Contains("not supported by this device", exception.Message);

        validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Low),
            },
        });

        InvalidDataException fanException = Assert.Throws<InvalidDataException>(() =>
            validator.Validate(defaults with
            {
                PluggedIn = defaults.PluggedIn with
                {
                    Fans = FanMode.Fixed,
                    FanControl = new FanControlSettings(
                        FanMode.Fixed,
                        5400,
                        ManualFanCurve.Default),
                },
            }));
        Assert.Contains("outside the device range", fanException.Message);

        validator.Validate(defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Fans = FanMode.Fixed,
                FanControl = new FanControlSettings(
                    FanMode.Fixed,
                    5000,
                    ManualFanCurve.Default),
            },
        });
    }

    [Fact]
    public void AddWindowsPowerState_RegistersOnlyPowerProvider()
    {
        var services = new ServiceCollection();

        services.AddWindowsPowerState();

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IPowerStateProvider)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddWindowsPowerState_ResolvesPowerProvider()
    {
        var services = new ServiceCollection();
        services.AddWindowsPowerState();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<WindowsPowerStateProvider>(
            provider.GetRequiredService<IPowerStateProvider>());
    }

    [Fact]
    public void AddVerifiedBladeDeviceProvider_ResolvesWindowsProviderOnly()
    {
        var services = new ServiceCollection();

        services.AddVerifiedBladeDeviceProvider();

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IFanRpmQueryRunFactory)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IProductionLightingPortFactory)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGpuClockOffsetController)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsAssignableFrom<IFanRpmQueryRunFactory>(
            provider.GetRequiredService<IFanRpmQueryRunFactory>());
        Assert.IsType<NvidiaGpuClockOffsetController>(
            provider.GetRequiredService<IGpuClockOffsetController>());
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

    [Fact]
    public void AddDiagnostics_RegistersOneSharedBoundedHistoryAndArchiveProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IServiceControlRuntime, StubRuntime>();

        services.AddDiagnostics();

        using ServiceProvider provider = services.BuildServiceProvider();
        DiagnosticHistory history = provider.GetRequiredService<DiagnosticHistory>();
        Assert.Same(history, Assert.IsType<DiagnosticHistory>(
            provider.GetRequiredService<ILoggerProvider>()));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IDiagnosticArchiveProvider)
                && descriptor.ImplementationType == typeof(DiagnosticArchiveProvider)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IFirmwareVersionSource)
                && descriptor.ImplementationType == typeof(WindowsFirmwareVersionSource)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IAuthorizedUserTokenSource)
                && descriptor.ImplementationType == typeof(WindowsAuthorizedUserTokenSource)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IDiagnosticMetadataSource)
                && descriptor.ImplementationType == typeof(WindowsDiagnosticMetadataSource)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.IsType<DiagnosticArchiveProvider>(
            provider.GetRequiredService<IDiagnosticArchiveProvider>());
    }

    private sealed class StubRuntime : IServiceControlRuntime
    {
        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubProductionBatteryFactory(DeviceCapabilities capabilities)
        : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = capabilities;

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(
                new HidSessionResult<IProductionBatteryPort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure));
    }

    private sealed class StubProductionLightingFactory(DeviceCapabilities capabilities)
        : IProductionLightingPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = capabilities;

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(
                new HidSessionResult<IProductionLightingPort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure));
    }
}
