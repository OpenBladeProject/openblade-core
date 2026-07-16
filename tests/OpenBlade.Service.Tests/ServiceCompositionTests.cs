using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceCompositionTests
{
    [Fact]
    public void AddWindowsTelemetry_RegistersNativeNvmlAndPowerProvider()
    {
        var services = new ServiceCollection();

        services.AddWindowsTelemetry();

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(INvmlApi));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGpuTemperatureSource)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IPowerTelemetryProvider)
                && descriptor.ImplementationFactory is not null
                && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void AddWindowsTelemetry_ResolvesPowerProviderWithInjectedNvmlApi()
    {
        var services = new ServiceCollection();
        services.AddWindowsTelemetry(() => new CountingNvmlApi(available: false));

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<WindowsPowerTelemetryProvider>(
            provider.GetRequiredService<IPowerTelemetryProvider>());
    }

    [Fact]
    public void AddWindowsTelemetry_ServiceProviderDisposesOwnedNvmlExactlyOnce()
    {
        var api = new CountingNvmlApi(available: true);
        var services = new ServiceCollection();
        services.AddWindowsTelemetry(() => api);
        ServiceProvider provider = services.BuildServiceProvider();
        IGpuTemperatureSource source = provider.GetRequiredService<IGpuTemperatureSource>();
        Assert.Equal(47, source.CaptureCelsius());

        provider.Dispose();

        Assert.Equal(1, api.ShutdownCount);
        Assert.Equal(1, api.DisposeCount);
    }

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
        public Task RefreshAsync(CancellationToken token) => throw new NotSupportedException();
        public Task RefreshConflictsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task ObserveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CountingNvmlApi(bool available) : INvmlApi
    {
        public bool IsAvailable => available;
        public int ShutdownCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool TryInitialize() => available;

        public bool TryGetDeviceHandleByIndex(uint index, out nint device)
        {
            device = available ? (nint)42 : 0;
            return available;
        }

        public bool TryGetTemperature(nint device, uint sensorType, out uint temperatureCelsius)
        {
            temperatureCelsius = 47;
            return available;
        }

        public void Shutdown()
        {
            ShutdownCount++;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
