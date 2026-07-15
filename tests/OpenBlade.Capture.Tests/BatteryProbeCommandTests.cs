using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryProbeCommandTests
{
    [Fact]
    public async Task RunAsync_ReadyProbeRendersOnlyClosedMetadata()
    {
        var output = new StringWriter();
        var command = new BatteryProbeCommand(
            () => Result(
                BatteryValidationProbeStage.Ready,
                BatteryValidationProbeClassification.Ready,
                openError: 5),
            output);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(BatteryProbeCommand.SuccessExitCode, exitCode);
        Assert.Equal(
            "Stage=Ready\r\n" +
            "Classification=Ready\r\n" +
            "HostIdentityMatched=True\r\n" +
            "ConflictCheckPassed=True\r\n" +
            "EnumeratedCandidateCount=3\r\n" +
            "CandidateOpenAttemptCount=3\r\n" +
            "CandidateOpenSuccessCount=2\r\n" +
            "CandidateOpenWin32Error=5\r\n" +
            "CandidateAccessFallbackCount=0\r\n" +
            "IdentityReadCount=2\r\n" +
            "VendorMatchCount=2\r\n" +
            "ProductMatchCount=1\r\n" +
            "UsagePageMatchCount=2\r\n" +
            "UsageMatchCount=2\r\n" +
            "FeatureLengthMatchCount=2\r\n" +
            "ExactMatchCount=1\r\n",
            output.ToString());
        Assert.DoesNotContain("1532", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("02E0", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DevicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Handle", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transport", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_BlockedProbeReturnsDiagnosticFailureAndRendersMissingError()
    {
        var output = new StringWriter();
        var command = new BatteryProbeCommand(
            () => Result(
                BatteryValidationProbeStage.HostIdentity,
                BatteryValidationProbeClassification.HostIdentityBlocked,
                openError: null),
            output);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(BatteryProbeCommand.ProbeFailureExitCode, exitCode);
        Assert.Contains("Classification=HostIdentityBlocked", output.ToString());
        Assert.Contains("CandidateOpenWin32Error=None", output.ToString());
    }

    [Fact]
    public async Task RunAsync_AccessFallbackReadyProbeIsSuccessful()
    {
        var output = new StringWriter();
        var command = new BatteryProbeCommand(
            () => Result(
                BatteryValidationProbeStage.Ready,
                BatteryValidationProbeClassification.ReadyWithAccessFallback,
                openError: 5) with
            {
                CandidateAccessFallbackCount = 1,
            },
            output);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(BatteryProbeCommand.SuccessExitCode, exitCode);
        Assert.Contains("Classification=ReadyWithAccessFallback", output.ToString());
        Assert.Contains("CandidateAccessFallbackCount=1", output.ToString());
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("--verbose")]
    public async Task RunAsync_RejectsEveryArgumentWithoutInvokingProbe(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new BatteryProbeCommand(
            () =>
            {
                invoked = true;
                return Result(
                    BatteryValidationProbeStage.Ready,
                    BatteryValidationProbeClassification.Ready,
                    null);
            },
            output);

        int exitCode = await command.RunAsync([argument], CancellationToken.None);

        Assert.Equal(BatteryProbeCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal("Usage: OpenBlade.Capture probe-battery\r\n", output.ToString());
    }

    private static BatteryValidationProbeResult Result(
        BatteryValidationProbeStage stage,
        BatteryValidationProbeClassification classification,
        int? openError) =>
        new(
            stage,
            classification,
            HostIdentityMatched: classification != BatteryValidationProbeClassification.HostIdentityBlocked,
            ConflictCheckPassed: classification is
                BatteryValidationProbeClassification.Ready or
                BatteryValidationProbeClassification.ReadyWithAccessFallback,
            EnumeratedCandidateCount: 3,
            CandidateOpenAttemptCount: 3,
            CandidateOpenSuccessCount: 2,
            CandidateOpenWin32Error: openError,
            CandidateAccessFallbackCount: 0,
            IdentityReadCount: 2,
            VendorMatchCount: 2,
            ProductMatchCount: 1,
            UsagePageMatchCount: 2,
            UsageMatchCount: 2,
            FeatureLengthMatchCount: 2,
            ExactMatchCount: classification is
                BatteryValidationProbeClassification.Ready or
                BatteryValidationProbeClassification.ReadyWithAccessFallback ? 1 : 0);
}
