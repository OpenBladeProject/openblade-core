namespace OpenBlade.Capture.Tests;

public sealed class StockSteelNomadWorkloadTests
{
    private const string ExpectedPath = @"C:\Benchmarks\3DMarkSteelNomad.exe";
    private static StockWorkloadProcess Exact => new(42, DateTimeOffset.UnixEpoch.AddSeconds(1),
        ExpectedPath, StockSteelNomadWorkload.ExpectedSha256, StockSteelNomadWorkload.ExpectedVersion);

    [Fact]
    public void PreexistingProcessIsNeverAttached()
    {
        var workload = new StockSteelNomadWorkload(ExpectedPath, () => [Exact], () => 95);
        Assert.Equal("PreexistingWorkloadRefused", workload.Arm(DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("version")]
    [InlineData("created")]
    public void WrongIdentityIsRefused(string field)
    {
        IReadOnlyList<StockWorkloadProcess> processes = [];
        var workload = new StockSteelNomadWorkload(ExpectedPath, () => processes, () => 95);
        Assert.Null(workload.Arm(DateTimeOffset.UnixEpoch));
        processes = [field switch
        {
            "path" => Exact with { Path = @"C:\Other\3DMarkSteelNomad.exe" },
            "hash" => Exact with { Sha256 = new string('0', 64) },
            "version" => Exact with { Version = "2.0" },
            _ => Exact with { StartedAtUtc = DateTimeOffset.UnixEpoch },
        }];
        Assert.Equal("WorkloadIdentityMismatch", workload.Observe().Failure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PidReuseOrDifferentProcessCannotReplaceBoundWorkload(bool reuse)
    {
        IReadOnlyList<StockWorkloadProcess> processes = [];
        var workload = new StockSteelNomadWorkload(ExpectedPath, () => processes, () => 95);
        Assert.Null(workload.Arm(DateTimeOffset.UnixEpoch));
        processes = [Exact];
        Assert.True(workload.Observe().RunningExact);
        processes = [reuse ? Exact with { StartedAtUtc = Exact.StartedAtUtc.AddSeconds(1) } : Exact with { ProcessId = 43 }];
        Assert.Equal("WorkloadProcessReplaced", workload.Observe().Failure);
    }

    [Fact]
    public void MultipleProcessesExitAndMissingUtilizationAreNotLoadEvidence()
    {
        IReadOnlyList<StockWorkloadProcess> processes = [];
        uint? utilization = 95;
        var workload = new StockSteelNomadWorkload(ExpectedPath, () => processes, () => utilization);
        Assert.Null(workload.Arm(DateTimeOffset.UnixEpoch));
        processes = [Exact, Exact with { ProcessId = 43 }];
        Assert.Equal("MultipleWorkloadsRefused", workload.Observe().Failure);
        processes = [Exact];
        utilization = null;
        Assert.Equal("GpuUtilizationUnavailable", workload.Observe().Failure);
        processes = [];
        Assert.Equal("WorkloadExited", workload.Observe().Failure);
    }
}
