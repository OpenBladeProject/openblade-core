using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class FanRpmQueryCommandTests
{
    [Fact]
    public async Task RunAsync_RendersSelectorsAndDocumentedMaximumWithoutRawProtocol()
    {
        var output = new StringWriter();
        var command = new FanRpmQueryCommand(
            _ => Task.FromResult(new FanRpmQueryResult(
                new FanRpmReadings(3000, 3100),
                FanRpmQueryFailure.None)),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            Lines(
                "Selector1Rpm=3000",
                "Selector2Rpm=3100",
                "MaxRpm=3100",
                "Failure=None",
                "Success=True"),
            output.ToString());
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Command", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0D/88")]
    [InlineData("010100")]
    public async Task RunAsync_RejectsEverySelectorOrRawArgument(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new FanRpmQueryCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult<FanRpmQueryResult>(null!);
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(FanRpmQueryCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(Lines("Usage: OpenBlade.Capture query-fan-rpm"), output.ToString());
    }

    private static string Lines(params string[] lines) =>
        string.Join(Environment.NewLine, lines) + Environment.NewLine;

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
