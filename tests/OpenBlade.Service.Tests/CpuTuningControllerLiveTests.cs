using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CpuTuningControllerLiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeWrapperCarriesExactReadbackAcrossApplyAndRestoreWithoutSkippingPeerChecks(bool receipt)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        if (receipt) { fixture.Curve.CompleteSubmission = (call, _) => CpuLiveCurveTuningTransactionTests.VerifiedResult(call); }
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(new(null, new(false, -1)), false, _ => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Stored, (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.Equal(receipt ? 28 : 52, fixture.Curve.Reads);
        Assert.Equal(30, fixture.Power.Reads); Assert.Equal(24, fixture.Curve.Peers.Count);
        Assert.Equal(24, fixture.Curve.Sent.Count);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, fixture.CurveStore.Journal!.State);
        Assert.All(fixture.Curve.Values, core => Assert.Equal(-1, core.Offset));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeLossAfterNativeReceiptCannotVerifyTheStep(bool cancel)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        using var cancellation = new CancellationTokenSource();
        bool admitted = true;
        fixture.Curve.CompleteSubmission = (call, _) =>
        {
            var result = CpuLiveCurveTuningTransactionTests.VerifiedResult(call);
            if (cancel) { cancellation.Cancel(); } else { admitted = false; }
            return result;
        };
        var result = await controller.MutateLiveAsync(new(null, new(false, -1)), false, _ => admitted, cancellation.Token);
        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Single(fixture.Curve.Sent); Assert.Equal(2, fixture.Curve.Reads);
        Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
        Assert.Equal(0, fixture.CurveStore.Journal.CompletedSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedMutationPreparationDoesNotBlockSameBootOwnedObservation(bool interruptedRestoration)
    {
        var fixture = new Fixture();
        var controller = new CpuTuningController(new UnusedSource(), curveBackend: fixture.Curve,
            curveStore: fixture.CurveStore, powerBackend: fixture.Power, powerStore: fixture.PowerStore,
            readRecoveryBoot: _ => fixture.Curve.Context.BootSequence, recoveryWorkerIdle: () => true);
        fixture.Curve.OnObserve = _ =>
        {
            fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = controller.CaptureLiveRuntimeLifetime() };
            return Task.FromResult(new CpuLiveCurveBackendObservation(fixture.Curve.Context,
                new(0xFF55, 0x005D000B, 0xD50, fixture.Curve.Values.ToArray())));
        };
        fixture.Power.OnObserve = () => fixture.Power.Context = fixture.Power.Context with
        { RuntimeLifetime = controller.CaptureLiveRuntimeLifetime() };
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(new(null, new(false, -1)), false, _ => true, default)).Status);
        if (interruptedRestoration)
        {
            int restores = 0;
            fixture.Curve.OnSend = command => ++restores == 3
                ? Task.FromResult(CpuLiveCurveSubmission.NotSubmitted) : fixture.Curve.Apply(command);
            Assert.Equal(ControlOperationStatus.Blocked,
                (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        }
        var prior = fixture.CurveStore.Journal;
        Assert.Equal(CpuLiveCurveOwnershipState.OwnedVerified, prior!.State);
        Assert.Equal(interruptedRestoration, prior.Restoring);
        Assert.Equal(interruptedRestoration ? 10 : 12, prior.CompletedSteps);
        int curveReads = fixture.Curve.Reads, powerReads = fixture.Power.Reads, sent = fixture.Curve.Sent.Count;
        var observed = await controller.ObserveLiveAsync(_ => true, default);
        Assert.True(observed.RestorePending); Assert.Equal(CpuTuningRecoveryState.RestoreReady, observed.LiveRecovery!.State);
        Assert.Same(prior, fixture.CurveStore.Journal);
        Assert.Equal(curveReads + 1, fixture.Curve.Reads); Assert.Equal(powerReads + 1, fixture.Power.Reads);
        Assert.Equal(sent, fixture.Curve.Sent.Count);
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("PossibleSubmission")]
    [InlineData("OwnedVerified")]
    [InlineData("Blocked")]
    [InlineData("invalid")]
    public async Task HistoricalPowerOwnershipBlocksEveryLiveOperationWithoutChangingJournal(string state)
    {
        var fixture = new Fixture();
        fixture.PowerStore.Invalid = state == "invalid";
        fixture.PowerStore.Journal = state == "invalid" ? null : CpuPowerTestData.Legacy(Enum.Parse<CpuLivePowerOwnershipState>(state));
        var original = fixture.PowerStore.Journal;
        var controller = fixture.Controller();
        var observed = await controller.ObserveLiveAsync(_ => true, default);
        Assert.Equal(CpuTuningRecoveryState.Blocked, observed.LiveRecovery!.State);
        Assert.True(observed.LiveRecovery.Features.HasFlag(CpuTuningFeatures.PowerLimit));
        Assert.Equal(ControlOperationStatus.Blocked, (await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked, (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.Same(original, fixture.PowerStore.Journal);
        Assert.Equal(0, fixture.Power.Reads); Assert.Equal(0, fixture.Curve.Reads);
        Assert.Empty(fixture.Curve.Sent); Assert.Equal(0, fixture.CurveStore.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LivePowerRequestsAreRejectedBeforeReadsOrOwnership(bool combined)
    {
        var fixture = new Fixture();
        var result = await fixture.Controller().MutateLiveAsync(new(45, combined ? new(true, -2) : null), false, _ => true, default);
        Assert.NotEqual(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(0, fixture.Power.Reads); Assert.Equal(0, fixture.Curve.Reads);
        Assert.Empty(fixture.Curve.Sent); Assert.Null(fixture.CurveStore.Journal);
    }

    [Fact]
    public async Task ReadOnlyPeerDriftDuringPreparationStopsBeforeCurveSetter()
    {
        var fixture = new Fixture();
        fixture.Power.OnObserve = () => { if (fixture.Power.Reads == 2) { fixture.Power.Pair = CpuPowerTestData.PairFixture(99000); } };
        var result = await fixture.Controller().MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
        Assert.NotEqual(ControlOperationStatus.Stored, result.Status);
        Assert.Empty(fixture.Curve.Sent); Assert.Null(fixture.PowerStore.Journal);
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
        Assert.Single(fixture.Curve.Sent);
    }

    [Fact]
    public async Task TerminalUnknownCannotEnterPostBootRecoveryOnTheUncertainController()
    {
        var fixture = new Fixture(); int bootReads = 0;
        var controller = new CpuTuningController(new UnusedSource(), curveBackend: fixture.Curve,
            curveStore: fixture.CurveStore, powerBackend: fixture.Power, powerStore: fixture.PowerStore,
            readRecoveryBoot: _ => { bootReads++; return new(CpuLiveCurveBootSequenceSource.WindowsKuserSharedData, 43); },
            recoveryWorkerIdle: () => true);
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<CpuLiveCurveSubmission>();
        fixture.Curve.OnSend = _ => { cancellation.Cancel(); return completion.Task; };
        var stopped = await controller.MutateLiveAsync(new(null, new(false, -1)), false, _ => true, cancellation.Token);
        Assert.True(stopped.CpuTuning!.RestorePending);
        await Task.Run(() => completion.SetResult(CpuLiveCurveSubmission.Unknown));
        var prior = fixture.CurveStore.Journal;
        int curveReads = fixture.Curve.Reads, powerReads = fixture.Power.Reads;
        var observed = await controller.ObserveLiveAsync(_ => true, default);
        Assert.True(observed.RestorePending); Assert.Equal(CpuTuningRecoveryState.Blocked, observed.LiveRecovery!.State);
        Assert.Same(prior, fixture.CurveStore.Journal);
        Assert.Equal(0, bootReads); Assert.Equal(curveReads, fixture.Curve.Reads); Assert.Equal(powerReads, fixture.Power.Reads);
        Assert.Single(fixture.Curve.Sent);
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
        Assert.Null(result.LiveState); Assert.Empty(fixture.Curve.Sent);
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
        Assert.Empty(fixture.Curve.Admissions); Assert.Empty(fixture.Curve.Sent);
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
        Assert.Empty(fixture.Curve.Sent);
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
        var fixture = new Fixture(); var store = fixture.CurveStore; var backend = fixture.Curve;
        var controller = fixture.Controller();
        Assert.Null(controller.CaptureLiveRuntimeLifetime());
        var lifetimes = new List<CpuLiveRuntimeLifetime>();
        backend.Admit = (_, _) =>
        {
            lifetimes.Add(Assert.IsType<CpuLiveRuntimeLifetime>(controller.CaptureLiveRuntimeLifetime()));
            return true;
        };
        await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default, 2, 3);
        var first = lifetimes[0];
        Assert.All(lifetimes, value => Assert.Equal(first, value));
        Assert.Equal(2, first.LowPowerGeneration); Assert.Equal(3, first.PowerSourceGeneration);
        Assert.Null(controller.CaptureLiveRuntimeLifetime());
        lifetimes.Clear();
        await controller.MutateLiveAsync(null, true, _ => true, default, 2, 3);
        Assert.All(lifetimes, value => Assert.Equal(first, value));
        lifetimes.Clear();
        await controller.MutateLiveAsync(new(null, new(true, -3)), false, _ => true, default, 4, 5);
        Assert.All(lifetimes, value =>
        {
            Assert.Equal(first.ServiceInstance, value.ServiceInstance);
            Assert.NotEqual(first, value);
        });
        controller = fixture.Controller();
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
            (await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default)).Status);
        int curveSends = fixture.Curve.Sent.Count;
        fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = lifetime with { PowerSourceGeneration = 4 } };
        fixture.Power.Context = fixture.Power.Context with { RuntimeLifetime = lifetime with { PowerSourceGeneration = 4 } };
        Assert.Equal(ControlOperationStatus.Blocked,
            (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.Equal(curveSends, fixture.Curve.Sent.Count);
    }

    [Fact]
    public async Task CapturedAdmissionExpiresAndLaterRequestsCannotRenewIt()
    {
        var fixture = new Fixture(); var store = fixture.CurveStore; var backend = fixture.Curve;
        var controller = fixture.Controller();
        var outside = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.CurveOptimizer);
        Func<bool>? first = null;
        backend.Admit = (_, _) =>
        {
            var current = controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.CurveOptimizer);
            Assert.True(current());
            if (first is null) { first = current; }
            return true;
        };
        Assert.False(outside());
        var applied = await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.True(applied.CpuTuning!.Live.CanWrite);
        Assert.False(first!());
        backend.Admit = (_, _) =>
        {
            Assert.False(first!());
            Assert.True(controller.CaptureLiveRuntimeAdmission(CpuTuningFeatures.CurveOptimizer)());
            return true;
        };
        Assert.Equal(ControlOperationStatus.Stored,
            (await controller.MutateLiveAsync(null, true, _ => true, default)).Status);
        Assert.False(first!());
    }

    [Fact]
    public async Task RuntimeRevocationAfterPendingFlushSettlesConfirmedNonSubmission()
    {
        var fixture = new Fixture(); var store = fixture.CurveStore; var backend = fixture.Curve;
        var controller = fixture.Controller();
        bool admitted = true;
        store.BeforeWrite = index => { if (index == 2) { admitted = false; } };
        var result = await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => admitted, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(backend.Sent);
        Assert.Equal(CpuLiveCurveOwnershipState.Restored, store.Journal!.State);
        Assert.Equal(0, store.Journal.CompletedSteps);
        Assert.Equal(CpuTuningRecoveryState.None, result.CpuTuning!.LiveRecovery!.State);
    }

    [Fact]
    public async Task CurveOnlyApplyAndRestoreBindFreshReadOnlyPowerPeerWithoutPowerAuthority()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        var original = fixture.Curve.Values.ToArray();
        bool Admit(CpuTuningFeatures features) => !features.HasFlag(CpuTuningFeatures.PowerLimit);
        // A previous successful Observe is not the peer baseline for this apply.
        await controller.ObserveLiveAsync(Admit, default);
        fixture.Power.Pair = CpuPowerTestData.PairFixture(91023, 80765);
        var applied = await controller.MutateLiveAsync(new(null, new(true, -2)), false, Admit, default);
        Assert.Equal(ControlOperationStatus.Stored, applied.Status);
        Assert.Equal(CpuTuningFeatures.CurveOptimizer, applied.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(12, fixture.Curve.Peers.Count);
        Assert.All(fixture.Curve.Peers, peer => { Assert.Equal(fixture.Power.Pair, peer.Expected); Assert.Null(peer.Original); });
        // Unowned PPT may change between separate operations; restore samples it afresh.
        fixture.Power.Pair = CpuPowerTestData.PairFixture(92034, 81765);
        fixture.Curve.Peers.Clear();
        var restored = await fixture.Controller().MutateLiveAsync(null, true, Admit, default);
        Assert.Equal(ControlOperationStatus.Stored, restored.Status);
        Assert.Equal(original, fixture.Curve.Values);
        Assert.Equal(CpuTuningFeatures.None, restored.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(12, fixture.Curve.Peers.Count);
        Assert.All(fixture.Curve.Peers, peer => { Assert.Equal(fixture.Power.Pair, peer.Expected); Assert.Null(peer.Original); });

        Assert.Null(fixture.PowerStore.Journal);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "unavailable")]
    [InlineData(true, "unavailable")]
    [InlineData(false, "drift")]
    [InlineData(true, "drift")]
    public async Task CurveOnlyPeerLossFailsClosedOnApplyAndRestore(bool restoring, string fault)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        if (restoring)
        {
            Assert.Equal(ControlOperationStatus.Stored,
                (await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default)).Status);
        }
        fixture.Curve.Sent.Clear(); fixture.Curve.Peers.Clear();
        if (fault == "missing")
        {
            controller = new(new UnusedSource(), curveBackend: fixture.Curve, curveStore: fixture.CurveStore);
        }
        else
        {
            // Lose the peer after preparation, at the backend submission boundary.
            fixture.Curve.CheckPeer = peer =>
            {
                Assert.Equal(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
                Assert.Null(peer.Original);
                if (fault == "unavailable") { fixture.Power.Unavailable = true; }
                else { fixture.Power.Pair = fixture.Power.Pair with { StapmRawBits = 0 }; }
                return !fixture.Power.Unavailable && peer.Expected == fixture.Power.Pair;
            };
        }
        var result = await controller.MutateLiveAsync(restoring ? null : new(null, new(true, -2)), restoring, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Sent);
        Assert.Null(fixture.PowerStore.Journal);
        Assert.False(result.CpuTuning!.LiveRecovery!.Features.HasFlag(CpuTuningFeatures.PowerLimit));
        if (fault != "missing")
        {
            Assert.Single(fixture.Curve.Peers);
            Assert.NotEqual(CpuLiveCurveOwnershipState.PossibleSubmission, fixture.CurveStore.Journal!.State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurveOnlyUnavailablePowerPreparationCannotReachSetter(bool restoring)
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        if (restoring)
        {
            Assert.Equal(ControlOperationStatus.Stored,
                (await controller.MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default)).Status);
        }
        fixture.Curve.Sent.Clear(); fixture.Curve.Peers.Clear(); fixture.Power.Unavailable = true;
        var result = await controller.MutateLiveAsync(restoring ? null : new(null, new(true, -2)), restoring, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Sent); Assert.Empty(fixture.Curve.Peers);
        Assert.Null(fixture.PowerStore.Journal);
    }

    [Fact]
    public async Task ReadOnlyPeerDriftDoesNotReopenHistoricalRestoredPowerOwnership()
    {
        var fixture = new Fixture(); var controller = fixture.Controller();
        fixture.PowerStore.Journal = CpuPowerTestData.Legacy(CpuLivePowerOwnershipState.Restored);
        var historical = fixture.PowerStore.Journal;
        fixture.Curve.AfterSend = () => fixture.Power.Pair = fixture.Power.Pair with { StapmRawBits = 0 };
        var result = await controller.MutateLiveAsync(new(null, new(true, -2)), false,
            features => !features.HasFlag(CpuTuningFeatures.PowerLimit), default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(CpuTuningFeatures.CurveOptimizer, result.CpuTuning!.LiveRecovery!.Features);
        Assert.Equal(CpuLiveCurveOwnershipState.Blocked, fixture.CurveStore.Journal!.State);
        Assert.Equal(CpuLivePowerOwnershipState.Restored, fixture.PowerStore.Journal.State);
        Assert.Same(historical, fixture.PowerStore.Journal);

    }

    [Fact]
    public async Task UnchangedPeerWithDifferentRuntimeGenerationCannotBindToCurveStep()
    {
        var fixture = new Fixture();
        fixture.Power.Pair = CpuPowerTestData.PairFixture(45000, 45000);
        var lifetime = new CpuLiveRuntimeLifetime(Guid.NewGuid(), 2, 3);
        fixture.Curve.Context = fixture.Curve.Context with { RuntimeLifetime = lifetime };
        fixture.Power.Context = fixture.Power.Context with { RuntimeLifetime = lifetime with { LowPowerGeneration = 4 } };
        var result = await fixture.Controller().MutateLiveAsync(new(null, new(true, -2)), false, _ => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(fixture.Curve.Peers); Assert.Empty(fixture.Curve.Sent);
    }

    private sealed class Fixture
    {
        internal readonly CpuLiveCurveTuningTransactionTests.MemoryStore CurveStore = new();
        internal readonly CpuPowerTestData.MemoryStore PowerStore = new();
        internal readonly CpuLiveCurveTuningTransactionTests.FakeBackend Curve;
        internal readonly CpuPowerTestData.FakeBackend Power;
        internal Fixture()
        {
            Curve = new(CurveStore); Power = new();
            Curve.CheckPeer = peer => !Power.Unavailable && peer.Expected == Power.Pair;
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
