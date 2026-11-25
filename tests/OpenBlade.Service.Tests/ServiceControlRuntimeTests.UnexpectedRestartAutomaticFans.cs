using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    // Crash recovery starts with a manual-fan safety baseline. Firmware
    // Automatic loses it, which is drift for a manual intent (see
    // UnexpectedRestart_FreshAutomaticStateBlocksStaleThermalFloor) but not
    // when the user's own intent is also Automatic. A service stop that left
    // the run marker behind, such as a local reinstall, took this path and
    // reported "Changed outside OpenBlade: Fans" with nothing changed.
    [Fact]
    public async Task UnexpectedRestart_AutomaticFansMatchingAutomaticIntentAreNotDrift()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var userSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet>
            {
                [userSid.Value] = profiles,
            },
            directory);
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults();
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository);
        context.Device.Capabilities = capabilities;
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            null);
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState!.Fans);
        Assert.Equal(FanMode.Automatic, status.DesiredState.Fans.Value);
        Assert.Empty(status.ExternalChanges);
    }
}
