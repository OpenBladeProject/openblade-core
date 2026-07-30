using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class DeviceModeValidationCommandTests
{
    [Fact]
    public async Task RunAsync_RequiresExactTargetConfirmation()
    {
        bool called = false;
        using var writer = new StringWriter();
        var command = new DeviceModeValidationCommand(
            (_, _) =>
            {
                called = true;
                throw new InvalidOperationException();
            },
            new StringReader(string.Empty),
            writer,
            _ => new NoopDisposable());

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(DeviceModeValidationCommand.UsageExitCode, exitCode);
        Assert.False(called);
        Assert.Contains(
            DeviceModeValidationCommand.ExactTargetConfirmation,
            writer.ToString());
    }

    [Fact]
    public async Task RunAsync_RequiresExplicitVisualConfirmation()
    {
        bool? visualConfirmation = null;
        using var writer = new StringWriter();
        var command = new DeviceModeValidationCommand(
            async (confirm, token) =>
            {
                visualConfirmation = await confirm(token);
                return Result(visualConfirmation.Value);
            },
            new StringReader("YES"),
            writer,
            _ => new NoopDisposable());

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                DeviceModeValidationCommand.ExactTargetConfirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        Assert.True(visualConfirmation);
        Assert.Contains("normal keyboard effect may be temporarily blank", writer.ToString());
        Assert.Contains("\"restored\": true", writer.ToString());
        Assert.Contains("\"productionAdmitted\": false", writer.ToString());
    }

    [Fact]
    public async Task RunAsync_UsesExactRz090528Identity()
    {
        using var writer = new StringWriter();
        var command = new DeviceModeValidationCommand(
            async (confirm, token) =>
            {
                bool visualConfirmed = await confirm(token);
                return Result(visualConfirmed);
            },
            new StringReader("YES"),
            writer,
            _ => new NoopDisposable(),
            DeviceModeValidationTarget.Rz090528);

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                DeviceModeValidationCommand.Rz090528ExactTargetConfirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        string output = writer.ToString();
        Assert.Contains("\"model\": \"RZ09-0528\"", output);
        Assert.Contains("\"productId\": \"02C6\"", output);
        Assert.Contains("\"biosVersion\": \"2.02\"", output);
        Assert.DoesNotContain("02E0", output);
    }

    private static DeviceModeValidationResult Result(bool visualConfirmed) => new(
        "Normal",
        "Driver",
        "Normal",
        true,
        true,
        true,
        visualConfirmed,
        true,
        true,
        true,
        false,
        visualConfirmed
            ? DeviceModeValidationFailure.None
            : DeviceModeValidationFailure.VisualCheckRejected);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
