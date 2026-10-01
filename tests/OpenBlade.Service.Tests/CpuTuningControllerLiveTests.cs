using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerLiveTests
{
    [Fact]
    public async Task CapturedAdmissionExpiresAndLaterRequestsCannotRenewIt()
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var backend = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        var outside = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit);
        Func<bool>? first = null;
        backend.Admit = (_, _) =>
        {
            var current = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit);
            Assert.True(current());
            if (first is null) { first = current; }
            return true;
        };
        Assert.False(outside());
        var applied = await controller.MutateLiveAsync(new(45, null), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.False(first!());
        backend.Admit = (_, _) =>
        {
            Assert.False(first!());
            Assert.True(controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.PowerLimit)());
            return true;
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.False(first!());
    }

    [Fact]
    public async Task RuntimeRevocationAfterPendingFlushSkipsSetterAndRetainsPreparedOwnership()
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var backend = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        bool admitted = true;
        store.BeforeWrite = index => { if (index == 2) { admitted = false; } return Task.CompletedTask; };
        var result = await controller.MutateLiveAsync(new(45, null), false, _ => admitted, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(backend.Sent);
        Assert.Equal(CpuLivePowerOwnershipState.Prepared, store.Journal!.State);
        Assert.Equal(0, store.Journal.CompletedSteps);
        Assert.Equal(CpuTuningRecoveryState.Blocked, result.CpuTuning!.LiveRecovery!.State);
    }

    [Fact]
    public async Task CombinedApplyPreparesBothJournalsBeforeFirstSendAndRestoresAcrossRestart()
    {
        var fixture = new Fixture();
        var curveOriginal = fixture.Curve.Values.ToArray(); var powerOriginal = fixture.Power.Pair;
        fixture.Power.OnSend = request =>
        {
            Assert.Equal(CpuLiveCurveOwnershipState.Prepared, fixture.CurveStore.Journal!.State);
            Assert.Empty(fixture.Curve.Sent);
            fixture.Power.Pair = request.After;
            return Task.FromResult(CpuLivePowerSubmission.Acknowledged);
        };
        fixture.Curve.OnSend = command =>
        {
            Assert.Equal(CpuLivePowerOwnershipState.OwnedVerified, fixture.PowerStore.Journal!.State);
            Assert.Equal(2, fixture.PowerStore.Journal.CompletedSteps);
            return fixture.Curve.Apply(command);
        };
        var controller = fixture.Controller();
        var applied = await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(Both, applied.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, applied.CpuTuning.LiveRecovery.State);
        Assert.Equal(new(null, 45000, 45000), applied.CpuTuning.LiveState!.PowerLimits);
        Assert.Equal(-2, applied.CpuTuning.LiveState.UniformCurveOptimizerValue);
        fixture.Power.OnSend = null; fixture.Curve.OnSend = null;
        var restored = await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(curveOriginal, fixture.Curve.Values); Assert.Equal(powerOriginal, fixture.Power.Pair);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.Equal(CpuTuningFeatures.None, restored.CpuTuning.LiveRecovery!.Features);
    }

    [Theory]
    [InlineData("reverse")]
    [InlineData("persist")]
    [InlineData("context")]
    public async Task SecondPreparationDenialPersistenceOrContextMismatchCannotSubmitEitherFamily(string fault)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        if (fault == "reverse") { fixture.Curve.Admit = (_, purpose) => purpose != CpuLiveCurveCommandPurpose.Restore; }
        if (fault == "persist") { fixture.CurveStore.FailWrite = 1; }
        if (fault == "context")
        { fixture.Curve.Context = fixture.Curve.Context with { BootSequence = fixture.Curve.Context.BootSequence with { Sequence = 43 } }; }
        var result = await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
        Assert.Equal(CpuLivePowerOwnershipState.Prepared, fixture.PowerStore.Journal!.State);
        if (fault == "persist")
        {
            Assert.Equal(Both, result.CpuTuning!.LiveRecovery!.Features);
            int reads = fixture.Curve.Reads + fixture.Power.Reads;
            await controller.MutateLiveAsync(null, true, _ => true, default);
            Assert.Equal(reads, fixture.Curve.Reads + fixture.Power.Reads);
        }
    }

    [Fact]
    public async Task RepeatedSingleFamilyEditsCoexistAndRetainBothFirstOriginals()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        var curveOriginal = fixture.Curve.Values.ToArray(); var powerOriginal = fixture.Power.Pair;
        foreach (var settings in new CpuTuningSettings[] { new(45, null), new(null, new(true, -2)), new(60, null), new(null, new(true, -3)) })
        { Assert.Equal(ControlOperationStatus.Stored, (await controller.MutateLiveAsync(settings, false, _ => true, default)).Status); }
        Assert.Equal(curveOriginal, fixture.CurveStore.Journal!.OriginalBaseline);
        Assert.Equal(powerOriginal, fixture.PowerStore.Journal!.OriginalBaseline.Decode());
        var restored = await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(curveOriginal, fixture.Curve.Values); Assert.Equal(powerOriginal, fixture.Power.Pair);
    }

    [Fact]
    public async Task CompleteRestoreAdmissionPrecedesAnyRestoreAndCleanDenialRetainsOriginals()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        int curveSends = fixture.Curve.Sent.Count, powerSends = fixture.Power.Sent.Count;
        fixture.Power.Admit = (_, purpose) => purpose != CpuLivePowerCommandPurpose.Restore;
        var result = await controller.MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(curveSends, fixture.Curve.Sent.Count); Assert.Equal(powerSends, fixture.Power.Sent.Count);
        fixture.Power.Admit = (_, _) => true;
        Assert.Equal(ControlOperationStatus.Stored, (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
    }

    [Fact]
    public async Task UncertainCurveRestoreNeverStartsPowerRestoreOrAnyLaterFamily()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        int powerSends = fixture.Power.Sent.Count;
        fixture.Curve.OnSend = _ => Task.FromResult(CpuLiveCurveSubmission.Unknown);
        var result = await controller.MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(Both, result.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
        Assert.Equal(powerSends, fixture.Power.Sent.Count);
        int reads = fixture.Curve.Reads + fixture.Power.Reads;
        await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        await controller.ObserveLiveAsync(_ => true, default);
        Assert.Equal(reads, fixture.Curve.Reads + fixture.Power.Reads);
    }

    [Fact]
    public async Task RuntimeRevocationBetweenVerifiedFamiliesStopsWithoutCompensation()
    {
        var fixture = new Fixture(); var controller = fixture.Controller(); bool admitted = true;
        fixture.PowerStore.BeforeWrite = index =>
        {
            if (index == 5) { admitted = false; } // Final PPT verified-progress CAS.
            return Task.CompletedTask;
        };
        var result = await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => admitted, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(2, fixture.Power.Sent.Count); Assert.Empty(fixture.Curve.Sent);
        Assert.Equal(CpuLivePowerOwnershipState.OwnedVerified, fixture.PowerStore.Journal!.State);
        Assert.Equal(CpuLiveCurveOwnershipState.Prepared, fixture.CurveStore.Journal!.State);
        Assert.Equal(Both, result.CpuTuning!.LiveRecovery!.Features);
    }

    [Fact]
    public async Task PeerDriftBetweenPowerStepsIsDurablyBlockedBeforeNextSetter()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        fixture.Power.AfterSend = () => fixture.Curve.Values[0] = new(0, -9);
        var result = await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Single(fixture.Power.Sent); Assert.Empty(fixture.Curve.Sent);
        Assert.Equal(CpuLiveCurveOwnershipState.Blocked, fixture.CurveStore.Journal!.State);
        fixture.Curve.Values[0] = new(0, -1);
        int reads = fixture.Curve.Reads + fixture.Power.Reads;
        await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(reads, fixture.Curve.Reads + fixture.Power.Reads);
    }

    [Fact]
    public async Task UnchangedFamilyDoesNotAcquireOwnershipAndTicketsCannotReplay()
    {
        var fixture = new Fixture();
        var result = await fixture.Controller().MutateLiveAsync(new(45, new(true, -1)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Null(fixture.CurveStore.Journal); Assert.Empty(fixture.Curve.Sent);
        Assert.Equal(CpuTuningFeatures.PowerLimit, result.CpuTuning!.LiveRecovery!.Features);
        var transaction = new CpuLiveCurveTuningTransaction(fixture.Curve, fixture.CurveStore);
        var prepared = await transaction.PrepareApplyAsync(new(true, -2), default);
        Assert.Empty(fixture.Curve.Sent);
        Assert.Equal(CpuLiveCurveOutcome.Verified,
            (await transaction.ExecutePreparedAsync(prepared.Operation!, null, default)).Outcome);
        int sends = fixture.Curve.Sent.Count;
        Assert.Equal(CpuLiveCurveOutcome.Blocked,
            (await transaction.ExecutePreparedAsync(prepared.Operation!, null, default)).Outcome);
        Assert.Equal(sends, fixture.Curve.Sent.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivePeerDriftAfterNoOpOrRestorationPersistsCoupledStop(bool restoredPeer)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        int powerSends;
        ControlOperationResult result;
        if (restoredPeer)
        {
            await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
            powerSends = fixture.Power.Sent.Count;
            fixture.Power.OnObserve = () =>
            {
                if (fixture.CurveStore.Journal!.State == CpuLiveCurveOwnershipState.Restored)
                { fixture.Curve.Values[0] = new(0, -9); }
            };
            result = await controller.MutateLiveAsync(null, true, _ => true, default);
            Assert.Equal(powerSends, fixture.Power.Sent.Count);
            Assert.Equal(CpuLiveCurveOwnershipState.Blocked, fixture.CurveStore.Journal!.State);
        }
        else
        {
            fixture.Power.AfterSend = () => fixture.Curve.Values[0] = new(0, -9);
            result = await controller.MutateLiveAsync(new(45, new(true, -1)), false, _ => true, default);
            Assert.Single(fixture.Power.Sent); Assert.Null(fixture.CurveStore.Journal);
            Assert.Empty(fixture.Curve.Sent);
        }
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(CpuLivePowerOwnershipState.Blocked, fixture.PowerStore.Journal!.State);
        fixture.Curve.Values[0] = new(0, -1); fixture.Power.OnObserve = null;
        int reads = fixture.Curve.Reads + fixture.Power.Reads;
        await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(reads, fixture.Curve.Reads + fixture.Power.Reads);
    }

    [Fact]
    public async Task PreparedTicketCannotExecuteAfterAnotherOperationReplacesItsJournal()
    {
        var fixture = new Fixture();
        var transaction = new CpuLivePowerTuningTransaction(fixture.Power, fixture.PowerStore);
        var first = await transaction.PrepareApplyAsync([new(CpuLivePowerLimit.Fast, 45)], default);
        await transaction.PrepareApplyAsync([new(CpuLivePowerLimit.Fast, 60)], default);
        Assert.Equal(CpuLivePowerOutcome.Blocked,
            (await transaction.ExecutePreparedAsync(first.Operation!, null, default)).Outcome);
        Assert.Empty(fixture.Power.Sent);
    }

    private const CpuTuningFeatures Both = CpuTuningFeatures.CurveOptimizer | CpuTuningFeatures.PowerLimit;
    private sealed class Fixture
    {
        internal readonly CpuLiveCurveTuningTransactionTests.MemoryStore CurveStore = new();
        internal readonly CpuLivePowerTuningTransactionTests.MemoryStore PowerStore = new();
        internal readonly CpuLiveCurveTuningTransactionTests.FakeBackend Curve;
        internal readonly CpuLivePowerTuningTransactionTests.FakeBackend Power;
        internal Fixture() { Curve = new(CurveStore); Power = new(PowerStore); }
        internal CpuTuningController Controller() => new(new UnusedSource(), curveBackend: Curve,
            curveStore: CurveStore, powerBackend: Power, powerStore: PowerStore);
    }

    private sealed class UnusedSource : ICpuTuningObservationSource
    {
        public DeviceIdentity? ReadIdentity() => throw new InvalidOperationException("Live operations cannot use saved identity.");
        public SavedCpuFirmwareReadResult ReadSaved(DeviceIdentity identity) => throw new InvalidOperationException();
        public CpuTuningBackendStatus InspectInstalled(DeviceIdentity identity, bool useInstalledBackend) => throw new InvalidOperationException();
    }
}
