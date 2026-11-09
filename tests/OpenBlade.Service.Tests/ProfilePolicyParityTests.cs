using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.Testing;

namespace OpenBlade.Service.Tests;

public sealed class ProfilePolicyParityTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static IEnumerable<object[]> Cases() =>
        ParityFixture.Rows("profile-policy.json", "application-profile-merge", "cases");

    [Theory]
    [MemberData(nameof(Cases))]
    public void ApplicationMergeMatchesSharedContract(string id)
    {
        JsonElement row = ParityFixture.Row("profile-policy.json", "application-profile-merge", "cases", id);
        Projection? baselineFields = row.GetProperty("baseline").Deserialize<Projection>(Options);
        Projection? overrideFields = row.GetProperty("override").Deserialize<Projection>(Options);
        // These sentinel fields are outside the fixture slice. Whole-record equality
        // below also verifies that merging the selected fields preserves them.
        PowerProfile? baseline = baselineFields is null ? null : ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Performance = baselineFields.Performance!.Value,
            Fans = baselineFields.Fans!.Value,
            FanControl = ToFanControl(baselineFields.FanControl),
            Display = baselineFields.Display,
            LaptopBehavior = LaptopBehaviorProfileSettings.Unconfigured() with
            {
                GamingModeEnabled = baselineFields.GamingModeEnabled,
                GamingModeKeyPolicy = new GamingModeKeyPolicy(true, false),
            },
        };
        ApplicationPowerProfileOverride? application = overrideFields is null ? null : new()
        {
            Performance = overrideFields.Performance,
            Fans = overrideFields.Fans,
            FanControl = ToFanControl(overrideFields.FanControl),
            Display = overrideFields.Display,
            GamingModeEnabled = overrideFields.GamingModeEnabled,
        };

        if (row.GetProperty("expectedError").GetString() is string error)
        {
            Assert.Equal("baseline-null", error);
            Assert.Throws<ArgumentNullException>(() => ApplicationProfileResolution.Apply(baseline!, application));
            return;
        }

        Projection expectedFields = row.GetProperty("expected").Deserialize<Projection>(Options)!;
        PowerProfile expected = baseline! with
        {
            Performance = expectedFields.Performance!.Value,
            Fans = expectedFields.Fans!.Value,
            FanControl = ToFanControl(expectedFields.FanControl),
            Display = expectedFields.Display,
            LaptopBehavior = baseline!.EffectiveLaptopBehavior with
            {
                GamingModeEnabled = expectedFields.GamingModeEnabled,
            },
        };
        PowerProfile actual = ApplicationProfileResolution.Apply(baseline!, application);
        Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline!, application), actual);
        Assert.True(expected == actual, $"{id}: expected {expected}; got {actual}");
        if (application is null)
        {
            Assert.Same(baseline, actual);
        }
        else
        {
            DisplayProfileSettings display = ApplicationProfileResolution.ApplyDisplay(
                baseline!.Display ?? DisplayProfileSettings.Unconfigured(), application.Display);
            Assert.Equal(ManagedProfilePolicyBaseline.ApplyDisplay(baseline!.Display ?? DisplayProfileSettings.Unconfigured(), application.Display), display);
            Assert.True(expectedFields.Display == display, $"{id}: direct display merge differs");
        }
    }

    [Fact]
    public void ActivatedMergePreservesOpaqueUtf16AndOriginalReferences()
    {
        string inherited = new((char)0xD800, 1);
        string selected = new((char)0xDC00, 1);
        var curve = new ManualFanCurve([new(32, 2100), new(84, 5300)], [new(34, 2200), new(88, 5200)], false);
        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Display = new(inherited, null, inherited),
            FanControl = new(FanMode.ManualCurve, 3900, curve),
            ThxAudio = new(false, null!, null!, []),
            LightingPowerPolicy = new(true, 7),
        };
        var value = new ApplicationPowerProfileOverride { Display = new(selected, false, null) };
        PowerProfile actual = ApplicationProfileResolution.Apply(baseline, value);
        Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline, value), actual);
        Assert.Same(selected, actual.Display!.RefreshRateKey);
        Assert.Same(inherited, actual.Display.ColorProfileKey);
        Assert.Same(curve, actual.FanControl!.ManualCurve);
        Assert.Same(baseline.FanControl, actual.FanControl);
        Assert.Same(baseline.ThxAudio, actual.ThxAudio);
        Assert.Same(baseline.LightingPowerPolicy, actual.LightingPowerPolicy);
        Assert.Same(baseline, ApplicationProfileResolution.Apply(baseline, null));
    }

    [Fact]
    public void ActivatedPolicyPreservesNullAndLegacyExceptionBranches()
    {
        Assert.Equal("baseline", Assert.Throws<ArgumentNullException>(
            () => ApplicationProfileResolution.Apply(null!, null)).ParamName);
        Assert.Null(ApplicationProfileResolution.ApplyDisplay(null!, null));
        Assert.Null(ManagedProfilePolicyBaseline.ApplyDisplay(null!, null));
        var complete = new DisplayProfileSettings("", false, "") { BrightnessPercent = 0 };
        Assert.Equal(ManagedProfilePolicyBaseline.ApplyDisplay(null!, complete),
            ApplicationProfileResolution.ApplyDisplay(null!, complete));
        foreach (DisplayProfileSettings partial in new[]
        {
            complete with { RefreshRateKey = null },
            complete with { HdrEnabled = null },
            complete with { ColorProfileKey = null },
            complete with { BrightnessPercent = null },
        })
        {
            Assert.Throws<NullReferenceException>(() => ManagedProfilePolicyBaseline.ApplyDisplay(null!, partial));
            Assert.Throws<NullReferenceException>(() => ApplicationProfileResolution.ApplyDisplay(null!, partial));
        }

        PowerProfile baseline = ProfileDefaults.CreateSafeDefaults().PluggedIn with
        {
            Performance = (PerformancePreset)(-123),
            Fans = (FanMode)456,
            Display = null,
            LaptopBehavior = null,
            FanControl = null,
        };
        foreach (ApplicationPowerProfileOverride value in new ApplicationPowerProfileOverride[]
        {
            new(), new() { Performance = (PerformancePreset)999 },
            new() { Fans = (FanMode)999, FanControl = new(FanMode.Fixed, 2000, ManualFanCurve.Default) },
        })
        {
            Assert.Equal(ManagedProfilePolicyBaseline.Apply(baseline, value),
                ApplicationProfileResolution.Apply(baseline, value));
        }
        var invalidLegacy = new ApplicationPowerProfileOverride { Fans = (FanMode)999 };
        ArgumentOutOfRangeException expected = Assert.Throws<ArgumentOutOfRangeException>(
            () => ManagedProfilePolicyBaseline.Apply(baseline, invalidLegacy));
        ArgumentOutOfRangeException actual = Assert.Throws<ArgumentOutOfRangeException>(
            () => ApplicationProfileResolution.Apply(baseline, invalidLegacy));
        Assert.Equal(expected.ParamName, actual.ParamName);
        Assert.Equal(expected.ActualValue, actual.ActualValue);
        Assert.Equal(expected.Message, actual.Message);
    }

    private static FanControlSettings? ToFanControl(FanProjection? value) => value is null
        ? null : new(value.Mode, value.FixedSpeedRpm, ManualFanCurve.Default);

    [Theory]
    [InlineData("{\"performance\":\"2\"}")]
    [InlineData("{\"Performance\":2}")]
    [InlineData("{\"gamingModeEnable\":false}")]
    [InlineData("{\"display\":{\"HDRenabled\":false}}")]
    [InlineData("{\"performance\":1,\"performance\":2}")]
    public void ProjectionRejectsNonContractRepresentations(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Projection>(json, Options));
    }

    private sealed record FanProjection(FanMode Mode, int FixedSpeedRpm);

    private sealed record Projection(
        PerformancePreset? Performance,
        FanMode? Fans,
        FanProjection? FanControl,
        DisplayProfileSettings? Display,
        bool? GamingModeEnabled);
}
