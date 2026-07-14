using System.IO.Pipes;
using OpenSynapse.Contracts;
using OpenSynapse.Tray;

namespace OpenSynapse.Tray.Tests;

public sealed class ServiceClientTests
{
    [Fact]
    public async Task GetStatusAsync_RoundTripsTypedStatus()
    {
        string pipeName = $"OpenSynapse.Tray.Tests.{Guid.NewGuid():N}";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunFakeServerAsync(pipeName, timeout.Token);
        ServiceClient client = new(pipeName);

        ServiceStatusSnapshot status = await client.GetStatusAsync(timeout.Token);
        await server;

        Assert.False(status.DeviceVerified);
        Assert.True(status.WritesBlocked);
        Assert.Equal(["RazerAppEngine"], status.Conflicts);
    }

    private static async Task RunFakeServerAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(
            server,
            cancellationToken);
        ServiceResponse response = new(
            OpenSynapseIpc.ProtocolVersion,
            request.RequestId,
            true,
            null,
            CreateStatus(),
            null);
        await IpcMessageCodec.WriteServerFrameAsync(server, response, cancellationToken);
    }

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            0,
            false,
            true,
            ["RazerAppEngine"],
            "Blocked",
            DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                profiles.BatteryPolicy),
            new TelemetrySnapshot(DateTimeOffset.MinValue, PowerSource.PluggedIn, null, null, null, null),
            null,
            null,
            null,
            false);
    }
}
