using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PadAbsentHyperBoostSustainedValidationCommandTests
{
    [Fact]
    public async Task RunAsync_RequiresPinnedPlanApprovalAndWritesSanitizedResult()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        int runCalls = 0;
        var output = new StringWriter();
        var command = Create(
            plan,
            (_, _) =>
            {
                runCalls++;
                return Task.FromResult(Success(plan));
            },
            PadAbsentHyperBoostSustainedValidationCommand
                .BuildInteractiveApproval(plan),
            output);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, runCalls);
        string serialized = ExtractJson(output.ToString());
        using JsonDocument document = JsonDocument.Parse(serialized);
        JsonElement root = document.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("padlessProductionAdmitted").GetBoolean());
        Assert.Equal(plan.PlanId, root.GetProperty("planId").GetString());
        Assert.Equal(plan.SourceSha256, root.GetProperty("planSha256").GetString());
        JsonElement result = root.GetProperty("result");
        Assert.Equal(
            "fullPower330W",
            result.GetProperty("initialAdapterState").GetString());
        Assert.True(result.GetProperty("continuousPadAbsenceConfirmed").GetBoolean());
        Assert.True(result.GetProperty("postRestorationPadAbsenceConfirmed").GetBoolean());
        Assert.True(result.GetProperty("balancedRestorationConfirmed").GetBoolean());
        JsonElement sustained = result.GetProperty("sustained");
        Assert.Equal(30, sustained.GetProperty("plannedSampleCount").GetInt32());
        Assert.Equal(30, sustained.GetProperty("completedSampleCount").GetInt32());
        Assert.Equal(30, sustained.GetProperty("samples").GetArrayLength());
        Assert.DoesNotContain("thermal1", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_MissingOrFailedSustainedResultCannotSucceed()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        PerformanceFanSetterValidationResult valid = Success(plan);
        PerformanceFanSetterValidationResult[] invalid =
        [
            valid with { SustainedValidation = null },
            valid with
            {
                Failure = PerformanceFanValidationFailure.SustainedValidationFailed,
                SustainedValidation = valid.SustainedValidation! with
                {
                    CompletedFullDuration = false,
                    Failure = HyperBoostSustainedValidationFailure.CpuTemperatureExceeded,
                },
            },
            valid with { Restored = false },
            valid with { ManualRecoveryRequired = true },
            valid with { PostRestorationCoolingPadAbsentConfirmed = false },
        ];

        foreach (PerformanceFanSetterValidationResult result in invalid)
        {
            var output = new StringWriter();
            var command = Create(
                plan,
                (_, _) => Task.FromResult(result),
                PadAbsentHyperBoostSustainedValidationCommand
                    .BuildInteractiveApproval(plan),
                output);

            int exitCode = await command.RunAsync(ExactArguments(), default);

            Assert.Equal(
                PadAbsentHyperBoostSustainedValidationCommand
                    .ValidationFailureExitCode,
                exitCode);
            using JsonDocument document = JsonDocument.Parse(
                ExtractJson(output.ToString()));
            Assert.False(document.RootElement
                .GetProperty("result")
                .GetProperty("success")
                .GetBoolean());
        }
    }

    [Fact]
    public async Task RunAsync_RuntimeBoundExternalLoadUsesExactIdentityApproval()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentExternalLoad()
                .BindRuntimeExactExecutable(new(
                    "game-benchmark.exe",
                    new string('A', 64)));
        var output = new StringWriter();
        var command = Create(
            plan,
            (_, _) => Task.FromResult(Success(plan)),
            PadAbsentHyperBoostSustainedValidationCommand
                .BuildInteractiveApproval(plan),
            output);

        int exitCode = await command.RunAsync(
            [
                "--confirm-target",
                PadAbsentHyperBoostSustainedValidationCommand
                    .ReviewedLoadTargetConfirmation,
                "--workload-pid",
                "42",
            ],
            default);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("game-benchmark.exe", text, StringComparison.Ordinal);
        Assert.Contains(new string('A', 64), text, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(ExtractJson(text));
        Assert.Equal(
            "externalReviewedLoad",
            document.RootElement.GetProperty("workloadKind").GetString());
        Assert.Equal(
            "runtimeExactExecutable",
            document.RootElement.GetProperty("workloadIdentityPolicy").GetString());
        Assert.Equal(
            "game-benchmark.exe",
            document.RootElement.GetProperty("workloadName").GetString());
        Assert.False(document.RootElement.GetProperty("productionAdmitted").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_InexactApprovalBlocksRunner()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        bool ran = false;
        var output = new StringWriter();
        var command = Create(
            plan,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success(plan));
            },
            "yes",
            output);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostSustainedValidationCommand.ApprovalRejectedExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains(plan.SourceSha256, output.ToString());
        Assert.Contains(
            "\"interactiveApprovalConfirmed\": false",
            output.ToString());
    }

    [Fact]
    public async Task RunAsync_PadPresenceBlocksBeforePromptOrRunner()
    {
        ReviewedHyperBoostThermalPlan plan =
            ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentNoLoad();
        bool ran = false;
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostSustainedValidationCommand(
            plan,
            () => CoolingPadPhysicalPresenceObservation.Present,
            (_, _) =>
            {
                ran = true;
                return Task.FromResult(Success(plan));
            },
            new StringReader(string.Empty),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(ExactArguments(), default);

        Assert.Equal(
            PadAbsentHyperBoostSustainedValidationCommand.ValidationFailureExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains("\"validationAttempted\": false", output.ToString());
    }

    private static PadAbsentHyperBoostSustainedValidationCommand Create(
        ReviewedHyperBoostThermalPlan plan,
        Func<PerformanceFanValidationTarget, CancellationToken,
            Task<PerformanceFanSetterValidationResult>> run,
        string approval,
        TextWriter output) => new(
            plan,
            () => CoolingPadPhysicalPresenceObservation.Absent,
            run,
            new StringReader(approval),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

    private static string[] ExactArguments() =>
    [
        "--confirm-target",
        PadAbsentHyperBoostSustainedValidationCommand.ExactTargetConfirmation,
    ];

    private static PerformanceFanSetterValidationResult Success(
        ReviewedHyperBoostThermalPlan plan)
    {
        HyperBoostThermalValidationSample[] samples = Enumerable.Range(1, 30)
            .Select(index => new HyperBoostThermalValidationSample(
                index,
                TimeSpan.FromSeconds(index * 2),
                CpuCelsius: 65,
                GpuCelsius: null,
                GpuInactive: true,
                Fan1Rpm: 1800,
                Fan2Rpm: 1800))
            .ToArray();
        var sustained = new HyperBoostSustainedValidationResult(
            plan.PlanId,
            plan.SourceSha256,
            PlannedSampleCount: 30,
            CompletedSampleCount: 30,
            CompletedFullDuration: true,
            ContinuousPadAbsenceConfirmed: true,
            FullPowerAdapterMaintained: true,
            HyperBoostStateMaintained: true,
            RequiredSensorsMaintained: true,
            ExternalWorkloadMaintained: true,
            samples,
            HyperBoostSustainedValidationFailure.None);
        return new(
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
            PostRestorationFullPowerAdapterConfirmed = true,
            PostRestorationCoolingPadAbsentConfirmed = true,
            PostRestorationSafetyFailure =
                PerformanceFanValidationSafetyFailure.None,
            BaselineBalancedConfirmed = true,
            SustainedValidation = sustained,
        };
    }

    private static string ExtractJson(string output)
    {
        int start = output.IndexOf('{', StringComparison.Ordinal);
        return start >= 0 ? output[start..] : output;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}
