using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanPhysicalButtonOwnershipValidationTests
{
    [Fact]
    public async Task Command_IgnoredButtonReleasesOnceAndValidatesPolicy()
    {
        var order = new List<string>();
        var output = new StringWriter();
        var command = Command(
            output,
            order,
            SuccessfulArm(),
            SuccessfulHandback(),
            [
                CoolingPadFanPhysicalButtonOwnershipValidationCommand.Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .Fixed2200Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .PressButtonConfirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .ButtonIgnoredConfirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .ReleaseConfirmation,
            ]);

        int exitCode = await command.RunAsync(Arguments(), default);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["arm", "confirm-fixed", "handback", "confirm-retained",
                "confirm-release"],
            order);
        using JsonDocument result = LastJson(output);
        Assert.True(result.RootElement
            .GetProperty("physicalButtonOwnershipPolicyValidated")
            .GetBoolean());
        Assert.True(result.RootElement
            .GetProperty("buttonIgnoredWhileHostOwned")
            .GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
    }

    [Fact]
    public async Task Command_ButtonOverrideStopsWithoutReleaseOrRetry()
    {
        var order = new List<string>();
        var output = new StringWriter();
        var command = Command(
            output,
            order,
            SuccessfulArm(),
            SuccessfulHandback(),
            [
                CoolingPadFanPhysicalButtonOwnershipValidationCommand.Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .Fixed2200Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .PressButtonConfirmation,
                "FAN-BUTTON-OVERRIDDEN-HOST-CONTROL",
            ]);

        int exitCode = await command.RunAsync(Arguments(), default);

        Assert.Equal(
            CoolingPadFanPhysicalButtonOwnershipValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        Assert.DoesNotContain("handback", order);
        using JsonDocument result = LastJson(output);
        Assert.False(result.RootElement
            .GetProperty("physicalButtonOwnershipPolicyValidated")
            .GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
        Assert.Contains("Restore the Auto UI state", output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_AcknowledgedArmFailureRequiresRetainedConfirmationForCleanup()
    {
        var order = new List<string>();
        var output = new StringWriter();
        CoolingPadFanCrashArmResult failed = SuccessfulArm() with
        {
            ApplyPhysicalConfirmed = false,
            ManualSynapseRecoveryRequired = true,
            Failure = CoolingPadFanCrashFailure.ApplyPhysicalNotConfirmed,
        };
        var command = Command(
            output,
            order,
            failed,
            SuccessfulHandback(),
            [
                CoolingPadFanPhysicalButtonOwnershipValidationCommand.Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .Fixed2200Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .Retained2200Confirmation,
                CoolingPadFanPhysicalButtonOwnershipValidationCommand
                    .ReleaseConfirmation,
            ]);

        int exitCode = await command.RunAsync(Arguments(), default);

        Assert.Equal(
            CoolingPadFanPhysicalButtonOwnershipValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        Assert.Equal(1, order.Count(item => item == "handback"));
        Assert.Contains("confirm-retained", order);
    }

    private static CoolingPadFanPhysicalButtonOwnershipValidationCommand Command(
        StringWriter output,
        List<string> order,
        CoolingPadFanCrashArmResult arm,
        CoolingPadFanCrashRecoveryResult handback,
        IReadOnlyList<string> responses) => new(
        async (confirm, cancellationToken) =>
        {
            order.Add("arm");
            await confirm(cancellationToken);
            order.Add("confirm-fixed");
            return arm;
        },
        async (confirmRetained, confirmRelease, cancellationToken) =>
        {
            order.Add("handback");
            Assert.True(await confirmRetained(cancellationToken));
            order.Add("confirm-retained");
            Assert.True(await confirmRelease(cancellationToken));
            order.Add("confirm-release");
            return handback;
        },
        new QueueReader(new Queue<string>(responses)),
        output);

    private static IReadOnlyList<string> Arguments() =>
    [
        "--confirm-target",
        CoolingPadFanPhysicalButtonOwnershipValidationCommand.Confirmation,
    ];

    private static CoolingPadFanCrashArmResult SuccessfulArm() => new(
        ApplyAttempted: true,
        ApplyAcknowledged: true,
        ApplyPhysicalConfirmed: true,
        HandleClosedBeforeExit: true,
        ManualSynapseRecoveryRequired: false,
        ManualPowerCycleMayBeRequired: false,
        CoolingPadFanCrashFailure.None);

    private static CoolingPadFanCrashRecoveryResult SuccessfulHandback() => new(
        Retained2200PhysicallyConfirmed: true,
        ReleaseAttempted: true,
        ReleaseAcknowledged: true,
        ReleasePhysicalConfirmed: true,
        HandleClosed: true,
        RetryAttempted: false,
        ManualSynapseRecoveryRequired: false,
        ManualPowerCycleMayBeRequired: false,
        CoolingPadFanCrashFailure.None);

    private static JsonDocument LastJson(StringWriter output)
    {
        string text = output.ToString();
        int start = text.IndexOf('{', StringComparison.Ordinal);
        int trailingMessage = text.IndexOf(
            Environment.NewLine + "Do not retry",
            start,
            StringComparison.Ordinal);
        return JsonDocument.Parse(trailingMessage < 0
            ? text[start..]
            : text[start..trailingMessage]);
    }

    private sealed class QueueReader(Queue<string> responses) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                responses.TryDequeue(out string? response) ? response : null);
    }
}
