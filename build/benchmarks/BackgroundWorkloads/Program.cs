using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Platform;
using OpenBlade.Service;

namespace OpenBlade.BackgroundWorkloads;

internal static class Program
{
    private const int Publications = 100_000;
    private const int Warmups = 1;
    private const int Rounds = 3;
    private static readonly SampleAsync Sample = BindProductionSampler();
    private delegate Task<WindowsGpuActivitySnapshot> SampleAsync(
        Func<WindowsGpuActivitySnapshot> read,
        Func<int, CancellationToken, Task> wait,
        CancellationToken cancellationToken);

    public static async Task<int> Main(string[] args)
    {
        try
        {
#if DEBUG
            Require(false, "Build and run this benchmark in Release.");
#endif
            string root = Path.GetFullPath(Argument(args, "--repo-root") ?? Directory.GetCurrentDirectory());
            string head = Git(root, "rev-parse", "HEAD");
            string? expected = Argument(args, "--expected-head");
            Require(expected is null || head.Equals(expected, StringComparison.OrdinalIgnoreCase), "Unexpected repository HEAD.");
            var provenance = new[]
            {
                Source(root, "src/OpenBlade.Service/StatusSubscriptionHub.cs", "Source.StatusSubscriptionHub", "source-linked complete helper"),
                Source(root, "src/OpenBlade.Platform/WindowsGpuAppManager.cs", "Source.WindowsGpuAppManager", "cached delegate to actual Platform static helper"),
            };
            var measurements = new List<object>();
            var retained = new List<object> { Retained("before-warmup") };
            for (int warmup = 0; warmup < Warmups; warmup++)
            {
                foreach (int readers in new[] { 1, 4, 8 })
                {
                    await FanoutAsync(readers, 2_000);
                }

                foreach (int count in new[] { 1, 16, 64 })
                {
                    await GpuAsync(count, false);
                    await GpuAsync(count, true);
                }
            }
            retained.Add(Retained("after-warmup"));
            for (int round = 1; round <= Rounds; round++)
            {
                foreach (int readers in new[] { 1, 4, 8 })
                {
                    measurements.Add(await MeasureAsync("status-hub-stalled-fanout", round, readers,
                        () => FanoutAsync(readers, Publications)));
                }

                foreach (int count in new[] { 1, 16, 64 })
                {
                    measurements.Add(await MeasureAsync("gpu-fake-awaited-sampling", round, count,
                        () => GpuAsync(count, false)));
                    measurements.Add(await MeasureAsync("gpu-fake-canceled-sampling", round, count,
                        () => GpuAsync(count, true)));
                }
                retained.Add(Retained($"after-round-{round}"));
            }
            using Process process = Process.GetCurrentProcess();
            var report = new
            {
                SchemaVersion = 1,
                Succeeded = true,
                Head = head,
                StartedFrom = root,
                Configuration = "Release",
                Framework = AppContext.TargetFrameworkName,
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessId = Environment.ProcessId,
                ProcessStartUtc = process.StartTime.ToUniversalTime(),
                Executable = Environment.ProcessPath,
                ServerGC = GCSettings.IsServerGC,
                Warmups,
                Rounds,
                PublicationsPerFanoutRound = Publications,
                GpuSampleWaitMilliseconds = 150,
                GpuSuccessfulReadsPerSampler = 3,
                CancellationTimingScope = "immediately before CancellationTokenSource.Cancel through observation of every started task; separate from whole-phase WallMilliseconds",
                AllocationScope = "GC.GetTotalAllocatedBytes(precise:true), process-wide; includes harness and awaited continuations",
                TimingScope = "setup, fake workload, semantic checks and cleanup; resource snapshots outside wall/CPU/allocation deltas except explicit pending-work checkpoint",
                ResourceSampling = "phase boundaries and one deterministic checkpoint while all GPU samples are awaiting; no periodic polling",
                RetainedGC = "forced full collection outside every timed phase; diagnostic checkpoints, no retention threshold",
                Isolation = "offline helper workloads only; no service IPC, hardware, native GPU/audio/input APIs, installed-path inspection or manager instances",
                Provenance = provenance,
                PlatformAssembly = AssemblyIdentity(typeof(WindowsGpuAppManager).Assembly),
                BenchmarkAssembly = AssemblyIdentity(typeof(Program).Assembly),
                Measurements = measurements,
                RetainedCheckpoints = retained,
            };
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            string? output = Argument(args, "--output");
            if (output is not null)
            {
                await File.WriteAllTextAsync(Path.GetFullPath(output), json);
            }

            Console.WriteLine(json);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Succeeded = false, Error = error.ToString() }));
            return 1;
        }
    }

    private static async Task<object> MeasureAsync(string name, int round, int concurrency, Func<Task<object>> workload)
    {
        object before = Resources();
        using Process process = Process.GetCurrentProcess();
        TimeSpan cpu = process.TotalProcessorTime;
        long allocated = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        object result = await workload();
        double wallMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        process.Refresh();
        double cpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        return new
        {
            Name = name,
            Round = round,
            Concurrency = concurrency,
            WallMilliseconds = wallMilliseconds,
            CpuMilliseconds = cpuMilliseconds,
            AllocatedBytes = allocatedBytes,
            Before = before,
            After = Resources(),
            Result = result
        };
    }

    private static async Task<object> FanoutAsync(int count, int publications)
    {
        var hub = new StatusSubscriptionHub(TimeProvider.System);
        var leases = new List<StatusSubscriptionLease>();
        var streams = new List<IAsyncEnumerable<ServiceStatusSnapshot>>();
        ServiceStatusSnapshot baseline = Snapshot();
        try
        {
            for (int i = 0; i < count; i++)
            {
                StatusSubscriptionLease lease = hub.Subscribe(CancellationToken.None);
                leases.Add(lease);
                streams.Add(lease.ReadAllAsync());
            }
            await AssertCapacityAsync(hub, count);
            for (int revision = 1; revision <= publications; revision++)
            {
                hub.Publish(baseline with { Revision = revision });
            }
            // Equal/older publications must not replace the final unread revision.
            hub.Publish(baseline with { Revision = publications });
            hub.Publish(baseline);
            foreach (StatusSubscriptionLease lease in leases)
            {
                await lease.DisposeAsync();
            }

            long digest = 0;
            int received = 0;
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach (IAsyncEnumerable<ServiceStatusSnapshot> stream in streams)
            {
                int perReader = 0;
                await foreach (ServiceStatusSnapshot item in stream.WithCancellation(readTimeout.Token))
                {
                    Require(item.Revision == publications, "Stalled reader did not retain the latest revision.");
                    perReader++;
                    received++;
                    digest += item.Revision;
                }
                Require(perReader == 1, "Capacity-one DropOldest boundary changed.");
            }
            Require(received == count && digest == (long)count * publications, "Fanout digest mismatch.");
            await AssertCapacityAsync(hub, 0);
            return new
            {
                Publications = publications,
                Delivered = received,
                LatestRevision = publications,
                Digest = digest,
                MaximumSubscribers = 8,
                Capacity = 1,
                FullMode = "DropOldest",
                CapacityRestoredAfterDisposal = true
            };
        }
        finally
        {
            foreach (StatusSubscriptionLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task AssertCapacityAsync(StatusSubscriptionHub hub, int existing)
    {
        var temporary = new List<StatusSubscriptionLease>();
        try
        {
            for (int i = existing; i < 8; i++)
            {
                temporary.Add(hub.Subscribe(CancellationToken.None));
            }

            bool rejected = false;
            try
            {
                StatusSubscriptionLease unexpected = hub.Subscribe(CancellationToken.None);
                await unexpected.DisposeAsync();
            }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Ninth subscriber was accepted.");
        }
        finally
        {
            foreach (StatusSubscriptionLease lease in temporary)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static async Task<object> GpuAsync(int count, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var tasks = new List<Task<WindowsGpuActivitySnapshot>>();
        int reads = 0;
        int waits = 0;
        var releaseWaits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsGpuActivitySnapshot[] samples =
        [
            new(true, [new(7, 99)]),
            new(true, [new(11, 1), new(12, 2), new(99, 50)]),
            new(true, [new(11, 5), new(12, 6), new(100, 80)]),
        ];
        int canceled = 0;
        long digest = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                int localReads = 0;
                tasks.Add(Sample(
                    () => { Interlocked.Increment(ref reads); return samples[localReads++]; },
                    async (milliseconds, token) =>
                    {
                        Require(milliseconds == 150, "Production sample interval changed.");
                        Interlocked.Increment(ref waits);
                        // A fake start barrier makes the pending-state assertion independent
                        // of host speed; the real requested delay starts after every sampler is ready.
                        await releaseWaits.Task.WaitAsync(token);
                        await Task.Delay(cancel ? Timeout.Infinite : milliseconds, token);
                    }, cancellation.Token));
            }
            Require(reads == count && waits == count && tasks.All(task => !task.IsCompleted),
                "Sampling did not suspend after exactly one initial read per sampler.");
            object pending = Resources();
            long cancellationStarted = 0;
            if (cancel)
            {
                cancellationStarted = Stopwatch.GetTimestamp();
                cancellation.Cancel();
            }
            else
            {
                releaseWaits.SetResult();
            }

            foreach (Task<WindowsGpuActivitySnapshot> task in tasks)
            {
                try
                {
                    WindowsGpuActivitySnapshot result = await task.WaitAsync(TimeSpan.FromSeconds(10));
                    Require(!cancel && result.IsAvailable && result.Processes.Count == 2,
                        "Unexpected sampling result.");
                    Require(result.Processes[0] == new WindowsGpuProcessActivity(12, 6)
                        && result.Processes[1] == new WindowsGpuProcessActivity(11, 5),
                        "Stable-process intersection/latest-value ordering changed.");
                    digest += result.Processes.Sum(item => item.ProcessId);
                }
                catch (OperationCanceledException) when (cancel && cancellation.IsCancellationRequested)
                { canceled++; }
            }
            double? cancellationToAllObservedCompletionMilliseconds = cancel
                ? Stopwatch.GetElapsedTime(cancellationStarted).TotalMilliseconds
                : null;
            Require(reads == count * (cancel ? 1 : 3) && waits == count * (cancel ? 1 : 2),
                "Read/delay counts changed, or a read occurred after cancellation.");
            Require(canceled == (cancel ? count : 0) && digest == (cancel ? 0 : count * 23L),
                "Cancellation observation or GPU result digest mismatch.");
            return new
            {
                Reads = reads,
                Waits = waits,
                CanceledTasks = canceled,
                CancellationToAllObservedCompletionMilliseconds = cancellationToAllObservedCompletionMilliseconds,
                Digest = digest,
                NoPostCancellationReads = cancel ? true : (bool?)null,
                Pending = pending
            };
        }
        finally
        {
            cancellation.Cancel();
            // Always observe every started task, including after an assertion fails.
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private static ServiceStatusSnapshot Snapshot()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new(0, false, true, [], null, [], "Offline benchmark fixture", null, null, profiles,
            new DesiredDeviceState(new(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new(FanMode.Automatic, SettingValueSource.PowerProfile),
                new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn, null, null, null, null, false);
    }

    private static SampleAsync BindProductionSampler()
    {
        MethodInfo method = typeof(WindowsGpuAppManager).GetMethod("ReadActivitySampleAsync",
            BindingFlags.Static | BindingFlags.NonPublic, null,
            [typeof(Func<WindowsGpuActivitySnapshot>), typeof(Func<int, CancellationToken, Task>), typeof(CancellationToken)], null)
            ?? throw new MissingMethodException("The exact production GPU sampling helper was not found.");
        return method.CreateDelegate<SampleAsync>();
    }

    private static object Resources()
    {
        using Process process = Process.GetCurrentProcess();
        ThreadPool.GetAvailableThreads(out int workers, out int io);
        return new
        {
            Utc = DateTimeOffset.UtcNow,
            ProcessThreads = process.Threads.Count,
            Handles = process.HandleCount,
            WorkingSetBytes = process.WorkingSet64,
            PrivateBytes = process.PrivateMemorySize64,
            ThreadPoolThreads = ThreadPool.ThreadCount,
            ThreadPoolPending = ThreadPool.PendingWorkItemCount,
            ThreadPoolCompleted = ThreadPool.CompletedWorkItemCount,
            AvailableWorkerThreads = workers,
            AvailableIoThreads = io
        };
    }

    private static object Retained(string phase)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        return new
        {
            Phase = phase,
            RetainedManagedBytes = GC.GetTotalMemory(false),
            info.HeapSizeBytes,
            info.FragmentedBytes,
            Resources = Resources()
        };
    }

    private static object Source(string root, string path, string resource, string mode)
    {
        using Stream embedded = typeof(Program).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded provenance: {resource}");
        string compiledHash = Convert.ToHexString(SHA256.HashData(embedded));
        using FileStream current = File.OpenRead(Path.Combine(root, path));
        string currentHash = Convert.ToHexString(SHA256.HashData(current));
        Require(compiledHash == currentHash, $"Source changed after build: {path}");
        Require(Git(root, "diff", "HEAD", "--", path).Length == 0, $"Measured source differs from HEAD: {path}");
        return new { Path = path, Mode = mode, CompiledSourceSha256 = compiledHash, CurrentSourceSha256 = currentHash };
    }

    private static object AssemblyIdentity(Assembly assembly)
    {
        using FileStream stream = File.OpenRead(assembly.Location);
        return new
        {
            assembly.FullName,
            assembly.Location,
            ModuleVersionId = assembly.ManifestModule.ModuleVersionId,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream))
        };
    }

    private static string Git(string root, params string[] arguments) =>
        GitAsync(root, arguments).GetAwaiter().GetResult();

    private static async Task<string> GitAsync(string root, string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Drain both pipes concurrently: either stream may fill before git exits.
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        Task<string[]> drains = Task.WhenAll(output, error);
        // Also observe a late drain failure if cleanup reaches its bounded timeout.
        _ = drains.ContinueWith(static task => { _ = task.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string[] captured = await drains.WaitAsync(timeout.Token).ConfigureAwait(false);
            Require(process.ExitCode == 0, $"git exited {process.ExitCode}: {captured[1]}");
            return captured[0].Trim();
        }
        catch (Exception failure)
        {
            bool timedOut = timeout.IsCancellationRequested;
            timeout.Cancel();
            string cleanup = string.Empty;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                cleanup = $" Process cleanup: {cleanupFailure.Message}";
            }
            try
            {
                await drains.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception drainFailure)
            {
                cleanup += $" Pipe cleanup: {drainFailure.Message}";
            }
            string capturedOutput = output.IsCompletedSuccessfully ? output.Result : "<incomplete>";
            string capturedError = error.IsCompletedSuccessfully ? error.Result : "<incomplete>";
            throw new InvalidOperationException(
                $"git {arguments[0]} failed (10-second timeout reached: {timedOut}). "
                + $"stdout: {capturedOutput}; stderr: {capturedError}.{cleanup}", failure);
        }
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0)
        {
            return null;
        }

        Require(index + 1 < args.Length, $"Missing value for {name}.");
        return args[index + 1];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
