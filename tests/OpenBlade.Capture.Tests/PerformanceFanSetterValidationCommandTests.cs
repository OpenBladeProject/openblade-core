using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PerformanceFanSetterValidationCommandTests
{
    [Theory]
    [InlineData("balanced", PerformanceFanValidationTarget.Balanced)]
    [InlineData("silent", PerformanceFanValidationTarget.Silent)]
    [InlineData("performance", PerformanceFanValidationTarget.Performance)]
    [InlineData("fixed5400", PerformanceFanValidationTarget.Fixed5400)]
    [InlineData("battery-saver", PerformanceFanValidationTarget.BatterySaver)]
    [InlineData("battery-balanced", PerformanceFanValidationTarget.BatteryBalanced)]
    public async Task RunAsync_RequiresExactNonInteractiveTargetConfirmation(
        string argument,
        PerformanceFanValidationTarget expected)
    {
        PerformanceFanValidationTarget? invokedTarget = null;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invokedTarget = target;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            [argument, "--confirm-target",
             PerformanceFanSetterValidationCommand.ExactTargetConfirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(expected, invokedTarget);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("readOnly").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.True(root.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(
            0,
            root.GetProperty("result")
                .GetProperty("candidateFanRpmSamples")
                .GetArrayLength());
        Assert.DoesNotContain("transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string[]> RejectedArguments => new()
    {
        Array.Empty<string>(),
        new[] { "balanced" },
        new[] { "balanced", "--confirm-target", "yes" },
        new[] { "balanced", "--confirm-target", "rz09-0581:1532:02e0:3.01" },
        new[] { "0D/02", "--confirm-target",
            PerformanceFanSetterValidationCommand.ExactTargetConfirmation },
        new[] { "fixed5400", "--confirm-target",
            PerformanceFanSetterValidationCommand.ExactTargetConfirmation, "010136" },
    };

    [Theory]
    [MemberData(nameof(RejectedArguments))]
    public async Task RunAsync_RejectsMissingInexactOrRawArguments(string[] arguments)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(arguments, default);

        Assert.Equal(PerformanceFanSetterValidationCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Contains(
            "--confirm-target RZ09-0581:1532:02E0:3.01",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UsesSuppliedBiosVersionForConfirmationAndOutput()
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new PerformanceFanSetterValidationCommand(
            (target, _) =>
            {
                invoked = true;
                return Task.FromResult(Success(target));
            },
            output,
            _ => NoopDisposable.Instance,
            biosVersion: "4.01");

        int oldBiosExitCode = await command.RunAsync(
            ["fixed5400", "--confirm-target", "RZ09-0581:1532:02E0:3.01"],
            default);

        Assert.Equal(PerformanceFanSetterValidationCommand.UsageExitCode, oldBiosExitCode);
        Assert.False(invoked);
        Assert.Contains(
            "--confirm-target RZ09-0581:1532:02E0:4.01",
            output.ToString(),
            StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        int exitCode = await command.RunAsync(
            ["fixed5400", "--confirm-target", "RZ09-0581:1532:02E0:4.01"],
            default);

        Assert.Equal(0, exitCode);
        Assert.True(invoked);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal(
            "4.01",
            document.RootElement
                .GetProperty("device")
                .GetProperty("biosVersion")
                .GetString());
    }

    private static PerformanceFanSetterValidationResult Success(
        PerformanceFanValidationTarget target) => new(
        target,
        new("01010500", "01020500"),
        new("01010000", "01020000"),
        null,
        [],
        new("01010500", "01020500"),
        ApplyAttempted: true,
        ApplyAcknowledged: true,
        CandidateConfirmed: true,
        RestoreAttempted: true,
        RestoreAcknowledged: true,
        Restored: true,
        IndeterminatePartialWrite: false,
        ManualRecoveryRequired: false,
        PerformanceFanValidationFailure.None);

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
