using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PadAbsentHyperBoostValidationCommandTests
{
    [Fact]
    public async Task RunAsync_RequiresTwoAbsenceSamplesInteractiveApprovalAndGuardedResult()
    {
        var observations = new Queue<CoolingPadPhysicalPresenceObservation>(
        [
            CoolingPadPhysicalPresenceObservation.Absent,
            CoolingPadPhysicalPresenceObservation.Absent,
        ]);
        int runCalls = 0;
        int delayCalls = 0;
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostValidationCommand(
            () => observations.Dequeue(),
            (target, _) =>
            {
                runCalls++;
                Assert.Equal(PerformanceFanValidationTarget.HyperBoost, target);
                return Task.FromResult(Success());
            },
            new StringReader(PadAbsentHyperBoostValidationCommand.InteractiveApproval),
            output,
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            },
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                PadAbsentHyperBoostValidationCommand.ExactTargetConfirmation,
            ],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, runCalls);
        Assert.Equal(1, delayCalls);
        Assert.Empty(observations);
        string serialized = ExtractLastJsonObject(output.ToString());
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("padlessProductionAdmitted").GetBoolean());
        Assert.True(root.GetProperty("firstAbsenceConfirmed").GetBoolean());
        Assert.True(root.GetProperty("secondAbsenceConfirmed").GetBoolean());
        Assert.True(root.GetProperty("interactiveApprovalConfirmed").GetBoolean());
        JsonElement result = root.GetProperty("result");
        Assert.True(result.GetProperty("fullPowerAdapterConfirmed").GetBoolean());
        Assert.Equal(
            "fullPower330W",
            result.GetProperty("initialAdapterState").GetString());
        Assert.True(result.GetProperty("finalPadAbsenceConfirmed").GetBoolean());
        Assert.True(result.GetProperty("hyperBoostReadbackConfirmed").GetBoolean());
        Assert.True(result.GetProperty("balancedRestorationConfirmed").GetBoolean());
        Assert.DoesNotContain("thermal1", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunAsync_FirstAbsenceFailureBlocksApprovalAndRunner(
        int observationValue)
    {
        CoolingPadPhysicalPresenceObservation observation =
            (CoolingPadPhysicalPresenceObservation)observationValue;
        bool ran = false;
        var output = new StringWriter();
        var command = Create(
            () => observation,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success());
            },
            PadAbsentHyperBoostValidationCommand.InteractiveApproval,
            output);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains("\"validationAttempted\": false", output.ToString());
    }

    [Fact]
    public async Task RunAsync_SecondSampleDetectingPresenceBlocksRunner()
    {
        var observations = new Queue<CoolingPadPhysicalPresenceObservation>(
        [
            CoolingPadPhysicalPresenceObservation.Absent,
            CoolingPadPhysicalPresenceObservation.Present,
        ]);
        bool ran = false;
        var command = Create(
            () => observations.Dequeue(),
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success());
            },
            PadAbsentHyperBoostValidationCommand.InteractiveApproval,
            new StringWriter());

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(ran);
    }

    [Fact]
    public async Task RunAsync_InexactInteractiveApprovalBlocksRunner()
    {
        bool ran = false;
        var output = new StringWriter();
        var command = Create(
            () => CoolingPadPhysicalPresenceObservation.Absent,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success());
            },
            "yes",
            output);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostValidationCommand.ApprovalRejectedExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains(
            "\"interactiveApprovalConfirmed\": false",
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_InexactArgumentsDoNotEnumerateOrPrompt()
    {
        int observeCalls = 0;
        bool ran = false;
        var output = new StringWriter();
        var command = Create(
            () =>
            {
                observeCalls++;
                return CoolingPadPhysicalPresenceObservation.Absent;
            },
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success());
            },
            PadAbsentHyperBoostValidationCommand.InteractiveApproval,
            output);

        int exitCode = await command.RunAsync(
            ["--confirm-target", "yes"],
            default);

        Assert.Equal(PadAbsentHyperBoostValidationCommand.UsageExitCode, exitCode);
        Assert.Equal(0, observeCalls);
        Assert.False(ran);
        Assert.Contains(
            PadAbsentHyperBoostValidationCommand.ExactTargetConfirmation,
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_RequiresEveryCommandSpecificSafetyAndRestorationFlag()
    {
        PerformanceFanSetterValidationResult valid = Success();
        PerformanceFanSetterValidationResult[] invalidResults =
        [
            valid with { Target = PerformanceFanValidationTarget.Balanced },
            valid with { BaselineBalancedConfirmed = false },
            valid with { FullPowerAdapterConfirmed = false },
            valid with { CoolingPadAbsentConfirmed = false },
            valid with
            {
                SafetyFailure =
                    PerformanceFanValidationSafetyFailure.CoolingPadPresent,
            },
            valid with { ApplyAttempted = false },
            valid with { ApplyAcknowledged = false },
            valid with { CandidateConfirmed = false },
            valid with { RestoreAttempted = false },
            valid with { RestoreAcknowledged = false },
            valid with { Restored = false },
            valid with { IndeterminatePartialWrite = true },
            valid with { ManualRecoveryRequired = true },
        ];

        foreach (PerformanceFanSetterValidationResult invalid in invalidResults)
        {
            var output = new StringWriter();
            PadAbsentHyperBoostValidationCommand command = Create(
                () => CoolingPadPhysicalPresenceObservation.Absent,
                (_, _) => Task.FromResult(invalid),
                PadAbsentHyperBoostValidationCommand.InteractiveApproval,
                output);

            int exitCode = await command.RunAsync(ExactArguments(), default);

            Assert.Equal(
                PadAbsentHyperBoostValidationCommand.ValidationFailureExitCode,
                exitCode);
            string serialized = ExtractLastJsonObject(output.ToString());
            using JsonDocument document = JsonDocument.Parse(serialized);
            Assert.False(document.RootElement
                .GetProperty("result")
                .GetProperty("success")
                .GetBoolean());
        }
    }

    private static PadAbsentHyperBoostValidationCommand Create(
        Func<CoolingPadPhysicalPresenceObservation> observe,
        Func<PerformanceFanValidationTarget, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        string approval,
        TextWriter output) => new(
        observe,
        run,
        new StringReader(approval),
        output,
        (_, _) => Task.CompletedTask,
        _ => NoopDisposable.Instance);

    private static string[] ExactArguments() =>
    [
        "--confirm-target",
        PadAbsentHyperBoostValidationCommand.ExactTargetConfirmation,
    ];

    private static PerformanceFanSetterValidationResult Success() => new(
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
        PerformanceFanValidationFailure.None)
    {
        FullPowerAdapterConfirmed = true,
        InitialAdapterPowerState = AdapterPowerState.FullPower330W,
        CoolingPadAbsentConfirmed = true,
        BaselineBalancedConfirmed = true,
    };

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
