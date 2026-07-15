using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryQuerySequenceCommandTests
{
    [Fact]
    public async Task RunAsync_ZeroArgumentsRendersFiveOrdinalsDeterministically()
    {
        var output = new StringWriter();
        var command = Command(SuccessResult(), output);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(BatteryQuerySequenceCommand.SuccessExitCode, exitCode);
        Assert.Equal(
            Lines(
                "Query1Setting=ProtectionOff", "Query1Failure=None",
                "Query2Setting=ProtectionOff", "Query2Failure=None",
                "Query3Setting=ProtectionOff", "Query3Failure=None",
                "Query4Setting=ProtectionOff", "Query4Failure=None",
                "Query5Setting=ProtectionOff", "Query5Failure=None",
                "CompletedCount=5", "Success=True"),
            output.ToString());
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("--verbose")]
    public async Task RunAsync_ExtraArgumentReturnsUsageWithoutRunning(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new BatteryQuerySequenceCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult(SuccessResult());
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(BatteryQuerySequenceCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(Lines("Usage: OpenBlade.Capture probe-battery-sequence"), output.ToString());
    }

    [Fact]
    public async Task RunAsync_FailureRendersOnlyClosedSafeDiagnosticFields()
    {
        var output = new StringWriter();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 31,
            TransferredByteCount: 12)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
        };
        BatteryQuerySequenceResult result = SuccessResult() with
        {
            Query3 = new(null, BatteryQuerySequenceFailure.QueryFailed)
            {
                Diagnostic = diagnostic,
            },
            Query4 = BatteryQuerySequenceOrdinalResult.NotAttempted,
            Query5 = BatteryQuerySequenceOrdinalResult.NotAttempted,
            CompletedCount = 3,
        };
        var command = Command(result, output);

        int exitCode = await command.RunAsync([], default);

        string rendered = output.ToString();
        Assert.Equal(BatteryQuerySequenceCommand.QueryFailureExitCode, exitCode);
        Assert.Contains("Query3Phase=GetCompletion", rendered);
        Assert.Contains("Query3SessionFailure=NativeIoFailure", rendered);
        Assert.Contains("Query3Win32Error=31", rendered);
        Assert.Contains("Query3TransferredBytes=12", rendered);
        Assert.Contains("Query3ProtocolValidationFailure=ContentValidator", rendered);
        Assert.Contains("CompletedCount=3", rendered);
        Assert.Contains("Success=False", rendered);
        foreach (string forbidden in new[]
                 {
                     "Path", "Payload", "Report", "Command", "Transaction", "Checksum",
                     "Handle", "Pointer", "IRP", "Process", "Service", "Synapse",
                 })
        {
            Assert.DoesNotContain(forbidden, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunAsync_ResultWithoutAllFiveSuccessfulReadsReturnsFailure()
    {
        var output = new StringWriter();
        BatteryQuerySequenceResult result = SuccessResult() with
        {
            Query5 = BatteryQuerySequenceOrdinalResult.NotAttempted,
            CompletedCount = 4,
        };

        int exitCode = await Command(result, output).RunAsync([], default);

        Assert.Equal(BatteryQuerySequenceCommand.QueryFailureExitCode, exitCode);
        Assert.Contains("Success=False", output.ToString());
    }

    private static BatteryQuerySequenceCommand Command(
        BatteryQuerySequenceResult result,
        TextWriter output) =>
        new(_ => Task.FromResult(result), output, _ => NoopDisposable.Instance);

    private static BatteryQuerySequenceResult SuccessResult()
    {
        var item = new BatteryQuerySequenceOrdinalResult(
            BatteryValidationSetting.ProtectionOff,
            BatteryQuerySequenceFailure.None);
        return new(item, item, item, item, item, CompletedCount: 5);
    }

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
