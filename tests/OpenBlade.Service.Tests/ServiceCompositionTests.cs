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

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsAssignableFrom<IFanRpmQueryRunFactory>(
            provider.GetRequiredService<IFanRpmQueryRunFactory>());
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
}
