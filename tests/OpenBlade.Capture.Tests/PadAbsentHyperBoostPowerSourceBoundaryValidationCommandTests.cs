using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PadAbsentHyperBoostPowerSourceBoundaryValidationCommandTests
{
    public static TheoryData<AdapterPowerState, string> Destinations => new()
    {
        {
            AdapterPowerState.UsbCPower100W,
            PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                .UsbCTargetConfirmation
        },
        {
            AdapterPowerState.Battery,
            PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                .BatteryTargetConfirmation
        },
    };

    [Theory]
    [MemberData(nameof(Destinations))]
    public async Task RunAsync_EmitsFirmwareExitEvidenceWithoutPolicyChange(
        AdapterPowerState destination,
        string targetConfirmation)
    {
        ReviewedHyperBoostPowerSourceBoundaryPlan plan =
            ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();
        var output = new StringWriter();
        int observations = 0;
        var command = new PadAbsentHyperBoostPowerSourceBoundaryValidationCommand(
            plan,
            destination,
            () =>
            {
                observations++;
                return CoolingPadPhysicalPresenceObservation.Absent;
            },
            async (actualDestination, transition, reconnect, cancellationToken) =>
            {
                Assert.Equal(destination, actualDestination);
                HyperBoostPowerSourceBoundaryAuthorization transitionResult =
                    await transition(cancellationToken);
                Assert.True(transitionResult.Confirmed);
                Assert.Equal(
                    CoolingPadPhysicalPresenceObservation.Absent,
                    transitionResult.CoolingPadObservation);
                HyperBoostPowerSourceBoundaryAuthorization reconnectResult =
                    await reconnect(cancellationToken);
                Assert.True(reconnectResult.Confirmed);
                Assert.Equal(
                    CoolingPadPhysicalPresenceObservation.Absent,
                    reconnectResult.CoolingPadObservation);
                return Result(plan, destination);
            },
            Input(destination),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            ["--confirm-target", targetConfirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(5, observations);
        using JsonDocument document = JsonDocument.Parse(
            ExtractLastJsonObject(output.ToString()));
        JsonElement root = document.RootElement;
        Assert.False(root.GetProperty("productionBehaviorChanged").GetBoolean());
        Assert.False(root.GetProperty("hyperBoostPowerTransitionPolicyChanged")
            .GetBoolean());
        JsonElement result = root.GetProperty("result");
        Assert.True(result.GetProperty("firmwareAutoExitConfirmed").GetBoolean());
        Assert.False(result.GetProperty("openBladeDisableIssuedDuringObservation")
            .GetBoolean());
        Assert.Equal(0, result.GetProperty("openBladeCleanupWriteCount").GetInt32());
        Assert.True(result.GetProperty("fullPowerReconnectConfirmed").GetBoolean());
        Assert.True(result.GetProperty("finalBalancedRestorationConfirmed")
            .GetBoolean());
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("payload", root.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", root.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", root.GetRawText(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_FirmwareExitFailureRecordsBoundedCleanup()
    {
        ReviewedHyperBoostPowerSourceBoundaryPlan plan =
            ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostPowerSourceBoundaryValidationCommand(
            plan,
            AdapterPowerState.Battery,
            () => CoolingPadPhysicalPresenceObservation.Absent,
            async (_, transition, reconnect, cancellationToken) =>
            {
                Assert.True((await transition(cancellationToken)).Confirmed);
                Assert.True((await reconnect(cancellationToken)).Confirmed);
                return Result(
                    plan,
                    AdapterPowerState.Battery,
                    firmwareExitConfirmed: false,
                    cleanupWriteCount: 2);
            },
            Input(AdapterPowerState.Battery),
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            ["--confirm-target",
             PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                 .BatteryTargetConfirmation],
            default);

        Assert.Equal(
            PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        using JsonDocument document = JsonDocument.Parse(
            ExtractLastJsonObject(output.ToString()));
        JsonElement result = document.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("firmwareAutoExitConfirmed").GetBoolean());
        Assert.Equal(2, result.GetProperty("openBladeCleanupWriteCount").GetInt32());
        Assert.True(result.GetProperty("finalBalancedRestorationConfirmed")
            .GetBoolean());
        Assert.False(result.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task RunAsync_PadPresenceBlocksBeforeApprovalOrRunner()
    {
        ReviewedHyperBoostPowerSourceBoundaryPlan plan =
            ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();
        bool ran = false;
        var output = new StringWriter();
        var command = new PadAbsentHyperBoostPowerSourceBoundaryValidationCommand(
            plan,
            AdapterPowerState.UsbCPower100W,
            () => CoolingPadPhysicalPresenceObservation.Present,
            (_, _, _, _) =>
            {
                ran = true;
                return Task.FromResult(Result(
                    plan,
                    AdapterPowerState.UsbCPower100W));
            },
            TextReader.Null,
            output,
            (_, _) => Task.CompletedTask,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync(
            ["--confirm-target",
             PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                 .UsbCTargetConfirmation],
            default);

        Assert.Equal(
            PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
                .ValidationFailureExitCode,
            exitCode);
        Assert.False(ran);
        Assert.Contains("\"validationAttempted\": false", output.ToString());
    }

    private static StringReader Input(AdapterPowerState destination) => new(
        PadAbsentHyperBoostPowerSourceBoundaryValidationCommand.InitialApproval +
        Environment.NewLine +
        PadAbsentHyperBoostPowerSourceBoundaryValidationCommand
            .TransitionApprovalFor(destination) +
        Environment.NewLine +
        PadAbsentHyperBoostPowerSourceBoundaryValidationCommand.ReconnectApproval);

    private static PerformanceFanSetterValidationResult Result(
        ReviewedHyperBoostPowerSourceBoundaryPlan plan,
        AdapterPowerState destination,
        bool firmwareExitConfirmed = true,
        int cleanupWriteCount = 0)
    {
        HyperBoostPowerSourceBoundaryValidationFailure boundaryFailure =
            firmwareExitConfirmed
                ? HyperBoostPowerSourceBoundaryValidationFailure.None
                : HyperBoostPowerSourceBoundaryValidationFailure.HyperBoostDidNotExit;
        ThermalModeState exitState = destination == AdapterPowerState.Battery
            ? ThermalModeState.BatteryBalancedAutomatic
            : ThermalModeState.BalancedAutomatic;
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
            RestoreAttempted: cleanupWriteCount > 0,
            RestoreAcknowledged: cleanupWriteCount > 0,
            Restored: true,
            IndeterminatePartialWrite: false,
            ManualRecoveryRequired: false,
            firmwareExitConfirmed
                ? PerformanceFanValidationFailure.None
                : PerformanceFanValidationFailure.SustainedValidationFailed)
        {
            FullPowerAdapterConfirmed = true,
            CoolingPadAbsentConfirmed = true,
            BaselineBalancedConfirmed = true,
            PowerSourceBoundaryValidation = new(
                plan.PlanId,
                plan.SourceSha256,
                destination,
                TransitionPromptConfirmed: true,
                TransitionTimePadAbsentConfirmed: true,
                DestinationAdapterObserved: true,
                DestinationAdapterMaintained: true,
                CompletedFullObservation: true,
                FirmwareAutoExitConfirmed: firmwareExitConfirmed,
                HyperBoostReappearedAfterExit: false,
                FirmwareExitState: firmwareExitConfirmed ? exitState : null,
                RequiredConsecutiveAutomaticSamples: 3,
                MaximumSampleCount: 20,
                Samples:
                [
                    new(1, TimeSpan.Zero, destination,
                        firmwareExitConfirmed
                            ? exitState
                            : ThermalModeState.HyperBoostObserved),
                ],
                OpenBladeDisableIssuedDuringObservation: false,
                ReconnectPromptConfirmed: true,
                ReconnectTimePadAbsentConfirmed: true,
                FullPowerReconnectConfirmed: true,
                ReconnectedAdapterState: AdapterPowerState.FullPower280W,
                BaselineReturnedWithoutCleanupWrite: cleanupWriteCount == 0,
                OpenBladeCleanupWriteCount: cleanupWriteCount,
                Failure: boundaryFailure),
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
