using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task HostFunctionRow_ProfileTransitionPreservesIntentWithoutFirmwareSubmission()
    {
        TestContext context = CreateWritableContext();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>(),
            HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.FunctionKeyPrimary,
            },
        };
        await runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            Battery = profiles.Battery with
            {
                LaptopBehavior = new LaptopBehaviorProfileSettings(null, FunctionKeyPrimaryMode.MultimediaKeys, null),
            },
        };
        await runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(0, context.Device.OneShotSetCalls);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys,
            runtime.GetStatus().Profiles.Battery.EffectiveLaptopBehavior.FunctionKeyPrimary);
        ControlOperationResult result = await runtime.ApplyOneShotLaptopControlAsync(
            new ApplyOneShotLaptopControl(OneShotLaptopControl.FunctionKeyPrimary,
                FunctionKeyMode: FunctionKeyPrimaryMode.FunctionKeys), CancellationToken.None);
        Assert.NotEqual(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(0, context.Device.OneShotSetCalls);
    }
}
