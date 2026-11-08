using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class IndependentLightingControlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentFieldsPreserveUnknownEffectAndUnrequestedField(bool brightnessOnly)
    {
        BatteryTestDevice device = CreateDevice();
        var requested = brightnessOnly ? new IndependentLightingSettings(70, null) : new(null, LogoLightingMode.Off);
        LightingSettings intent = LightingSettingsFactory.SpectrumCycling(80) with { IndependentControls = requested };
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(intent, default);
        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.Single(device.IndependentLightingWrites);
        Assert.Null(device.State.Lighting);
        Assert.Equal<byte?>((byte)(brightnessOnly ? 70 : 50), device.State.IndependentLighting!.Brightness);
        Assert.Equal(brightnessOnly ? LogoLightingMode.Static : LogoLightingMode.Off, device.State.IndependentLighting.LogoMode);
    }

    [Fact]
    public async Task OldProfileWithoutIndependentOptInDoesNotWriteOrInventEffect()
    {
        BatteryTestDevice device = CreateDevice();
        device.State = device.State with { IndependentLighting = null };
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(
            LightingSettingsFactory.SpectrumCycling(80), default);
        Assert.Equal(ControlResultStatus.Unchanged, outcome.Result.Status);
        Assert.Empty(device.IndependentLightingWrites);
        Assert.Null(device.State.Lighting);
        Assert.Null(device.State.IndependentLighting);
    }

    [Fact]
    public async Task SetterCompletionCannotConfirmUnknownIndependentReadback()
    {
        BatteryTestDevice device = CreateDevice();
        device.AutoConfirmWrites = false;
        device.State = device.State with { IndependentLighting = null, LogoLighting = null };
        ScopedApplyOutcome outcome = await new ControlCoordinator(device).ApplyLightingOutcomeAsync(
            LightingSettingsFactory.SpectrumCycling(80) with { IndependentControls = new(70, null) }, default);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.NotEqual(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Null(device.State.Lighting);
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    public void ProfileSchemaPreservesExplicitOptInOnlyInCurrentVersion(int schema, bool configured)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        IndependentLightingSettings? controls = configured ? new(70, null) : null;
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = defaults.PluggedIn.Lighting with { IndependentControls = controls } },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = schema, profiles }, ProfileStore.SerializerOptions);
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "independent-profile-not-written.json"));
        ProfileSet loaded = store.ReadDocument(json, out bool migrated, out _, legacyBatteryProtectionDefault: true);
        Assert.Equal(controls, loaded.PluggedIn.Lighting.IndependentControls);
        Assert.Equal(schema != ProfileStore.CurrentSchemaVersion, migrated);
    }

    [Theory]
    [InlineData("independentControls")]
    [InlineData("IndependentControls")]
    [InlineData("INDEPENDENTCONTROLS")]
    public void PreviousSchemaCannotSmuggleNewWriteIntent(string propertyName)
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = defaults.PluggedIn.Lighting with { IndependentControls = new(70, null) } },
        };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 23, profiles }, ProfileStore.SerializerOptions);
        json = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(json).Replace("independentControls", propertyName, StringComparison.Ordinal));
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "independent-profile-not-written.json"));
        Assert.Throws<InvalidDataException>(() => store.ReadDocument(json, out _, out _, true));
    }

    private static BatteryTestDevice CreateDevice() => new()
    {
        Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsKeyboardBrightness = true,
            SupportsIndependentLightingControls = true,
            LightingEffects = new HashSet<LightingEffect>(),
            LogoLightingModes = Enum.GetValues<LogoLightingMode>().ToHashSet(),
        },
        State = new(null, null, null, null)
        {
            IndependentLighting = new(50, LogoLightingMode.Static),
            LogoLighting = LogoLightingMode.Static,
        },
    };
}
