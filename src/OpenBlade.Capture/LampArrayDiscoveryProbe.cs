using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.System;

namespace OpenBlade.Capture;

internal sealed record LampArrayLampSnapshot(
    int Index,
    float X,
    float Y,
    float Z,
    string Purposes,
    int RedLevelCount,
    int GreenLevelCount,
    int BlueLevelCount,
    int GainLevelCount,
    long UpdateLatencyMicroseconds);

internal sealed record LampArrayVirtualKeySnapshot(
    string Key,
    IReadOnlyList<int> Indices);

internal sealed record LampArrayDeviceSnapshot(
    ushort VendorId,
    ushort ProductId,
    string Kind,
    int LampCount,
    bool IsAvailable,
    bool IsConnected,
    bool IsEnabled,
    bool SupportsVirtualKeys,
    double BrightnessLevel,
    long MinUpdateIntervalMicroseconds,
    float BoundingBoxWidth,
    float BoundingBoxHeight,
    float BoundingBoxDepth,
    IReadOnlyList<LampArrayLampSnapshot> Lamps,
    IReadOnlyList<LampArrayVirtualKeySnapshot> VirtualKeys)
{
    public string GeometrySha256 { get; } = Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new
                {
                    Lamps,
                    VirtualKeys,
                }))));
}

internal sealed record LampArrayProbeSnapshot(
    int EnumeratedCount,
    IReadOnlyList<LampArrayDeviceSnapshot> ExactDevices,
    string? Failure)
{
    internal bool Success => Failure is null && ExactDevices.Count > 0;
}

internal interface ILampArrayDiscoveryProbe
{
    Task<LampArrayProbeSnapshot> RunAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsLampArrayDiscoveryProbe : ILampArrayDiscoveryProbe
{
    private const ushort TargetVendorId = 0x1532;
    private const ushort TargetProductId = 0x02C6;

    public async Task<LampArrayProbeSnapshot> RunAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<DeviceInformation> devices = await EnumerateAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var exact = new List<LampArrayDeviceSnapshot>();
            foreach (DeviceInformation device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LampArray? lampArray = await LampArray.FromIdAsync(device.Id)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (lampArray is null
                    || lampArray.HardwareVendorId != TargetVendorId
                    || lampArray.HardwareProductId != TargetProductId)
                {
                    continue;
                }

                exact.Add(Snapshot(lampArray));
            }

            return new(devices.Count, exact, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(0, [], $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static async Task<IReadOnlyList<DeviceInformation>> EnumerateAsync(
        CancellationToken cancellationToken)
    {
        var devices = new ConcurrentQueue<DeviceInformation>();
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(
            LampArray.GetDeviceSelector());
        watcher.Added += (_, device) => devices.Enqueue(device);
        watcher.EnumerationCompleted += (_, _) => completed.TrySetResult();
        watcher.Stopped += (_, _) => completed.TrySetResult();
        watcher.Start();
        try
        {
            await completed.Task
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (watcher.Status is
                DeviceWatcherStatus.Started or
                DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }

        return devices.ToArray();
    }

    private static LampArrayDeviceSnapshot Snapshot(LampArray lampArray)
    {
        Vector3 boundingBox = lampArray.BoundingBox;
        var lamps = new List<LampArrayLampSnapshot>(lampArray.LampCount);
        for (int index = 0; index < lampArray.LampCount; index++)
        {
            LampInfo info = lampArray.GetLampInfo(index);
            Vector3 position = info.Position;
            lamps.Add(new(
                info.Index,
                position.X,
                position.Y,
                position.Z,
                info.Purposes.ToString(),
                info.RedLevelCount,
                info.GreenLevelCount,
                info.BlueLevelCount,
                info.GainLevelCount,
                checked((long)(info.UpdateLatency.TotalMilliseconds * 1000))));
        }

        IReadOnlyList<LampArrayVirtualKeySnapshot> virtualKeys =
            lampArray.SupportsVirtualKeys
                ? Enum.GetValues<VirtualKey>()
                    .Distinct()
                    .Select(key => new LampArrayVirtualKeySnapshot(
                        key.ToString(),
                        lampArray.GetIndicesForKey(key).ToArray()))
                    .Where(mapping => mapping.Indices.Count > 0)
                    .ToArray()
                : [];
        return new(
            lampArray.HardwareVendorId,
            lampArray.HardwareProductId,
            lampArray.LampArrayKind.ToString(),
            lampArray.LampCount,
            lampArray.IsAvailable,
            lampArray.IsConnected,
            lampArray.IsEnabled,
            lampArray.SupportsVirtualKeys,
            lampArray.BrightnessLevel,
            checked((long)(lampArray.MinUpdateInterval.TotalMilliseconds * 1000)),
            boundingBox.X,
            boundingBox.Y,
            boundingBox.Z,
            lamps,
            virtualKeys);
    }
}

internal sealed class LampArrayDiscoveryCommand(
    ILampArrayDiscoveryProbe probe,
    TextWriter output)
{
    internal const int UsageExitCode = 1;
    internal const int ProbeFailureExitCode = 2;
    internal const int ExactDeviceNotFoundExitCode = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 0)
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture probe-rz09-0528-02c6-lamp-array")
                .ConfigureAwait(false);
            return UsageExitCode;
        }

        LampArrayProbeSnapshot snapshot = await probe.RunAsync(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonOptions))
            .ConfigureAwait(false);
        return snapshot.Failure is not null
            ? ProbeFailureExitCode
            : snapshot.ExactDevices.Count == 0
                ? ExactDeviceNotFoundExitCode
                : 0;
    }
}
