using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528CpuPmObservationProbeTests
{
    private const string Run = "abcdef0123456789abcdef0123456789";
    private static string[] Valid() => [Rz090528CpuPmObservationProbe.TransportApproval, Rz090528CpuPmObservationProbe.LifetimeApproval, "--run-id", Run];

    [Fact]
    public void ExactScopeParsesWithOnlyCanonicalGuidAndDistinctFlags()
    {
        var parsed = Rz090528CpuPmObservationProbe.Parse(Valid()); Assert.Equal(Guid.ParseExact(Run, "N"), parsed!.RunId);
        Assert.NotNull(Rz090528CpuPmObservationProbe.Parse(["--run-id", Run, Valid()[1], Valid()[0]]));
        Assert.Equal(Path.Combine(Rz090528CpuPmObservationProbe.PrivateRoot, Run), Rz090528CpuPmObservationProbe.GetPrivateRunDirectory(parsed));
    }

    [Fact]
    public void MalformedOrExpandedArgumentsRefuseWithoutOutputOrInvocation()
    {
        string[][] cases = [[], [Valid()[0]], [Valid()[0], Valid()[1]], [Valid()[0], Valid()[0], "--run-id", Run],
            [Valid()[0], Valid()[1], "--run-id"], [Valid()[0].ToUpperInvariant(), Valid()[1], "--run-id", Run],
            [Valid()[0], Valid()[1], "--run-id", Run, "--set-ppt=45"],
            [Valid()[0], Valid()[1], "--run-id", "../outside"], [Valid()[0], Valid()[1], "--run-id", Run.ToUpperInvariant()],
            [Valid()[0], Valid()[1], "--run-id", Guid.Parse(Run).ToString("D")],
            [Valid()[0], Valid()[1], "--run-id", Guid.Empty.ToString("N")],
            [Valid()[0], Valid()[1], "--output", Run], [Valid()[0], Valid()[1], "--run-id", null!]];
        foreach (var args in cases)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Assert.Null(Rz090528CpuPmObservationProbe.Parse(args));
            Assert.Equal(2, Rz090528CpuPmObservationProbe.RunUnavailable(args, output, error));
            Assert.Empty(output.ToString()); Assert.Contains("Usage:", error.ToString());
        }
        Assert.Null(Rz090528CpuPmObservationProbe.Parse(null));
    }

    [Fact]
    public void CorrectFlagsStillCannotCreateNativeAuthorityOrOutput()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(3, Rz090528CpuPmObservationProbe.RunUnavailable(Valid(), output, error));
        Assert.Contains("authenticated live-parent lifetime authority is not implemented", output.ToString());
        Assert.Contains("NativeExecution=NotStarted", output.ToString());
        Assert.Contains("LivePptTarget=Unknown", output.ToString()); Assert.Empty(error.ToString());
        Assert.DoesNotContain(Run, output.ToString());
    }
}
