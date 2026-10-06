using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerLiveTests
{
    [Theory]
    [InlineData("context", "power transaction ContextMismatch")]
    [InlineData("state", "power transaction StateChanged")]
    [InlineData("none", "power observation")]
    public async Task CompletedPowerTimingsPreserveOwnershipClassification(string drift, string expectedReason)
    {
        var fixture = new Fixture();
        var transaction = new CpuLivePowerTuningTransaction(fixture.Power, fixture.PowerStore);
        Assert.Equal(CpuLivePowerOutcome.Verified,
            (await transaction.ApplySelectiveAsync([new(CpuLivePowerLimit.Fast, 90)], default)).Outcome);
        if (drift == "context")
        { fixture.Power.Context = fixture.Power.Context with { BootSequence = fixture.Power.Context.BootSequence with { Sequence = 43 } }; }
        if (drift == "state") { fixture.Power.Pair = CpuLivePowerTuningTransactionTests.PairFixture(91000, 80765); }
        var diagnostic = new CpuSavedAdmissionDiagnostic();
        fixture.Power.OnObserve = () =>
        {
            var timing = diagnostic.BeginPowerObservation()!;
            timing.MoveTo(CpuPowerObservationPhase.FirstNative);
            timing.MoveTo(CpuPowerObservationPhase.PostRange);
            timing.MoveTo(CpuPowerObservationPhase.CompleteGetter);
            diagnostic.CompletePowerObservation(timing);
            Assert.Null(diagnostic.LiveObservationReason);
        };
        int writes = fixture.Power.Sent.Count;
        var status = await fixture.Controller().ObserveLiveAsync(_ => true, default, diagnostic: diagnostic);
        Assert.Equal(drift == "none" ? CpuTuningRecoveryState.RestoreReady : CpuTuningRecoveryState.Blocked, status.LiveRecovery!.State);
        string reason = Assert.IsType<string>(diagnostic.LiveObservationReason);
        Assert.StartsWith(expectedReason + "; power phases total ", reason);
        Assert.Contains("CompleteGetter=", reason);
        Assert.Equal(2, reason.Split("power phases total", StringSplitOptions.None).Length);
        Assert.Null(diagnostic.TakePowerTimingSummary()); Assert.Equal(reason, diagnostic.LiveObservationReason);
        Assert.Equal(writes, fixture.Power.Sent.Count);
        if (drift != "none") { Assert.Equal(CpuLivePowerOwnershipState.Blocked, fixture.PowerStore.Journal!.State); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateNonSubmissionSettlesThroughRuntimeWrapperOnlyAfterSuccessfulCas(bool failSettlement)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        using var cancellation = new CancellationTokenSource();
        // Complete off the test synchronization context so the runtime-wrapper
        // continuation can run inline before the next explicit request.
        var completion = new TaskCompletionSource<CpuLiveCurveSubmission>();
        fixture.Curve.OnSend = _ => { cancellation.Cancel(); return completion.Task; };
        var stopped = await controller.MutateLiveAsync(new(null, new(false, -1)), false, _ => true, cancellation.Token);
        Assert.Equal(ControlOperationStatus.Failed, stopped.Status);
        Assert.True(stopped.CpuTuning!.RestorePending);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
        if (failSettlement) { fixture.CurveStore.FailWrite = fixture.CurveStore.Writes + 1; }
        await Task.Run(() => completion.SetResult(CpuLiveCurveSubmission.NotSubmitted));
        int reads = fixture.Curve.Reads;
        var observed = await controller.ObserveLiveAsync(_ => true, default);
        Assert.Equal(failSettlement, observed.RestorePending);
        Assert.Equal(failSettlement ? CpuTuningRecoveryState.Blocked : CpuTuningRecoveryState.None,
            observed.LiveRecovery!.State);
        Assert.Equal(failSettlement ? CpuLiveCurveOwnershipState.PossibleSubmission : CpuLiveCurveOwnershipState.Restored,
            fixture.CurveStore.Journal.State);
        if (failSettlement) { Assert.Equal(reads, fixture.Curve.Reads); }
        Assert.Single(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
    }

    [Fact]
    public async Task CancelledObservationDoesNotReplaceIndependentRestorationAdmission()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        var applied = await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        using var observation = new CancellationTokenSource();
        var unavailable = await controller.ObserveLiveAsync((_, operationToken) =>
        {
            Assert.True(operationToken.CanBeCanceled);
            observation.Cancel();
            return false;
        }, observation.Token);
        Assert.Null(unavailable.LiveState);
        using var restoration = new CancellationTokenSource();
        var restored = await controller.MutateLiveAsync(null, true, _ => true, restoration.Token);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.False(restoration.IsCancellationRequested);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, fixture.CurveStore.Journal!.State);
        Assert.All(fixture.Curve.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Fact]
    public async Task FirstRuntimeRejectionSurvivesTransactionFallbackAndLaterDiagnostic()
    {
        var fixture = new Fixture(); var diagnostic = new CpuSavedAdmissionDiagnostic();
        int admissionCalls = 0;
        var result = await fixture.Controller().ObserveLiveAsync(_ =>
        { admissionCalls++; return diagnostic.Check(false, "runtime controlling session"); }, default, diagnostic: diagnostic);
        Assert.Equal(1, admissionCalls); Assert.Equal(0, fixture.Curve.Reads); Assert.Equal(0, fixture.Power.Reads);
        Assert.Equal("curve transaction ObservationUnavailable", diagnostic.LiveObservationReason);
        Assert.Equal("runtime controlling session", diagnostic.FirstFailure);
        diagnostic.Check(false, "late native failure");
        Assert.Equal("runtime controlling session", diagnostic.FirstFailure);
        Assert.Null(result.LiveState); Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
    }
    [Fact]
    public async Task FullCurveObservationWithFreshAdmissionPresentsWritableWithoutAdmittingCommands()
    {
        var fixture = new Fixture();
        fixture.Curve.Admit = (_, _) => false;
        var requested = new List<CpuTuningFeatures>();
        var controller = fixture.Controller();
        var status = await controller.ObserveLiveAsync(feature => { requested.Add(feature); return true; }, default);
        Assert.True(status.Live.CanWrite);
        Assert.Equal(-1, status.LiveState!.UniformCurveOptimizerValue);
        Assert.NotNull(status.LiveState.PowerLimits);
        Assert.Equal(CpuTuningFeatures.CurveOptimizer, requested[^1]);
        Assert.Empty(fixture.Curve.Admissions); Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
        // The presentation flag cannot admit a command or its exact reverse route.
        var denied = await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, denied.Status);
        Assert.False(denied.CpuTuning!.Live.CanWrite); Assert.Empty(fixture.Curve.Sent);
    }

    [Fact]
    public async Task ReadOnlyCurveDenialRunsAfterBothQueriesAndDoesNotRevokeQueryLease()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        Func<bool>? capturedQuery = null;
        var status = await controller.ObserveLiveAsync(feature =>
        {
            if (feature == CpuTuningFeatures.None) { return true; }
            Assert.True(fixture.Curve.Reads > 0); Assert.True(fixture.Power.Reads > 0);
            capturedQuery = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.None);
            return false;
        }, default);
        Assert.False(status.Live.CanWrite);
        Assert.Equal(-1, status.LiveState!.UniformCurveOptimizerValue);
        Assert.NotNull(status.LiveState.PowerLimits);
        Assert.NotNull(capturedQuery); Assert.False(capturedQuery()); // Operation has expired.
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("revoked")]
    [InlineData("incomplete")]
    [InlineData("query-failed")]
    [InlineData("ownership")]
    public async Task IneligibleCurveObservationNeverPresentsWritable(string fault)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        if (fault == "ownership")
        {
            await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
            fixture.Curve.Context = fixture.Curve.Context with
            { BootSequence = fixture.Curve.Context.BootSequence with { Sequence = 43 } };
        }
        if (fault == "incomplete") { fixture.Curve.Values = fixture.Curve.Values[..^1]; }
        if (fault == "query-failed") { fixture.Curve.Context = fixture.Curve.Context with { EnabledCoreMask = 0 }; }
        int queries = 0, sends = fixture.Curve.Sent.Count;
        var status = await controller.ObserveLiveAsync(feature => fault switch
        {
            "denied" => feature == CpuTuningFeatures.None,
            "revoked" => ++queries == 1,
            _ => true,
        }, default);
        Assert.False(status.Live.CanWrite); Assert.Equal(sends, fixture.Curve.Sent.Count);
        if (fault is "incomplete" or "query-failed" or "revoked") { Assert.Null(status.LiveState); }
        if (fault == "ownership") { Assert.Equal(CpuTuningRecoveryState.Blocked, status.LiveRecovery!.State); }
    }

    [Fact]
    public async Task PowerOnlyObservationRemainsObservedOnlyEvenWithRuntimeAdmission()
    {
        var fixture = new Fixture();
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: fixture.Power, powerStore: fixture.PowerStore);
        var status = await controller.ObserveLiveAsync(_ => true, default);
        Assert.False(status.Live.CanWrite); Assert.NotNull(status.LiveState!.PowerLimits);
        Assert.Empty(status.LiveState.CoreValues);
    }

    [Fact]
    public async Task RuntimeLifetimeIsStableWithinServiceAndChangesAtRestartOrPowerBoundary()
    {
        var store = new CpuLivePowerTuningTransactionTests.MemoryStore();
        var backend = new CpuLivePowerTuningTransactionTests.FakeBackend(store);
        var controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        Assert.Null(controller.CaptureLiveRuntimeLifetime());
        var lifetimes = new List<CpuLiveRuntimeLifetime>();
        backend.Admit = (_, _) =>
        {
            lifetimes.Add(Assert.IsType<CpuLiveRuntimeLifetime>(controller.CaptureLiveRuntimeLifetime()));
            return true;
        };
        await controller.MutateLiveAsync(new(45, null), false, _ => true, default, 2, 3);
        var first = lifetimes[0];
        Assert.All(lifetimes, value => Assert.Equal(first, value));
        Assert.Equal(2, first.LowPowerGeneration); Assert.Equal(3, first.PowerSourceGeneration);
        Assert.Null(controller.CaptureLiveRuntimeLifetime());
        lifetimes.Clear();
        await controller.MutateLiveAsync(null, true, _ => true, default, 2, 3);
        Assert.All(lifetimes, value => Assert.Equal(first, value));
        lifetimes.Clear();
        await controller.MutateLiveAsync(new(60, null), false, _ => true, default, 4, 5);
        Assert.All(lifetimes, value =>
        {
            Assert.Equal(first.ServiceInstance, value.ServiceInstance);
            Assert.NotEqual(first, value);
        });
        controller = new CpuTuningController(new UnusedSource(), powerBackend: backend, powerStore: store);
        lifetimes.Clear();
        await controller.MutateLiveAsync(null, true, _ => true, default, 4, 5);
        Assert.NotEmpty(lifetimes);
        Assert.All(lifetimes, value => Assert.NotEqual(first.ServiceInstance, value.ServiceInstance));
    }

    [Fact]
    public async Task OwnedContextWithEarlierRuntimeLifetimeBlocksRestorationBeforeSubmission()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        var lifetime = new CpuLiveRuntimeLifetime(Guid.NewGuid(), 2, 3);
        fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = lifetime };
        fixture.Power.Context = fixture.Power.Context with { RuntimeLifetime = lifetime };
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default)).Status);
        int curveSends = fixture.Curve.Sent.Count, powerSends = fixture.Power.Sent.Count;
        fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = lifetime with { PowerSourceGeneration = 4 } };
        fixture.Power.Context = fixture.Power.Context with { RuntimeLifetime = lifetime with { PowerSourceGeneration = 4 } };
        Assert.Equal(ControlOperationStatus.Blocked,
            (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.Equal(curveSends, fixture.Curve.Sent.Count); Assert.Equal(powerSends, fixture.Power.Sent.Count);
    }

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
        Assert.False(applied.CpuTuning!.Live.CanWrite);
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
        // Only BIOS provenance differs between the independently observed families.
        fixture.Power.Context = fixture.Power.Context with { Identity = fixture.Power.Context.Identity with { BiosVersion = "Unknown" } };
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
        Assert.True(applied.CpuTuning!.Live.CanWrite);
        Assert.Equal(Both, applied.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(CpuTuningRecoveryState.RestoreReady, applied.CpuTuning.LiveRecovery.State);
        Assert.Equal(new(null, 45000, 45000), applied.CpuTuning.LiveState!.PowerLimits);
        Assert.Equal(-2, applied.CpuTuning.LiveState.UniformCurveOptimizerValue);
        fixture.Power.OnSend = null; fixture.Curve.OnSend = null;
        fixture.Curve.Context = fixture.Curve.Context with { Identity = fixture.Curve.Context.Identity with { BiosVersion = "2.03" } };
        var restored = await fixture.Controller().MutateLiveAsync(null, true, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.True(restored.CpuTuning!.Live.CanWrite);
        Assert.Equal(curveOriginal, fixture.Curve.Values); Assert.Equal(powerOriginal, fixture.Power.Pair);
        Assert.False(restored.CpuTuning!.RestorePending);
        Assert.Equal(CpuTuningFeatures.None, restored.CpuTuning.LiveRecovery!.Features);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedPowerPeerIsBoundAtCurveSubmissionAndLossNeverSubmits(bool unavailable)
    {
        var fixture = new Fixture();
        fixture.Power.Pair = CpuLivePowerTuningTransactionTests.PairFixture(45000, 45000);
        var expected = fixture.Power.Pair;
        fixture.Power.Admit = (_, _) => false; // Unchanged PPT requires no setter admission.
        fixture.Curve.CheckPeer = peer =>
        {
            Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
            Assert.Equal(expected, peer.Expected); Assert.Null(peer.Original);
            fixture.Power.Unavailable = unavailable;
            if (!unavailable) { fixture.Power.Pair = CpuLivePowerTuningTransactionTests.PairFixture(46000, 45000); }
            return !fixture.Power.Unavailable && fixture.Power.Pair == peer.Expected;
        };
        var result = await fixture.Controller().MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
        Assert.Single(fixture.Curve.Peers); Assert.Null(fixture.PowerStore.Journal);
        Assert.NotEqual(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
    }

    [Fact]
    public async Task CombinedRequestsCarryLatestPeerExpectedAndFirstOriginalAcrossEdits()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        var originalCurve = fixture.Curve.Values.ToArray(); var originalPower = fixture.Power.Pair;
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default)).Status);
        Assert.All(fixture.Power.Sent, request => Assert.Equal(originalCurve, request.CurvePeer!.Expected.CoreOffsets));
        Assert.All(fixture.Curve.Peers, peer =>
        {
            Assert.Equal(45000u, peer.Expected.Fast.Milliwatts); Assert.Equal(45000u, peer.Expected.Slow.Milliwatts);
            Assert.Equal(originalPower, peer.Original);
        });
        fixture.Power.Sent.Clear(); fixture.Curve.Peers.Clear();
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(new(60, new(true, -3)), false, _ => true, default)).Status);
        Assert.All(fixture.Power.Sent, request =>
        {
            Assert.All(request.CurvePeer!.Expected.CoreOffsets, core => Assert.Equal(-2, core.Offset));
            Assert.Equal(originalCurve, request.CurvePeer.Original!.CoreOffsets);
        });
        Assert.All(fixture.Curve.Peers, peer =>
        {
            Assert.Equal(60000u, peer.Expected.Fast.Milliwatts); Assert.Equal(originalPower, peer.Original);
        });
    }

    [Fact]
    public async Task UnchangedPeerWithDifferentRuntimeGenerationCannotBindToCurveStep()
    {
        var fixture = new Fixture();
        fixture.Power.Pair = CpuLivePowerTuningTransactionTests.PairFixture(45000, 45000);
        var lifetime = new CpuLiveRuntimeLifetime(Guid.NewGuid(), 2, 3);
        fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = lifetime };
        fixture.Power.Context = fixture.Power.Context with { RuntimeLifetime = lifetime with { LowPowerGeneration = 4 } };
        var result = await fixture.Controller().MutateLiveAsync(new(45, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Peers); Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Power.Sent);
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
        Assert.False(result.CpuTuning!.Live.CanWrite);
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
        internal Fixture()
        {
            Curve = new(CurveStore); Power = new(PowerStore);
            Curve.CheckPeer = peer => !Power.Unavailable && peer.Expected == Power.Pair;
            Power.CheckPeer = peer => peer.Expected.CoreOffsets.SequenceEqual(Curve.Values);
        }
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
