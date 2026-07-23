using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class DeviceModeQueryCommandTests
{
    [Theory]
    [InlineData("0000", "Normal")]
    [InlineData("0300", "Driver")]
    public async Task RunAsync_PrintsTypedMode(string payload, string expectedMode)
    {
        using var writer = new StringWriter();
        var command = new DeviceModeQueryCommand(
            _ => Task.FromResult(new HidSessionResult<GetterCandidateProbeSnapshot>(
                new(payload),
                null)),
            writer,
            _ => new NoopDisposable());

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Mode={expectedMode}", writer.ToString());
        Assert.Contains($"ResponsePayload={payload}", writer.ToString());
        Assert.Contains("Failure=None", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_RejectsArgumentsWithoutOpeningHardware()
    {
        bool called = false;
        using var writer = new StringWriter();
        var command = new DeviceModeQueryCommand(
            _ =>
            {
                called = true;
                return Task.FromResult(
                    default(HidSessionResult<GetterCandidateProbeSnapshot>));
            },
            writer,
            _ => new NoopDisposable());

        int exitCode = await command.RunAsync(["unexpected"], CancellationToken.None);

        Assert.Equal(DeviceModeQueryCommand.UsageExitCode, exitCode);
        Assert.False(called);
        Assert.Contains("Usage: OpenBlade.Capture query-device-mode", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_ReportsProtocolFailureWithoutInventingMode()
    {
        using var writer = new StringWriter();
        var result = new HidSessionResult<GetterCandidateProbeSnapshot>(
            null,
            HidSessionFailureKind.ProtocolFailure)
        {
            Diagnostic = new(
                HidSessionFailurePhase.ProtocolValidation,
                HidSessionFailureKind.ProtocolFailure,
                null,
                0)
            {
                ProtocolValidationFailure =
                    HidProtocolValidationFailure.PayloadValidator,
            },
        };
        var command = new DeviceModeQueryCommand(
            _ => Task.FromResult(result),
            writer,
            _ => new NoopDisposable());

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(DeviceModeQueryCommand.QueryFailureExitCode, exitCode);
        Assert.Contains("Mode=Unavailable", writer.ToString());
        Assert.Contains("Failure=ProtocolFailure", writer.ToString());
        Assert.Contains("ProtocolValidation=PayloadValidator", writer.ToString());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
