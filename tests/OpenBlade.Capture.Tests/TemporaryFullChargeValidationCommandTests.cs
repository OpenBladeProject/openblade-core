using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class TemporaryFullChargeValidationCommandTests
{
    [Fact]
    public async Task RunAsync_AcceptsNoArgumentsAndRendersOnlyTypedClassifications()
    {
        var output = new StringWriter();
        var command = new TemporaryFullChargeValidationCommand(
            _ => Task.FromResult(Success()),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Contains("BaselineProtection=Limit80", output.ToString());
        Assert.Contains("BaselineTemporaryFullCharge=False", output.ToString());
        Assert.Contains("EnableAcknowledged=True", output.ToString());
        Assert.Contains("CandidateProtection=Limit80", output.ToString());
        Assert.Contains("CandidateTemporaryFullCharge=True", output.ToString());
        Assert.Contains("RestorationProtection=Limit80", output.ToString());
        Assert.Contains("RestorationTemporaryFullCharge=False", output.ToString());
        Assert.Contains("CancelAcknowledged=True", output.ToString());
        Assert.Contains("Success=True", output.ToString());
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Catalog", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("cancel")]
    [InlineData("true")]
    public async Task RunAsync_RejectsEveryArgumentWithoutInvokingValidation(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new TemporaryFullChargeValidationCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult(Success());
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(TemporaryFullChargeValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(
            Lines("Usage: OpenBlade.Capture validate-temporary-full-charge"),
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_CancellationTakesPrecedenceOverReturnedResult()
    {
        Action? cancel = null;
        var output = new StringWriter();
        var command = new TemporaryFullChargeValidationCommand(
            _ =>
            {
                cancel!();
                return Task.FromResult(Success());
            },
            output,
            callback =>
            {
                cancel = callback;
                return NoopDisposable.Instance;
            });

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(TemporaryFullChargeValidationCommand.CanceledExitCode, exitCode);
        Assert.Equal(Lines("Canceled=True"), output.ToString());
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("yes", false)]
    [InlineData(" YES", false)]
    public async Task ConsoleApproval_RequiresExactYesAndDescribesReversibleAction(
        string response,
        bool expected)
    {
        var output = new StringWriter();
        var approval = new ConsoleTemporaryFullChargeApproval(
            new StringReader(response + Environment.NewLine),
            output);

        bool approved = await approval.ConfirmAsync(
            BatteryValidationSetting.Limit80,
            false,
            default);

        Assert.Equal(expected, approved);
        Assert.Contains("Baseline protection: Limit80", output.ToString());
        Assert.Contains("Baseline temporary full charge: False", output.ToString());
        Assert.Contains("cancel it, and verify restoration", output.ToString());
    }

    private static TemporaryFullChargeValidationResult Success() => new(
        BatteryValidationSetting.Limit80,
        false,
        true,
        true,
        BatteryValidationSetting.Limit80,
        true,
        true,
        true,
        true,
        BatteryValidationSetting.Limit80,
        false,
        TemporaryFullChargeValidationPrimaryFailure.None,
        TemporaryFullChargeValidationRestorationFailure.None);

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
