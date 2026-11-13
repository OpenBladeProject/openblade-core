using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class Rz090529IndependentZoneTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentProfilePreservesZoneIntentAndLegacyProfileCannotIntroduceIt(bool touchpad)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        IndependentLightingSettings fields = touchpad ? new(null, null, null, false) : new(null, null, true);
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Lighting = defaults.PluggedIn.Lighting with { IndependentControls = fields },
            },
        };
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "profiles.json"));
        ProfileSet loaded = store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(27, profiles), ProfileStore.SerializerOptions));
        Assert.Equal(fields, loaded.PluggedIn.Lighting.IndependentControls);
        Assert.Throws<InvalidDataException>(() => store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(26, profiles), ProfileStore.SerializerOptions)));
        ProfileSet migrated = store.ReadDocument(JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(26, defaults), ProfileStore.SerializerOptions));
        Assert.Null(migrated.PluggedIn.Lighting.IndependentControls);
        Assert.False(migrated.PluggedIn.Lighting.ControlsKeyboardEffect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedZoneIsConfirmedWithoutKeyboardOrOtherFieldChanges(bool touchpad)
    {
        BatteryTestDevice device = CreateDevice();
        IndependentLightingSettings request = touchpad ? new(null, null, null, true) : new(null, null, true);
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(
            LightingSettingsFactory.SpectrumCycling(80) with { IndependentControls = request }, default);
        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(request, Assert.Single(device.IndependentLightingWrites));
        Assert.Equal(new IndependentLightingSettings(50, LogoLightingMode.Static, !touchpad, touchpad), device.State.IndependentLighting);
        Assert.Null(device.State.Lighting);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRequestedZoneReadbackCannotConfirmAWrite(bool touchpad)
    {
        BatteryTestDevice device = CreateDevice();
        device.AutoConfirmWrites = false;
        device.State = device.State with { IndependentLighting = new(50, LogoLightingMode.Static) };
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(
            LightingSettingsFactory.SpectrumCycling(80) with
            {
                IndependentControls = touchpad ? new(null, null, null, true) : new(null, null, true),
            }, default);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.NotEqual(ControlResultStatus.Applied, outcome.Result.Status);
    }

    [Fact]
    public async Task GenericIndependentCapabilityDoesNotAdmitZoneMutation()
    {
        BatteryTestDevice device = CreateDevice();
        device.Capabilities = device.Capabilities with { SupportsVaporChamberLighting = false, SupportsTouchpadStatusIndicator = false };
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(
            LightingSettingsFactory.SpectrumCycling(80) with { IndependentControls = new(null, null, true, true) }, default);
        Assert.NotEqual(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Empty(device.IndependentLightingWrites);
    }

    [Fact]
    public void UnknownObservationsDoNotInventIndicatorDrift()
    {
        var tracker = new ConfirmedStateDriftTracker();
        ConfirmedDeviceState baseline = new(null, null, null, null) { IndependentLighting = new(null, null, false, true) };
        tracker.Observe(baseline);
        tracker.Observe(baseline with { IndependentLighting = null });
        Assert.Empty(tracker.ExternalChanges);
        tracker.Observe(baseline with { IndependentLighting = new(null, null, true, true) });
        Assert.Contains(DeviceSubsystem.Lighting, tracker.ExternalChanges);
    }

    private static BatteryTestDevice CreateDevice() => new()
    {
        Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsIndependentLightingControls = true,
            SupportsVaporChamberLighting = true,
            SupportsTouchpadStatusIndicator = true,
            LightingEffects = new HashSet<LightingEffect>(),
        },
        State = new(null, null, null, null) { IndependentLighting = new(50, LogoLightingMode.Static, false, false) },
    };
}
