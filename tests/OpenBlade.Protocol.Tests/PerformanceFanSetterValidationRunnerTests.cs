using System.Security.Cryptography;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class PerformanceFanSetterValidationRunnerTests
{
    public static TheoryData<
        PerformanceFanValidationTarget,
        string,
        string,
        string[]> SuccessfulTargets => new()
    {
        {
            PerformanceFanValidationTarget.Balanced,
            "01010500",
            "01020500",
            ["01010000", "01020000"]
        },
        {
            PerformanceFanValidationTarget.Silent,
            "01010000",
            "01020000",
            ["01010500", "01020500"]
        },
        {
            PerformanceFanValidationTarget.Performance,
            "01010000",
            "01020000",
            ["01010200", "01020200"]
        },
        {
            PerformanceFanValidationTarget.Fixed5400,
            "01010000",
            "01020000",
            ["01010001", "01020001", "010136", "010236"]
        },
        {
            PerformanceFanValidationTarget.BatterySaver,
            "01010600",
            "01020600",
            ["01010300", "01020300"]
        },
        {
            PerformanceFanValidationTarget.BatteryBalanced,
            "01010300",
            "01020300",
            ["01010600", "01020600"]
        },
        {
            PerformanceFanValidationTarget.HyperBoost,
            "01010000",
            "01020000",
            ["01010700", "01020700"]
        },
        {
            PerformanceFanValidationTarget.HyperBoostFromSilent,
            "01010500",
            "01020500",
            ["01010700", "01020700"]
        },
        {
            PerformanceFanValidationTarget.HyperBoostFromPerformance,
            "01010200",
            "01020200",
            ["01010700", "01020700"]
        },
    };

    [Theory]
    [MemberData(nameof(SuccessfulTargets))]
    public async Task RunAsync_AppliesReadsBackAndRestoresTypedBaseline(
        PerformanceFanValidationTarget target,
        string baseline1,
        string baseline2,
        string[] expectedCandidateWrites)
    {
        var session = new StatefulSession(baseline1, baseline2);

        PerformanceFanSetterValidationResult result =
            await Runner(session).RunAsync(target, default);

        Assert.True(result.Success);
        Assert.True(result.ApplyAcknowledged);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(baseline1, result.Baseline!.Thermal1Hex);
        Assert.Equal(baseline2, result.Restoration!.Thermal2Hex);

        string[] writes = session.Commands
            .Where(command => command.CommandId is 0x01 or 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray();
        Assert.Equal(expectedCandidateWrites, writes[..expectedCandidateWrites.Length]);
        Assert.Equal([baseline1, baseline2], writes[^2..]);

        if (target == PerformanceFanValidationTarget.Fixed5400)
        {
            Assert.Equal("01010001", result.Candidate!.Thermal1Hex);
            Assert.Equal("01020001", result.Candidate.Thermal2Hex);
            Assert.Equal(new FanRpmReadings(5300, 5300), result.CandidateFanRpm);
            Assert.Equal(3, result.CandidateFanRpmSamples.Count);
            Assert.Equal(6, session.Commands.Count(command => command.CommandId == 0x88));
            Assert.Equal([0x02, 0x02, 0x01, 0x01],
                session.Commands
                    .Where(command => command.CommandId is 0x01 or 0x02)
                    .Take(4)
                    .Select(command => command.CommandId)
                    .ToArray());
        }
        else
        {
            Assert.Null(result.CandidateFanRpm);
            Assert.Empty(result.CandidateFanRpmSamples);
            Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x88);
        }
    }

    [Fact]
    public async Task RunAsync_SafetyGateRunsAfterBalancedBaselineAndBeforeAnyWrite()
    {
        var session = new StatefulSession("01010000", "01020000");
        var gate = new ScriptedSafetyGate(new(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: false,
            PerformanceFanValidationSafetyFailure.CoolingPadPresent));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate).RunAsync(
                PerformanceFanValidationTarget.HyperBoost,
                default);

        Assert.Equal(1, gate.Calls);
        Assert.Equal(PerformanceFanValidationFailure.OpenFailed, result.Failure);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.CoolingPadPresent,
            result.SafetyFailure);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.False(result.CoolingPadAbsentConfirmed);
        Assert.False(result.ApplyAttempted);
        Assert.Equal(4, session.Commands.Count);
        Assert.All(session.Commands, command => Assert.Equal(0x82, command.CommandId));
    }

    [Fact]
    public async Task RunAsync_SafetyGateConfirmationIsCarriedIntoSuccessfulResult()
    {
        var session = new StatefulSession("01010000", "01020000");
        var gate = new ScriptedSafetyGate(new(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: true,
            PerformanceFanValidationSafetyFailure.None));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate).RunAsync(
                PerformanceFanValidationTarget.HyperBoost,
                default);

        Assert.True(result.Success);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.True(result.CoolingPadAbsentConfirmed);
        Assert.Equal(1, gate.Calls);
    }

    [Theory]
    [InlineData(
        PerformanceFanValidationTarget.HyperBoostFromSilent,
        "01010500",
        "01020500")]
    [InlineData(
        PerformanceFanValidationTarget.HyperBoostFromPerformance,
        "01010200",
        "01020200")]
    public async Task RunAsync_DirectBaseSafetyUsesTwoIdenticalRequestedBasePairs(
        PerformanceFanValidationTarget target,
        string baseline1,
        string baseline2)
    {
        var session = new StatefulSession(baseline1, baseline2);
        var gate = new ScriptedSafetyGate(new(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: false,
            CoolingPadPresentConfirmed: true,
            CoolingPadOpaqueZeroConfirmed: true,
            PerformanceFanValidationSafetyFailure.None));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate).RunAsync(target, default);

        Assert.True(result.Success);
        Assert.True(result.BaselineRequiredBaseModeConfirmed);
        Assert.False(result.BaselineBalancedConfirmed);
        Assert.True(result.FullPowerAdapterConfirmed);
        Assert.True(result.CoolingPadPresentConfirmed);
        Assert.True(result.CoolingPadOpaqueZeroConfirmed);
        Assert.False(result.BalancedIntermediaryObserved);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(4, session.Commands.Take(4).Count(command =>
            command.CommandId == 0x82));

        string[] writes = session.Commands
            .Where(command => command.CommandId is 0x01 or 0x02)
            .Select(command => Convert.ToHexString(command.Arguments.Span))
            .ToArray();
        Assert.Equal(["01010700", "01020700", baseline1, baseline2], writes);
        Assert.DoesNotContain("01010000", writes);
        Assert.DoesNotContain("01020000", writes);
    }

    [Fact]
    public async Task RunAsync_SafetyGateRequiresTwoIdenticalBalancedPairs()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            stateAfterFirstThermalPair1: "01010500",
            stateAfterFirstThermalPair2: "01020500");
        var gate = new ScriptedSafetyGate(new(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: true,
            PerformanceFanValidationSafetyFailure.None));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate).RunAsync(
                PerformanceFanValidationTarget.HyperBoost,
                default);

        Assert.Equal(
            PerformanceFanValidationFailure.BaselinePairMismatch,
            result.Failure);
        Assert.Equal(0, gate.Calls);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(
            session.Commands,
            command => command.CommandId is 0x01 or 0x02);
    }

    [Fact]
    public async Task RunAsync_SustainedHyperBoostCompletesPlanAndRestores()
    {
        var session = new StatefulSession("01010000", "01020000");
        var gate = new ScriptedSafetyGate(SafeGateResult);
        ReviewedHyperBoostThermalPlan plan = Plan();

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate,
            sustainedPlan: plan,
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(70, 60))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.True(result.Success);
        HyperBoostSustainedValidationResult sustained =
            Assert.IsType<HyperBoostSustainedValidationResult>(
                result.SustainedValidation);
        Assert.True(sustained.Success);
        Assert.Equal(plan.PlanId, sustained.PlanId);
        Assert.Equal(plan.SourceSha256, sustained.PlanSha256);
        Assert.Equal(2, sustained.PlannedSampleCount);
        Assert.Equal(2, sustained.CompletedSampleCount);
        Assert.Equal(2, sustained.Samples.Count);
        Assert.All(sustained.Samples, sample =>
        {
            Assert.Equal(70, sample.CpuCelsius);
            Assert.Equal(60, sample.GpuCelsius);
            Assert.Equal(5300, sample.Fan1Rpm);
            Assert.Equal(5300, sample.Fan2Rpm);
        });
        Assert.Equal(4, gate.Calls);
        Assert.True(result.PostRestorationFullPowerAdapterConfirmed);
        Assert.True(result.PostRestorationCoolingPadAbsentConfirmed);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.None,
            result.PostRestorationSafetyFailure);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_AdapterTransitionPromptsAfterMode7AndRestoresImmediately()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            adapterPowerState: AdapterPowerState.UsbCPower100W);
        int boundaryCalls = 0;
        var runner = Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan());

        PerformanceFanSetterValidationResult result = await runner.RunAsync(
            _ =>
            {
                boundaryCalls++;
                Assert.Equal(
                    6,
                    session.Commands.Count(command => command.CommandId == 0x82));
                Assert.Equal(
                    2,
                    session.Commands.Count(command => command.CommandId == 0x02));
                return Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.PadAbsentConfirmed);
            },
            default);

        Assert.True(result.Success);
        HyperBoostAdapterTransitionValidationResult transition =
            Assert.IsType<HyperBoostAdapterTransitionValidationResult>(
                result.AdapterTransitionValidation);
        Assert.True(transition.Success);
        Assert.Equal(AdapterPowerState.FullPower280W, transition.InitialAdapterState);
        Assert.Equal(AdapterPowerState.UsbCPower100W, transition.ObservedAdapterState);
        Assert.Equal(1, boundaryCalls);
        Assert.Equal(1, session.Commands.Count(command => command.CommandId == 0x8C));
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x02));
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_AdapterTransitionRetainsAdmittedSlimQFullPowerClass()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            adapterPowerState: AdapterPowerState.UsbCPower100W);
        var safety = new PerformanceFanValidationSafetyGateResult(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: true,
            PerformanceFanValidationSafetyFailure.None)
        {
            AdapterPowerState = AdapterPowerState.FullPower330W,
        };

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(safety),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                _ => Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.PadAbsentConfirmed),
                default);

        Assert.True(result.Success);
        Assert.Equal(
            AdapterPowerState.FullPower330W,
            result.AdapterTransitionValidation!.InitialAdapterState);
        Assert.True(result.AdapterTransitionValidation.Success);
    }

    [Theory]
    [InlineData((int)AdapterPowerState.Unknown, false)]
    [InlineData((int)AdapterPowerState.FullPower280W, false)]
    [InlineData((int)AdapterPowerState.UsbCPower100W, true)]
    public async Task RunAsync_AdapterTransitionRequiresReviewedTypedUsbCState(
        int adapterStateValue,
        bool expectedSuccess)
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            adapterPowerState: (AdapterPowerState)adapterStateValue);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                _ => Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.PadAbsentConfirmed),
                default);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(expectedSuccess, result.AdapterTransitionValidation!.Success);
        Assert.Equal((AdapterPowerState)adapterStateValue,
            result.AdapterTransitionValidation.ObservedAdapterState);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_AdapterReadFailureIsNotEvidenceAndStillRestores()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failAdapterRead: true);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                _ => Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.PadAbsentConfirmed),
                default);

        Assert.Equal(
            HyperBoostAdapterTransitionValidationFailure.AdapterPowerReadFailed,
            result.AdapterTransitionValidation!.Failure);
        Assert.False(result.AdapterTransitionValidation.FreshAdapterReadCompleted);
        Assert.False(result.Success);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_RejectedAdapterTransitionPromptStillRestoresWithoutQuery()
    {
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                _ => Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.Rejected),
                default);

        Assert.Equal(
            HyperBoostAdapterTransitionValidationFailure.TransitionNotConfirmed,
            result.AdapterTransitionValidation!.Failure);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x8C);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_FreshPadPresenceVetoRestoresWithoutAdapterQuery()
    {
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                _ => Task.FromResult(
                    HyperBoostAdapterTransitionAuthorization.CoolingPadPresent),
                default);

        Assert.Equal(
            HyperBoostAdapterTransitionValidationFailure.CoolingPadPresent,
            result.AdapterTransitionValidation!.Failure);
        Assert.False(result.AdapterTransitionValidation
            .TransitionTimePadAbsentConfirmed);
        Assert.DoesNotContain(session.Commands, command => command.CommandId == 0x8C);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_CanceledAdapterTransitionPromptStillRestores()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            adapterTransitionPlan: AdapterPlan()).RunAsync(
                token =>
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(token);
                },
                cancellation.Token);

        Assert.Equal(PerformanceFanValidationFailure.Canceled, result.Failure);
        Assert.Equal(
            HyperBoostAdapterTransitionValidationFailure.Canceled,
            result.AdapterTransitionValidation!.Failure);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    public static TheoryData<
        AdapterPowerState,
        string,
        string,
        int> FirmwareAutoExitCases => new()
    {
        {
            AdapterPowerState.UsbCPower100W,
            "01010000",
            "01020000",
            (int)ThermalModeState.BalancedAutomatic
        },
        {
            AdapterPowerState.Battery,
            "01010600",
            "01020600",
            (int)ThermalModeState.BatteryBalancedAutomatic
        },
    };

    [Theory]
    [MemberData(nameof(FirmwareAutoExitCases))]
    public async Task RunAsync_PowerSourceBoundaryConfirmsFirmwareExitWithoutDisableWrite(
        AdapterPowerState destination,
        string exitFirst,
        string exitSecond,
        int expectedExitStateValue)
    {
        PowerSourceBoundaryStep[] observations =
        [
            new(destination, "01010700", "01020700"),
            .. Enumerable.Range(0, 19).Select(_ =>
                new PowerSourceBoundaryStep(destination, exitFirst, exitSecond)),
            new(AdapterPowerState.FullPower280W, "01010000", "01020000"),
        ];
        var session = new PowerSourceBoundarySession(observations);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            powerSourceBoundaryPlan: PowerSourceBoundaryPlan()).RunAsync(
                destination,
                _ => Task.FromResult(PadAbsentAuthorization),
                _ => Task.FromResult(PadAbsentAuthorization),
                default);

        Assert.True(result.Success);
        HyperBoostPowerSourceBoundaryValidationResult boundary =
            Assert.IsType<HyperBoostPowerSourceBoundaryValidationResult>(
                result.PowerSourceBoundaryValidation);
        Assert.True(boundary.ObservationSucceeded);
        Assert.True(boundary.FirmwareAutoExitConfirmed);
        Assert.Equal((ThermalModeState)expectedExitStateValue,
            boundary.FirmwareExitState);
        Assert.True(boundary.DestinationAdapterObserved);
        Assert.True(boundary.DestinationAdapterMaintained);
        Assert.True(boundary.CompletedFullObservation);
        Assert.False(boundary.HyperBoostReappearedAfterExit);
        Assert.False(boundary.OpenBladeDisableIssuedDuringObservation);
        Assert.True(boundary.FullPowerReconnectConfirmed);
        Assert.True(boundary.BaselineReturnedWithoutCleanupWrite);
        Assert.Equal(0, boundary.OpenBladeCleanupWriteCount);
        Assert.False(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
        Assert.Equal(20, boundary.Samples.Count);
    }

    [Fact]
    public async Task RunAsync_PowerSourceBoundaryRestoresOnceOnlyAfterFullPowerReconnect()
    {
        PowerSourceBoundaryStep[] observations = Enumerable.Range(0, 20)
            .Select(_ => new PowerSourceBoundaryStep(
                AdapterPowerState.Battery,
                "01010700",
                "01020700"))
            .Append(new(
                AdapterPowerState.FullPower330W,
                "01010700",
                "01020700"))
            .ToArray();
        var session = new PowerSourceBoundarySession(observations);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            powerSourceBoundaryPlan: PowerSourceBoundaryPlan()).RunAsync(
                AdapterPowerState.Battery,
                _ => Task.FromResult(PadAbsentAuthorization),
                _ => Task.FromResult(PadAbsentAuthorization),
                default);

        Assert.False(result.Success);
        Assert.Equal(
            PerformanceFanValidationFailure.SustainedValidationFailed,
            result.Failure);
        HyperBoostPowerSourceBoundaryValidationResult boundary =
            Assert.IsType<HyperBoostPowerSourceBoundaryValidationResult>(
                result.PowerSourceBoundaryValidation);
        Assert.Equal(
            HyperBoostPowerSourceBoundaryValidationFailure.HyperBoostDidNotExit,
            boundary.Failure);
        Assert.False(boundary.FirmwareAutoExitConfirmed);
        Assert.Equal(2, boundary.OpenBladeCleanupWriteCount);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);

        int lastAdapterQuery = session.Commands.FindLastIndex(
            command => command.CommandId == 0x8C);
        int firstCleanupWrite = session.Commands.FindIndex(
            lastAdapterQuery + 1,
            command => command.CommandId == 0x02);
        Assert.True(firstCleanupWrite > lastAdapterQuery);
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x02));
    }

    [Fact]
    public async Task RunAsync_PowerSourceBoundaryNeverCleansUpBeforeFullPowerReconnect()
    {
        PowerSourceBoundaryStep[] observations = Enumerable.Range(0, 21)
            .Select(_ => new PowerSourceBoundaryStep(
                AdapterPowerState.UsbCPower100W,
                "01010000",
                "01020000"))
            .ToArray();
        var session = new PowerSourceBoundarySession(observations);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            powerSourceBoundaryPlan: PowerSourceBoundaryPlan()).RunAsync(
                AdapterPowerState.UsbCPower100W,
                _ => Task.FromResult(PadAbsentAuthorization),
                _ => Task.FromResult(PadAbsentAuthorization),
                default);

        Assert.False(result.Success);
        Assert.Equal(
            HyperBoostPowerSourceBoundaryValidationFailure.FullPowerReconnectRequired,
            result.PowerSourceBoundaryValidation!.Failure);
        Assert.False(result.PowerSourceBoundaryValidation.FullPowerReconnectConfirmed);
        Assert.Equal(0, result.PowerSourceBoundaryValidation.OpenBladeCleanupWriteCount);
        Assert.False(result.RestoreAttempted);
        Assert.False(result.Restored);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
    }

    [Fact]
    public async Task RunAsync_PowerSourceBoundaryRejectsTransientFirmwareExit()
    {
        PowerSourceBoundaryStep[] observations =
        [
            new(AdapterPowerState.Battery, "01010600", "01020600"),
            new(AdapterPowerState.Battery, "01010600", "01020600"),
            new(AdapterPowerState.Battery, "01010600", "01020600"),
            new(AdapterPowerState.Battery, "01010700", "01020700"),
            new(AdapterPowerState.FullPower280W, "01010000", "01020000"),
        ];
        var session = new PowerSourceBoundarySession(observations);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            powerSourceBoundaryPlan: PowerSourceBoundaryPlan()).RunAsync(
                AdapterPowerState.Battery,
                _ => Task.FromResult(PadAbsentAuthorization),
                _ => Task.FromResult(PadAbsentAuthorization),
                default);

        Assert.False(result.Success);
        HyperBoostPowerSourceBoundaryValidationResult boundary =
            Assert.IsType<HyperBoostPowerSourceBoundaryValidationResult>(
                result.PowerSourceBoundaryValidation);
        Assert.Equal(
            HyperBoostPowerSourceBoundaryValidationFailure
                .HyperBoostReappearedAfterExit,
            boundary.Failure);
        Assert.True(boundary.HyperBoostReappearedAfterExit);
        Assert.False(boundary.CompletedFullObservation);
        Assert.Equal(0, boundary.OpenBladeCleanupWriteCount);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_PowerSourceBoundaryRejectedReconnectPerformsNoCleanupWrite()
    {
        PowerSourceBoundaryStep[] observations = Enumerable.Range(0, 20)
            .Select(_ => new PowerSourceBoundaryStep(
                AdapterPowerState.UsbCPower100W,
                "01010000",
                "01020000"))
            .ToArray();
        var session = new PowerSourceBoundarySession(observations);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            powerSourceBoundaryPlan: PowerSourceBoundaryPlan()).RunAsync(
                AdapterPowerState.UsbCPower100W,
                _ => Task.FromResult(PadAbsentAuthorization),
                _ => Task.FromResult(new HyperBoostPowerSourceBoundaryAuthorization(
                    Confirmed: false,
                    CoolingPadPhysicalPresenceObservation.Absent)),
                default);

        Assert.False(result.Success);
        Assert.Equal(
            HyperBoostPowerSourceBoundaryValidationFailure.ReconnectNotConfirmed,
            result.PowerSourceBoundaryValidation!.Failure);
        Assert.False(result.PowerSourceBoundaryValidation.FullPowerReconnectConfirmed);
        Assert.Equal(0, result.PowerSourceBoundaryValidation.OpenBladeCleanupWriteCount);
        Assert.False(result.RestoreAttempted);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
    }

    [Fact]
    public async Task RunAsync_PostRestorationPadObservationFailsValidationAfterSafeRestore()
    {
        var session = new StatefulSession("01010000", "01020000");
        var gate = new ScriptedSafetyGate(
            SafeGateResult,
            SafeGateResult,
            SafeGateResult,
            new PerformanceFanValidationSafetyGateResult(
                FullPowerAdapterConfirmed: true,
                CoolingPadAbsentConfirmed: false,
                PerformanceFanValidationSafetyFailure.CoolingPadPresent));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate,
            sustainedPlan: Plan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(70, 60))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.Equal(
            PerformanceFanValidationFailure.SustainedValidationFailed,
            result.Failure);
        Assert.True(result.SustainedValidation!.Success);
        Assert.True(result.Restored);
        Assert.True(result.PostRestorationFullPowerAdapterConfirmed);
        Assert.False(result.PostRestorationCoolingPadAbsentConfirmed);
        Assert.Equal(
            PerformanceFanValidationSafetyFailure.CoolingPadPresent,
            result.PostRestorationSafetyFailure);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(4, gate.Calls);
    }

    [Fact]
    public async Task RunAsync_SustainedCpuThresholdBreachFailsButRestoresOnce()
    {
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(maximumCpuTemperature: 80),
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(81, 60))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.Equal(
            PerformanceFanValidationFailure.SustainedValidationFailed,
            result.Failure);
        Assert.Equal(
            HyperBoostSustainedValidationFailure.CpuTemperatureExceeded,
            result.SustainedValidation!.Failure);
        Assert.False(result.SustainedValidation.Success);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x02));
    }

    [Fact]
    public async Task RunAsync_SustainedPadReappearanceFailsAndRestores()
    {
        var session = new StatefulSession("01010000", "01020000");
        var gate = new ScriptedSafetyGate(
            SafeGateResult,
            new PerformanceFanValidationSafetyGateResult(
                FullPowerAdapterConfirmed: true,
                CoolingPadAbsentConfirmed: false,
                PerformanceFanValidationSafetyFailure.CoolingPadPresent));

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: gate,
            sustainedPlan: Plan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(70, 60))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.Equal(
            HyperBoostSustainedValidationFailure.CoolingPadPresent,
            result.SustainedValidation!.Failure);
        Assert.False(result.SustainedValidation.ContinuousPadAbsenceConfirmed);
        Assert.True(result.Restored);
        Assert.Equal(3, gate.Calls);
    }

    [Fact]
    public async Task RunAsync_SustainedThermalStateLossFailsAndRestores()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            loseThermalStateAfterCompletedPair: 3);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(70, 60))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.Equal(
            HyperBoostSustainedValidationFailure.HyperBoostStateLost,
            result.SustainedValidation!.Failure);
        Assert.False(result.SustainedValidation.HyperBoostStateMaintained);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringSustainedObservationStillRestores()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(),
            thermalTelemetrySource: new CancelingTelemetrySource(cancellation))
                .RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    cancellation.Token);

        Assert.Equal(PerformanceFanValidationFailure.Canceled, result.Failure);
        Assert.Equal(
            HyperBoostSustainedValidationFailure.Canceled,
            result.SustainedValidation!.Failure);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData((int)HyperBoostGpuSensorPolicy.AllowInactive, true)]
    [InlineData((int)HyperBoostGpuSensorPolicy.Required, false)]
    public async Task RunAsync_SustainedGpuInactiveHonorsReviewedPolicy(
        int policyValue,
        bool expectedSuccess)
    {
        HyperBoostGpuSensorPolicy policy =
            (HyperBoostGpuSensorPolicy)policyValue;
        var session = new StatefulSession("01010000", "01020000");

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(gpuSensorPolicy: policy),
            thermalTelemetrySource: new ScriptedTelemetrySource(
                new(70, null, GpuInactive: true))).RunAsync(
                    PerformanceFanValidationTarget.HyperBoost,
                    default);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(
            expectedSuccess
                ? HyperBoostSustainedValidationFailure.None
                : HyperBoostSustainedValidationFailure.GpuTemperatureUnavailable,
            result.SustainedValidation!.Failure);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_ExternalLoadMaintainsExactRuntimeIdentity()
    {
        var session = new StatefulSession("01010000", "01020000");
        var workload = new ScriptedWorkloadSource(
            HyperBoostExternalWorkloadObservation.RunningExact);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: ExternalPlan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(new(70, 60)),
            externalWorkloadSource: workload).RunAsync(
                PerformanceFanValidationTarget.HyperBoost,
                default);

        Assert.True(result.Success);
        Assert.True(result.SustainedValidation!.ExternalWorkloadMaintained);
        Assert.Equal(
            result.SustainedValidation.PlannedSampleCount + 1,
            workload.Calls);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_ExternalLoadIdentityLossFailsAndRestores()
    {
        var session = new StatefulSession("01010000", "01020000");
        var workload = new ScriptedWorkloadSource(
            HyperBoostExternalWorkloadObservation.IdentityMismatch);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: ExternalPlan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(new(70, 60)),
            externalWorkloadSource: workload).RunAsync(
                PerformanceFanValidationTarget.HyperBoost,
                default);

        Assert.Equal(
            HyperBoostSustainedValidationFailure.ExternalWorkloadIdentityMismatch,
            result.SustainedValidation!.Failure);
        Assert.False(result.SustainedValidation.ExternalWorkloadMaintained);
        Assert.Single(workload.ObservedPlans);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_SustainedFanReadFailureFailsButRestores()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failFanSelector: 2);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(),
            thermalTelemetrySource: new ScriptedTelemetrySource(new(70, 60)))
                .RunAsync(PerformanceFanValidationTarget.HyperBoost, default);

        Assert.Equal(
            HyperBoostSustainedValidationFailure.FanRpmReadFailed,
            result.SustainedValidation!.Failure);
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_SustainedFailureAndRestoreFailureRequiresManualRecovery()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failWriteNumber: 3);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            safetyGate: new ScriptedSafetyGate(SafeGateResult),
            sustainedPlan: Plan(maximumCpuTemperature: 80),
            thermalTelemetrySource: new ScriptedTelemetrySource(new(81, 60)))
                .RunAsync(PerformanceFanValidationTarget.HyperBoost, default);

        Assert.Equal(
            HyperBoostSustainedValidationFailure.CpuTemperatureExceeded,
            result.SustainedValidation!.Failure);
        Assert.Equal(
            PerformanceFanValidationFailure.RestoreWriteIndeterminate,
            result.Failure);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.False(result.Restored);
    }

    [Fact]
    public void Constructor_RequiresPlanTelemetryAndSafetyGateTogether()
    {
        var session = new StatefulSession("01010000", "01020000");
        ReviewedHyperBoostThermalPlan plan = Plan();

        Assert.Throws<ArgumentException>(() => Runner(
            session,
            sustainedPlan: plan));
        Assert.Throws<ArgumentException>(() => Runner(
            session,
            thermalTelemetrySource: new ScriptedTelemetrySource(new(70, 60))));
        Assert.Throws<ArgumentException>(() => Runner(
            session,
            sustainedPlan: plan,
            thermalTelemetrySource: new ScriptedTelemetrySource(new(70, 60))));
    }

    [Theory]
    [InlineData(4900, 5300)]
    [InlineData(5300, 6100)]
    public async Task RunAsync_FixedFanRpmOutsideHighRangeFailsAfterRestoration(
        int firstRpm,
        int secondRpm)
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: firstRpm,
            secondFanRpm: secondRpm);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Fixed5400, default);

        Assert.Equal(
            PerformanceFanValidationFailure.FixedFanRpmOutOfRange,
            result.Failure);
        Assert.Equal(new FanRpmReadings(firstRpm, secondRpm), result.CandidateFanRpm);
        Assert.Equal(3, result.CandidateFanRpmSamples.Count);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(6, session.Commands.Count(command => command.CommandId == 0x88));
    }

    [Fact]
    public async Task RunAsync_FixedFanRequiresThreeConsecutiveHighSamples()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            fanRpmSamples:
            [
                new(5100, 5200),
                new(5200, 5300),
                new(4900, 5300),
                new(5100, 5100),
                new(5200, 5200),
                new(5300, 5300),
            ]);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 6,
            stateCheckEverySamples: 5).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.True(result.Success);
        Assert.Equal(new FanRpmReadings(5300, 5300), result.CandidateFanRpm);
        Assert.Equal(
            [
                new(5100, 5200),
                new(5200, 5300),
                new(4900, 5300),
                new(5100, 5100),
                new(5200, 5200),
                new(5300, 5300),
            ],
            result.CandidateFanRpmSamples);
        Assert.Equal(12, session.Commands.Count(command => command.CommandId == 0x88));
        Assert.True(result.Restored);
    }

    [Fact]
    public async Task RunAsync_FixedFanStateLossDuringRampFailsAndRestores()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: 3000,
            secondFanRpm: 3000,
            loseFixedStateAfterCompletedFanSamples: 1);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 3,
            stateCheckEverySamples: 1).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                default);

        Assert.Equal(
            PerformanceFanValidationFailure.CandidateTargetMismatch,
            result.Failure);
        Assert.Equal(new FanRpmReadings(3000, 3000), result.CandidateFanRpm);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x88));
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringFixedFanRampStillRestores()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new StatefulSession(
            "01010000",
            "01020000",
            firstFanRpm: 3000,
            secondFanRpm: 3000,
            afterCompletedFanSample: cancellation.Cancel);

        PerformanceFanSetterValidationResult result = await Runner(
            session,
            maximumSamples: 3,
            stateCheckEverySamples: 3,
            sampleInterval: TimeSpan.FromMilliseconds(1)).RunAsync(
                PerformanceFanValidationTarget.Fixed5400,
                cancellation.Token);

        Assert.Equal(PerformanceFanValidationFailure.Canceled, result.Failure);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Fact]
    public async Task RunAsync_FixedFanRpmReadFailureStillRestoresConfirmedState()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failFanSelector: 2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Fixed5400, default);

        Assert.Equal(
            PerformanceFanValidationFailure.FixedFanRpmReadFailed,
            result.Failure);
        Assert.Null(result.CandidateFanRpm);
        Assert.True(result.CandidateConfirmed);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.Restored);
        Assert.False(result.IndeterminatePartialWrite);
        Assert.False(result.ManualRecoveryRequired);
    }

    [Theory]
    [InlineData(PerformanceFanValidationTarget.Balanced, "01010000", "01020000")]
    [InlineData(PerformanceFanValidationTarget.BatterySaver, "01010300", "01020300")]
    [InlineData(PerformanceFanValidationTarget.BatteryBalanced, "01010600", "01020600")]
    public async Task RunAsync_AbortsWithoutWritesWhenTargetIsAlreadyActive(
        PerformanceFanValidationTarget target,
        string first,
        string second)
    {
        var session = new StatefulSession(first, second);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(target, default);

        Assert.Equal(PerformanceFanValidationFailure.TargetAlreadyActive, result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x02);
    }

    [Theory]
    [InlineData("01010500", "01020500")]
    [InlineData("01010200", "01020200")]
    [InlineData("01010300", "01020300")]
    [InlineData("01010600", "01020600")]
    public async Task RunAsync_HyperBoostRequiresBalancedAutomaticBaseline(
        string first,
        string second)
    {
        var session = new StatefulSession(first, second);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.HyperBoost, default);

        Assert.Equal(
            PerformanceFanValidationFailure.BaselineNotBalanced,
            result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(
            session.Commands,
            command => command.CommandId is 0x01 or 0x02);
    }

    [Fact]
    public async Task RunAsync_HyperBoostBaselineIsNeverUsedForAnotherTarget()
    {
        var session = new StatefulSession("01010700", "01020700");

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Silent, default);

        Assert.Equal(
            PerformanceFanValidationFailure.BaselinePairMismatch,
            result.Failure);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(
            session.Commands,
            command => command.CommandId is 0x01 or 0x02);
    }

    [Theory]
    [InlineData("01010001", "01020001")]
    [InlineData("01010401", "01020401")]
    [InlineData("01010000", "01020200")]
    public async Task RunAsync_FailsClosedForFixedUnknownOrMismatchedBaseline(
        string baseline1,
        string baseline2)
    {
        var session = new StatefulSession(baseline1, baseline2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Silent, default);

        Assert.False(result.Success);
        Assert.False(result.ApplyAttempted);
        Assert.DoesNotContain(session.Commands, command => command.CommandId is 0x01 or 0x02);
        Assert.Contains(result.Failure, new[]
        {
            PerformanceFanValidationFailure.BaselineFixedSpeedCannotBeRestored,
            PerformanceFanValidationFailure.BaselineReadFailed,
            PerformanceFanValidationFailure.BaselinePairMismatch,
        });
    }

    [Fact]
    public async Task RunAsync_DoesNotRollbackAfterIndeterminatePartialWrite()
    {
        var session = new StatefulSession(
            "01010000",
            "01020000",
            failWriteNumber: 2);

        PerformanceFanSetterValidationResult result = await Runner(session)
            .RunAsync(PerformanceFanValidationTarget.Performance, default);

        Assert.Equal(
            PerformanceFanValidationFailure.ApplyWriteIndeterminate,
            result.Failure);
        Assert.True(result.ApplyAttempted);
        Assert.False(result.ApplyAcknowledged);
        Assert.False(result.RestoreAttempted);
        Assert.True(result.IndeterminatePartialWrite);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(2, session.Commands.Count(command => command.CommandId == 0x02));
    }

    private static PerformanceFanSetterValidationRunner Runner(
        IRazerCommandSession session,
        int maximumSamples = 3,
        int stateCheckEverySamples = 3,
        TimeSpan? sampleInterval = null,
        IPerformanceFanValidationSafetyGate? safetyGate = null,
        ReviewedHyperBoostThermalPlan? sustainedPlan = null,
        IHyperBoostThermalTelemetrySource? thermalTelemetrySource = null,
        IHyperBoostExternalWorkloadSource? externalWorkloadSource = null,
        ReviewedHyperBoostAdapterTransitionPlan? adapterTransitionPlan = null,
        ReviewedHyperBoostPowerSourceBoundaryPlan? powerSourceBoundaryPlan = null) => new(
        static () => new ValidationRunContext(),
        new ActorFactory(new ValidationActor(
            session,
            new UnsupportedBatteryCommands(),
            new UnsupportedTemporaryCommands())),
        sustainedPlan is null && powerSourceBoundaryPlan is null
            ? TimeProvider.System
            : new ImmediateTimeProvider(),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5),
        new PendingDisposalRegistry(),
        fixedFanSampleInterval: sampleInterval ?? TimeSpan.Zero,
        fixedFanMaximumSamples: maximumSamples,
        fixedFanStateCheckEverySamples: stateCheckEverySamples,
        safetyGate: safetyGate,
        sustainedPlan: sustainedPlan,
        thermalTelemetrySource: thermalTelemetrySource,
        externalWorkloadSource: externalWorkloadSource,
        adapterTransitionPlan: adapterTransitionPlan,
        powerSourceBoundaryPlan: powerSourceBoundaryPlan);

    private static readonly PerformanceFanValidationSafetyGateResult SafeGateResult =
        new(
            FullPowerAdapterConfirmed: true,
            CoolingPadAbsentConfirmed: true,
            PerformanceFanValidationSafetyFailure.None)
        {
            AdapterPowerState = AdapterPowerState.FullPower280W,
        };

    private static ReviewedHyperBoostThermalPlan Plan(
        double maximumCpuTemperature = 90,
        HyperBoostGpuSensorPolicy gpuSensorPolicy =
            HyperBoostGpuSensorPolicy.Required)
    {
        string json = $$"""
            {
              "planId": "test-no-load-v1",
              "modelNumber": "RZ09-0581",
              "biosVersion": "4.00",
              "durationSeconds": 1,
              "sampleIntervalMilliseconds": 500,
              "maximumCpuTemperatureCelsius": {{maximumCpuTemperature}},
              "maximumGpuTemperatureCelsius": 85,
              "gpuSensorPolicy": "{{(gpuSensorPolicy == HyperBoostGpuSensorPolicy.Required ? "required" : "allowInactive")}}",
              "minimumFan1Rpm": 1000,
              "minimumFan2Rpm": 1000,
              "workloadKind": "sustainedNoLoadDwell",
              "workloadIdentityPolicy": "notApplicable",
              "workloadName": null,
              "workloadSha256": null
            }
            """;
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        return ReviewedHyperBoostThermalPlan.Parse(
            bytes,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static ReviewedHyperBoostAdapterTransitionPlan AdapterPlan() =>
        ReviewedHyperBoostAdapterTransitionPlan.LoadTrusted();

    private static ReviewedHyperBoostPowerSourceBoundaryPlan PowerSourceBoundaryPlan() =>
        ReviewedHyperBoostPowerSourceBoundaryPlan.LoadTrusted();

    private static readonly HyperBoostPowerSourceBoundaryAuthorization
        PadAbsentAuthorization = new(
            Confirmed: true,
            CoolingPadPhysicalPresenceObservation.Absent);

    private static ReviewedHyperBoostThermalPlan ExternalPlan() =>
        ReviewedHyperBoostThermalPlan.LoadTrustedPadAbsentExternalLoad()
            .BindRuntimeExactExecutable(new(
                "reviewed-workload.exe",
                new string('B', 64)));

    private sealed class ScriptedSafetyGate(
        PerformanceFanValidationSafetyGateResult first,
        params PerformanceFanValidationSafetyGateResult[] additional)
        : IPerformanceFanValidationSafetyGate
    {
        private readonly PerformanceFanValidationSafetyGateResult[] results =
            [first, .. additional];

        public int Calls { get; private set; }

        public Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
            ValidationRunContext context,
            IRazerCommandSession session,
            CancellationToken cancellationToken)
        {
            int index = Math.Min(Calls, results.Length - 1);
            Calls++;
            return Task.FromResult(results[index]);
        }
    }

    private sealed class ScriptedTelemetrySource(
        HyperBoostTemperatureObservation first,
        params HyperBoostTemperatureObservation[] additional)
        : IHyperBoostThermalTelemetrySource
    {
        private readonly HyperBoostTemperatureObservation[] observations =
            [first, .. additional];
        private int calls;

        public ValueTask<HyperBoostTemperatureObservation> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Math.Min(calls++, observations.Length - 1);
            return ValueTask.FromResult(observations[index]);
        }
    }

    private sealed class ScriptedWorkloadSource(
        HyperBoostExternalWorkloadObservation observation)
        : IHyperBoostExternalWorkloadSource
    {
        internal List<ReviewedHyperBoostThermalPlan> ObservedPlans { get; } = [];

        internal int Calls => ObservedPlans.Count;

        public ValueTask<HyperBoostExternalWorkloadObservation> ObserveAsync(
            ReviewedHyperBoostThermalPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedPlans.Add(plan);
            return ValueTask.FromResult(observation);
        }
    }

    private sealed class CancelingTelemetrySource(CancellationTokenSource cancellation)
        : IHyperBoostThermalTelemetrySource
    {
        public ValueTask<HyperBoostTemperatureObservation> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            callback(state);
            return CompletedTimer.Instance;
        }

        private sealed class CompletedTimer : ITimer
        {
            internal static CompletedTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ActorFactory(ValidationActor actor) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(
            ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(actor, null));
    }

    private sealed class StatefulSession(
        string firstHex,
        string secondHex,
        int? failWriteNumber = null,
        int firstFanRpm = 5300,
        int secondFanRpm = 5300,
        int? failFanSelector = null,
        IReadOnlyList<FanRpmReadings>? fanRpmSamples = null,
        int? loseFixedStateAfterCompletedFanSamples = null,
        Action? afterCompletedFanSample = null,
        string? stateAfterFirstThermalPair1 = null,
        string? stateAfterFirstThermalPair2 = null,
        int? loseThermalStateAfterCompletedPair = null,
        AdapterPowerState adapterPowerState = AdapterPowerState.UsbCPower100W,
        bool failAdapterRead = false) : IRazerCommandSession
    {
        private readonly byte[][] baseline =
            [Convert.FromHexString(firstHex), Convert.FromHexString(secondHex)];
        private readonly byte[][] state =
            [Convert.FromHexString(firstHex), Convert.FromHexString(secondHex)];
        private int completedFanSamples;
        private int completedThermalPairs;
        private int writeCount;

        internal List<RazerCommandSpec> Commands { get; } = [];

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            Commands.Add(command);
            if (command.CommandId == 0x82)
            {
                int index = command.Arguments.Span[1] - 1;
                byte[] response = state[index].ToArray();
                if (command.PayloadValidator is not null
                    && !command.PayloadValidator(response))
                {
                    return Failure(HidSessionFailureKind.ProtocolFailure);
                }

                if (index == 1)
                {
                    completedThermalPairs++;
                    if (completedThermalPairs == 1
                        && stateAfterFirstThermalPair1 is not null
                        && stateAfterFirstThermalPair2 is not null)
                    {
                        state[0] = Convert.FromHexString(stateAfterFirstThermalPair1);
                        state[1] = Convert.FromHexString(stateAfterFirstThermalPair2);
                    }

                    else if (completedThermalPairs == loseThermalStateAfterCompletedPair)
                    {
                        state[0] = baseline[0].ToArray();
                        state[1] = baseline[1].ToArray();
                    }
                }

                return Success(response);
            }

            if (command.CommandId == 0x88)
            {
                int selector = command.Arguments.Span[1];
                if (selector == failFanSelector)
                {
                    return Failure(HidSessionFailureKind.Timeout);
                }

                FanRpmReadings sample = fanRpmSamples is { Count: > 0 }
                    ? fanRpmSamples[Math.Min(completedFanSamples, fanRpmSamples.Count - 1)]
                    : new(firstFanRpm, secondFanRpm);
                int rpm = selector == 1 ? sample.Selector1Rpm : sample.Selector2Rpm;
                byte[] response = [0x01, (byte)selector, checked((byte)(rpm / 100))];
                if (selector == 2)
                {
                    completedFanSamples++;
                    if (completedFanSamples == loseFixedStateAfterCompletedFanSamples)
                    {
                        state[0] = baseline[0].ToArray();
                        state[1] = baseline[1].ToArray();
                    }

                    afterCompletedFanSample?.Invoke();
                }

                return Success(response);
            }

            if (command.CommandId == 0x8C)
            {
                if (failAdapterRead)
                {
                    return Failure(HidSessionFailureKind.Timeout);
                }

                byte[] response = adapterPowerState switch
                {
                    AdapterPowerState.UsbCPower100W => [0x07, 0x11],
                    AdapterPowerState.FullPower280W => [0x11, 0x11],
                    AdapterPowerState.FullPower330W => [0x12, 0x11],
                    _ => [0x00, 0x00],
                };
                return Success(response);
            }

            writeCount++;
            if (writeCount == failWriteNumber)
            {
                return Failure(HidSessionFailureKind.Timeout);
            }

            byte[] payload = command.Arguments.ToArray();
            if (command.PayloadValidator is not null
                && !command.PayloadValidator(payload))
            {
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }

            if (command.CommandId == 0x02)
            {
                int index = payload[1] - 1;
                state[index] = payload.ToArray();
            }

            return Success(payload);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(byte[] payload) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure(
            HidSessionFailureKind failure) =>
            Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
    }

    private sealed record PowerSourceBoundaryStep(
        AdapterPowerState AdapterState,
        string FirstThermalHex,
        string SecondThermalHex);

    private sealed class PowerSourceBoundarySession(
        params PowerSourceBoundaryStep[] steps) : IRazerCommandSession
    {
        private readonly PowerSourceBoundaryStep[] steps = steps.Length > 0
            ? steps
            : throw new ArgumentException("At least one boundary step is required.", nameof(steps));
        private readonly byte[][] state =
            [Convert.FromHexString("01010000"), Convert.FromHexString("01020000")];
        private int nextStep;

        internal List<RazerCommandSpec> Commands { get; } = [];

        public RazerCommandSessionDisposition Disposition { get; private set; } =
            RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            _ = assignTransactionId();
            Commands.Add(command);
            if (command.CommandId == 0x8C)
            {
                PowerSourceBoundaryStep step =
                    steps[Math.Min(nextStep++, steps.Length - 1)];
                state[0] = Convert.FromHexString(step.FirstThermalHex);
                state[1] = Convert.FromHexString(step.SecondThermalHex);
                byte[] adapter = step.AdapterState switch
                {
                    AdapterPowerState.Battery => [0x00, 0x11],
                    AdapterPowerState.UsbCPower100W => [0x07, 0x11],
                    AdapterPowerState.FullPower280W => [0x11, 0x11],
                    AdapterPowerState.FullPower330W => [0x12, 0x11],
                    _ => [0x00, 0x00],
                };
                return Success(adapter);
            }

            if (command.CommandId == 0x82)
            {
                int index = command.Arguments.Span[1] - 1;
                byte[] response = state[index].ToArray();
                return command.PayloadValidator is null
                    || command.PayloadValidator(response)
                        ? Success(response)
                        : Failure(HidSessionFailureKind.ProtocolFailure);
            }

            byte[] payload = command.Arguments.ToArray();
            if (command.PayloadValidator is not null
                && !command.PayloadValidator(payload))
            {
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }

            if (command.CommandId == 0x02)
            {
                state[payload[1] - 1] = payload.ToArray();
            }

            return Success(payload);
        }

        public ValueTask DisposeAsync()
        {
            Disposition = RazerCommandSessionDisposition.FullyDisposed;
            return ValueTask.CompletedTask;
        }

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Success(
            byte[] payload) => Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));

        private static Task<HidSessionResult<ReadOnlyMemory<byte>>> Failure(
            HidSessionFailureKind failure) => Task.FromResult(
                new HidSessionResult<ReadOnlyMemory<byte>>(default, failure));
    }

    private sealed class UnsupportedBatteryCommands : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            BatteryValidationSetting setting,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnsupportedTemporaryCommands : ITemporaryFullChargeCommandClient
    {
        public Task<HidSessionResult<bool>> QueryAsync(
            byte transactionId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<HidSessionResult<HidUnit>> SetAsync(
            byte transactionId,
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
