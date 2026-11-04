using System.Reflection;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class LaptopControlRecoveryIdentityTests
{
    [Fact]
    public void ValidatedFactoriesCarryTheirExactSelectedIdentityWithoutOpeningHardware()
    {
        AssertIdentity(ProductionBatteryFactory.CreateWindows(), "RZ09-0581|1532:02E0");
        AssertIdentity(BatteryValidationFactory.CreateWindowsRz090528ProductionBattery(), "RZ09-0528|1532:02C6");
    }

    [Fact]
    public void EveryExperimentalFactoryCarriesItsSelectedIdentityWithoutDiscovery()
    {
        Assert.Equal(5, ExperimentalBladeModels.All.Count);
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            var factory = new ExperimentalBladeFactory(model,
                Unused<IHostIdentitySource>(), Unused<IHidCandidateNative>(),
                Unused<IHardwareAccessGuard>(), Unused<IHidFeatureIo>(),
                static () => throw new InvalidOperationException("Metadata must not capture power."));
            AssertIdentity(factory, $"{model.ModelNumber}|1532:{model.ProductId:X4}");
        }
    }

    private static void AssertIdentity(IProductionBatteryPortFactory factory, string expected)
    {
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            static () => throw new InvalidOperationException("Metadata must not probe hardware."),
            factory, VerifiedBladeProviderFixture.UnavailableFanRpmFactory());
        Assert.Equal(expected, provider.LaptopControlRecoveryIdentity);
    }

    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedDependency>();

    public class UnusedDependency : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"Metadata must not access {targetMethod?.Name}.");
    }
}
