using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.System;

namespace OpenBlade.SessionAgent;

internal sealed record LampArrayLampFingerprint(
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

internal sealed record LampArrayVirtualKeyFingerprint(
    string Key,
    IReadOnlyList<int> Indices);

internal sealed class WindowsLampArrayObservationSource : ILampArrayObservationSource
{
    private const ushort TargetVendorId = 0x1532;
    private const ushort TargetProductId = 0x02C6;
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    public async Task<LampArrayObservation> ObserveAsync(
        CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        bounded.CancelAfter(ObservationTimeout);
        CancellationToken observationToken = bounded.Token;
        try
        {
            IReadOnlyList<DeviceInformation> devices = await EnumerateAsync(
                    observationToken)
                .ConfigureAwait(false);
            var exact = new List<LampArray>();
            int openFailures = 0;
            foreach (DeviceInformation device in devices)
            {
                observationToken.ThrowIfCancellationRequested();
                try
                {
                    LampArray? lampArray = await LampArray.FromIdAsync(device.Id)
                        .AsTask(observationToken)
                        .ConfigureAwait(false);
                    if (lampArray is not null
                        && lampArray.HardwareVendorId == TargetVendorId
                        && lampArray.HardwareProductId == TargetProductId)
                    {
                        exact.Add(lampArray);
                    }
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (bounded.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    openFailures++;
                }
            }

            if (exact.Count == 0)
            {
                if (openFailures > 0)
                {
                    throw new InvalidOperationException(
                        "One or more LampArray endpoints could not be opened.");
                }

                return new(
                    devices.Count,
                    ExactDeviceCount: 0,
                    Kind: null,
                    LampCount: null,
                    IsConnected: null,
                    IsEnabled: null,
                    IsAvailable: null,
                    SupportsVirtualKeys: null,
                    MinimumUpdateIntervalMicroseconds: null,
                    GeometrySha256: null);
            }

            LampArray first = exact[0];
            return new(
                devices.Count,
                exact.Count,
                first.LampArrayKind.ToString(),
                first.LampCount,
                first.IsConnected,
                first.IsEnabled,
                first.IsAvailable,
                first.SupportsVirtualKeys,
                checked((long)(first.MinUpdateInterval.TotalMilliseconds * 1000)),
                ComputeGeometrySha256(first));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Windows LampArray observation failed " +
                $"({exception.GetType().Name}, 0x{exception.HResult:X8}).");
        }
    }

    private static async Task<IReadOnlyList<DeviceInformation>> EnumerateAsync(
        CancellationToken cancellationToken)
    {
        var devices = new ConcurrentQueue<DeviceInformation>();
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DeviceWatcher watcher = DeviceInformation.CreateWatcher(
            LampArray.GetDeviceSelector());
        watcher.Added += (_, device) => devices.Enqueue(device);
        watcher.EnumerationCompleted += (_, _) => completed.TrySetResult(true);
        watcher.Stopped += (_, _) => completed.TrySetResult(false);
        watcher.Start();
        try
        {
            bool enumerationCompleted = await completed.Task
                .WaitAsync(EnumerationTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!enumerationCompleted)
            {
                throw new InvalidOperationException(
                    "The Windows LampArray watcher stopped before enumeration completed.");
            }
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

    private static string ComputeGeometrySha256(LampArray lampArray)
    {
        var lamps = new List<LampArrayLampFingerprint>(lampArray.LampCount);
        for (int index = 0; index < lampArray.LampCount; index++)
        {
            LampInfo info = lampArray.GetLampInfo(index);
            System.Numerics.Vector3 position = info.Position;
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

        IReadOnlyList<LampArrayVirtualKeyFingerprint> virtualKeys =
            lampArray.SupportsVirtualKeys
                ? Enum.GetValues<VirtualKey>()
                    .Distinct()
                    .Select(key => new LampArrayVirtualKeyFingerprint(
                        key.ToString(),
                        lampArray.GetIndicesForKey(key).ToArray()))
                    .Where(mapping => mapping.Indices.Count > 0)
                    .ToArray()
                : [];
        byte[] serialized = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                Lamps = lamps,
                VirtualKeys = virtualKeys,
            }));
        return Convert.ToHexString(SHA256.HashData(serialized));
    }
}
