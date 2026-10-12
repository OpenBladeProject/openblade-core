using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528HyperBoostValidationCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncorrectPadAcknowledgementNeverCreatesHardwareRunner(bool wrong)
    {
        string[] arguments = wrong
            ? ["--confirm-target", Rz090528HyperBoostValidationCommand.Confirmation,
                "--confirm-pad-ready", "present"]
            : ["--confirm-target", Rz090528HyperBoostValidationCommand.Confirmation];
        int result = await Rz090528HyperBoostValidationCommand.RunAsync(
            arguments, () => throw new InvalidOperationException("Hardware must not be opened"),
            new StringWriter(), CancellationToken.None);
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task ExactAcknowledgementsSelectOnlyPerformanceRoundTripAndDoNotClaimProductionAdmission()
    {
        var runner = new Runner();
        var output = new StringWriter();
        int code = await Rz090528HyperBoostValidationCommand.RunAsync(
            ["--confirm-target", Rz090528HyperBoostValidationCommand.Confirmation,
                "--confirm-pad-ready", Rz090528HyperBoostValidationCommand.PadConfirmation],
            () => runner, output, CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Equal(PerformanceFanValidationTarget.HyperBoostFromPerformance, runner.Target);
        Assert.Contains("\"ProductionAdmitted\": false", output.ToString());
        Assert.Contains("\"ManualRecoveryRequired\": true", output.ToString());
    }

    private sealed class Runner : IPerformanceFanSetterValidationRunner
    {
        internal PerformanceFanValidationTarget Target;
        public Task<PerformanceFanSetterValidationResult> RunAsync(
            PerformanceFanValidationTarget target, CancellationToken cancellationToken)
        {
            Target = target;
            return Task.FromResult(new PerformanceFanSetterValidationResult(
                target, null, null, null, [], null,
                true, true, false, false, false, false, true, true,
                PerformanceFanValidationFailure.CandidatePairMismatch));
        }
    }
}
