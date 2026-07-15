using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryQueryCommandTests
{
    [Fact]
    public async Task RunAsync_RendersSafeSuccessfulQuery()
    {
        int invocations = 0;
        var output = new StringWriter();
        var command = new BatteryQueryCommand(
            _ =>
            {
                invocations++;
                return Task.FromResult(new BatteryQueryResult(
                    BatteryValidationSetting.Limit80,
                    BatteryQueryFailure.None));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, invocations);
        Assert.Equal(
            Lines("Setting=Limit80", "Failure=None", "Success=True"),
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_RendersOnlySafeFailureEvidence()
    {
        var output = new StringWriter();
        var result = new BatteryQueryResult(null, BatteryQueryFailure.QueryFailed)
        {
            Diagnostic = new HidSessionFailureDiagnostic(
                HidSessionFailurePhase.GetCompletion,
                HidSessionFailureKind.NativeIoFailure,
                NativeWin32Error: 31,
                TransferredByteCount: 12),
        };
        var command = new BatteryQueryCommand(
            _ => Task.FromResult(result),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(BatteryQueryCommand.QueryFailureExitCode, exitCode);
        Assert.Contains("Phase=GetCompletion", output.ToString());
        Assert.Contains("SessionFailure=NativeIoFailure", output.ToString());
        Assert.Contains("Win32Error=31", output.ToString());
        Assert.Contains("TransferredBytes=12", output.ToString());
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Path", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RejectsArgumentsWithoutInvokingQuery()
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new BatteryQueryCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult(new BatteryQueryResult(null, BatteryQueryFailure.QueryFailed));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(["extra"], default);

        Assert.Equal(BatteryQueryCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(Lines("Usage: OpenBlade.Capture query-battery"), output.ToString());
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
