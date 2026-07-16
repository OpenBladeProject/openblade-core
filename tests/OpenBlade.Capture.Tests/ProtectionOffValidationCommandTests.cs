using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class ProtectionOffValidationCommandTests
{
    [Fact]
    public async Task RunAsync_AcceptsNoSettingAndRendersOnlyTypedClassifications()
    {
        var output = new StringWriter();
        var command = new ProtectionOffValidationCommand(
            _ => Task.FromResult(Success()), output, _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Contains("BaselineProtection=Limit80", output.ToString());
        Assert.Contains("BaselineTemporaryFullCharge=False", output.ToString());
        Assert.Contains("CandidateProtection=ProtectionOff", output.ToString());
        Assert.Contains("CandidateTemporaryFullCharge=False", output.ToString());
        Assert.Contains("RestorationProtection=Limit80", output.ToString());
        Assert.Contains("RestorationTemporaryFullCharge=False", output.ToString());
        Assert.Contains("Success=True", output.ToString());
        Assert.DoesNotContain("Payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Catalog", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("80")]
    [InlineData("50")]
    public async Task RunAsync_RejectsAllSettingArgumentsWithoutInvokingValidation(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new ProtectionOffValidationCommand(
            _ => { invoked = true; return Task.FromResult(Success()); },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(ProtectionOffValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(Lines("Usage: OpenBlade.Capture validate-protection-off"), output.ToString());
    }

    private static ProtectionOffValidationResult Success() => new(
        BatteryValidationSetting.Limit80, false, true, true,
        BatteryValidationSetting.ProtectionOff, false, true,
        BatteryValidationSetting.Limit80, false,
        ProtectionOffValidationPrimaryFailure.None,
        ProtectionOffValidationRestorationFailure.None);

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
