using System.Text.Json.Nodes;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

// Capability validation tests mutate frames the codec itself wrote, so each
// rejection comes from the named capability rule rather than an unknown member.
public sealed partial class IpcMessageCodecTests
{
    private static JsonNode? HyperBoostBasesNode(DeviceFeatureAccess? turbo) =>
        System.Text.Json.JsonSerializer.SerializeToNode(
            new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly)
            {
                Turbo = turbo,
            },
            WireOptions);

    [Fact]
    public async Task ReadServerFrameAsync_AcceptsTurboAccessAtHyperBoostAccess()
    {
        // Control for the two rejections below: the same frame with an admitted
        // Turbo slot decodes, so they fail only on the Turbo rule.
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            HyperBoostAccess = DeviceFeatureAccess.ReadOnly,
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadOnly,
                DeviceFeatureAccess.ReadOnly,
                DeviceFeatureAccess.ObservedOnly)
            {
                Turbo = DeviceFeatureAccess.ReadOnly,
            },
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(
            CreateStatus() with { Capabilities = capabilities });

        Assert.Equal(DeviceFeatureAccess.ReadOnly, actual.Capabilities!.HyperBoostBaseModes!.Turbo);
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsTurboAboveReadOnlyHyperBoostAccess()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostAccess))] = (int)DeviceFeatureAccess.ReadOnly;
            JsonObject bases = Assert.IsType<JsonObject>(HyperBoostBasesNode(DeviceFeatureAccess.ReadWrite));
            bases[Wire(nameof(HyperBoostBaseModeCapabilities.Balanced))] = (int)DeviceFeatureAccess.ReadOnly;
            bases[Wire(nameof(HyperBoostBaseModeCapabilities.Silent))] = (int)DeviceFeatureAccess.ReadOnly;
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostBaseModes))] = bases;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUndefinedTurboAccessBelowHyperBoostAccess()
    {
        // -1 passes the access-ordering rule, so only the defined-value rule rejects it.
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostAccess))] = (int)DeviceFeatureAccess.ReadWrite;
            JsonObject bases = Assert.IsType<JsonObject>(HyperBoostBasesNode(turbo: null));
            bases[Wire(nameof(HyperBoostBaseModeCapabilities.Turbo))] = -1;
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostBaseModes))] = bases;
        });
    }

    [Fact]
    public async Task HyperBoostEntryRestriction_RoundTripsAndKeepsExitAdmitted()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
            HyperBoostWithoutCoolingPadAccess = DeviceFeatureAccess.ReadWrite,
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ReadWrite,
                DeviceFeatureAccess.ObservedOnly)
            {
                Turbo = DeviceFeatureAccess.ReadWrite,
                CoolingPadEntryBaseMode = PerformancePreset.Turbo,
            },
            HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
        };

        ServiceStatusSnapshot actual = await RoundTripStatusAsync(
            CreateStatus() with { Capabilities = capabilities });

        DeviceCapabilities decoded = actual.Capabilities!;
        Assert.Equal(HyperBoostEntryRestriction.VendorSystemSku, decoded.HyperBoostEntryRestriction);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, decoded.HyperBoostAccess);
        Assert.True(HyperBoostCapabilityPolicy.CanExit(decoded));
        Assert.Null(HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(decoded));
        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(decoded));
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsEntryRestrictionWithoutHyperBoostAccess()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            capabilities.Remove(Wire(nameof(DeviceCapabilities.HyperBoostAccess)));
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostEntryRestriction))] =
                (int)HyperBoostEntryRestriction.VendorSystemSku;
        });
    }

    [Fact]
    public async Task ReadServerFrameAsync_RejectsUndefinedEntryRestriction()
    {
        await AssertMalformedNestedCapabilityRejectedAsync(capabilities =>
        {
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostAccess))] = (int)DeviceFeatureAccess.ReadWrite;
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostBaseModes))] = HyperBoostBasesNode(turbo: null);
            capabilities[Wire(nameof(DeviceCapabilities.HyperBoostEntryRestriction))] = 99;
        });
    }

    private static ServiceStatusSnapshot CreateHyperBoostStatus(FanMode fans) =>
        CreateStatus() with
        {
            WritesBlocked = false,
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ObservedOnly)
                {
                    Turbo = DeviceFeatureAccess.ReadWrite,
                },
                HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
            },
            ConfirmedState = new ConfirmedDeviceState(
                null,
                fans,
                new ConfirmedBatteryState(true, 80, false),
                null)
            {
                HyperBoostActive = true,
            },
        };

    [Theory]
    [InlineData(FanMode.Automatic)]
    [InlineData(FanMode.Fixed)]
    public async Task ConfirmedHyperBoost_RoundTripsWithAutomaticOrManualFans(FanMode fans)
    {
        // Synapse can run mode 7 with manual fans; the decoded state must stay
        // publishable so the tray can offer the exit.
        ServiceStatusSnapshot actual = await RoundTripStatusAsync(CreateHyperBoostStatus(fans));

        Assert.True(actual.ConfirmedState!.HyperBoostActive);
        Assert.Equal(fans, actual.ConfirmedState.Fans);
    }

    [Fact]
    public async Task ConfirmedHyperBoost_RejectsFanModesThatModeSevenCannotReport()
    {
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using MemoryStream stream = new();
            await IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateHyperBoostStatus(FanMode.ManualCurve)),
                CancellationToken.None);
        });

        Assert.Equal("IPC server status is invalid: confirmed HyperBoost state is inconsistent.", failure.Message);
    }

    [Theory]
    [InlineData(FanMode.Automatic, true)]
    [InlineData(FanMode.Fixed, false)]
    public async Task Protocol51_ProjectsManualFanHyperBoostAsUnknownAndHidesEntryRestriction(
        FanMode fans, bool projectedActive)
    {
        using MemoryStream stream = new();
        await IpcMessageCodec.WriteProtocol51ReadOnlyServerFrameAsync(
            stream,
            new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, CreateHyperBoostStatus(fans)),
            CancellationToken.None);

        Assert.True(ReadFrozenProtocol51Frame(stream).Status!.WritesBlocked);
        JsonObject status = FramedJsonObject(stream)["status"]!.AsObject();
        Assert.False(status["capabilities"]!.AsObject().ContainsKey("hyperBoostEntryRestriction"));
        JsonObject confirmed = status["confirmedState"]!.AsObject();
        if (projectedActive)
        {
            Assert.True(confirmed["hyperBoostActive"]!.GetValue<bool>());
            Assert.Equal((int)FanMode.Automatic, confirmed["fans"]!.GetValue<int>());
        }
        else
        {
            // Protocol 51 shows unknown HyperBoost activity as off.
            Assert.False(confirmed["hyperBoostActive"]!.GetValue<bool>());
            Assert.Null(confirmed["fans"]);
        }
    }
}
