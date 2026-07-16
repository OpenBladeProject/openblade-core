using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class TemporaryFullChargeQueryCommandTests
{
    [Fact]
    public async Task RunAsync_RendersTypedStatusWithoutRawEvidence()
    {
        var output = new StringWriter();
        var command = new TemporaryFullChargeQueryCommand(
            _ => Task.FromResult(new TemporaryFullChargeQueryResult(
                Active: false,
                TemporaryFullChargeQueryFailure.None)),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Equal(Lines("Active=False", "Failure=None", "Success=True"), output.ToString());
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Catalog", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("04")]
    public async Task RunAsync_RejectsEverySettingOrRawArgument(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new TemporaryFullChargeQueryCommand(
            _ => { invoked = true; return Task.FromResult<TemporaryFullChargeQueryResult>(null!); },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(TemporaryFullChargeQueryCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(Lines("Usage: OpenBlade.Capture query-temporary-full-charge"), output.ToString());
    }

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
