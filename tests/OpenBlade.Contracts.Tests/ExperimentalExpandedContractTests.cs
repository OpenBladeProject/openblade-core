using System.Buffers.Binary;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class ExperimentalExpandedContractTests
{
    [Theory]
    [InlineData(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High, false)]
    [InlineData(CustomPerformanceLevel.High, CustomPerformanceLevel.High, false)]
    [InlineData(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High, true)]
    public async Task LegacyReadOnlyProjection_HidesUnrepresentableConfirmedCustomState(
        CustomPerformanceLevel cpu, CustomPerformanceLevel gpu, bool maximumFans)
    {
        var custom = new CustomPerformanceSettings(cpu, gpu) { MaximumFans = maximumFans };
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = custom,
            },
        };
        var desired = new DesiredDeviceState(
            new(PerformancePreset.Custom, SettingValueSource.PowerProfile),
            new(FanMode.Automatic, SettingValueSource.PowerProfile),
            new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
            new(profiles.BatteryPolicy, true, false))
        { CustomPerformance = custom };
        var capabilities = DeviceCapabilities.Unavailable() with
        {
            ReportsPerformanceWithManualFans = true,
            CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>
            {
                new(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High),
                new(CustomPerformanceLevel.High, CustomPerformanceLevel.High),
                new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High),
            })
            { SupportsMaximumFans = true },
        };
        var status = new ServiceStatusSnapshot(1, true, false, [], null, [], "Ready", capabilities,
            new ConfirmedDeviceState(PerformancePreset.Custom, FanMode.Automatic, null, null)
            { CustomPerformance = custom }, profiles, desired, PowerSource.PluggedIn, null,
            new(PerformancePreset.Custom, OverrideLifetime.Persistent, custom), null, null, false,
            ServiceVersion: "0.28.0-test");
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        byte[] bytes = stream.ToArray();
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        JsonObject root = JsonNode.Parse(bytes.AsSpan(sizeof(int), length))!.AsObject();
        JsonObject projected = root["status"]!.AsObject();
        Assert.True(projected["writesBlocked"]!.GetValue<bool>());
        Assert.Null(projected["confirmedState"]!["performance"]);
        Assert.Null(projected["confirmedState"]!["customPerformance"]);
        if (maximumFans)
        {
            Assert.Null(projected["confirmedState"]!["fans"]);
        }
        foreach (JsonNode? settings in new[]
        {
            projected["profiles"]!["pluggedIn"]!["customPerformance"],
            projected["desiredState"]!["customPerformance"],
            projected["performanceOverride"]!["customPerformance"],
        })
        {
            Assert.Equal((int)CustomPerformanceLevel.Medium, settings!["cpu"]!.GetValue<int>());
            Assert.False(settings.AsObject().ContainsKey("maximumFans"));
        }
        JsonObject customCapabilities = projected["capabilities"]!["customPerformance"]!.AsObject();
        Assert.False(projected["capabilities"]!.AsObject().ContainsKey("reportsPerformanceWithManualFans"));
        Assert.False(customCapabilities.ContainsKey("supportsMaximumFans"));
        Assert.Single(customCapabilities["allowedCombinations"]!.AsArray());
        // Projection is read-only and must not rewrite live desired or confirmed intent.
        Assert.Equal(custom, status.ConfirmedState!.CustomPerformance);
        Assert.Equal(custom, status.DesiredState.CustomPerformance);
    }

    [Fact]
    public async Task CpuBoostAndMaximumFans_RoundTripThroughTypedProfileRequest()
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High)
        { MaximumFans = true };
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = custom,
            },
        };
        var request = new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), profiles);
        using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        var decoded = Assert.IsType<UpdateProfilesRequest>(
            await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
        Assert.Equal(custom, decoded.Profiles.PluggedIn.CustomPerformance);
    }

    [Fact]
    public void ExplicitBreathingModes_DoNotGrantDualColorOnSingleZoneHardware()
    {
        var capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsBreathingColorSelection = true,
            SupportedBreathingColorModes = new HashSet<BreathingColorMode>
                { BreathingColorMode.Random, BreathingColorMode.Single },
        };
        Assert.True(capabilities.SupportsBreathingMode(BreathingColorMode.Random));
        Assert.True(capabilities.SupportsBreathingMode(BreathingColorMode.Single));
        Assert.False(capabilities.SupportsBreathingMode(BreathingColorMode.Dual));
        Assert.False(capabilities.SupportsBreathingMode((BreathingColorMode)999));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBreathingModeSet_PreservesLegacySelectionMeaning(bool selection)
    {
        var capabilities = DeviceCapabilities.Unavailable() with { SupportsBreathingColorSelection = selection };
        Assert.True(capabilities.SupportsBreathingMode(BreathingColorMode.Random));
        Assert.Equal(selection, capabilities.SupportsBreathingMode(BreathingColorMode.Single));
        Assert.Equal(selection, capabilities.SupportsBreathingMode(BreathingColorMode.Dual));
        Assert.False(capabilities.SupportsUniformLightingFrames);
        Assert.False(capabilities.ReportsPerformanceWithManualFans);
    }
}
