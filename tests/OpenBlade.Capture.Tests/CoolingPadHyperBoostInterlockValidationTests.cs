using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadHyperBoostInterlockValidationTests
{
    [Fact]
    public async Task Command_HandsBackPadBeforeGuardedHyperBoostValidation()
    {
        var order = new List<string>();
        var output = new StringWriter();
        var command = Command(
            output,
            order,
            SuccessfulArm(),
            SuccessfulHandback(),
            SuccessfulHyperBoost());

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                CoolingPadHyperBoostInterlockValidationCommand.Confirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["arm", "confirm-fixed", "handback", "confirm-retained",
                "confirm-release", "hyperboost"],
            order);
        using JsonDocument result = LastJson(output);
        Assert.True(result.RootElement.GetProperty("hyperBoostInterlockValidated")
            .GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("balancedIntermediaryObserved")
            .GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("activePadModeReadbackConfirmed")
            .GetBoolean());
    }

    [Fact]
    public async Task Command_FailedHandbackNeverSubmitsHyperBoostOrRetries()
    {
        var order = new List<string>();
        var output = new StringWriter();
        CoolingPadFanCrashRecoveryResult failed = SuccessfulHandback() with
        {
            ReleaseAcknowledged = false,
            ReleasePhysicalConfirmed = false,
            ManualSynapseRecoveryRequired = true,
            Failure = CoolingPadFanCrashFailure.ReleaseIndeterminate,
        };
        var command = Command(
            output,
            order,
            SuccessfulArm(),
            failed,
            SuccessfulHyperBoost());

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                CoolingPadHyperBoostInterlockValidationCommand.Confirmation,
            ],
            default);

        Assert.Equal(
            CoolingPadHyperBoostInterlockValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.DoesNotContain("hyperboost", order);
        Assert.Equal(1, order.Count(item => item == "handback"));
        using JsonDocument result = LastJson(output);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.GetProperty("retryAttempted").GetBoolean());
    }

    [Fact]
    public async Task Command_AcknowledgedArmFailureStillAttemptsOneHandback()
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
            SuccessfulHyperBoost());

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                CoolingPadHyperBoostInterlockValidationCommand.Confirmation,
            ],
            default);

        Assert.Equal(
            CoolingPadHyperBoostInterlockValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.Equal(1, order.Count(item => item == "handback"));
        Assert.DoesNotContain("hyperboost", order);
    }

    private static CoolingPadHyperBoostInterlockValidationCommand Command(
        StringWriter output,
        List<string> order,
        CoolingPadFanCrashArmResult arm,
        CoolingPadFanCrashRecoveryResult handback,
        PerformanceFanSetterValidationResult hyperBoost)
    {
        var responses = new Queue<string>(
        [
            CoolingPadHyperBoostInterlockValidationCommand.Confirmation,
            CoolingPadHyperBoostInterlockValidationCommand.Fixed2200Confirmation,
            CoolingPadHyperBoostInterlockValidationCommand.Retained2200Confirmation,
            CoolingPadHyperBoostInterlockValidationCommand.ReleaseConfirmation,
        ]);
        return new CoolingPadHyperBoostInterlockValidationCommand(
            async (confirm, cancellationToken) =>
            {
                order.Add("arm");
                Assert.True(await confirm(cancellationToken));
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
            (target, _) =>
            {
                order.Add("hyperboost");
                Assert.Equal(PerformanceFanValidationTarget.HyperBoost, target);
                return Task.FromResult(hyperBoost);
            },
            new QueueReader(responses),
            output);
    }

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

    private static PerformanceFanSetterValidationResult SuccessfulHyperBoost() => new(
        PerformanceFanValidationTarget.HyperBoost,
        new PerformanceFanThermalPair("baseline-1", "baseline-2"),
        new PerformanceFanThermalPair("candidate-1", "candidate-2"),
        CandidateFanRpm: null,
        CandidateFanRpmSamples: Array.Empty<FanRpmReadings>(),
        new PerformanceFanThermalPair("restore-1", "restore-2"),
        ApplyAttempted: true,
        ApplyAcknowledged: true,
        CandidateConfirmed: true,
        RestoreAttempted: true,
        RestoreAcknowledged: true,
        Restored: true,
        IndeterminatePartialWrite: false,
        ManualRecoveryRequired: false,
        PerformanceFanValidationFailure.None);

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
