using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class IpcMessageCodecTests
{
    [Fact]
    public async Task AmbientLightingFrameRequest_RoundTripsOnlyKeyboardSizedRgbData()
    {
        RgbColor[] colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[1] = new RgbColor(12, 34, 56);
        var expected = new PushAmbientLightingFrameRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new AmbientLightingFrame(colors).ToRgbBytes());
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, expected, CancellationToken.None);
        Assert.True(stream.Length < 700, $"Compact ambient request was {stream.Length} bytes.");
        stream.Position = 0;
        var actual = Assert.IsType<PushAmbientLightingFrameRequest>(
            await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(expected.RgbBytes, actual.RgbBytes);
        AmbientLightingFrame actualFrame = AmbientLightingFrame.FromRgbBytes(actual.RgbBytes);
        Assert.Equal(colors, actualFrame.Slots);
        Assert.True(actualFrame.IsWellFormed());
    }

    [Fact]
    public async Task AmbientLightingFrameRequest_RejectsColorInLayoutHole()
    {
        byte[] bytes = new byte[AmbientLightingFrame.RgbByteCount];
        bytes[0] = 1;
        var request = new PushAmbientLightingFrameRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            bytes);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    public static TheoryData<ServiceRequest> Requests => new()
    {
        new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
        new ObserveStateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            IncludeLiveReadings: true)
        {
            RequestedLiveReadings = LiveReadingSensors.BatteryPower
                | LiveReadingSensors.CpuTemperature,
        },
        new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
        new ExportDiagnosticsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LiveReadingsEnabled: true),
        new UpdateProfilesRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            ProfileDefaults.CreateSafeDefaults()),
        new SetPerformanceOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent)),
        new CyclePerformanceShortcutRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            PerformanceShortcutSource.FunctionP),
        new SetPerformanceShortcutPreviewRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Generation: 4,
            PerformancePreset.Performance),
        new SetFanOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange)),
        new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(
                new LightingSettings(LightingEffect.Off, new RgbColor(0, 0, 0), 0),
                OverrideLifetime.Persistent)),
        new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.BreathingDual(
                    new RgbColor(12, 34, 56),
                    new RgbColor(78, 90, 123),
                    65),
                OverrideLifetime.UntilPowerSourceChange)),
        new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.AudioMeter(
                    colorBoost: 1.5,
                    brightness: 60,
                    decayMilliseconds: 750),
                OverrideLifetime.Persistent)),
        new StepKeyboardBrightnessRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            KeyboardBrightnessDirection.Decrease,
            OverrideLifetime.UntilPowerSourceChange),
        new SetDisplayBrightnessHidUsageRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            DisplayBrightnessHidUsage.Increment),
        new SetExternalLightingOverridePolicyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: true),
        new SetLightingRefreshRateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LightingRefreshRate.SixtyHz),
        new SetFirmwareCompatibilityOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: true),
        new ReapplyChangedSettingsRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid()),
        new SetLightingSuppressionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LightingSuppressionReason.UserIdle,
            Suppressed: true),
        new PulseReactiveKeyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ReactiveKeyPulse(77)),
        new SetPlaybackMuteIndicatorRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Muted: true),
        new SetBatteryPolicyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            BatteryPolicy.Create(75)),
        new SetBatteryProtectionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: false),
        new SetFullChargeOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
        new SetFullChargeOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false),
        new ApplyOneShotLaptopControlRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ApplyOneShotLaptopControl(
                OneShotLaptopControl.StartupAnimation,
                Enabled: true)),
        new ApplyOneShotLaptopControlRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ApplyOneShotLaptopControl(
                OneShotLaptopControl.FunctionKeyPrimary,
                FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys)),
        new ShutdownOpenBladeRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
    };

    [Fact]
    public async Task AudioMeterDecay_DefaultsToOffWhenOlderPeerOmitsProperty()
    {
        var request = new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.AudioMeter(1, 50, 750),
                OverrideLifetime.Persistent));
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(encoded, request, CancellationToken.None);
        byte[] framedBytes = encoded.ToArray();
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(framedBytes);
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(
                framedBytes,
                sizeof(int),
                payloadLength)));
        JsonObject value = Assert.IsType<JsonObject>(root["value"]);
        JsonObject lighting = Assert.IsType<JsonObject>(value["value"]);
        JsonObject parameters = Assert.IsType<JsonObject>(lighting["parameters"]);
        Assert.True(parameters.Remove("decayMilliseconds"));

        SetLightingOverrideRequest decoded = Assert.IsType<SetLightingOverrideRequest>(
            await IpcMessageCodec.ReadRequestAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));

        Assert.Equal(
            LightingSettingsFactory.DefaultAudioMeterDecayMilliseconds,
            Assert.IsType<AudioMeterLightingParameters>(decoded.Value?.Value.Parameters)
                .DecayMilliseconds);
    }

    [Fact]
    public async Task ReactivePulseRequest_RejectsSlotOutsideCapturedMatrix()
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $$"""
            {
              "operation":"pulseReactiveKey",
              "version":{{OpenBladeIpc.ProtocolVersion}},
              "requestId":"{{requestId}}",
              "pulse":{"matrixSlot":102}
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task LightingSuppressionRequest_RejectsUnknownReason()
    {
        var request = new SetLightingSuppressionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            (LightingSuppressionReason)999,
            Suppressed: true);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task DisplayBrightnessHidUsageRequest_RejectsUnknownUsage()
    {
        var request = new SetDisplayBrightnessHidUsageRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            (DisplayBrightnessHidUsage)0x0071);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task PerformanceShortcutRequest_RejectsUnknownSource()
    {
        var request = new CyclePerformanceShortcutRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            (PerformanceShortcutSource)999);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("", 1, (int)PerformancePreset.Performance)]
    [InlineData("50c08f46-0a13-4df1-a921-4391891abdf0", 0, (int)PerformancePreset.Performance)]
    [InlineData("50c08f46-0a13-4df1-a921-4391891abdf0", 1, 999)]
    public async Task PerformanceShortcutPreviewRequest_RejectsInvalidIdentityOrPreset(
        string sessionId,
        long generation,
        int preset)
    {
        var request = new SetPerformanceShortcutPreviewRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            string.IsNullOrEmpty(sessionId) ? Guid.Empty : Guid.Parse(sessionId),
            generation,
            (PerformancePreset)preset);
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task ObserveStateRequest_RejectsUnknownSensorFlags()
    {
        var request = new ObserveStateRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            IncludeLiveReadings: true)
        {
            RequestedLiveReadings = (LiveReadingSensors)(1 << 10),
        };
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public async Task RequestHelpers_RoundTripEveryDerivedRequestThroughBaseType(
        ServiceRequest expected)
    {
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        ServiceRequest actual = await IpcMessageCodec.ReadRequestAsync(
            stream,
            CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(expected.GetType(), actual.GetType());
    }

    [Fact]
    public async Task LightingRequestRejectsMismatchedTypedEffectParameters()
    {
        var malformed = new LightingSettings(
            LightingEffect.Static,
            new RgbColor(12, 34, 56),
            64)
        {
            Parameters = new SpectrumCyclingLightingParameters(),
        };
        var request = new SetLightingOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ManualOverride<LightingSettings>(
                malformed,
                OverrideLifetime.Persistent));

        using MemoryStream stream = new();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public async Task LightingRequestAcceptsLegacyShapeWithoutParameterMember()
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $$"""
            {
              "operation":"setLightingOverride",
              "version":{{OpenBladeIpc.ProtocolVersion}},
              "requestId":"{{requestId}}",
              "value":{
                "value":{
                  "effect":1,
                  "color":{"red":12,"green":34,"blue":56},
                  "brightness":64
                },
                "lifetime":1
              }
            }
            """);

        SetLightingOverrideRequest request = Assert.IsType<SetLightingOverrideRequest>(
            await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.Equal(requestId, request.RequestId);
        Assert.Equal(LightingEffect.Static, request.Value!.Value.Effect);
        Assert.Null(request.Value.Value.Parameters);
        Assert.True(LightingSettingsValidation.IsWellFormed(request.Value.Value));
    }

    [Fact]
    public async Task VersionFortySixBatteryContracts_RoundTripExactShape()
    {
        var capabilities = new DeviceCapabilities(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(),
            new HashSet<int> { 80 },
            SupportsBatteryProtectionOff: true,
            SupportsTemporaryFullCharge: false)
        {
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ReadWrite,
            WritableSpecialKeySlots = SpecialKeySlotSet.All,
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
            ObservedLightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Spectrum,
                LightingEffect.Static,
                LightingEffect.Wheel,
            },
            ObservedPowerSourceBehaviors =
            [
                new PowerSourceBehaviorObservation(
                    PowerSource.UsbC,
                    PerformancePreset.Balanced,
                    FanMode.Automatic,
                    FixedFanControlAvailable: false,
                    PerformanceModeAvailable: false,
                    CustomModeAvailable: false,
                    InadequatePowerWarningVisible: true),
            ],
        };
        var desired = new DesiredBatteryState(BatteryPolicy.Create(80), true, false);
        var confirmed = new ConfirmedBatteryState(true, 80, false);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                BatteryProtectionEnabled = false,
                WindowsPowerMode = WindowsPowerMode.BestPerformance,
            },
            Battery = defaults.Battery with
            {
                BatteryProtectionEnabled = true,
                WindowsPowerMode = WindowsPowerMode.BestPowerEfficiency,
            },
            UsbC = defaults.UsbC with { BatteryProtectionEnabled = false },
        };
        ServiceRequest request = new SetBatteryProtectionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            false);
        ServiceStatusSnapshot status = CreateStatus() with
        {
            PotentialControllers = ["RazerAppEngine"],
            ControllerDetectionWarning = null,
            ExternalChanges = [DeviceSubsystem.Fans, DeviceSubsystem.Lighting],
            Capabilities = capabilities,
            ConfirmedState = new ConfirmedDeviceState(null, null, confirmed, null),
            DesiredState = CreateStatus().DesiredState with { Battery = desired },
            Profiles = profiles,
            BatteryPolicyOverride = BatteryPolicy.Create(75),
            BatteryProtectionOverride = false,
        };

        Assert.Equal(46, OpenBladeIpc.ProtocolVersion);
        Assert.Equal(request, await RoundTripRequestAsync(request));
        ServiceStatusSnapshot roundTrip = await RoundTripStatusAsync(status);
        Assert.Equal([80], roundTrip.Capabilities!.ChargeLimits);
        Assert.True(capabilities.ObservedLightingEffects.SetEquals(
            roundTrip.Capabilities.ObservedLightingEffects));
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            roundTrip.Capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.All,
            roundTrip.Capabilities.WritableSpecialKeySlots);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            roundTrip.Capabilities.LampArrayLightingAccess);
        Assert.Equal(
            capabilities.ObservedPowerSourceBehaviors,
            roundTrip.Capabilities.ObservedPowerSourceBehaviors);
        Assert.Equal(desired, roundTrip.DesiredState.Battery);
        Assert.Equal(confirmed, roundTrip.ConfirmedState!.Battery);
        Assert.Equal(profiles, roundTrip.Profiles);
        Assert.Equal(BatteryPolicy.Create(75), roundTrip.BatteryPolicyOverride);
        Assert.False(roundTrip.BatteryProtectionOverride);
    }

    [Fact]
    public async Task GpuClockTuning_RoundTripsIndependentlyFromPerformancePreset()
    {
        var offsets = new GpuClockOffsetSettings(125, 500);
        var capabilities = new GpuClockOffsetCapabilities(
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            new GpuClockOffsetRange(-1000, 1000, offsets.CoreMegahertz),
            new GpuClockOffsetRange(-2000, 6000, offsets.MemoryMegahertz));
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ConfirmedState = new ConfirmedDeviceState(
                PerformancePreset.Balanced,
                FanMode.Automatic,
                null,
                null)
            {
                GpuClockOffsets = capabilities,
            },
            DesiredState = CreateStatus().DesiredState with
            {
                Performance = new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                GpuClockTuning = new GpuClockTuningSettings(
                    Enabled: true,
                    offsets),
            },
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(status);

        Assert.Equal(capabilities, actual.ConfirmedState?.GpuClockOffsets);
        Assert.Equal(PerformancePreset.Balanced, actual.ConfirmedState?.Performance);
        Assert.Equal(
            new GpuClockTuningSettings(Enabled: true, offsets),
            actual.DesiredState.GpuClockTuning);
    }

    [Fact]
    public async Task VersionEighteenStatus_RejectsMalformedGpuClockOffsetRange()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                ConfirmedState = new ConfirmedDeviceState(
                    PerformancePreset.Custom,
                    FanMode.Automatic,
                    null,
                    null)
                {
                    CustomPerformance = CustomPerformanceSettings.Default,
                    GpuClockOffsets = new GpuClockOffsetCapabilities(
                        "NVIDIA GPU",
                        new GpuClockOffsetRange(100, -100, 0),
                        new GpuClockOffsetRange(-2000, 6000, 0)),
                },
            });
        using MemoryStream stream = FrameJson(
            JsonSerializer.Serialize<ServiceServerFrame>(frame));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("PerformancePresets")]
    [InlineData("FanModes")]
    [InlineData("LightingEffects")]
    [InlineData("ObservedLightingEffects")]
    [InlineData("OneShotLaptopControls")]
    [InlineData("ObservedPowerSourceBehaviors")]
    public async Task ReadServerFrameAsync_RejectsNullNestedCapabilityCollection(
        string propertyName)
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.SafeTargetDefaults(),
            });
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(root[nameof(ServiceStatusEvent.Status)]);
        JsonObject capabilities = Assert.IsType<JsonObject>(
            status[nameof(ServiceStatusSnapshot.Capabilities)]);
        capabilities[propertyName] = null;
        using MemoryStream stream = FrameJson(root.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnknownObservedLightingEffect()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.Unavailable() with
                {
                    ObservedLightingEffects =
                        new HashSet<LightingEffect> { (LightingEffect)999 },
                },
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                new MemoryStream(),
                frame,
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsMalformedCustomPerformanceCapabilities()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            JsonObject customPerformance = Assert.IsType<JsonObject>(
                capabilities[nameof(DeviceCapabilities.CustomPerformance)]);
            customPerformance[
                nameof(CustomPerformanceCapabilities.AllowedCombinations)] = null;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsMalformedCpuPowerLimitCapabilities()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            JsonObject cpuPowerLimit = Assert.IsType<JsonObject>(
                capabilities[nameof(DeviceCapabilities.CpuPowerLimit)]);
            JsonObject watts = Assert.IsType<JsonObject>(
                cpuPowerLimit[nameof(CpuPowerLimitCapabilities.Watts)]);
            watts[nameof(DeviceNumericRange.Minimum)] = 0;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsMalformedCpuVoltageOptimizerCapabilities()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            JsonObject cpuVoltageOptimizer = Assert.IsType<JsonObject>(
                capabilities[nameof(DeviceCapabilities.CpuVoltageOptimizer)]);
            JsonObject value = Assert.IsType<JsonObject>(
                cpuVoltageOptimizer[nameof(CpuVoltageOptimizerCapabilities.Value)]);
            value[nameof(DeviceNumericRange.Step)] = 0;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsMalformedGpuOverclockCapabilities()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            JsonObject gpuOverclock = Assert.IsType<JsonObject>(
                capabilities[nameof(DeviceCapabilities.GpuOverclock)]);
            JsonObject memoryOffset = Assert.IsType<JsonObject>(
                gpuOverclock[
                    nameof(GpuOverclockCapabilities.MemoryOffsetMegahertz)]);
            memoryOffset[nameof(DeviceNumericRange.Minimum)] = 3_001;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownKeyboardLightingControlMode()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus());
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(
            root[nameof(ServiceStatusEvent.Status)]);
        status[nameof(ServiceStatusSnapshot.KeyboardLightingControlMode)] = 999;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownSessionControlAccess()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus());
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(
            root[nameof(ServiceStatusEvent.Status)]);
        status[nameof(ServiceStatusSnapshot.ControlAccess)] = 999;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownDisplayBrightnessHidAvailability()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus());
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(
            root[nameof(ServiceStatusEvent.Status)]);
        status[nameof(ServiceStatusSnapshot.DisplayBrightnessHidAvailability)] = 999;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsDuplicateObservedPowerSourceBehaviors()
    {
        var observation = new PowerSourceBehaviorObservation(
            PowerSource.UsbC,
            PerformancePreset.Balanced,
            FanMode.Automatic,
            FixedFanControlAvailable: false,
            PerformanceModeAvailable: false,
            CustomModeAvailable: false,
            InadequatePowerWarningVisible: true);
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.Unavailable() with
                {
                    ObservedPowerSourceBehaviors = [observation, observation],
                },
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(JsonSerializer.Serialize(frame)),
                CancellationToken.None));
    }

    [Fact]
    public async Task VersionFortySixStatus_AcceptsCompleteCapabilityCollections()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults(),
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);

        Assert.Equal(46, OpenBladeIpc.ProtocolVersion);
        AssertStatusEqual(expected, actual);
    }

    [Theory]
    [InlineData(DisplayBrightnessHidAvailability.Unknown)]
    [InlineData(DisplayBrightnessHidAvailability.Available)]
    [InlineData(DisplayBrightnessHidAvailability.Unavailable)]
    public async Task DisplayBrightnessHidAvailability_RoundTrips(
        DisplayBrightnessHidAvailability availability)
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            DisplayBrightnessHidAvailability = availability,
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);

        Assert.Equal(availability, actual.DisplayBrightnessHidAvailability);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnknownSpecialKeyBindingsAccess()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.Unavailable() with
                {
                    SpecialKeyBindingsAccess = (DeviceFeatureAccess)999,
                },
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                new MemoryStream(),
                frame,
                CancellationToken.None));
    }

    [Theory]
    [InlineData(DeviceFeatureAccess.ReadWrite, (int)SpecialKeySlotSet.None)]
    [InlineData(DeviceFeatureAccess.ObservedOnly, (int)SpecialKeySlotSet.M3)]
    [InlineData(DeviceFeatureAccess.ReadWrite, 32)]
    public async Task WriteServerFrameAsync_RejectsInconsistentWritableSpecialKeySlots(
        DeviceFeatureAccess access,
        int slots)
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.Unavailable() with
                {
                    SpecialKeyBindingsAccess = access,
                    WritableSpecialKeySlots = (SpecialKeySlotSet)slots,
                },
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                new MemoryStream(),
                frame,
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnknownLampArrayLightingAccess()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                Capabilities = DeviceCapabilities.Unavailable() with
                {
                    LampArrayLightingAccess = (DeviceFeatureAccess)999,
                },
            });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                new MemoryStream(),
                frame,
                CancellationToken.None));
    }

    [Fact]
    public async Task FirmwareMetadata_NewReaderDefaultsFieldsOmittedByOlderFrame()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            Firmware = new FirmwareVersions(
                "3.01",
                "1.04",
                "1.6.0.0",
                "98.3.5c.40.3d",
                "RZ09-0581",
                "NVIDIA GeForce RTX 5090 Laptop GPU",
                ThunderboltFirmwareAccess: DeviceFeatureAccess.ObservedOnly,
                TouchpadFirmwareAccess: DeviceFeatureAccess.ObservedOnly),
        };
        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);
        Assert.Equal(expected.Firmware, actual.Firmware);

        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            expected);
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            encoded,
            frame,
            CancellationToken.None);
        byte[] framedBytes = encoded.ToArray();
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(framedBytes);
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(
                framedBytes,
                sizeof(int),
                payloadLength)));
        JsonObject status = Assert.IsType<JsonObject>(
            root["status"]);
        JsonObject firmware = Assert.IsType<JsonObject>(
            status["firmware"]);
        firmware.Remove("modelNumber");
        firmware.Remove("vbiosGpuName");
        firmware.Remove("thunderboltFirmwareVersion");
        firmware.Remove("touchpadFirmwareVersion");
        firmware.Remove("thunderboltFirmwareAccess");
        firmware.Remove("touchpadFirmwareAccess");
        using MemoryStream stream = FrameJson(root.ToJsonString());

        ServiceStatusEvent olderFrame = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(
                stream,
                CancellationToken.None));
        Assert.Equal("Unavailable", olderFrame.Status.Firmware?.ModelNumber);
        Assert.Equal("Unavailable", olderFrame.Status.Firmware?.VbiosGpuName);
        Assert.Equal(
            "Unavailable",
            olderFrame.Status.Firmware?.ThunderboltFirmwareVersion);
        Assert.Equal("Unavailable", olderFrame.Status.Firmware?.TouchpadFirmwareVersion);
        Assert.Null(olderFrame.Status.Firmware?.ThunderboltFirmwareAccess);
        Assert.Null(olderFrame.Status.Firmware?.TouchpadFirmwareAccess);
    }

    [Fact]
    public async Task BatteryHealth_RoundTripsAndDefaultsOlderFramesToUnavailable()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            BatteryHealth = new BatteryHealthStatus(95_200, 90_440),
        };
        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);
        Assert.Equal(expected.BatteryHealth, actual.BatteryHealth);

        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            expected);
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            encoded,
            frame,
            CancellationToken.None);
        byte[] framedBytes = encoded.ToArray();
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(framedBytes);
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(
                framedBytes,
                sizeof(int),
                payloadLength)));
        JsonObject status = Assert.IsType<JsonObject>(
            root["status"]);
        Assert.True(status.Remove("batteryHealth"));
        using MemoryStream stream = FrameJson(root.ToJsonString());

        ServiceStatusEvent olderFrame = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(
                stream,
                CancellationToken.None));
        Assert.Null(olderFrame.Status.BatteryHealth);
    }

    [Theory]
    [InlineData(0, 90_000)]
    [InlineData(95_000, 0)]
    [InlineData(95_000, 190_001)]
    [InlineData(1_000_001, 90_000)]
    public async Task BatteryHealth_RejectsMalformedCapacityValues(
        int designCapacity,
        int fullChargeCapacity)
    {
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            BatteryHealth = new BatteryHealthStatus(
                designCapacity,
                fullChargeCapacity),
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnknownLightingOwnership()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                LightingRendererOwnership = (LightingRendererOwnershipState)999,
            });

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
        Assert.Equal(
            "IPC server status is invalid: lighting-renderer ownership is unknown.",
            exception.Message);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnknownLightingRefreshRate()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with
            {
                LightingRefreshRate = (LightingRefreshRate)999,
            });

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
        Assert.Equal(
            "IPC server status is invalid: lighting refresh rate is unknown.",
            exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 0.1.0")]
    [InlineData("0.1.0 ")]
    public async Task WriteServerFrameAsync_RejectsMissingOrUntrimmedServiceVersion(
        string? serviceVersion)
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with { ServiceVersion = serviceVersion });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsOversizedServiceVersion()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with { ServiceVersion = new string('1', 65) });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteRequestAsync_RejectsEmptyRequestId()
    {
        using MemoryStream stream = new();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(
                stream,
                new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.Empty),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("[80,80]")]
    [InlineData("[80,75]")]
    [InlineData("[45]")]
    [InlineData("[85]")]
    [InlineData("[52]")]
    [InlineData("[50,55,60,65,70,75,80,80]")]
    public async Task ReadServerFrameAsync_RejectsInvalidRawChargeLimits(string chargeLimitsJson)
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with { Capabilities = DeviceCapabilities.Unavailable() });
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(root[nameof(ServiceStatusEvent.Status)]);
        JsonObject capabilities = Assert.IsType<JsonObject>(
            status[nameof(ServiceStatusSnapshot.Capabilities)]);
        capabilities[nameof(DeviceCapabilities.ChargeLimits)] = JsonNode.Parse(chargeLimitsJson);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("\"temporaryFullCharge\":true")]
    [InlineData("\"protectionEnabled\":true")]
    [InlineData("\"arbitraryMember\":true")]
    public async Task ReadRequestAsync_RejectsLegacyOrUnknownBatteryPolicyMember(
        string extraMember)
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $"{{\"operation\":\"setBatteryPolicy\",\"version\":{OpenBladeIpc.ProtocolVersion},\"requestId\":\"{requestId}\",\"value\":{{\"chargeLimitPercent\":80,{extraMember}}}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsDuplicateChargeLimitPercentMember()
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $"{{\"operation\":\"setBatteryPolicy\",\"version\":{OpenBladeIpc.ProtocolVersion},\"requestId\":\"{requestId}\",\"value\":{{\"chargeLimitPercent\":80,\"chargeLimitPercent\":75}}}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsBatteryProtectionRequestWithoutEnabled()
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $"{{\"operation\":\"setBatteryProtection\",\"version\":{OpenBladeIpc.ProtocolVersion},\"requestId\":\"{requestId}\"}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ServerFrameHelpers_RoundTripResponseAndStatusEventThroughBaseType()
    {
        ServiceStatusSnapshot status = CreateStatus();
        ServiceServerFrame[] expected =
        [
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                Success: true,
                Error: null,
                Status: status,
                Result: null),
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
        ];

        foreach (ServiceServerFrame frame in expected)
        {
            using MemoryStream stream = new();
            await IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None);
            stream.Position = 0;

            ServiceServerFrame actual = await IpcMessageCodec.ReadServerFrameAsync(
                stream,
                CancellationToken.None);

            Assert.Equal(frame.GetType(), actual.GetType());
            if (frame is ServiceResponse expectedResponse)
            {
                ServiceResponse actualResponse = Assert.IsType<ServiceResponse>(actual);
                Assert.Equal(expectedResponse.Version, actualResponse.Version);
                Assert.Equal(expectedResponse.RequestId, actualResponse.RequestId);
                Assert.Equal(expectedResponse.Success, actualResponse.Success);
                AssertStatusEqual(expectedResponse.Status!, actualResponse.Status!);
            }
            else
            {
                AssertStatusEqual(
                    Assert.IsType<ServiceStatusEvent>(frame).Status,
                    Assert.IsType<ServiceStatusEvent>(actual).Status);
            }
        }
    }

    [Fact]
    public async Task ServerFrameHelpers_RoundTripTypedFailureCode()
    {
        Guid requestId = Guid.NewGuid();
        ServiceServerFrame expected = new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            requestId,
            Success: false,
            Error: "Busy",
            Status: null,
            Result: null)
        {
            ErrorCode = ServiceResponseErrorCode.CapacityUnavailable,
        };
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteServerFrameAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        ServiceResponse actual = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));

        Assert.False(actual.Success);
        Assert.Equal(requestId, actual.RequestId);
        Assert.Equal(ServiceResponseErrorCode.CapacityUnavailable, actual.ErrorCode);
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownTypedFailureCode()
    {
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $"{{\"frame\":\"response\",\"version\":{OpenBladeIpc.ProtocolVersion},"
            + $"\"requestId\":\"{requestId}\",\"success\":false,\"error\":\"Busy\","
            + "\"status\":null,\"result\":null,\"errorCode\":99}}");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ServerFrameHelpers_RoundTripMaximumDiagnosticArchiveUnderSixtyFourKiBFrameLimit()
    {
        byte[] bytes = CreateZipBytes(OpenBladeIpc.MaximumDiagnosticArchiveBytes);
        ServiceServerFrame expected = new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Success: true,
            Error: null,
            Status: null,
            Result: null,
            Archive: new DiagnosticArchive("OpenBlade-diagnostics.zip", bytes));
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteServerFrameAsync(stream, expected, CancellationToken.None);

        Assert.InRange(stream.Length, 1, OpenBladeIpc.MaximumMessageBytes + sizeof(int));
        stream.Position = 0;
        ServiceResponse actual = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal("OpenBlade-diagnostics.zip", actual.Archive!.SuggestedFileName);
        Assert.Equal(bytes, actual.Archive.ZipBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("diagnostics")]
    [InlineData("diagnostics.txt")]
    [InlineData("../diagnostics.zip")]
    [InlineData("folder/diagnostics.zip")]
    [InlineData("folder\\diagnostics.zip")]
    [InlineData("C:\\diagnostics.zip")]
    public async Task WriteServerFrameAsync_RejectsUnsafeDiagnosticArchiveFileName(string fileName)
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = CreateArchiveResponse(
            new DiagnosticArchive(fileName, CreateZipBytes(4)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsEmptyDiagnosticArchive()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = CreateArchiveResponse(
            new DiagnosticArchive("diagnostics.zip", []));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsDiagnosticArchiveWithoutLocalFileSignature()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = CreateArchiveResponse(
            new DiagnosticArchive("diagnostics.zip", [0x50, 0x4b, 0x05, 0x06]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsOversizedDiagnosticArchive()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = CreateArchiveResponse(
            new DiagnosticArchive(
                "diagnostics.zip",
                CreateZipBytes(OpenBladeIpc.MaximumDiagnosticArchiveBytes + 1)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task WriteServerFrameAsync_RejectsSuccessfulResponseWithoutExactlyOneBody(
        bool includeStatus,
        bool includeResult,
        bool includeArchive)
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Success: true,
            Error: null,
            Status: includeStatus ? CreateStatus() : null,
            Result: includeResult
                ? new ControlOperationResult(ControlOperationStatus.Applied, "Applied")
                : null,
            Archive: includeArchive
                ? new DiagnosticArchive("diagnostics.zip", CreateZipBytes(4))
                : null);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsUnknownDiscriminatorWithoutEchoingContent()
    {
        const string secret = "unknown-secret-operation";
        using MemoryStream stream = FrameJson(
            $$"""{"operation":"{{secret}}","version":{{OpenBladeIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}"}""");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.DoesNotContain(secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteRequestAsync_RejectsPreviousProtocolVersion()
    {
        using MemoryStream stream = new();
        ServiceRequest request = new GetStatusRequest(
            OpenBladeIpc.ProtocolVersion - 1,
            Guid.NewGuid());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));

        Assert.Equal("IPC protocol version is unsupported.", exception.Message);
    }

    [Fact]
    public async Task ReadRequestAsync_RejectsImmediatelyPreviousProtocolPeer()
    {
        int previousProtocolVersion = OpenBladeIpc.ProtocolVersion - 1;
        using MemoryStream stream = FrameJson(
            $$"""{"operation":"getStatus","version":{{previousProtocolVersion}},"requestId":"{{Guid.NewGuid()}}"}""");

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.Equal("IPC protocol version is unsupported.", exception.Message);
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsImmediatelyPreviousProtocolPeer()
    {
        int previousProtocolVersion = OpenBladeIpc.ProtocolVersion - 1;
        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $$"""
              {
                "frame":"response",
                "version":{{previousProtocolVersion}},
                "requestId":"{{requestId}}",
                "success":false,
                "error":"IPC protocol version is unsupported.",
                "status":null,
                "result":null,
                "archive":null
              }
              """);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));

        Assert.Equal("IPC protocol version is unsupported.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(OpenBladeIpc.MaximumMessageBytes + 1)]
    public async Task ReadRequestAsync_RejectsInvalidLengthBeforePayloadAllocation(int length)
    {
        using MemoryStream stream = new();
        await stream.WriteAsync(BitConverter.GetBytes(length));
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMessageLargerThanSixtyFourKiB()
    {
        using MemoryStream stream = new();
        ServiceServerFrame frame = new ServiceResponse(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Success: false,
            Error: new string('x', OpenBladeIpc.MaximumMessageBytes),
            Status: null,
            Result: null);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task MaximumSequenceTimelineFitsWorstCaseStatusFrame()
    {
        string timeline = KeyboardLightingSequenceCodec.Encode(
            CreateMaximumSizedSequenceFrames());
        LightingSettings lighting = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0581,
            timeline,
            loop: true,
            brightness: 100);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Lighting = lighting },
            Battery = defaults.Battery with { Lighting = lighting },
            UsbC = defaults.UsbC with { Lighting = lighting },
        };
        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot status = baseline with
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Sequence,
                },
                KeyboardLightingLayoutId = KeyboardLightingLayoutIds.Rz09_0581,
            },
            ConfirmedState = new ConfirmedDeviceState(null, null, null, lighting),
            Profiles = profiles,
            DesiredState = baseline.DesiredState with
            {
                Lighting = new ResolvedSetting<LightingSettings>(
                    lighting,
                    SettingValueSource.PowerProfile),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                lighting,
                OverrideLifetime.Persistent),
        };
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteServerFrameAsync(
            stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
            CancellationToken.None);

        int payloadLength = BitConverter.ToInt32(stream.ToArray(), 0);
        Assert.InRange(payloadLength, 30_000, OpenBladeIpc.MaximumMessageBytes);
        stream.Position = 0;
        ServiceStatusEvent roundTrip = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(timeline, Assert.IsType<SequenceLightingParameters>(
            roundTrip.Status.Profiles.PluggedIn.Lighting.Parameters).TimelineBase64);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(KeyboardLightingLayoutIds.Rz09_0581, false)]
    public async Task WriteServerFrameAsync_RejectsInconsistentSequenceLayoutCapability(
        string? layoutId,
        bool includesSequence)
    {
        HashSet<LightingEffect> effects = includesSequence
            ? [LightingEffect.Sequence]
            : [];

        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                LightingEffects = effects,
                KeyboardLightingLayoutId = layoutId,
            },
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMalformedSequenceTimelineInProfile()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings malformed = LightingSettingsFactory.Sequence(
            KeyboardLightingLayoutIds.Rz09_0581,
            "not-base64",
            loop: false,
            brightness: 100);

        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            Profiles = profiles with
            {
                PluggedIn = profiles.PluggedIn with { Lighting = malformed },
            },
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnsortedControllerNames()
    {
        using MemoryStream stream = new();
        ServiceStatusSnapshot status = CreateStatus() with
        {
            PotentialControllers = ["Razer Synapse Service", "Razer Central Service"],
        };
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            status);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsDuplicateControllerNames()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            PotentialControllers = ["RazerAppEngine", "RazerAppEngine"],
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMoreThanSixteenControllerNames()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            PotentialControllers = Enumerable.Range(0, 17)
                .Select(index => $"Controller{index:D2}")
                .ToArray(),
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnsortedExternalChanges()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ExternalChanges = [DeviceSubsystem.Lighting, DeviceSubsystem.Fans],
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsDuplicateExternalChanges()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ExternalChanges = [DeviceSubsystem.Fans, DeviceSubsystem.Fans],
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task UnconfirmedWrites_RoundTripWhenPopulated()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            UnconfirmedWrites = [DeviceSubsystem.Performance, DeviceSubsystem.Battery],
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);

        Assert.Equal(expected.UnconfirmedWrites, actual.UnconfirmedWrites);
    }

    [Fact]
    public async Task UnconfirmedWrites_DefaultToEmptyWhenOlderWriterOmitsProperty()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus());
        using var encoded = new MemoryStream();
        await IpcMessageCodec.WriteServerFrameAsync(
            encoded,
            frame,
            CancellationToken.None);
        byte[] framedBytes = encoded.ToArray();
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(framedBytes);
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(Encoding.UTF8.GetString(
                framedBytes,
                sizeof(int),
                payloadLength)));
        JsonObject status = Assert.IsType<JsonObject>(
            root["status"]);
        Assert.True(status.Remove("unconfirmedWrites"));

        ServiceStatusEvent actual = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));

        Assert.Empty(actual.Status.UnconfirmedWrites);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMissingUnconfirmedWriteCollection()
    {
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            UnconfirmedWrites = null!,
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsUnsortedUnconfirmedWrites()
    {
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            UnconfirmedWrites = [DeviceSubsystem.Lighting, DeviceSubsystem.Fans],
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsDuplicateUnconfirmedWrites()
    {
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            UnconfirmedWrites = [DeviceSubsystem.Fans, DeviceSubsystem.Fans],
        });
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMoreThanFourUnconfirmedWrites()
    {
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            UnconfirmedWrites =
            [
                DeviceSubsystem.Performance,
                DeviceSubsystem.Fans,
                DeviceSubsystem.Battery,
                DeviceSubsystem.Lighting,
                DeviceSubsystem.Lighting,
            ],
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownUnconfirmedSubsystemValue()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            UnconfirmedWrites = [(DeviceSubsystem)9],
        };
        ServiceServerFrame frame = new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(JsonSerializer.Serialize(frame)),
                CancellationToken.None));
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsMoreThanFourExternalChanges()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ExternalChanges =
            [
                DeviceSubsystem.Performance,
                DeviceSubsystem.Fans,
                DeviceSubsystem.Battery,
                DeviceSubsystem.Lighting,
                DeviceSubsystem.Lighting,
            ],
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUnknownDeviceSubsystemValue()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ExternalChanges = [(DeviceSubsystem)9],
        };
        ServiceServerFrame frame = new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status);
        string json = JsonSerializer.Serialize<ServiceServerFrame>(frame);
        using MemoryStream stream = FrameJson(json);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(-1_000_001, null, null, null, null)]
    [InlineData(1_000_001, null, null, null, null)]
    [InlineData(null, -1, null, null, null)]
    [InlineData(null, 20_001, null, null, null)]
    [InlineData(null, null, -1, null, null)]
    [InlineData(null, null, 20_001, null, null)]
    [InlineData(null, null, null, -1, null)]
    [InlineData(null, null, null, 116, null)]
    [InlineData(null, null, null, null, -1)]
    [InlineData(null, null, null, null, 116)]
    public async Task WriteServerFrameAsync_RejectsOutOfRangeLiveReadings(
        int? batteryPowerFlowMilliwatts,
        int? fan1Rpm,
        int? fan2Rpm,
        int? cpuTemperatureCelsius,
        int? gpuTemperatureCelsius)
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            LiveReadings = new LiveReadings(
                batteryPowerFlowMilliwatts,
                fan1Rpm,
                fan2Rpm,
                cpuTemperatureCelsius,
                gpuTemperatureCelsius),
        };

        await AssertStatusWriteRejectedAsync(status);
    }

    [Fact]
    public async Task LiveReadings_AcceptDocumentedBoundariesAndRoundTrip()
    {
        ServiceStatusSnapshot low = CreateStatus() with
        {
            LiveReadings = new LiveReadings(-1_000_000, 0, 0, 0, 0),
        };
        ServiceStatusSnapshot high = CreateStatus() with
        {
            LiveReadings = new LiveReadings(1_000_000, 20_000, 20_000, 115, 115),
        };

        Assert.Equal(low.LiveReadings, (await RoundTripStatusAsync(low)).LiveReadings);
        Assert.Equal(high.LiveReadings, (await RoundTripStatusAsync(high)).LiveReadings);
    }

    [Fact]
    public async Task WriteServerFrameAsync_RejectsOversizedAdvisoryAtSixtyFourKiBFrameLimit()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            ControllerDetectionWarning = new string('x', OpenBladeIpc.MaximumMessageBytes),
        };

        using MemoryStream stream = new();
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
                CancellationToken.None));

        Assert.Equal("IPC message exceeds the 64 KiB limit.", exception.Message);
    }

    private static MemoryStream FrameJson(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(sizeof(int)));
        return new MemoryStream(framed);
    }

    private static async Task<ServiceRequest> RoundTripRequestAsync(ServiceRequest request)
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        stream.Position = 0;
        return await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None);
    }

    private static async Task<ServiceStatusSnapshot> RoundTripStatusAsync(
        ServiceStatusSnapshot status)
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(
            stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
            CancellationToken.None);
        stream.Position = 0;
        ServiceStatusEvent statusEvent = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        return statusEvent.Status;
    }

    private static async Task AssertStatusWriteRejectedAsync(ServiceStatusSnapshot status)
    {
        using MemoryStream stream = new();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
                CancellationToken.None));
    }

    private static ServiceResponse CreateArchiveResponse(DiagnosticArchive archive) =>
        new(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Success: true,
            Error: null,
            Status: null,
            Result: null,
            Archive: archive);

    private static byte[] CreateZipBytes(int length)
    {
        byte[] bytes = new byte[length];
        if (length >= 4)
        {
            bytes[0] = 0x50;
            bytes[1] = 0x4b;
            bytes[2] = 0x03;
            bytes[3] = 0x04;
        }

        return bytes;
    }

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(
                PerformancePreset.Balanced,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                profiles.PluggedIn.Lighting,
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(
                profiles.BatteryPolicy,
                protectionEnabled: true,
                temporaryFullCharge: false));
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: false,
            WritesBlocked: true,
            PotentialControllers: ["RazerAppEngine"],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Blocked",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: desired,
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: new LiveReadings(-18_400, 2_300, 2_500),
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            Firmware: null,
            ServiceVersion: "0.1.0-test",
            KeyboardDriverModeActive: true);
    }

    private static KeyboardLightingSequenceFrame[] CreateMaximumSizedSequenceFrames()
    {
        var frames = new KeyboardLightingSequenceFrame[584];
        for (int index = 0; index < frames.Length; index++)
        {
            var slots = new RgbColor[AmbientLightingFrame.SlotCount];
            slots[1] = index % 2 == 0
                ? new RgbColor(1, 2, 3)
                : new RgbColor(4, 5, 6);
            frames[index] = new KeyboardLightingSequenceFrame(1, slots);
        }

        return frames;
    }

    private static async Task AssertMalformedNestedCapabilityRejectedAsync(
        Action<JsonObject> mutateCapabilities)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(
                        CustomPerformanceLevel.Medium,
                        CustomPerformanceLevel.High),
                }),
            CpuPowerLimit = new CpuPowerLimitCapabilities(
                new DeviceNumericRange(45, 95, 1),
                DeviceFeatureAccess.ReadOnly),
            CpuVoltageOptimizer = new CpuVoltageOptimizerCapabilities(
                new DeviceNumericRange(-30, -1, 1),
                DeviceFeatureAccess.ReadOnly)
            {
                SupportsEnableDisable = true,
            },
            GpuOverclock = new GpuOverclockCapabilities(
                new DeviceNumericRange(-1_000, 400, null),
                new DeviceNumericRange(-1_000, 3_000, null),
                DeviceFeatureAccess.ObservedOnly)
            {
                SupportsEnableDisable = true,
            },
        };
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            CreateStatus() with { Capabilities = capabilities });
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject status = Assert.IsType<JsonObject>(
            root[nameof(ServiceStatusEvent.Status)]);
        JsonObject serializedCapabilities = Assert.IsType<JsonObject>(
            status[nameof(ServiceStatusSnapshot.Capabilities)]);
        mutateCapabilities(serializedCapabilities);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(
                FrameJson(root.ToJsonString()),
                CancellationToken.None));
    }

    private static void AssertStatusEqual(
        ServiceStatusSnapshot expected,
        ServiceStatusSnapshot actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.DeviceVerified, actual.DeviceVerified);
        Assert.Equal(expected.WritesBlocked, actual.WritesBlocked);
        Assert.Equal(expected.PotentialControllers, actual.PotentialControllers);
        Assert.Equal(expected.ControllerDetectionWarning, actual.ControllerDetectionWarning);
        Assert.Equal(expected.ExternalChanges, actual.ExternalChanges);
        Assert.Equal(expected.UnconfirmedWrites, actual.UnconfirmedWrites);
        Assert.Equal(expected.Message, actual.Message);
        Assert.NotNull(expected.Capabilities);
        Assert.NotNull(actual.Capabilities);
        Assert.True(expected.Capabilities.PerformancePresets.SetEquals(actual.Capabilities.PerformancePresets));
        Assert.True(expected.Capabilities.FanModes.SetEquals(actual.Capabilities.FanModes));
        Assert.True(expected.Capabilities.LightingEffects.SetEquals(actual.Capabilities.LightingEffects));
        Assert.True(expected.Capabilities.SupportedLightingRefreshRates.SetEquals(
            actual.Capabilities.SupportedLightingRefreshRates));
        Assert.True(expected.Capabilities.ObservedLightingEffects.SetEquals(
            actual.Capabilities.ObservedLightingEffects));
        Assert.Equal(
            expected.Capabilities.LampArrayLightingAccess,
            actual.Capabilities.LampArrayLightingAccess);
        Assert.True(expected.Capabilities.ChargeLimits.SetEquals(actual.Capabilities.ChargeLimits));
        Assert.Equal(expected.Capabilities.SupportsBatteryProtectionOff, actual.Capabilities.SupportsBatteryProtectionOff);
        Assert.Equal(expected.Capabilities.SupportsTemporaryFullCharge, actual.Capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(expected.ConfirmedState, actual.ConfirmedState);
        Assert.Equal(expected.Profiles, actual.Profiles);
        Assert.Equal(expected.DesiredState, actual.DesiredState);
        Assert.Equal(expected.ActivePowerSource, actual.ActivePowerSource);
        Assert.Equal(expected.LiveReadings, actual.LiveReadings);
        Assert.Equal(expected.PerformanceOverride, actual.PerformanceOverride);
        Assert.Equal(
            expected.PerformanceShortcutPreview,
            actual.PerformanceShortcutPreview);
        Assert.Equal(expected.FanOverride, actual.FanOverride);
        Assert.Equal(expected.LightingOverride, actual.LightingOverride);
        Assert.Equal(expected.FullChargeOverride, actual.FullChargeOverride);
        Assert.Equal(expected.ServiceVersion, actual.ServiceVersion);
        Assert.Equal(expected.BatteryHealth, actual.BatteryHealth);
        Assert.Equal(
            expected.KeyboardDriverModeActive,
            actual.KeyboardDriverModeActive);
        Assert.Equal(
            expected.AlwaysOverrideExternalLightingChanges,
            actual.AlwaysOverrideExternalLightingChanges);
        Assert.Equal(expected.LightingRefreshRate, actual.LightingRefreshRate);
    }
}
