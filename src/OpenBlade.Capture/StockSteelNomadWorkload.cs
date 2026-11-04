using System.Diagnostics;
using System.Security.Cryptography;

namespace OpenBlade.Capture;

internal sealed record StockWorkloadProcess(int ProcessId, DateTimeOffset StartedAtUtc,
    string Path, string Sha256, string Version);

internal sealed class StockSteelNomadWorkload(
    string expectedPath, Func<IReadOnlyList<StockWorkloadProcess>> enumerate, Func<uint?> utilization) : IStockLoadWorkload
{
    internal const string ExpectedExecutableName = "3DMarkSteelNomad.exe";
    internal const string ExpectedSha256 = "8C690B613734D680268727B9A73475709593D606125C2B73234171C5BB3D312E";
    internal const string ExpectedVersion = "1.0.5.1";
    private DateTimeOffset armedAt;
    private readonly string fullExpectedPath = Path.IsPathFullyQualified(expectedPath)
        && Path.GetFileName(expectedPath).Equals(ExpectedExecutableName, StringComparison.OrdinalIgnoreCase)
        ? Path.GetFullPath(expectedPath) : throw new ArgumentException("Exact workload executable path required.", nameof(expectedPath));
    internal StockWorkloadProcess? Bound { get; private set; }

    public string? Arm(DateTimeOffset armedAtUtc)
    {
        if (enumerate().Count != 0)
        {
            return "PreexistingWorkloadRefused";
        }

        armedAt = armedAtUtc;
        return null;
    }

    public StockLoadWorkloadObservation Observe()
    {
        var processes = enumerate();
        if (processes.Count == 0)
        {
            return new(false, null, Bound is null ? null : "WorkloadExited");
        }

        if (processes.Count != 1)
        {
            return new(false, null, "MultipleWorkloadsRefused");
        }

        var process = processes[0];
        if (process.StartedAtUtc <= armedAt || !Path.GetFullPath(process.Path).Equals(fullExpectedPath, StringComparison.OrdinalIgnoreCase)
            || process.Sha256 != ExpectedSha256 || process.Version != ExpectedVersion)
        {
            return new(false, null, "WorkloadIdentityMismatch");
        }

        if (Bound is not null && (process.ProcessId != Bound.ProcessId || process.StartedAtUtc != Bound.StartedAtUtc))
        {
            return new(false, null, "WorkloadProcessReplaced");
        }

        Bound ??= process;
        uint? percent = utilization();
        return percent is <= 100 ? new(true, percent, null) : new(false, null, "GpuUtilizationUnavailable");
    }

    internal static IReadOnlyList<StockWorkloadProcess> EnumerateWindows()
    {
        var result = new List<StockWorkloadProcess>();
        Process[] processes = Process.GetProcessesByName("3DMarkSteelNomad");
        try
        {
            foreach (var process in processes)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException("Workload exited during identity read.");
                }

                string path = process.MainModule?.FileName ?? throw new InvalidOperationException("Workload identity unavailable.");
                result.Add(new(process.Id, process.StartTime.ToUniversalTime(), Path.GetFullPath(path),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty));
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
        return result;
    }
}
