using System.Text;
using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Platform;

namespace OpenBlade.Service.Tests;

// The native Service's GPU temperature is pinned against managed. Activity rows replay
// WindowsGpuActivitySource over scripted counter snapshots, and power rows replay
// WindowsNvidiaGpuPowerState.ClassifyPowerData. Sampler rows drive
// SafeWindowsFanTemperatureSource.ReadAsync for the GPU alone, or, when a step's reader
// is live, ReadForLiveReadingsAsync, the live-readings path since 8331bbf4, over a scripted
// activity source, power state, driver session and clock, and record what each step
// returned, how the session was used and when its lease ends. Publication rows write a
// status carrying the temperature through the real v91 status codec.
public sealed partial class ServiceControlRuntimeTests
{
    private const string NativeGpuTemperatureVectors = "gpu-temperature-vectors.json";
    private const string RegenerateNativeGpuTemperatureVectors =
        "OPENBLADE_REGENERATE_NATIVE_GPU_TEMPERATURE_VECTORS";

    private const string NativeGpuTemperatureComment =
        "Recorded by ServiceControlRuntimeTests.NativeGpuTemperatureVectors; "
        + "regenerate with OPENBLADE_REGENERATE_NATIVE_GPU_TEMPERATURE_VECTORS set to this directory. "
        + "An activity row is WindowsGpuActivitySource.IsActiveAsync over its refresh result and two "
        + "snapshots (null is unavailable, otherwise each process's [pid, utilization]); its expect is "
        + "the result and the snapshot reads. A power row is WindowsNvidiaGpuPowerState.ClassifyPowerData. "
        + "A sampler row's steps advance the clock by advanceMs, then read the GPU alone through "
        + "SafeWindowsFanTemperatureSource.ReadAsync (ReadForLiveReadingsAsync when its reader is live), "
        + "fire the session timer (fire) or dispose the source. "
        + "A read takes its workload state, its power states in call order and the session's reading, "
        + "advances the clock by readMs during the session's read and cancels during the named call. "
        + "Each expect is the reading, whether it was cancelled, the power "
        + "reads of that step, the session's cumulative reads and releases and the timer's absolute due "
        + "time in milliseconds. A publication row is whether the v91 codec writes a status carrying it.";

    private static readonly string[] NativeGpuTemperatureSections =
        ["activity", "power", "sampler", "publication"];

    [Fact]
    public async Task NativeGpuTemperatureVectors_MatchManagedSourceAndCodec()
    {
        string? output = Environment.GetEnvironmentVariable(RegenerateNativeGpuTemperatureVectors);
        string path = string.IsNullOrEmpty(output)
            ? Path.Combine(AppContext.BaseDirectory, NativeGpuTemperatureVectors)
            : Path.Combine(output, NativeGpuTemperatureVectors);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(
            ["comment", .. NativeGpuTemperatureSections],
            root.Select(member => member.Key).ToArray());
        if (!string.IsNullOrEmpty(output))
        {
            root["comment"] = NativeGpuTemperatureComment;
        }

        Assert.Equal(NativeGpuTemperatureComment, (string)root["comment"]!);
        var failures = new List<string>();
        foreach (JsonNode? entry in root["activity"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            JsonNode?[] snapshots = [row["first"], row["second"]];
            int reads = 0;
            var source = new WindowsGpuActivitySource(
                () => (bool)row["refresh"]!,
                () => GpuActivitySnapshot(snapshots[reads++]),
                (_, _) => Task.CompletedTask);
            bool? active = await source.IsActiveAsync(CancellationToken.None);
            Record(row, "expect", new JsonObject { ["active"] = active, ["reads"] = reads });
        }

        foreach (JsonNode? entry in root["power"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            FanStopGpuPowerState power = WindowsNvidiaGpuPowerState.ClassifyPowerData(
                Convert.FromHexString((string)row["data"]!),
                (uint)row["type"]!);
            Record(row, "expect", power.ToString());
        }

        foreach (JsonNode? entry in root["sampler"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "expect", await ReplayNativeGpuSamplerAsync(row["steps"]!.AsArray()));
        }

        foreach (JsonNode? entry in root["publication"]!.AsArray())
        {
            JsonObject row = entry!.AsObject();
            Record(row, "accepted", await CodecWritesGpuTemperatureAsync((int)row["celsius"]!));
        }

        if (!string.IsNullOrEmpty(output))
        {
            // One row per line keeps the shared file reviewable in a diff.
            var text = new StringBuilder("{\n\"comment\":").Append(root["comment"]!.ToJsonString());
            foreach (string section in NativeGpuTemperatureSections)
            {
                text.Append(",\n\"").Append(section).Append("\":[\n")
                    .AppendJoin(",\n", root[section]!.AsArray().Select(row => row!.ToJsonString()))
                    .Append("\n]");
            }

            text.Append("\n}\n");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        Assert.Empty(failures);

        void Record(JsonObject row, string member, JsonNode? observed)
        {
            if (!string.IsNullOrEmpty(output))
            {
                row[member] = observed;
            }
            else if (!JsonNode.DeepEquals(row[member], observed))
            {
                failures.Add($"{row["name"]} {member}: {observed?.ToJsonString() ?? "null"}");
            }
        }
    }

    private static WindowsGpuActivitySnapshot GpuActivitySnapshot(JsonNode? processes) =>
        processes is null
            ? new WindowsGpuActivitySnapshot(false, [])
            : new WindowsGpuActivitySnapshot(
                true,
                processes.AsArray()
                    .Select(process => new WindowsGpuProcessActivity(
                        (int)process![0]!,
                        (double)process[1]!))
                    .ToArray());

    private static async Task<JsonArray> ReplayNativeGpuSamplerAsync(JsonArray steps)
    {
        var time = new GpuLeaseTimeProvider();
        var activity = new ScriptedGpuActivity();
        var session = new ScriptedGpuSession();
        var power = new Queue<FanStopGpuPowerState>();
        FanStopGpuPowerState lastPower = FanStopGpuPowerState.Unknown;
        int powerReads = 0;
        string? cancel = null;
        var cancellation = new CancellationTokenSource();
        var source = new SafeWindowsFanTemperatureSource(
            activity,
            () => null,
            session,
            time,
            gpuPowerState: () =>
            {
                powerReads++;
                if ((cancel == "power" && powerReads == 1) || (cancel == "recheck" && powerReads == 2))
                {
                    cancellation.Cancel();
                }

                if (power.Count > 0)
                {
                    lastPower = power.Dequeue();
                }

                return lastPower;
            });
        var expects = new JsonArray();
        try
        {
            foreach (JsonNode? entry in steps)
            {
                JsonObject step = entry!.AsObject();
                time.Advance(TimeSpan.FromMilliseconds((int?)step["advanceMs"] ?? 0));
                var expect = new JsonObject();
                switch ((string?)step["action"] ?? "read")
                {
                    case "read":
                        cancellation = new CancellationTokenSource();
                        cancel = (string?)step["cancel"];
                        activity.Active = (bool?)step["active"];
                        activity.OnSample = cancel == "activity" ? cancellation.Cancel : null;
                        session.Temperature = (int?)step["session"];
                        TimeSpan readTime = TimeSpan.FromMilliseconds((int?)step["readMs"] ?? 0);
                        session.BeforeRead = () =>
                        {
                            time.Advance(readTime);
                            if (cancel == "read")
                            {
                                cancellation.Cancel();
                            }
                        };
                        power.Clear();
                        foreach (JsonNode? state in step["power"]!.AsArray())
                        {
                            power.Enqueue((string)state! switch
                            {
                                "on" => FanStopGpuPowerState.PoweredOn,
                                "down" => FanStopGpuPowerState.PoweredDown,
                                _ => FanStopGpuPowerState.Unknown,
                            });
                        }

                        powerReads = 0;
                        try
                        {
                            FanTemperatureReadings readings = (string?)step["reader"] switch
                            {
                                null => await source.ReadAsync(
                                    includeCpu: false,
                                    includeGpu: true,
                                    cancellation.Token),
                                "live" => await source.ReadForLiveReadingsAsync(
                                    includeCpu: false,
                                    includeGpu: true,
                                    cancellation.Token),
                                string reader => throw new InvalidDataException($"Unknown sampler reader {reader}."),
                            };
                            expect["celsius"] = readings.GpuCelsius;
                            expect["cancelled"] = false;
                        }
                        catch (OperationCanceledException)
                        {
                            expect["celsius"] = null;
                            expect["cancelled"] = true;
                        }

                        expect["powerReads"] = powerReads;
                        break;
                    case "fire":
                        time.Fire();
                        break;
                    case "dispose":
                        source.Dispose();
                        break;
                    default:
                        throw new InvalidDataException($"Unknown sampler action {step["action"]}.");
                }

                expect["reads"] = session.Reads;
                expect["releases"] = session.Releases;
                expect["leaseAtMs"] = time.DueAtMilliseconds;
                expects.Add(expect);
            }
        }
        finally
        {
            source.Dispose();
        }

        return expects;
    }

    private async Task<bool> CodecWritesGpuTemperatureAsync(int celsius)
    {
        TestContext context = CreateContext(providerAvailable: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus() with
        {
            LiveReadings = new LiveReadings(null, null, null, null, celsius),
        };
        using var stream = new MemoryStream();
        try
        {
            await IpcMessageCodec.WriteServerFrameAsync(
                stream,
                new ServiceStatusEvent(OpenBladeIpc.ProtocolVersion, status),
                CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        stream.Position = 0;
        var decoded = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(stream, CancellationToken.None));
        Assert.Equal(celsius, decoded.Status.LiveReadings?.GpuTemperatureCelsius);
        return true;
    }

    private sealed class ScriptedGpuActivity : IGpuActivitySource
    {
        public bool? Active { get; set; }

        public Action? OnSample { get; set; }

        public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnSample?.Invoke();
            return new(Active);
        }
    }

    private sealed class ScriptedGpuSession : IGpuTemperatureSession
    {
        public int? Temperature { get; set; }

        public Action? BeforeRead { get; set; }

        public int Reads { get; private set; }

        public int Releases { get; private set; }

        public int? TryRead()
        {
            Reads++;
            BeforeRead?.Invoke();
            return Temperature;
        }

        public void Release() => Releases++;
    }

    // A clock whose timers fire only when asked; each timer's due time is absolute.
    private sealed class GpuLeaseTimeProvider : TimeProvider
    {
        private readonly List<LeaseTimer> timers = [];
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;

        public long? DueAtMilliseconds =>
            timers.Select(timer => timer.DueAt).SingleOrDefault() / TimeSpan.TicksPerMillisecond;

        public void Fire()
        {
            foreach (LeaseTimer timer in timers.ToArray())
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new LeaseTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        private sealed class LeaseTimer(
            GpuLeaseTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            public long? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.timestamp + dueTime.Ticks;
                return true;
            }

            public void Fire()
            {
                if (DueAt is not null)
                {
                    DueAt = null;
                    callback(state);
                }
            }

            public void Dispose() => owner.timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
