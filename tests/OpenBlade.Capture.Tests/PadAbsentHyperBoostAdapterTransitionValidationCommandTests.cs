using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PadAbsentHyperBoostAdapterTransitionValidationCommandTests
{
    [Fact]
    public async Task RunAsync_PromptsTransitionThroughRunnerAndEmitsSanitizedSuccess()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        var output = new StringWriter();
        int observations = 0;
        var command = new PadAbsentHyperBoostAdapterTransitionValidationCommand(
            plan,
            () =>
            {
                observations++;
                return CoolingPadPhysicalPresenceObservation.Absent;
            },
            async (confirmTransition, cancellationToken) =>
            {
                Assert.DoesNotContain(
                    PadAbsentHyperBoostAdapterTransitionValidationCommand
                        .TransitionApproval,
                    output.ToString());
                Assert.True(await confirmTransition(cancellationToken));
                return Result(plan, AdapterPowerState.UsbCPower100W);
            },
            new StringReader(
                PadAbsentHyperBoostAdapterTransitionValidationCommand.InitialApproval +
                Environment.NewLine +
                PadAbsentHyperBoostAdapterTransitionValidationCommand.TransitionApproval),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(0, exitCode);
        Assert.Equal(3, observations);
        string serialized = ExtractLastJsonObject(output.ToString());
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("padlessProductionAdmitted").GetBoolean());
        Assert.True(root.GetProperty("postRestorationPadAbsentConfirmed").GetBoolean());
        Assert.Equal(
            "usbCPower100W",
            root.GetProperty("result").GetProperty("observedAdapterState").GetString());
        Assert.True(root.GetProperty("result")
            .GetProperty("reviewedAdapterTransitionConfirmed").GetBoolean());
        Assert.True(root.GetProperty("result")
            .GetProperty("balancedRestorationConfirmed").GetBoolean());
        Assert.DoesNotContain("payload", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_UnknownAdapterObservationIsNotPositiveEvidence()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        var output = new StringWriter();
        PadAbsentHyperBoostAdapterTransitionValidationCommand command = Create(
            plan,
            async (confirmTransition, cancellationToken) =>
            {
                Assert.True(await confirmTransition(cancellationToken));
                return Result(plan, AdapterPowerState.Unknown);
            },
            output);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostAdapterTransitionValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        using JsonDocument document = JsonDocument.Parse(
            ExtractLastJsonObject(output.ToString()));
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("reviewedAdapterTransitionConfirmed").GetBoolean());
        Assert.Equal("adapterPowerUnknown",
            result.GetProperty("transitionFailure").GetString());
        Assert.True(result.GetProperty("balancedRestorationConfirmed").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_InexactTransitionApprovalStillReturnsRestoredFailure()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostAdapterTransitionValidationCommand(
            plan,
            () => CoolingPadPhysicalPresenceObservation.Absent,
            async (confirmTransition, cancellationToken) =>
            {
                bool confirmed = await confirmTransition(cancellationToken);
                Assert.False(confirmed);
                return Result(
                    plan,
                    AdapterPowerState.Unknown,
                    promptConfirmed: false);
            },
            new StringReader(
                PadAbsentHyperBoostAdapterTransitionValidationCommand.InitialApproval +
                Environment.NewLine + "no"),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostAdapterTransitionValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        using JsonDocument document = JsonDocument.Parse(
            ExtractLastJsonObject(output.ToString()));
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("transitionPromptConfirmed").GetBoolean());
        Assert.True(result.GetProperty("balancedRestorationConfirmed").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_PadPresenceBlocksBeforePromptOrRunner()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        bool ran = false;
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostAdapterTransitionValidationCommand(
            plan,
            () => CoolingPadPhysicalPresenceObservation.Present,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Result(plan, AdapterPowerState.UsbCPower100W));
            },
            TextReader.Null,
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostAdapterTransitionValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains("\"validationAttempted\": false", output.ToString());
    }

    private static PadAbsentHyperBoostAdapterTransitionValidationCommand Create(
        ReviewedHyperBoostThermalPlan plan,
        Func<Func<CancellationToken, Task<bool>>, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        TextWriter output) => new(
            plan,
            () => CoolingPadPhysicalPresenceObservation.Absent,
            run,
            new StringReader(
                PadAbsentHyperBoostAdapterTransitionValidationCommand.InitialApproval +
                Environment.NewLine +
                PadAbsentHyperBoostAdapterTransitionValidationCommand.TransitionApproval),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

    private static string[] ExactArguments() =>
    [
        "--confirm-target",
        PadAbsentHyperBoostAdapterTransitionValidationCommand.ExactTargetConfirmation,
    ];

    private static PerformanceFanSetterValidationResult Result(
        ReviewedHyperBoostThermalPlan plan,
        AdapterPowerState observed,
        bool promptConfirmed = true)
    {
        bool transitionConfirmed =
            promptConfirmed && observed == AdapterPowerState.UsbCPower100W;
        HyperBoostAdapterTransitionValidationFailure transitionFailure =
            transitionConfirmed
                ? HyperBoostAdapterTransitionValidationFailure.None
                : promptConfirmed && observed == AdapterPowerState.Unknown
                    ? HyperBoostAdapterTransitionValidationFailure.AdapterPowerUnknown
                    : HyperBoostAdapterTransitionValidationFailure.TransitionNotConfirmed;
        return new PerformanceFanSetterValidationResult(
            PerformanceFanValidationTarget.HyperBoost,
            new("01010000", "01020000"),
            new("01010700", "01020700"),
            null,
            [],
            new("01010000", "01020000"),
            ApplyAttempted: true,
            ApplyAcknowledged: true,
            CandidateConfirmed: true,
            RestoreAttempted: true,
            RestoreAcknowledged: true,
            Restored: true,
            IndeterminatePartialWrite: false,
            ManualRecoveryRequired: false,
            transitionConfirmed
                ? PerformanceFanValidationFailure.None
                : PerformanceFanValidationFailure.SustainedValidationFailed)
        {
            FullPowerAdapterConfirmed = true,
            CoolingPadAbsentConfirmed = true,
            BaselineBalancedConfirmed = true,
            AdapterTransitionValidation = new(
                plan.PlanId,
                plan.SourceSha256,
                promptConfirmed,
                FreshAdapterReadCompleted: promptConfirmed,
                AdapterPowerState.FullPower330W,
                observed,
                transitionConfirmed,
                transitionFailure),
        };
    }

    private static string ExtractLastJsonObject(string output)
    {
        int start = output.IndexOf("{\r\n", StringComparison.Ordinal);
        return start >= 0 ? output[start..] : output;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}
