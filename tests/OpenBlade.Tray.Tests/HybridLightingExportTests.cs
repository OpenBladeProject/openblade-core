using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class HybridLightingExportTests
{
    public static TheoryData<int> PreviousExportVersions
    {
        get
        {
            var versions = new TheoryData<int>();
            foreach (int version in Enumerable.Range(1, 16))
            {
                versions.Add(version);
            }
            return versions;
        }
    }

    [Theory]
    [MemberData(nameof(PreviousExportVersions))]
    public void EveryPreviousExportKeepsKeyboardEffectControlUnset(int version)
    {
        ProfileSet original = ProfileDefaults.CreateSafeDefaults();
        JsonNode document = CurrentDocument(original);
        document["schemaVersion"] = version;
        ProfileSet imported = ProfileExportSerializer.Deserialize(document.ToJsonString());
        Assert.All(new[] { imported.PluggedIn.Lighting, imported.Battery.Lighting, imported.UsbC.Lighting }, lighting =>
        {
            Assert.False(lighting.ControlsKeyboardEffect);
            Assert.Null(lighting.IndependentControls);
        });
        Assert.Equal(original.PluggedIn.Lighting.Effect, imported.PluggedIn.Lighting.Effect);
        Assert.Equal(original.PluggedIn.Lighting.Brightness, imported.PluggedIn.Lighting.Brightness);
    }

    [Fact]
    public void CurrentExportPreservesOptInThroughTypedAndLegacyOffNormalization()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        LightingSettings typed = LightingSettingsFactory.Static(new(10, 20, 30), 65) with { ControlsKeyboardEffect = true };
        var legacyOff = new LightingSettings(LightingEffect.Off, default, 0)
        {
            ControlsKeyboardEffect = true,
            LogoMode = LogoLightingMode.Static,
        };
        ProfileSet source = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = typed },
            UsbC = defaults.UsbC with { Lighting = legacyOff },
        };
        JsonNode document = CurrentDocument(source);
        Assert.Equal(17, document["schemaVersion"]!.GetValue<int>());
        ProfileSet loaded = ProfileExportSerializer.Deserialize(document.ToJsonString());
        Assert.Equal(typed, loaded.PluggedIn.Lighting);
        Assert.Equal(legacyOff with { Parameters = LightingSettingsFactory.Off().Parameters }, loaded.UsbC.Lighting);
        Assert.False(loaded.Battery.Lighting.ControlsKeyboardEffect);
    }

    [Theory]
    [InlineData("controlsKeyboardEffect")]
    [InlineData("ControlsKeyboardEffect")]
    [InlineData("CONTROLSKEYBOARDEFFECT")]
    public void PreviousExportCannotIntroduceKeyboardEffectOptIn(string property)
    {
        JsonNode document = CurrentDocument(ProfileDefaults.CreateSafeDefaults());
        document["schemaVersion"] = 16;
        document["profiles"]!["pluggedIn"]!["lighting"]![property] = true;
        Assert.Throws<InvalidDataException>(() => ProfileExportSerializer.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void LegacyApplicationOverrideCannotHideOptInBehindDiscardedFields()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var definition = new ApplicationProfileDefinition(Guid.NewGuid(), "Game", @"C:\Games\game.exe",
            ApplicationActivationMode.WhileRunningAfterActivation, new ApplicationProfileOverrideSet
            {
                Battery = new ApplicationPowerProfileOverride
                {
                    Lighting = defaults.Battery.Lighting with { ControlsKeyboardEffect = true },
                },
            });
        JsonNode document = CurrentDocument(defaults with { ApplicationProfiles = [definition] });
        document["schemaVersion"] = 13;
        Assert.Throws<InvalidDataException>(() => ProfileExportSerializer.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void CurrentExportRejectsMalformedMarkerAndFutureEnvelope()
    {
        JsonNode document = CurrentDocument(ProfileDefaults.CreateSafeDefaults());
        document["profiles"]!["pluggedIn"]!["lighting"]!["controlsKeyboardEffect"] = "true";
        Assert.Throws<InvalidDataException>(() => ProfileExportSerializer.Deserialize(document.ToJsonString()));
        document["profiles"]!["pluggedIn"]!["lighting"]!.AsObject().Remove("controlsKeyboardEffect");
        document["schemaVersion"] = 18;
        Assert.Throws<InvalidDataException>(() => ProfileExportSerializer.Deserialize(document.ToJsonString()));
    }

    private static JsonNode CurrentDocument(ProfileSet profiles) => JsonNode.Parse(
        ProfileExportSerializer.Serialize(profiles, "1.0.0", DateTimeOffset.UtcNow))!;
}
