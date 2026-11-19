using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.ManualCurve)]
    public async Task Protocol51Projects5500RpmIntentIntoFrozenNumericBounds(FanMode mode)
    {
        ServiceStatusSnapshot baseline = CreateProtocol51PresetStatus(PerformancePreset.Balanced);
        var points = new[] { new FanCurvePoint(40, 4300), new FanCurvePoint(90, 5500) };
        var settings = new FanControlSettings(mode, 5500, new(points, points, true));
        var range = new FanSpeedRange(4300, 5500, 100);
        ServiceStatusSnapshot status = baseline with
        {
            Capabilities = baseline.Capabilities! with
            {
                FanSpeedRange = range,
                ObservedFanSpeedRangesByPerformancePreset = new Dictionary<PerformancePreset, FanSpeedRange>
                { [PerformancePreset.Balanced] = range },
            },
            Profiles = baseline.Profiles with
            {
                PluggedIn = baseline.Profiles.PluggedIn with { Fans = mode, FanControl = settings },
                Battery = baseline.Profiles.Battery with { Fans = mode, FanControl = settings },
                UsbC = baseline.Profiles.UsbC with { Fans = mode, FanControl = settings },
            },
            DesiredState = baseline.DesiredState with { Fans = new(mode, SettingValueSource.PowerProfile), FanControl = settings },
            FanControlOverride = settings,
        };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        Assert.True(ReadFrozenProtocol51Frame(stream).Status!.WritesBlocked);
        string json = FramedJsonObject(stream).ToJsonString();
        Assert.DoesNotContain("5500", json, StringComparison.Ordinal);
        Assert.DoesNotContain("observedFanSpeedRangesByPerformancePreset", json, StringComparison.Ordinal);
        Assert.Contains("5400", json, StringComparison.Ordinal);
        Assert.Equal(5500, status.DesiredState.FanControl!.FixedSpeedRpm);
        Assert.Equal(5500, status.Profiles.PluggedIn.FanControl!.ManualCurve.Cpu[^1].SpeedRpm);
    }

    [Theory]
    [InlineData(PerformancePreset.Turbo, false)]
    [InlineData(PerformancePreset.MaximumPower, false)]
    [InlineData(PerformancePreset.Gaming, false)]
    [InlineData(PerformancePreset.Creator, false)]
    [InlineData(PerformancePreset.Turbo, true)]
    [InlineData(PerformancePreset.MaximumPower, true)]
    [InlineData(PerformancePreset.Gaming, true)]
    [InlineData(PerformancePreset.Creator, true)]
    public async Task Protocol51_FrozenReaderAcceptsNewPresetProjectionWithoutFalseConfirmation(
        PerformancePreset preset, bool response)
    {
        ServiceStatusSnapshot status = CreateProtocol51PresetStatus(preset);
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            Protocol51Frame(status, preset, response), CancellationToken.None);

        Frozen51Frame legacy = ReadFrozenProtocol51Frame(stream);
        Frozen51Status projected = Assert.IsType<Frozen51Status>(legacy.Status);
        Assert.True(projected.WritesBlocked);
        Assert.Contains("Update or repair", projected.Message, StringComparison.Ordinal);
        Assert.Null(projected.ConfirmedState!.Performance);
        Assert.Null(projected.PerformanceOverride);
        Assert.Null(projected.PerformanceShortcutPreview);
        Assert.Equal(Frozen51Preset.Balanced, projected.DesiredState!.Performance!.Value);
        Assert.Equal(Frozen51Preset.Balanced, projected.Profiles!.PluggedIn!.Performance);
        Assert.Equal(Frozen51Preset.BatterySaver, projected.Profiles.Battery!.Performance);
        Assert.Equal(Frozen51Preset.Balanced, projected.Profiles.UsbC!.Performance);
        Assert.Equal(new[] { Frozen51Preset.Silent, Frozen51Preset.Balanced },
            projected.Capabilities!.PerformancePresets!.OrderBy(value => (int)value));
        Assert.Equal(Frozen51Preset.Balanced,
            Assert.Single(projected.Capabilities.ObservedPowerSourceBehaviors!).SelectedPerformance);
        Assert.Equal(2300, projected.LiveReadings.GetProperty("fan1Rpm").GetInt32());
        Assert.True(projected.KeyboardDriverModeActive.GetBoolean());
        Assert.Equal("0.1.0-test", projected.ServiceVersion.GetString());
        Frozen51WindowsPowerModes associations = projected.Profiles.WindowsPowerModes!;
        Assert.Equal(new int?[] { 0, 1, 2, 2, 1, 0 },
            new[] { associations.Silent, associations.Balanced, associations.Performance,
                associations.BatterySaver, associations.BatteryBalanced, associations.Custom });
        Assert.Equal("known-refresh", projected.Profiles.PluggedIn.Display.GetProperty("refreshRateKey").GetString());
        var root = FramedJsonObject(stream);
        Assert.False(root["status"]!["profiles"]!.AsObject().ContainsKey("applicationProfiles"));
        Assert.False(root["status"]!.AsObject().ContainsKey("experimentalSupportSource"));
        Assert.False(root["status"]!.AsObject().ContainsKey("supportBasis"));
        Assert.NotNull(status.CpuTuning);
        Assert.False(root["status"]!.AsObject().ContainsKey("cpuTuning"));
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    [InlineData(PerformancePreset.Custom)]
    public async Task Protocol51_FrozenReaderPreservesKnownPresetState(PerformancePreset preset)
    {
        ServiceStatusSnapshot status = CreateProtocol51PresetStatus(preset);
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            Protocol51Frame(status, preset, response: true), CancellationToken.None);

        Frozen51Frame legacy = ReadFrozenProtocol51Frame(stream);
        Frozen51Status projected = legacy.Status!;
        Assert.True(projected.WritesBlocked);
        Assert.Equal((Frozen51Preset)(int)preset, projected.ConfirmedState!.Performance);
        Assert.Equal((Frozen51Preset)(int)preset, projected.DesiredState!.Performance!.Value);
        Assert.Equal((Frozen51Preset)(int)preset, projected.Profiles!.PluggedIn!.Performance);
        Assert.Equal((Frozen51Preset)(int)preset, projected.PerformanceOverride!.Value);
        Assert.Equal((Frozen51Preset)(int)preset, projected.PerformanceShortcutPreview!.Preset);
    }

    [Fact]
    public async Task Protocol51_FrozenReaderRejectsUnknownEnumsAndMembersAndProjectionOmitsNullAssociations()
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateStatus()), CancellationToken.None);
        Frozen51Frame legacy = ReadFrozenProtocol51Frame(stream);
        Assert.True(legacy.Status!.WritesBlocked);
        var associations = FramedJsonObject(stream)["status"]!["profiles"]!["windowsPowerModes"]!.AsObject();
        foreach (string member in new[] { "turbo", "maximumPower", "gaming", "creator" })
        {
            Assert.False(associations.ContainsKey(member));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Frozen51WindowsPowerModes>(
                "{\"" + member + "\":null}", Frozen51Options));
        }
        foreach (int value in new[] { 6, 7, 8, 9 })
        {
            Assert.Throws<JsonException>(() => RequireFrozen51Preset((Frozen51Preset)value));
        }
    }

    [Theory]
    [InlineData(PerformancePreset.Turbo, false)]
    [InlineData(PerformancePreset.MaximumPower, false)]
    [InlineData(PerformancePreset.Gaming, false)]
    [InlineData(PerformancePreset.Creator, false)]
    [InlineData(PerformancePreset.Silent, true)]
    [InlineData(PerformancePreset.Balanced, true)]
    [InlineData(PerformancePreset.Performance, true)]
    [InlineData(PerformancePreset.BatterySaver, true)]
    [InlineData(PerformancePreset.BatteryBalanced, true)]
    [InlineData(PerformancePreset.Custom, true)]
    public async Task Protocol51_FrozenReaderProjectsResultPresetOnlyWhenRepresentable(
        PerformancePreset preset, bool representable)
    {
        using MemoryStream stream = new();
        var result = new ControlOperationResult(ControlOperationStatus.Applied, "Known result")
        {
            HyperBoostRestoredPerformance = preset,
            CpuTuning = CreateProtocol51PresetStatus(preset).CpuTuning,
        };
        Assert.NotNull(result.CpuTuning);
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceResponse(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true, null, null, result),
            CancellationToken.None);
        Frozen51Frame legacy = ReadFrozenProtocol51Frame(stream);
        Assert.Equal(representable ? (Frozen51Preset?)(int)preset : null,
            legacy.Result!.HyperBoostRestoredPerformance);
        Assert.Equal("Known result", legacy.Result.Message.GetString());
        Assert.False(FramedJsonObject(stream)["result"]!.AsObject().ContainsKey("cpuTuning"));
    }

    [Theory]
    [InlineData(PerformancePreset.Turbo)]
    [InlineData(PerformancePreset.MaximumPower)]
    [InlineData(PerformancePreset.Gaming)]
    [InlineData(PerformancePreset.Creator)]
    public async Task Protocol51_FrozenReaderDoesNotLabelDesiredPlaceholderAsConfirmed(PerformancePreset preset)
    {
        ServiceStatusSnapshot status = CreateProtocol51PresetStatus(preset);
        status = status with
        {
            DesiredState = status.DesiredState with
            {
                Performance = status.DesiredState.Performance with { Source = SettingValueSource.ConfirmedFirmwareState },
            },
        };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        Frozen51Status legacy = ReadFrozenProtocol51Frame(stream).Status!;
        Assert.True(legacy.WritesBlocked);
        Assert.Null(legacy.ConfirmedState!.Performance);
        Assert.Equal(Frozen51SettingSource.PowerProfile, legacy.DesiredState!.Performance!.Source);
    }

    [Theory]
    [InlineData(SettingValueSource.ConfirmedFirmwareState, 0)]
    [InlineData(SettingValueSource.PowerProfile, 1)]
    [InlineData(SettingValueSource.ApplicationProfile, 1)]
    [InlineData(SettingValueSource.TemporaryOverride, 2)]
    [InlineData(SettingValueSource.PersistentOverride, 3)]
    public async Task Protocol51_FrozenReaderPreservesOriginalSettingSourceNumbers(
        SettingValueSource source, int legacySource)
    {
        ServiceStatusSnapshot status = CreateStatus();
        status = status with
        {
            DesiredState = status.DesiredState with
            {
                Performance = status.DesiredState.Performance with { Source = source },
                Fans = status.DesiredState.Fans with { Source = source },
                Lighting = status.DesiredState.Lighting with { Source = source },
            },
        };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        Frozen51Status legacy = ReadFrozenProtocol51Frame(stream).Status!;
        Assert.True(legacy.WritesBlocked);
        Assert.Equal(legacySource, (int)legacy.DesiredState!.Performance!.Source);
        Assert.Equal(legacySource, legacy.DesiredState.Fans.GetProperty("source").GetInt32());
        Assert.Equal(legacySource, legacy.DesiredState.Lighting.GetProperty("source").GetInt32());
    }

    private static ServiceServerFrame Protocol51Frame(
        ServiceStatusSnapshot status, PerformancePreset preset, bool response) => response
        ? new ServiceResponse(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true, null, status, null)
        : new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status);

    private static ServiceStatusSnapshot CreateProtocol51PresetStatus(PerformancePreset preset)
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        CustomPerformanceSettings? custom = preset == PerformancePreset.Custom
            ? CustomPerformanceSettings.Default : null;
        PowerProfile profile = baseline.Profiles.PluggedIn with
        {
            Performance = preset,
            Display = new DisplayProfileSettings("known-refresh", true, null),
        };
        var rule = new ApplicationProfileDefinition(Guid.NewGuid(), "Known rule", @"C:\Games\game.exe",
            ApplicationActivationMode.WhileRunningAfterActivation, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride { Performance = preset },
            });
        ServiceStatusSnapshot status = baseline with
        {
            WritesBlocked = false,
            Capabilities = baseline.Capabilities! with
            {
                PerformancePresets = new HashSet<PerformancePreset>
                {
                    PerformancePreset.Silent, PerformancePreset.Balanced, preset,
                },
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite)
                {
                    Custom = DeviceFeatureAccess.ReadWrite,
                    CoolingPadEntryBaseMode = PerformancePreset.Balanced,
                },
                ObservedPowerSourceBehaviors =
                [
                    new(PowerSource.PluggedIn, PerformancePreset.Balanced, FanMode.Automatic,
                        null, null, null, null),
                    new(PowerSource.Battery, preset, FanMode.Automatic, null, null, null, null),
                ],
            },
            ConfirmedState = new ConfirmedDeviceState(preset, FanMode.Automatic, null, null)
            {
                CustomPerformance = custom,
            },
            Profiles = baseline.Profiles with
            {
                PluggedIn = profile,
                Battery = profile,
                UsbC = profile,
                ApplicationProfiles = [rule],
                WindowsPowerModes = new WindowsPowerModeAssociations(
                    WindowsPowerMode.BestPowerEfficiency, WindowsPowerMode.Balanced,
                    WindowsPowerMode.BestPerformance, WindowsPowerMode.BestPerformance,
                    WindowsPowerMode.Balanced, WindowsPowerMode.BestPowerEfficiency)
                {
                    Turbo = WindowsPowerMode.BestPerformance,
                    MaximumPower = WindowsPowerMode.BestPerformance,
                    Gaming = WindowsPowerMode.Balanced,
                    Creator = WindowsPowerMode.BestPowerEfficiency,
                },
            },
            DesiredState = baseline.DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(preset, SettingValueSource.PowerProfile),
                CustomPerformance = custom,
            },
            PerformanceOverride = new PerformanceOverride(preset, OverrideLifetime.Persistent, custom),
            PerformanceShortcutPreview = new PerformanceShortcutPreview(Guid.NewGuid(), 1, preset),
            IsExperimentalDevice = true,
            ExperimentalSupportSource = "SourceReviewed",
            CpuTuning = new(
                new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, -10, [])),
                new(new(65, 65123, 95001), [new(0, -10), new(1, 5)]),
                new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.Available),
                new(DeviceFeatureAccess.ObservedOnly, CpuTuningBackendAvailability.MissingDependency),
                CpuTuningPendingBoot.Requested, false),
        };
        Assert.True(status.CpuTuning.IsWellFormed);
        return status;
    }

    // Frozen affected-object schema from protocol 51 commit
    // a7c3c1bceadabcc3f30d925142e6afbf8807fff1 (Profiles, DeviceState, Overrides,
    // Automation and ServiceMessages). Unrelated known payloads stay JsonElement;
    // every affected container rejects unknown members, and preset validation uses
    // this frozen enum rather than today's Contracts or today's IPC reader.
    private enum Frozen51SettingSource
    {
        ConfirmedFirmwareState = 0,
        PowerProfile = 1,
        TemporaryOverride = 2,
        PersistentOverride = 3,
    }

    private enum Frozen51Preset
    {
        Silent = 0,
        Balanced = 1,
        Performance = 2,
        BatterySaver = 3,
        BatteryBalanced = 4,
        Custom = 5,
    }

    private static readonly JsonSerializerOptions Frozen51Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    private static Frozen51Frame ReadFrozenProtocol51Frame(MemoryStream stream)
    {
        byte[] bytes = stream.ToArray();
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (length != bytes.Length - sizeof(int))
        {
            throw new JsonException("Invalid frozen v51 frame length.");
        }
        Frozen51Frame frame = JsonSerializer.Deserialize<Frozen51Frame>(
            bytes.AsSpan(sizeof(int), length), Frozen51Options)!;
        if (frame.Version != 51 || frame.Frame is not ("statusEvent" or "response"))
        {
            throw new JsonException("Invalid frozen v51 frame version or kind.");
        }
        if (frame.Result?.HyperBoostRestoredPerformance is { } restored)
        {
            RequireFrozen51Preset(restored);
        }
        if (frame.Status is not { } status)
        {
            return frame;
        }
        if (status.Capabilities is { } capabilities)
        {
            foreach (Frozen51Preset value in capabilities.PerformancePresets ?? [])
            {
                RequireFrozen51Preset(value);
            }
            foreach (Frozen51Behavior value in capabilities.ObservedPowerSourceBehaviors ?? [])
            {
                RequireFrozen51Preset(value.SelectedPerformance);
            }
        }
        if (status.ConfirmedState?.Performance is { } confirmed)
        {
            RequireFrozen51Preset(confirmed);
        }
        RequireFrozen51Preset(status.DesiredState!.Performance!.Value);
        RequireFrozen51Source((int)status.DesiredState.Performance.Source);
        RequireFrozen51Source(status.DesiredState.Fans.GetProperty("source").GetInt32());
        RequireFrozen51Source(status.DesiredState.Lighting.GetProperty("source").GetInt32());
        RequireFrozen51Preset(status.Profiles!.PluggedIn!.Performance);
        RequireFrozen51Preset(status.Profiles.Battery!.Performance);
        RequireFrozen51Preset(status.Profiles.UsbC!.Performance);
        if (status.PerformanceOverride is { } performanceOverride)
        {
            RequireFrozen51Preset(performanceOverride.Value);
        }
        if (status.PerformanceShortcutPreview is { } preview)
        {
            RequireFrozen51Preset(preview.Preset);
        }
        return frame;
    }

    private static void RequireFrozen51Source(int value)
    {
        if (!Enum.IsDefined((Frozen51SettingSource)value))
        {
            throw new JsonException("The frozen v51 reader rejects undefined setting sources.");
        }
    }

    private static void RequireFrozen51Preset(Frozen51Preset value)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException("The frozen v51 reader rejects undefined performance presets.");
        }
    }

    private sealed class Frozen51Frame
    {
        public string? Frame { get; init; }

        public int Version { get; init; }

        public Guid RequestId { get; init; }

        public bool Success { get; init; }

        public string? Error { get; init; }

        public Frozen51Status? Status { get; init; }

        public Frozen51Result? Result { get; init; }

        public JsonElement Archive { get; init; }

        public JsonElement ErrorCode { get; init; }
    }

    private sealed class Frozen51Status
    {
        public JsonElement Revision { get; init; }

        public JsonElement DeviceVerified { get; init; }

        public bool WritesBlocked { get; init; }

        public JsonElement PotentialControllers { get; init; }

        public JsonElement ControllerDetectionWarning { get; init; }

        public JsonElement ExternalChanges { get; init; }

        public string Message { get; init; } = "";

        public Frozen51Capabilities? Capabilities { get; init; }

        public Frozen51Confirmed? ConfirmedState { get; init; }

        public Frozen51Profiles? Profiles { get; init; }

        public Frozen51Desired? DesiredState { get; init; }

        public JsonElement ActivePowerSource { get; init; }

        public JsonElement LiveReadings { get; init; }

        public Frozen51Override? PerformanceOverride { get; init; }

        public JsonElement FanOverride { get; init; }

        public JsonElement LightingOverride { get; init; }

        public JsonElement FullChargeOverride { get; init; }

        public JsonElement Firmware { get; init; }

        public JsonElement ServiceVersion { get; init; }

        public JsonElement LightingRendererOwnership { get; init; }

        public JsonElement LightingSuppressions { get; init; }

        public JsonElement StartupAnimationEnabled { get; init; }

        public JsonElement FunctionKeyPrimary { get; init; }

        public JsonElement GamingModeEnabled { get; init; }

        public JsonElement AlwaysOverrideExternalLightingChanges { get; init; }

        public JsonElement KeyboardDriverModeActive { get; init; }

        public JsonElement BatteryHealth { get; init; }

        public JsonElement KeyboardLightingControlMode { get; init; }

        public JsonElement ControlAccess { get; init; }

        public JsonElement AllowUntestedFirmware { get; init; }

        public JsonElement FirmwareCompatibility { get; init; }

        public JsonElement DisplayBrightnessHidAvailability { get; init; }

        public JsonElement LightingRefreshRate { get; init; }

        public JsonElement UnconfirmedWrites { get; init; }

        public JsonElement FanControlOverride { get; init; }

        public Frozen51Preview? PerformanceShortcutPreview { get; init; }

        public JsonElement BatteryPolicyOverride { get; init; }

        public JsonElement BatteryProtectionOverride { get; init; }

        public JsonElement PadlessHyperBoostEligibility { get; init; }

        public JsonElement HyperBoostRestorePending { get; init; }
    }

    private sealed class Frozen51HyperBoostBaseModes
    {
        public int Silent { get; init; }

        public int Balanced { get; init; }

        public int Performance { get; init; }
    }

    private sealed class Frozen51Capabilities
    {
        public Frozen51Preset[]? PerformancePresets { get; init; }

        public JsonElement FanModes { get; init; }

        public JsonElement LightingEffects { get; init; }

        public JsonElement ChargeLimits { get; init; }

        public JsonElement SupportsBatteryProtectionOff { get; init; }

        public JsonElement SupportsTemporaryFullCharge { get; init; }

        public JsonElement SupportsKeyboardBrightness { get; init; }

        public JsonElement SupportsPlaybackMuteIndicator { get; init; }

        public JsonElement FanSpeedRange { get; init; }

        public JsonElement CustomPerformance { get; init; }

        public JsonElement CpuPowerLimit { get; init; }

        public JsonElement CpuVoltageOptimizer { get; init; }

        public JsonElement GpuOverclock { get; init; }

        public JsonElement HyperBoostAccess { get; init; }

        public JsonElement HyperBoostWithoutCoolingPadAccess { get; init; }

        public Frozen51HyperBoostBaseModes? HyperBoostBaseModes { get; init; }

        public JsonElement SpecialKeyBindingsAccess { get; init; }

        public JsonElement WritableSpecialKeySlots { get; init; }

        public JsonElement LampArrayLightingAccess { get; init; }

        public JsonElement KeyboardLightingLayoutId { get; init; }

        public JsonElement SupportedLightingRefreshRates { get; init; }

        public JsonElement ObservedLightingEffects { get; init; }

        public Frozen51Behavior[]? ObservedPowerSourceBehaviors { get; init; }

        public JsonElement LogoLightingModes { get; init; }

        public JsonElement OneShotLaptopControls { get; init; }
    }

    private sealed class Frozen51Behavior
    {
        public JsonElement Source { get; init; }

        public Frozen51Preset SelectedPerformance { get; init; }

        public JsonElement SelectedFans { get; init; }

        public JsonElement FixedFanControlAvailable { get; init; }

        public JsonElement PerformanceModeAvailable { get; init; }

        public JsonElement CustomModeAvailable { get; init; }

        public JsonElement InadequatePowerWarningVisible { get; init; }

        public JsonElement IsWellFormed { get; init; }
    }

    private sealed class Frozen51Confirmed
    {
        public Frozen51Preset? Performance { get; init; }

        public JsonElement Fans { get; init; }

        public JsonElement Battery { get; init; }

        public JsonElement Lighting { get; init; }

        public JsonElement HyperBoostActive { get; init; }

        public JsonElement CustomPerformance { get; init; }

        public JsonElement GpuClockOffsets { get; init; }

        public JsonElement LogoLighting { get; init; }
    }

    private sealed class Frozen51Profiles
    {
        public Frozen51Profile? PluggedIn { get; init; }

        public Frozen51Profile? Battery { get; init; }

        public Frozen51Profile? UsbC { get; init; }

        public JsonElement BatteryPolicy { get; init; }

        public JsonElement LightingPowerPolicy { get; init; }

        public Frozen51WindowsPowerModes? WindowsPowerModes { get; init; }
    }

    private sealed class Frozen51Profile
    {
        public Frozen51Preset Performance { get; init; }

        public JsonElement Fans { get; init; }

        public JsonElement Lighting { get; init; }

        public JsonElement Display { get; init; }

        public JsonElement CustomPerformance { get; init; }

        public JsonElement GpuClockTuning { get; init; }

        public JsonElement FanControl { get; init; }

        public JsonElement LightingPowerPolicy { get; init; }

        public JsonElement LaptopBehavior { get; init; }

        public JsonElement BatteryProtectionEnabled { get; init; }

        public JsonElement WindowsPowerMode { get; init; }
    }

    private sealed class Frozen51WindowsPowerModes
    {
        public int? Silent { get; init; }

        public int? Balanced { get; init; }

        public int? Performance { get; init; }

        public int? BatterySaver { get; init; }

        public int? BatteryBalanced { get; init; }

        public int? Custom { get; init; }
    }

    private sealed class Frozen51Desired
    {
        public Frozen51ResolvedPerformance? Performance { get; init; }

        public JsonElement Fans { get; init; }

        public JsonElement Lighting { get; init; }

        public JsonElement Battery { get; init; }

        public JsonElement CustomPerformance { get; init; }

        public JsonElement GpuClockTuning { get; init; }

        public JsonElement FanControl { get; init; }
    }

    private sealed class Frozen51ResolvedPerformance
    {
        public Frozen51Preset Value { get; init; }

        public Frozen51SettingSource Source { get; init; }
    }

    private sealed class Frozen51Override
    {
        public Frozen51Preset Value { get; init; }

        public JsonElement Lifetime { get; init; }

        public JsonElement CustomPerformance { get; init; }

        public JsonElement GpuClockTuning { get; init; }
    }

    private sealed class Frozen51Preview
    {
        public JsonElement SessionId { get; init; }

        public JsonElement Generation { get; init; }

        public Frozen51Preset Preset { get; init; }
    }

    private sealed class Frozen51Result
    {
        public JsonElement Status { get; init; }

        public JsonElement Message { get; init; }

        public Frozen51Preset? HyperBoostRestoredPerformance { get; init; }
    }
}
