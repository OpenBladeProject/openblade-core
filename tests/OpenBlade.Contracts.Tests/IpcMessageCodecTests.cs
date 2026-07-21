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
            new AmbientLightingFrame(colors));
        using MemoryStream stream = new();

        await IpcMessageCodec.WriteRequestAsync(stream, expected, CancellationToken.None);
        stream.Position = 0;
        var actual = Assert.IsType<PushAmbientLightingFrameRequest>(
            await IpcMessageCodec.ReadRequestAsync(stream, CancellationToken.None));

        Assert.Equal(expected.RequestId, actual.RequestId);
        Assert.Equal(colors, actual.Frame.Slots);
        Assert.True(actual.Frame.IsWellFormed());
    }

    [Fact]
    public async Task AmbientLightingFrameRequest_RejectsColorInLayoutHole()
    {
        RgbColor[] colors = new RgbColor[AmbientLightingFrame.SlotCount];
        colors[0] = new RgbColor(1, 0, 0);
        var request = new PushAmbientLightingFrameRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new AmbientLightingFrame(colors));
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
            IncludeLiveReadings: true),
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
        new SetExternalLightingOverridePolicyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: true),
        new SetLightingSuppressionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            LightingSuppressionReason.UserIdle,
            Suppressed: true),
        new PulseReactiveKeyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            new ReactiveKeyPulse(77)),
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
    public async Task VersionFourteenBatteryContracts_RoundTripExactShape()
    {
        var capabilities = new DeviceCapabilities(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(),
            new HashSet<int> { 80 },
            SupportsBatteryProtectionOff: true,
            SupportsTemporaryFullCharge: false);
        var desired = new DesiredBatteryState(BatteryPolicy.Create(80), true, false);
        var confirmed = new ConfirmedBatteryState(true, 80, false);
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
        };

        Assert.Equal(17, OpenBladeIpc.ProtocolVersion);
        Assert.Equal(request, await RoundTripRequestAsync(request));
        ServiceStatusSnapshot roundTrip = await RoundTripStatusAsync(status);
        Assert.Equal([80], roundTrip.Capabilities!.ChargeLimits);
        Assert.Equal(desired, roundTrip.DesiredState.Battery);
        Assert.Equal(confirmed, roundTrip.ConfirmedState!.Battery);
    }

    [Theory]
    [InlineData("PerformancePresets")]
    [InlineData("FanModes")]
    [InlineData("LightingEffects")]
    [InlineData("OneShotLaptopControls")]
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
    public async Task VersionFourteenStatus_AcceptsCompleteCapabilityCollections()
    {
        ServiceStatusSnapshot expected = CreateStatus() with
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults(),
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(expected);

        Assert.Equal(17, OpenBladeIpc.ProtocolVersion);
        AssertStatusEqual(expected, actual);
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

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
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
            ServiceVersion: "0.1.0-test");
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
        Assert.Equal(expected.Message, actual.Message);
        Assert.NotNull(expected.Capabilities);
        Assert.NotNull(actual.Capabilities);
        Assert.True(expected.Capabilities.PerformancePresets.SetEquals(actual.Capabilities.PerformancePresets));
        Assert.True(expected.Capabilities.FanModes.SetEquals(actual.Capabilities.FanModes));
        Assert.True(expected.Capabilities.LightingEffects.SetEquals(actual.Capabilities.LightingEffects));
        Assert.True(expected.Capabilities.ChargeLimits.SetEquals(actual.Capabilities.ChargeLimits));
        Assert.Equal(expected.Capabilities.SupportsBatteryProtectionOff, actual.Capabilities.SupportsBatteryProtectionOff);
        Assert.Equal(expected.Capabilities.SupportsTemporaryFullCharge, actual.Capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(expected.ConfirmedState, actual.ConfirmedState);
        Assert.Equal(expected.Profiles, actual.Profiles);
        Assert.Equal(expected.DesiredState, actual.DesiredState);
        Assert.Equal(expected.ActivePowerSource, actual.ActivePowerSource);
        Assert.Equal(expected.LiveReadings, actual.LiveReadings);
        Assert.Equal(expected.PerformanceOverride, actual.PerformanceOverride);
        Assert.Equal(expected.FanOverride, actual.FanOverride);
        Assert.Equal(expected.LightingOverride, actual.LightingOverride);
        Assert.Equal(expected.FullChargeOverride, actual.FullChargeOverride);
        Assert.Equal(expected.ServiceVersion, actual.ServiceVersion);
        Assert.Equal(
            expected.AlwaysOverrideExternalLightingChanges,
            actual.AlwaysOverrideExternalLightingChanges);
    }
}
