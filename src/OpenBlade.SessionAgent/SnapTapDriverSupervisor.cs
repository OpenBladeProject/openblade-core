using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;

namespace OpenBlade.SessionAgent;

/// <summary>Owns worker lifetime without sharing its subscribed driver file object.</summary>
internal static class SnapTapDriverSupervisor
{
    internal const string WorkerMode = "--snap-tap-driver-worker";
    internal static string HeartbeatName(int parent) => $"Local\\OpenBlade.SnapTap.Heartbeat.{parent}";
    internal static string StartGateName(int parent) => $"Local\\OpenBlade.SnapTap.Start.{parent}";

    internal static int Run(Func<bool> shouldStop)
    {
        var admission = new RazerSnapTapDriverAdmission();
        if (!admission.TryGetEndpoint(out var endpoint, out bool driverMissing))
        {
            return driverMissing ? 5 : 3;
        }
        // This independent CreateFile remains unsubscribed. Duplicating the
        // worker's handle would retain its subscription after worker death.
        using var guardian = new RazerSnapTapDriverTransport(endpoint!.Path, () => admission.IsCurrent(endpoint));
        IReadOnlyList<byte[]> baseline = guardian.ReadHooks();
        if (baseline.Any(IsAdHook))
        {
            return 1;
        }
        string journal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenBlade", "snaptap-driver-ownership.json");
        string boot = ReadBootIdentity();
        // An interrupted supervisor cannot establish ownership from coincidentally
        // matching global records. Keep that case closed for explicit recovery.
        if (File.Exists(journal))
        {
            using var previousFile = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (previousFile.Length > 131072)
            {
                return 4;
            }
            OwnershipJournal? previous = JsonSerializer.Deserialize<OwnershipJournal>(previousFile);
            if (!CanDiscardJournal(previous?.BootIdentity, boot, baseline))
            {
                return 4;
            }
            previousFile.Dispose();
            File.Delete(journal);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        using (var stream = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, new OwnershipJournal(boot, baseline.ToArray()));
            stream.Flush(true);
        }
        using var heartbeat = new EventWaitHandle(false, EventResetMode.AutoReset, HeartbeatName(Environment.ProcessId));
        using var startGate = new EventWaitHandle(false, EventResetMode.ManualReset, StartGateName(Environment.ProcessId));
        startGate.Reset();
        using var job = new SnapTapWorkerJob();
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset,
            SnapTapInputProcess.StopEventName(Environment.ProcessId));
        stop.Reset();
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add(WorkerMode);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process? worker = null;
        bool clean = false;
        try
        {
            worker = Process.Start(start) ?? throw new InvalidOperationException("The keyboard worker could not start.");
            job.Assign(worker);
            startGate.Set();
            long lastHeartbeat = Environment.TickCount64;
            bool started = false;
            while (!worker.HasExited)
            {
                if (heartbeat.WaitOne(50))
                {
                    lastHeartbeat = Environment.TickCount64;
                    started = true;
                }
                if (shouldStop() || Environment.TickCount64 - lastHeartbeat > (started ? 1500 : 10000))
                {
                    stop.Set();
                    if (!worker.WaitForExit(1500))
                    {
                        worker.Kill();
                        if (!worker.WaitForExit(1500))
                        {
                            return 1;
                        }
                    }
                    break;
                }
            }
            // Prove the exact child exited before touching global hook records.
            if (!worker.HasExited)
            {
                return 1;
            }
            IReadOnlyList<byte[]> hooks = guardian.ReadHooks();
            if (!SameUnownedHooks(baseline, hooks))
            {
                return 1;
            }
            foreach (ushort scan in new ushort[] { 0x1E, 0x20 })
            {
                byte[] owned = RazerSnapTapDriverProtocol.EncodeHook(scan);
                if (hooks.Any(record => record.AsSpan().SequenceEqual(owned)))
                {
                    // A fresh read reconciles hook ownership after child closure.
                    // Never synthesize a guessed key release or reset the driver.
                    guardian.ClearHook(RazerSnapTapDriverProtocol.EncodeClearHook(scan));
                    hooks = guardian.ReadHooks();
                    if (hooks.Any(record => record.AsSpan().SequenceEqual(owned))
                        || !SameUnownedHooks(baseline, hooks))
                    {
                        return 1;
                    }
                }
            }
            clean = hooks.Count == baseline.Count && SameUnownedHooks(baseline, hooks)
                && worker.ExitCode is 0 or 3;
            return clean ? worker.ExitCode : 4;
        }
        finally
        {
            // Exceptions in admission, enumeration or recovery must also close
            // the mutator. Do not leave it alive behind a failed supervisor.
            if (worker is not null)
            {
                if (!worker.HasExited)
                {
                    stop.Set();
                    if (!worker.WaitForExit(1500))
                    {
                        worker.Kill();
                        worker.WaitForExit(1500);
                    }
                }
                worker.Dispose();
            }
            if (clean)
            {
                File.Delete(journal);
            }
        }
    }

    internal static bool CanDiscardJournal(string? previousBoot, string currentBoot, IReadOnlyList<byte[]> hooks) =>
        !string.IsNullOrWhiteSpace(previousBoot) && !string.IsNullOrWhiteSpace(currentBoot)
        && !string.Equals(previousBoot, currentBoot, StringComparison.Ordinal) && !hooks.Any(IsAdHook);

    private static string ReadBootIdentity()
    {
        using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
        query.Options.Timeout = TimeSpan.FromSeconds(2);
        using ManagementObjectCollection results = query.Get();
        foreach (ManagementBaseObject result in results)
        {
            using (result)
            {
                if (result["LastBootUpTime"] is string value && !string.IsNullOrWhiteSpace(value))
                {
                    return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
            }
        }
        throw new InvalidOperationException("Windows boot identity is unavailable.");
    }

    private sealed record OwnershipJournal(string BootIdentity, byte[][] BaselineHooks);

    private static bool IsAdHook(byte[] record) =>
        RazerSnapTapDriverProtocol.TryDecodeHook(record, record.Length, out var hook)
        && hook is not null && RazerSnapTapDriverProtocol.IsAdHook(hook);

    internal static bool SameUnownedHooks(IReadOnlyList<byte[]> baseline, IReadOnlyList<byte[]> current)
    {
        var remaining = current.Where(record => !IsCanonical(record)).ToList();
        foreach (byte[] record in baseline)
        {
            int index = remaining.FindIndex(candidate => candidate.AsSpan().SequenceEqual(record));
            if (index < 0)
            {
                return false;
            }
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static bool IsCanonical(byte[] record) => record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(0x1E))
        || record.AsSpan().SequenceEqual(RazerSnapTapDriverProtocol.EncodeHook(0x20));
}
