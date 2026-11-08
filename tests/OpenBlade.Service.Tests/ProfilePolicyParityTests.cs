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
        Assert.True(expected == actual, $"{id}: expected {expected}; got {actual}");
        if (application is null)
        {
            Assert.Same(baseline, actual);
        }
        else
        {
            DisplayProfileSettings display = ApplicationProfileResolution.ApplyDisplay(
                baseline!.Display ?? DisplayProfileSettings.Unconfigured(), application.Display);
            Assert.True(expectedFields.Display == display, $"{id}: direct display merge differs");
        }
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
