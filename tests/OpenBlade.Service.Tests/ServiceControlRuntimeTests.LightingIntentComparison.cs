using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false, "matching")]
    [InlineData(false, "color")]
    [InlineData(false, "brightness")]
    [InlineData(false, "effect")]
    [InlineData(true, "matching")]
    [InlineData(true, "color")]
    [InlineData(true, "brightness")]
    [InlineData(true, "effect")]
    public async Task LightingIntentMarker_IsNotExternalReadbackEvidence(
        bool profileTransition, string observation)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings requested = LightingSettingsFactory.Static(new RgbColor(10, 20, 30), 50) with
        {
            ControlsKeyboardEffect = true,
        };
        LightingSettings reported = observation switch
        {
            "color" => LightingSettingsFactory.Static(new RgbColor(30, 20, 10), 50),
            "brightness" => LightingSettingsFactory.Static(requested.Color, 60),
            "effect" => LightingSettingsFactory.SpectrumCycling(50),
            _ => requested with { ControlsKeyboardEffect = false },
        };
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with { Lighting = reported };
            }
        };

        if (profileTransition)
        {
            ProfileSet profiles = context.Runtime.GetStatus().Profiles;
            await context.Runtime.UpdateProfilesAsync(profiles with
            {
                Battery = profiles.Battery with { Lighting = requested },
            }, default);
            context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
            await context.Runtime.HandlePowerSourceChangedAsync(default);
        }
        else
        {
            await context.Runtime.SetLightingOverrideAsync(
                new(requested, OverrideLifetime.UntilPowerSourceChange), default);
        }

        Assert.Equal(reported, context.Runtime.GetStatus().ConfirmedState!.Lighting);
        Assert.Equal(observation != "matching",
            context.Runtime.GetStatus().ExternalChanges.Contains(DeviceSubsystem.Lighting));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupLightingIntent_PreservesExplicitAuthorityComparedWithUnmanagedProfile(bool explicitIntent)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings remembered = profiles.PluggedIn.Lighting with
        {
            ControlsKeyboardEffect = explicitIntent,
        };
        var automation = new ProfileAutomationState(profiles,
            new BatteryAutomationPreferences(LastKnownLighting: remembered), PowerSource.PluggedIn);

        if (explicitIntent)
        {
            Assert.Equal(remembered, automation.LightingOverride!.Value);
            Assert.Equal(OverrideLifetime.UntilPowerSourceChange, automation.LightingOverride.Lifetime);
            Assert.Equal(remembered, automation.Resolve(new(null, null, null, null)).Lighting.Value);
        }
        else
        {
            Assert.Null(automation.LightingOverride);
        }
        automation.ChangePowerSource(PowerSource.Battery);
        Assert.Null(automation.LightingOverride);
    }
}
