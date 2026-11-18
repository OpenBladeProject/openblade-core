using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
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

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task CombinedHostEffectRequiresZoneReadback(bool profileTransition, bool confirmZone)
    {
        BatteryTestDevice device = CreateDevice();
        device.Capabilities = device.Capabilities with { LightingEffects = new HashSet<LightingEffect> { LightingEffect.Fire } };
        LightingSettings requested = LightingSettingsFactory.Fire(60) with
        {
            ControlsKeyboardEffect = true,
            IndependentControls = new(VaporChamberLightingEnabled: true),
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting)
            {
                device.State = device.State with
                {
                    Lighting = null,
                    IndependentLighting = confirmZone ? device.State.IndependentLighting : null,
                };
            }
        };
        var coordinator = new ControlCoordinator(device);
        ControlResult result;
        if (profileTransition)
        {
            var desired = new DesiredDeviceState(
                new(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new(FanMode.Automatic, SettingValueSource.PowerProfile),
                new(requested, SettingValueSource.PowerProfile),
                new(BatteryPolicy.Create(80), true, false));
            result = (await coordinator.ApplyDesiredStateOutcomeAsync(desired, default)).Results.Lighting;
        }
        else
        {
            result = (await coordinator.ApplyLightingOutcomeAsync(requested, default)).Result;
        }

        Assert.Equal(confirmZone ? ControlResultStatus.Applied : ControlResultStatus.Failed, result.Status);
        Assert.Single(device.LightingWrites);
        Assert.Single(device.IndependentLightingWrites);
    }

    [Fact]
    public async Task MatchedBuiltInEffectIsNotRewrittenForIndependentFields()
    {
        BatteryTestDevice device = CreateDevice();
        device.Capabilities = device.Capabilities with { LightingEffects = new HashSet<LightingEffect> { LightingEffect.Static } };
        LightingSettings active = LightingSettingsFactory.Static(new(12, 34, 56), 50);
        device.State = device.State with { Lighting = active };
        LightingSettings requested = active with
        {
            ControlsKeyboardEffect = true,
            IndependentControls = new(Brightness: 42, TouchpadStatusIndicatorEnabled: true),
        };

        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(requested, default);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Empty(device.LightingWrites);
        Assert.Equal(requested.IndependentControls, Assert.Single(device.IndependentLightingWrites));
    }

    [Fact]
    public async Task MissingKeyboardCapabilityKeepsAnUncertainFieldWriteInFieldOnlyRecovery()
    {
        BatteryTestDevice device = CreateDevice();
        device.IndependentLightingWriteFailure = new BladeWriteException(
            WriteSubmissionState.Indeterminate, "The zone setter response was unavailable.");
        LightingSettings requested = LightingSettingsFactory.Fire(60) with
        {
            ControlsKeyboardEffect = true,
            IndependentControls = new(VaporChamberLightingEnabled: true),
        };

        ScopedApplyOutcome result = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(requested, default);

        Assert.True(result.ConfirmationUnavailable);
        Assert.Equal(1, device.StateReadCalls);
        Assert.Empty(device.LightingWrites);
        Assert.Single(device.IndependentLightingWrites);
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
