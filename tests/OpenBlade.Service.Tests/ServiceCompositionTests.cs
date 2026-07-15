using Microsoft.Extensions.DependencyInjection;
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
