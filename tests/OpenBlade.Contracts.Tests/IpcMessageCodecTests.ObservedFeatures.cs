using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed partial class IpcMessageCodecTests
{
    private static IntelCpuPowerLimitCapabilities ObservedIntelCpuPowerLimit() => new(
        new DeviceNumericRange(1_000, 190_000, 125),
        new DeviceNumericRange(1_000, 200_000, 125),
        [1, 2, 4, 8, 28, 56, 128],
        DefaultLongTermPowerMilliwatts: 120_000,
        DefaultShortTermPowerMilliwatts: 160_000,
        DefaultTimeWindowSeconds: 56,
        DeviceFeatureAccess.ObservedOnly)
    {
        SupportsShortTermPowerEnableDisable = true,
    };

    private static ServiceStatusSnapshot CreateObservedFeatureStatus() => CreateStatus() with
    {
        Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            IntelCpuPowerLimit = ObservedIntelCpuPowerLimit(),
            FirmwareGamingGpuOverclockAccess = DeviceFeatureAccess.ObservedOnly,
            LocalDimming = new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
        },
        ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            new ConfirmedBatteryState(true, 80, false),
            null)
        {
            LocalDimmingActive = true,
        },
    };

    [Fact]
    public async Task ObservedFeatures_RoundTripWithEveryField()
    {
        ServiceStatusSnapshot status = CreateObservedFeatureStatus();

        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(status);

        Assert.Equal(ObservedIntelCpuPowerLimit(), decoded.Capabilities!.IntelCpuPowerLimit);
        Assert.True(decoded.Capabilities.IntelCpuPowerLimit!.SupportsShortTermPowerEnableDisable);
        Assert.Equal(DeviceFeatureAccess.ObservedOnly, decoded.Capabilities.FirmwareGamingGpuOverclockAccess);
        Assert.Equal(new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, true), decoded.Capabilities.LocalDimming);
        Assert.True(decoded.ConfirmedState!.LocalDimmingActive);
    }

    [Fact]
    public async Task ObservedFeatures_AbsentFieldsStayAbsentOnTheWire()
    {
        ServiceStatusSnapshot status = CreateStatus() with
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults(),
            ConfirmedState = new ConfirmedDeviceState(null, null, null, null),
        };
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);
        JsonObject encoded = FramedJsonObject(stream)["status"]!.AsObject();
        JsonObject capabilities = encoded["capabilities"]!.AsObject();
        Assert.False(capabilities.ContainsKey("intelCpuPowerLimit"));
        Assert.False(capabilities.ContainsKey("firmwareGamingGpuOverclockAccess"));
        Assert.False(capabilities.ContainsKey("localDimming"));
        Assert.False(encoded["confirmedState"]!.AsObject().ContainsKey("localDimmingActive"));

        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(status);
        Assert.Null(decoded.Capabilities!.IntelCpuPowerLimit);
        Assert.Null(decoded.Capabilities.FirmwareGamingGpuOverclockAccess);
        Assert.Null(decoded.Capabilities.LocalDimming);
        Assert.Null(decoded.ConfirmedState!.LocalDimmingActive);
    }

    [Fact]
    public async Task ObservedFeatures_Protocol51ProjectionOmitsEveryNewField()
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateObservedFeatureStatus()),
            CancellationToken.None);

        JsonObject projected = FramedJsonObject(stream)["status"]!.AsObject();
        JsonObject capabilities = projected["capabilities"]!.AsObject();
        Assert.False(capabilities.ContainsKey("intelCpuPowerLimit"));
        Assert.False(capabilities.ContainsKey("firmwareGamingGpuOverclockAccess"));
        Assert.False(capabilities.ContainsKey("localDimming"));
        Assert.False(projected["confirmedState"]!.AsObject().ContainsKey("localDimmingActive"));
        Assert.True(ReadFrozenProtocol51Frame(stream).Status!.WritesBlocked);
    }

    [Fact]
    public async Task ClearLocalDimmingRequest_RoundTripsAndProtocol51ParsesItForEdgeRejection()
    {
        var request = new ClearLocalDimmingRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        Assert.Equal(request, await RoundTripRequestAsync(request));

        Guid requestId = Guid.NewGuid();
        using MemoryStream stream = FrameJson(
            $$"""{"operation":"clearLocalDimming","version":51,"requestId":"{{requestId}}"}""");
        ClearLocalDimmingRequest legacy = Assert.IsType<ClearLocalDimmingRequest>(
            await IpcMessageCodec.ReadRequestWithProtocol51CompatibilityAsync(stream, CancellationToken.None));
        Assert.Equal(OpenBladeIpc.ReadOnlyCompatibilityProtocolVersion, legacy.Version);
    }

    [Theory]
    [InlineData(DeviceFeatureAccess.ObservedOnly, true)]
    [InlineData(DeviceFeatureAccess.ReadWrite, true)]
    [InlineData((DeviceFeatureAccess)99, false)]
    public void LocalDimmingCapabilities_AdmitClearOnlyWithReadableState(DeviceFeatureAccess access, bool supportsClear)
    {
        Assert.False(new LocalDimmingCapabilities(access, supportsClear).IsWellFormed);
        Assert.True(new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, true).IsWellFormed);
        Assert.True(new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, false).IsWellFormed);
    }

    private static IntelCpuPowerLimitCapabilities UnknownRangeIntelCpuPowerLimit() => ObservedIntelCpuPowerLimit() with
    {
        LongTermPowerMilliwatts = null,
        ShortTermPowerMilliwatts = null,
        TimeWindowSeconds = null,
    };

    [Fact]
    public async Task IntelCpuPowerLimit_UnknownRangesRoundTripWithTheirDefaults()
    {
        IntelCpuPowerLimitCapabilities limit = UnknownRangeIntelCpuPowerLimit();
        Assert.True(limit.IsWellFormed);
        Assert.False(limit.RangesKnown);
        Assert.True(ObservedIntelCpuPowerLimit().RangesKnown);
        ServiceStatusSnapshot decoded = await RoundTripStatusAsync(CreateStatus() with
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with { IntelCpuPowerLimit = limit },
        });

        Assert.Equal(limit, decoded.Capabilities!.IntelCpuPowerLimit);
        Assert.Equal(120_000, decoded.Capabilities.IntelCpuPowerLimit!.DefaultLongTermPowerMilliwatts);
    }

    public static TheoryData<IntelCpuPowerLimitCapabilities> MalformedIntelCpuPowerLimits() =>
    [
        ObservedIntelCpuPowerLimit() with { LongTermPowerMilliwatts = new DeviceNumericRange(0, 190_000, 125) },
        ObservedIntelCpuPowerLimit() with { ShortTermPowerMilliwatts = new DeviceNumericRange(1_000, 200_000, null) },
        ObservedIntelCpuPowerLimit() with { TimeWindowSeconds = [] },
        ObservedIntelCpuPowerLimit() with { TimeWindowSeconds = [1, 56, 28] },
        ObservedIntelCpuPowerLimit() with { TimeWindowSeconds = [0, 56] },
        ObservedIntelCpuPowerLimit() with { TimeWindowSeconds = [1, 28, 64] },
        ObservedIntelCpuPowerLimit() with { DefaultLongTermPowerMilliwatts = 120_001 },
        ObservedIntelCpuPowerLimit() with { DefaultShortTermPowerMilliwatts = 250_000 },
        ObservedIntelCpuPowerLimit() with { Access = (DeviceFeatureAccess)99 },
        // Unknown ranges are allowed only for a limit that is not writable.
        UnknownRangeIntelCpuPowerLimit() with { Access = DeviceFeatureAccess.ReadWrite },
        UnknownRangeIntelCpuPowerLimit() with { DefaultLongTermPowerMilliwatts = 0 },
        UnknownRangeIntelCpuPowerLimit() with { DefaultTimeWindowSeconds = 0 },
        ObservedIntelCpuPowerLimit() with { Access = DeviceFeatureAccess.ReadWrite, TimeWindowSeconds = null },
    ];

    [Theory]
    [MemberData(nameof(MalformedIntelCpuPowerLimits))]
    public async Task IntelCpuPowerLimit_MalformedShapesAreRejectedOnBothSides(IntelCpuPowerLimitCapabilities limit)
    {
        Assert.True(ObservedIntelCpuPowerLimit().IsWellFormed);
        Assert.False(limit.IsWellFormed);
        await AssertStatusWriteRejectedAsync(CreateStatus() with
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with { IntelCpuPowerLimit = limit },
        });
    }

    [Fact]
    public async Task LocalDimming_MalformedClearAdmissionIsRejectedOnRead()
    {
        ServiceServerFrame frame = new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateObservedFeatureStatus());
        JsonObject root = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize<ServiceServerFrame>(frame)));
        JsonObject capabilities = Assert.IsType<JsonObject>(
            root[nameof(ServiceStatusEvent.Status)]![nameof(ServiceStatusSnapshot.Capabilities)]);
        JsonObject localDimming = Assert.IsType<JsonObject>(capabilities[nameof(DeviceCapabilities.LocalDimming)]);
        localDimming[nameof(LocalDimmingCapabilities.Access)] = (int)DeviceFeatureAccess.ObservedOnly;
        using MemoryStream stream = FrameJson(root.ToJsonString());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
    }
}
