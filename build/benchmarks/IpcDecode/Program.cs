using System.Diagnostics;
using OpenBlade.Contracts;

const int Iterations = 10_000;
const int Rounds = 5;
ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
var desired = new DesiredDeviceState(
    new(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
    new(FanMode.Automatic, SettingValueSource.PowerProfile),
    new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
    new(profiles.BatteryPolicy, protectionEnabled: true, temporaryFullCharge: false));
var status = new ServiceStatusSnapshot(
    Revision: 1, DeviceVerified: false, WritesBlocked: true,
    PotentialControllers: ["RazerAppEngine"], ControllerDetectionWarning: null,
    ExternalChanges: [], Message: "Blocked", Capabilities: DeviceCapabilities.Unavailable(),
    ConfirmedState: null, Profiles: profiles, DesiredState: desired,
    ActivePowerSource: PowerSource.PluggedIn, LiveReadings: new(-18400, 2300, 2500),
    PerformanceOverride: null, FanOverride: null, LightingOverride: null,
    FullChargeOverride: false, Firmware: null, ServiceVersion: "benchmark",
    KeyboardDriverModeActive: true);

using var request = new MemoryStream();
await IpcMessageCodec.WriteRequestAsync(request,
    new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion,
        Guid.Parse("12345678-1234-1234-1234-123456789abc"), profiles), CancellationToken.None);
using var frame = new MemoryStream();
await IpcMessageCodec.WriteServerFrameAsync(frame,
    new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status), CancellationToken.None);

await MeasureAsync("profile request", request, async () =>
{
    var decoded = (UpdateProfilesRequest)await IpcMessageCodec.ReadRequestAsync(request, CancellationToken.None);
    if (decoded.Profiles != profiles)
    {
        throw new InvalidDataException("Decoded profiles changed.");
    }
});
await MeasureAsync("status event", frame, async () =>
{
    var decoded = (ServiceStatusEvent)await IpcMessageCodec.ReadServerFrameAsync(frame, CancellationToken.None);
    if (decoded.Status.Profiles != profiles || decoded.Status.LiveReadings != status.LiveReadings)
    {
        throw new InvalidDataException("Decoded status changed.");
    }
});

static async Task MeasureAsync(string name, MemoryStream stream, Func<Task> decode)
{
    for (int iteration = 0; iteration < 2_000; iteration++)
    {
        stream.Position = 0;
        await decode();
    }

    var times = new double[Rounds];
    var allocations = new long[Rounds];
    for (int round = 0; round < Rounds; round++)
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            stream.Position = 0;
            await decode();
        }
        times[round] = Stopwatch.GetElapsedTime(started).TotalMicroseconds / Iterations;
        allocations[round] = (GC.GetTotalAllocatedBytes(precise: true) - allocated) / Iterations;
    }
    Array.Sort(times);
    Array.Sort(allocations);
    Console.WriteLine($"{name}: {stream.Length} bytes; median {times[Rounds / 2]:F2} us/decode; {allocations[Rounds / 2]} allocated bytes/decode; {Rounds} x {Iterations} decodes");
}
