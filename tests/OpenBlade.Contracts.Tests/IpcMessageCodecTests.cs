using System.Buffers.Binary;
using System.Text;
using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class IpcMessageCodecTests
{
    public static TheoryData<ServiceRequest> Requests => new()
    {
        new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
        new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
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
        new SetBatteryPolicyRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            BatteryPolicy.Create(75)),
        new SetFullChargeOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), true),
        new SetFullChargeOverrideRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false),
    };

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
    public async Task ReadRequestAsync_RejectsUnknownDiscriminatorWithoutEchoingContent()
    {
        const string secret = "unknown-secret-operation";
        using MemoryStream stream = FrameJson(
            $$"""{"operation":"{{secret}}","version":2,"requestId":"{{Guid.NewGuid()}}"}""");

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

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));
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
    public async Task WriteServerFrameAsync_RejectsUnsortedConflictNames()
    {
        using MemoryStream stream = new();
        ServiceStatusSnapshot status = CreateStatus() with
        {
            Conflicts = ["Razer Synapse Service", "Razer Central Service"],
        };
        ServiceServerFrame frame = new ServiceStatusEvent(
            OpenBladeIpc.ProtocolVersion,
            status);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteServerFrameAsync(stream, frame, CancellationToken.None));
    }

    private static MemoryStream FrameJson(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] framed = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(sizeof(int)));
        return new MemoryStream(framed);
    }

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        TelemetrySnapshot telemetry = new(
            DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            PowerSource.PluggedIn,
            80,
            null,
            null,
            null);
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(
                PerformancePreset.Balanced,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                profiles.PluggedIn.Lighting,
                SettingValueSource.PowerProfile),
            profiles.BatteryPolicy);
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: false,
            WritesBlocked: true,
            Conflicts: ["RazerAppEngine"],
            Message: "Blocked",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: desired,
            Telemetry: telemetry,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);
    }

    private static void AssertStatusEqual(
        ServiceStatusSnapshot expected,
        ServiceStatusSnapshot actual)
    {
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.DeviceVerified, actual.DeviceVerified);
        Assert.Equal(expected.WritesBlocked, actual.WritesBlocked);
        Assert.Equal(expected.Conflicts, actual.Conflicts);
        Assert.Equal(expected.Message, actual.Message);
        Assert.NotNull(expected.Capabilities);
        Assert.NotNull(actual.Capabilities);
        Assert.True(expected.Capabilities.PerformancePresets.SetEquals(actual.Capabilities.PerformancePresets));
        Assert.True(expected.Capabilities.FanModes.SetEquals(actual.Capabilities.FanModes));
        Assert.True(expected.Capabilities.LightingEffects.SetEquals(actual.Capabilities.LightingEffects));
        Assert.Equal(expected.Capabilities.MinimumChargeLimit, actual.Capabilities.MinimumChargeLimit);
        Assert.Equal(expected.Capabilities.MaximumChargeLimit, actual.Capabilities.MaximumChargeLimit);
        Assert.Equal(expected.Capabilities.ChargeLimitStep, actual.Capabilities.ChargeLimitStep);
        Assert.Equal(expected.ConfirmedState, actual.ConfirmedState);
        Assert.Equal(expected.Profiles, actual.Profiles);
        Assert.Equal(expected.DesiredState, actual.DesiredState);
        Assert.Equal(expected.Telemetry, actual.Telemetry);
        Assert.Equal(expected.PerformanceOverride, actual.PerformanceOverride);
        Assert.Equal(expected.FanOverride, actual.FanOverride);
        Assert.Equal(expected.LightingOverride, actual.LightingOverride);
        Assert.Equal(expected.FullChargeOverride, actual.FullChargeOverride);
    }
}
