using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryValidationCommandTests
{
    [Theory]
    [InlineData("off", BatteryValidationSetting.ProtectionOff)]
    [InlineData("50", BatteryValidationSetting.Limit50)]
    [InlineData("55", BatteryValidationSetting.Limit55)]
    [InlineData("60", BatteryValidationSetting.Limit60)]
    [InlineData("65", BatteryValidationSetting.Limit65)]
    [InlineData("70", BatteryValidationSetting.Limit70)]
    [InlineData("75", BatteryValidationSetting.Limit75)]
    [InlineData("80", BatteryValidationSetting.Limit80)]
    public async Task RunAsync_AcceptsEveryExactCandidateToken(
        string token,
        BatteryValidationSetting expected)
    {
        BatteryValidationSetting? observed = null;
        var output = new StringWriter();
        var command = new BatteryValidationCommand(
            (candidate, _) =>
            {
                observed = candidate;
                return Task.FromResult(Success());
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([token], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, observed);
    }

    [Theory]
    [InlineData()]
    [InlineData("85")]
    [InlineData("OFF")]
    [InlineData("50", "extra")]
    public async Task RunAsync_RejectsMissingUnknownOrNonExactCandidates(params string[] arguments)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new BatteryValidationCommand(
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(Success());
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(arguments, CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Contains("validate-battery <off|50|55|60|65|70|75|80>", output.ToString());
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("yes", false)]
    [InlineData("YES ", false)]
    [InlineData("", false)]
    public async Task ConsoleApproval_ShowsPriorAndCandidateAndRequiresExactYes(
        string response,
        bool expected)
    {
        var input = new StringReader(response + Environment.NewLine);
        var output = new StringWriter();
        var approval = new ConsoleBatteryApproval(input, output);

        bool approved = await approval.ConfirmAsync(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit75,
            CancellationToken.None);

        Assert.Equal(expected, approved);
        Assert.Contains("Prior: Limit80", output.ToString());
        Assert.Contains("Candidate: Limit75", output.ToString());
        Assert.Contains("YES", output.ToString());
    }

    [Fact]
    public async Task RunAsync_RendersEverySuccessfulResultField()
    {
        var output = new StringWriter();
        var command = CreateCommand(_ => Task.FromResult(Success()), output);

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            Lines(
                "ApplyAttempted=True",
                "Applied=True",
                "ReadBack=True",
                "Restored=True",
                "PrimaryFailure=None",
                "RestorationFailure=None",
                "Success=True"),
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_RendersConflictWithoutClaimingAnApply()
    {
        var output = new StringWriter();
        var command = CreateCommand(
            _ => Task.FromResult(new BatteryValidationResult(
                ApplyAttempted: false,
                Applied: false,
                ReadBack: false,
                Restored: false,
                PrimaryFailure: BatteryValidationPrimaryFailure.Conflict,
                RestorationFailure: BatteryValidationRestorationFailure.None)),
            output);

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Contains("ApplyAttempted=False", output.ToString());
        Assert.Contains("PrimaryFailure=Conflict", output.ToString());
    }

    [Fact]
    public async Task RunAsync_AppendsOnlySafeBaselineDiagnosticFields()
    {
        var output = new StringWriter();
        BatteryValidationResult result = new(
            ApplyAttempted: false,
            Applied: false,
            ReadBack: false,
            Restored: false,
            PrimaryFailure: BatteryValidationPrimaryFailure.BaselineQueryFailed,
            RestorationFailure: BatteryValidationRestorationFailure.None)
        {
            BaselineQueryDiagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.GetCompletion,
                HidSessionFailureKind.NativeIoFailure,
                NativeWin32Error: 31,
                TransferredByteCount: 12),
        };
        var command = CreateCommand(_ => Task.FromResult(result), output);

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Contains("BaselineQueryPhase=GetCompletion", output.ToString());
        Assert.Contains("BaselineQuerySessionFailure=NativeIoFailure", output.ToString());
        Assert.Contains("BaselineQueryWin32Error=31", output.ToString());
        Assert.Contains("BaselineQueryTransferredBytes=12", output.ToString());
        Assert.DoesNotContain("Payload=", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureWhenRestorationFails()
    {
        var output = new StringWriter();
        var command = CreateCommand(
            _ => Task.FromResult(new BatteryValidationResult(
                ApplyAttempted: true,
                Applied: true,
                ReadBack: true,
                Restored: false,
                PrimaryFailure: BatteryValidationPrimaryFailure.None,
                RestorationFailure: BatteryValidationRestorationFailure.RestoreMismatch)),
            output);

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Contains("Restored=False", output.ToString());
        Assert.Contains("RestorationFailure=RestoreMismatch", output.ToString());
    }

    [Fact]
    public async Task RunAsync_AppendsOnlySafeRestorationDiagnosticFields()
    {
        var output = new StringWriter();
        BatteryValidationResult result = new(
            ApplyAttempted: true,
            Applied: true,
            ReadBack: true,
            Restored: false,
            PrimaryFailure: BatteryValidationPrimaryFailure.None,
            RestorationFailure: BatteryValidationRestorationFailure.RestoreQueryFailed)
        {
            RestorationDiagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.GetCompletion,
                HidSessionFailureKind.Timeout,
                NativeWin32Error: 995,
                TransferredByteCount: 0)
            {
                ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
            },
        };
        var command = CreateCommand(_ => Task.FromResult(result), output);

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.ValidationFailureExitCode, exitCode);
        Assert.Contains("RestorationPhase=GetCompletion", output.ToString());
        Assert.Contains("RestorationSessionFailure=Timeout", output.ToString());
        Assert.Contains("RestorationWin32Error=995", output.ToString());
        Assert.Contains("RestorationTransferredBytes=0", output.ToString());
        Assert.Contains("RestorationProtocolValidationFailure=PayloadValidator", output.ToString());
        Assert.DoesNotContain("Payload=", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_CancellationTakesPrecedenceOverReturnedFailure()
    {
        Action? cancel = null;
        var output = new StringWriter();
        var command = new BatteryValidationCommand(
            (_, _) =>
            {
                cancel!();
                return Task.FromResult(new BatteryValidationResult(
                    ApplyAttempted: false,
                    Applied: false,
                    ReadBack: false,
                    Restored: false,
                    PrimaryFailure: BatteryValidationPrimaryFailure.OpenFailed,
                    RestorationFailure: BatteryValidationRestorationFailure.None));
            },
            output,
            callback =>
            {
                cancel = callback;
                return NoopDisposable.Instance;
            });

        int exitCode = await command.RunAsync(["75"], CancellationToken.None);

        Assert.Equal(BatteryValidationCommand.CanceledExitCode, exitCode);
        Assert.Equal(Lines("Canceled=True"), output.ToString());
    }

    private static BatteryValidationCommand CreateCommand(
        Func<CancellationToken, Task<BatteryValidationResult>> run,
        TextWriter output) => new(
            (_, cancellationToken) => run(cancellationToken),
            output,
            _ => NoopDisposable.Instance);

    private static BatteryValidationResult Success() => new(
        ApplyAttempted: true,
        Applied: true,
        ReadBack: true,
        Restored: true,
        PrimaryFailure: BatteryValidationPrimaryFailure.None,
        RestorationFailure: BatteryValidationRestorationFailure.None);

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
