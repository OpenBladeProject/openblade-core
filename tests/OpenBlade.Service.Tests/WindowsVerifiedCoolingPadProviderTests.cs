using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedCoolingPadProviderTests
{
    [Fact]
    public async Task ObserveAsync_LazilyOpensOnceAndReusesExactPort()
    {
        var port = new FakePort(Success(0x2C), Success(0x31));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot first =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot second =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(DeviceFeatureAccess.ReadOnly, provider.FanObservationAccess);
        Assert.Equal(CoolingPadConnectionState.PresentExact, first.Connection);
        Assert.Equal((byte)0x2C, first.OpaqueFanObservation);
        Assert.True(first.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.None, first.Failure);
        Assert.Equal((byte)0x31, second.OpaqueFanObservation);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(2, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_SuccessfulOpaqueZeroRemainsDistinctFromAbsence()
    {
        var port = new FakePort(Success(0));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.PresentExact, result.Connection);
        Assert.Equal((byte)0, result.OpaqueFanObservation);
        Assert.True(result.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.None, result.Failure);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_ThrottlesHealthyReadsWhileKeepingPortOpen()
    {
        var time = new ManualTimeProvider();
        var port = new FakePort(Success(0x2C), Success(0x31));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            readQueryAdmitted: true,
            timeProvider: time,
            observationInterval: TimeSpan.FromSeconds(15));

        CoolingPadObservationSnapshot first =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot cached =
            await provider.ObserveAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(15));
        CoolingPadObservationSnapshot refreshed =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.True(first.IsFresh);
        Assert.True(cached.IsFresh);
        Assert.Equal((byte)0x2C, cached.OpaqueFanObservation);
        Assert.True(refreshed.IsFresh);
        Assert.Equal((byte)0x31, refreshed.OpaqueFanObservation);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(2, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveFreshAsync_BypassesHealthyReadThrottle()
    {
        var time = new ManualTimeProvider();
        var port = new FakePort(Success(0x2C), Success(0x31));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            readQueryAdmitted: true,
            timeProvider: time,
            observationInterval: TimeSpan.FromSeconds(15));

        CoolingPadObservationSnapshot first =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot forced =
            await provider.ObserveFreshAsync(CancellationToken.None);

        Assert.Equal((byte)0x2C, first.OpaqueFanObservation);
        Assert.True(first.IsFresh);
        Assert.Equal((byte)0x31, forced.OpaqueFanObservation);
        Assert.True(forced.IsFresh);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(2, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveFreshAsync_PreSubmissionExhaustionReopensAndRetriesOnce()
    {
        var firstPort = new FakePort(Failure(HidSessionFailureKind.Exhausted));
        var secondPort = new FakePort(Success(0x2C));
        var factory = new FakeFactory(firstPort, secondPort);
        var discovery = new ScriptedDiscovery(
            CoolingPadConnectionState.PresentExact,
            CoolingPadConnectionState.PresentExact);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            discovery);

        CoolingPadObservationSnapshot result =
            await provider.ObserveFreshAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.PresentExact, result.Connection);
        Assert.Equal((byte)0x2C, result.OpaqueFanObservation);
        Assert.True(result.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.None, result.Failure);
        Assert.Equal(2, discovery.ObserveCount);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.ObserveCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, secondPort.ObserveCount);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent, CoolingPadObservationFailure.None)]
    [InlineData(CoolingPadConnectionState.Ambiguous, CoolingPadObservationFailure.None)]
    [InlineData(
        CoolingPadConnectionState.ObservationFailed,
        CoolingPadObservationFailure.Discovery)]
    public async Task ObserveFreshAsync_ExhaustionRediscoveryStopsWithoutSecondOpen(
        CoolingPadConnectionState replacementConnection,
        CoolingPadObservationFailure expectedFailure)
    {
        var firstPort = new FakePort(Failure(HidSessionFailureKind.Exhausted));
        var factory = new FakeFactory(firstPort);
        var discovery = new ScriptedDiscovery(
            CoolingPadConnectionState.PresentExact,
            replacementConnection);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            discovery);

        CoolingPadObservationSnapshot result =
            await provider.ObserveFreshAsync(CancellationToken.None);

        Assert.Equal(replacementConnection, result.Connection);
        Assert.Equal(expectedFailure, result.Failure);
        Assert.Equal(2, discovery.ObserveCount);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, firstPort.DisposeCount);
    }

    [Fact]
    public async Task ObserveFreshAsync_SecondExhaustionDoesNotRetryAgain()
    {
        var firstPort = new FakePort(Failure(HidSessionFailureKind.Exhausted));
        var secondPort = new FakePort(Failure(HidSessionFailureKind.Exhausted));
        var factory = new FakeFactory(firstPort, secondPort);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot result =
            await provider.ObserveFreshAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.ObservationFailed, result.Connection);
        Assert.False(result.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.Transport, result.Failure);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, secondPort.DisposeCount);
    }

    [Fact]
    public async Task ObserveAsync_ConfirmedAbsenceDoesNotOpenPort()
    {
        var factory = new FakeFactory(new FakePort(Success(0x2C)));
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.Absent));

        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.Absent, result.Connection);
        Assert.Null(result.OpaqueFanObservation);
        Assert.True(result.IsFresh);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ObserveAsync_UnadmittedMachineReportsPresenceWithoutQuerying()
    {
        var port = new FakePort(Success(0x2C));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            readQueryAdmitted: false);

        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(DeviceFeatureAccess.ObservedOnly, provider.FanObservationAccess);
        Assert.Equal(CoolingPadConnectionState.PresentExact, result.Connection);
        Assert.Null(result.OpaqueFanObservation);
        Assert.True(result.IsFresh);
        Assert.Equal(0, factory.OpenCount);
        Assert.Equal(0, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_LowPowerGateBlocksDiscoveryOpenAndQuery()
    {
        var gate = new LowPowerGate();
        Assert.True(gate.Enter());
        var discovery = new FixedDiscovery(CoolingPadConnectionState.PresentExact);
        var port = new FakePort(Success(0x2C));
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            discovery,
            lowPowerGate: gate);

        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.ObservationFailed, result.Connection);
        Assert.Null(result.OpaqueFanObservation);
        Assert.False(result.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.LowPower, result.Failure);
        Assert.Equal(0, discovery.ObserveCount);
        Assert.Equal(0, factory.OpenCount);
        Assert.Equal(0, port.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_QueuedObservationCannotCrossLowPowerBoundary()
    {
        var gate = new LowPowerGate();
        var port = new BlockingPort(0x2C);
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            lowPowerGate: gate);

        Task<CoolingPadObservationSnapshot> first =
            provider.ObserveAsync(CancellationToken.None);
        await port.WaitUntilObservedAsync();
        Task<CoolingPadObservationSnapshot> queued =
            provider.ObserveAsync(CancellationToken.None);

        Assert.True(gate.Enter());
        port.Complete();

        CoolingPadObservationSnapshot firstResult = await first;
        CoolingPadObservationSnapshot queuedResult = await queued;

        Assert.Equal(CoolingPadConnectionState.ObservationFailed, firstResult.Connection);
        Assert.Null(firstResult.OpaqueFanObservation);
        Assert.False(firstResult.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.LowPower, firstResult.Failure);
        Assert.Equal(CoolingPadConnectionState.ObservationFailed, queuedResult.Connection);
        Assert.Null(queuedResult.OpaqueFanObservation);
        Assert.Equal(CoolingPadObservationFailure.LowPower, queuedResult.Failure);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.ObserveCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(CoolingPadConnectionState.Absent)]
    [InlineData(CoolingPadConnectionState.Ambiguous)]
    public async Task ObserveAsync_RefreshRediscoveryDropsPortAndClearsOpaqueValue(
        CoolingPadConnectionState changedConnection)
    {
        var time = new ManualTimeProvider();
        var discovery = new ScriptedDiscovery(
            CoolingPadConnectionState.PresentExact,
            changedConnection);
        var port = new FakePort(Success(0x2C), Success(0x31));
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            discovery,
            readQueryAdmitted: true,
            timeProvider: time,
            observationInterval: TimeSpan.FromSeconds(15));

        CoolingPadObservationSnapshot baseline =
            await provider.ObserveAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(15));
        CoolingPadObservationSnapshot refreshed =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal((byte)0x2C, baseline.OpaqueFanObservation);
        Assert.Equal(changedConnection, refreshed.Connection);
        Assert.Null(refreshed.OpaqueFanObservation);
        Assert.True(refreshed.IsFresh);
        Assert.Equal(1, port.ObserveCount);
        Assert.Equal(1, port.DisposeCount);
        Assert.Equal(2, discovery.ObserveCount);
    }

    [Fact]
    public async Task ObserveAsync_TransientFailureRetainsStaleOpaqueValueAndReopens()
    {
        var firstPort = new FakePort(
            Success(0x2C),
            Failure(HidSessionFailureKind.Timeout));
        var secondPort = new FakePort(Success(0x35));
        var factory = new FakeFactory(firstPort, secondPort);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot baseline =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot failed =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot recovered =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal((byte)0x2C, baseline.OpaqueFanObservation);
        Assert.Equal(CoolingPadConnectionState.ObservationFailed, failed.Connection);
        Assert.Equal((byte)0x2C, failed.OpaqueFanObservation);
        Assert.False(failed.IsFresh);
        Assert.Equal(CoolingPadObservationFailure.Transport, failed.Failure);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal((byte)0x35, recovered.OpaqueFanObservation);
        Assert.True(recovered.IsFresh);
        Assert.Equal(2, factory.OpenCount);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.Canceled, true)]
    [InlineData(HidSessionFailureKind.QueuedCanceled, false)]
    public async Task ObserveAsync_CancellationDropsOnlyPotentiallySubmittedSession(
        HidSessionFailureKind failure,
        bool expectReopen)
    {
        var firstPort = new FakePort(
            Failure(failure),
            Success(0x2C));
        var secondPort = new FakePort(Success(0x31));
        var factory = new FakeFactory(firstPort, secondPort);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot canceled =
            await provider.ObserveAsync(CancellationToken.None);
        CoolingPadObservationSnapshot next =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(CoolingPadObservationFailure.Canceled, canceled.Failure);
        Assert.Equal(
            expectReopen ? (byte)0x31 : (byte)0x2C,
            next.OpaqueFanObservation);
        Assert.Equal(expectReopen ? 2 : 1, factory.OpenCount);
        Assert.Equal(expectReopen ? 1 : 0, firstPort.DisposeCount);
    }

    [Fact]
    public async Task InvalidateAsync_CanceledWhileOperationRunsDoesNotClaimSuccess()
    {
        var port = new BlockingPort(0x2C);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));
        Task<CoolingPadObservationSnapshot> observation =
            provider.ObserveAsync(CancellationToken.None);
        await port.WaitUntilObservedAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.InvalidateAsync(cancellation.Token));

        port.Complete();
        await observation;
        await provider.InvalidateAsync(CancellationToken.None);

        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.ConflictBlocked, CoolingPadObservationFailure.Conflict)]
    [InlineData(HidSessionFailureKind.IdentityBlocked, CoolingPadObservationFailure.IdentityOrAccess)]
    [InlineData(HidSessionFailureKind.ProtocolFailure, CoolingPadObservationFailure.Protocol)]
    public async Task ObserveAsync_MapsOpenFailureWithoutFabricatingObservation(
        HidSessionFailureKind failure,
        CoolingPadObservationFailure expected)
    {
        var factory = new FakeFactory(failure);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(CoolingPadConnectionState.ObservationFailed, result.Connection);
        Assert.Null(result.OpaqueFanObservation);
        Assert.False(result.IsFresh);
        Assert.Equal(expected, result.Failure);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task InvalidateAsync_DisposesHandleAndNextObservationReopens()
    {
        var firstPort = new FakePort(Success(0x20));
        var secondPort = new FakePort(Success(0x21));
        var factory = new FakeFactory(firstPort, secondPort);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));

        await provider.ObserveAsync(CancellationToken.None);
        await provider.InvalidateAsync(CancellationToken.None);
        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal((byte)0x21, result.OpaqueFanObservation);
    }

    [Fact]
    public async Task DisposeAsync_DisposesOwnedPortOnce()
    {
        var port = new FakePort(Success(0x2C));
        var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            new FixedDiscovery(CoolingPadConnectionState.PresentExact));
        await provider.ObserveAsync(CancellationToken.None);

        await provider.DisposeAsync();
        await provider.DisposeAsync();
        CoolingPadObservationSnapshot result =
            await provider.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, port.DisposeCount);
        Assert.Equal(CoolingPadObservationFailure.Disposed, result.Failure);
    }

    [Fact]
    public async Task DormantFanBackend_ReusesOneProviderOwnedSessionForReadAcquireUpdateRelease()
    {
        var port = new FakePort(Success(0x00));
        var executor = new FakeHostControlExecutor();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createFanExecutor: candidate =>
                ReferenceEquals(candidate, port) ? executor : null);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        CoolingPadObservationSnapshot observed =
            await provider.ObserveFreshAsync(CancellationToken.None);
        await backend.AcquireTargetAsync(
            new CoolingPadFanTarget(2_200),
            CancellationToken.None);
        await backend.UpdateTargetAsync(
            new CoolingPadFanTarget(2_250),
            CancellationToken.None);
        await backend.HandbackAsync(CancellationToken.None);

        Assert.Equal((byte)0, observed.OpaqueFanObservation);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(
            ["acquire:2200", "update:2250", "release"],
            executor.Operations);
    }

    [Theory]
    [InlineData(true, WriteSubmissionState.KnownNotApplied)]
    [InlineData(false, WriteSubmissionState.Indeterminate)]
    public async Task DormantFanBackend_IdentityLossDistinguishesAcquireFromOwnedUpdate(
        bool acquire,
        WriteSubmissionState expectedState)
    {
        var port = new FakePort(Success(0));
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            new FixedDiscovery(CoolingPadConnectionState.Absent),
            createFanExecutor: _ => new FakeHostControlExecutor());
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => acquire
                ? backend.AcquireTargetAsync(
                    new CoolingPadFanTarget(2_200),
                    CancellationToken.None)
                : backend.UpdateTargetAsync(
                    new CoolingPadFanTarget(2_250),
                    CancellationToken.None));

        Assert.Equal(expectedState, failure.SubmissionState);
        Assert.Equal(0, port.ObserveCount);
    }

    [Fact]
    public async Task DormantFanBackend_IndeterminateWriteDropsTheSharedPortWithoutRetry()
    {
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor
        {
            Failure = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "unknown"),
        };
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createFanExecutor: _ => executor);
        await provider.ObserveFreshAsync(CancellationToken.None);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => backend.UpdateTargetAsync(
                new CoolingPadFanTarget(2_250),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(["update:2250"], executor.Operations);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task DormantFanBackend_PreSubmissionExhaustionDropsPortWithoutRetry()
    {
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor
        {
            Failure = new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "exhausted")
            {
                FailureKind = HidSessionFailureKind.Exhausted,
            },
        };
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createFanExecutor: _ => executor);
        await provider.ObserveFreshAsync(CancellationToken.None);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => backend.UpdateTargetAsync(
                new CoolingPadFanTarget(2_250),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(HidSessionFailureKind.Exhausted, failure.FailureKind);
        Assert.Equal(["update:2250"], executor.Operations);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task DormantFanBackend_OwnedControllerConflictBecomesIndeterminate()
    {
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor
        {
            Failure = new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "controller conflict")
            {
                FailureKind = HidSessionFailureKind.ConflictBlocked,
            },
        };
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            new FakeFactory(port),
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createFanExecutor: _ => executor);
        await provider.ObserveFreshAsync(CancellationToken.None);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => backend.UpdateTargetAsync(
                new CoolingPadFanTarget(2_250),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(["update:2250"], executor.Operations);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task DormantFanBackend_LowPowerBlocksBeforeDiscoveryOpenOrSubmission()
    {
        var lowPowerGate = new LowPowerGate();
        Assert.True(lowPowerGate.Enter());
        var discovery = new FixedDiscovery(CoolingPadConnectionState.PresentExact);
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            discovery,
            lowPowerGate: lowPowerGate,
            createFanExecutor: _ => executor);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => backend.AcquireTargetAsync(
                new CoolingPadFanTarget(2_200),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(0, discovery.ObserveCount);
        Assert.Equal(0, factory.OpenCount);
        Assert.Empty(executor.Operations);
    }

    [Fact]
    public async Task DormantFanBackend_LowPowerAllowsOnlyOwnedHandbackOnOpenSession()
    {
        var lowPowerGate = new LowPowerGate();
        var discovery = new FixedDiscovery(CoolingPadConnectionState.PresentExact);
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            discovery,
            lowPowerGate: lowPowerGate,
            createFanExecutor: _ => executor);
        await provider.ObserveFreshAsync(CancellationToken.None);
        var backend = (ICoolingPadFanSemanticBackend)provider;
        Assert.True(lowPowerGate.Enter());

        await backend.HandbackForLifecycleAsync(CancellationToken.None);

        Assert.Equal(["release"], executor.Operations);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, discovery.ObserveCount);
    }

    [Fact]
    public async Task DormantFanBackend_LowPowerLifecycleHandbackDoesNotOpenSession()
    {
        var lowPowerGate = new LowPowerGate();
        Assert.True(lowPowerGate.Enter());
        var port = new FakePort(Success(0));
        var executor = new FakeHostControlExecutor();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            lowPowerGate: lowPowerGate,
            createFanExecutor: _ => executor);
        var backend = (ICoolingPadFanSemanticBackend)provider;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(
            () => backend.HandbackForLifecycleAsync(CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.Equal(0, factory.OpenCount);
        Assert.Empty(executor.Operations);
    }

    [Fact]
    public async Task DormantLightingSink_ReusesTheProviderOwnedReadSession()
    {
        var port = new FakePort(Success(0));
        var lighting = new FakeLightingSink();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createLightingExecutor: candidate =>
                ReferenceEquals(candidate, port) ? lighting : null);

        await provider.ObserveFreshAsync(CancellationToken.None);
        ICoolingPadLightingSessionOwner owner = provider.CreateLightingSessionOwner();
        CoolingPadLightingBrightnessObservation queried =
            await owner.QueryBrightnessAsync(CancellationToken.None);
        CoolingPadLightingWriteOutcome frame = await owner.SubmitFrameAsync(
            SolidLightingSlots(new RgbColor(0x12, 0x34, 0x56)),
            CancellationToken.None);
        CoolingPadLightingWriteOutcome brightness = await owner.SetBrightnessAsync(
            50,
            CancellationToken.None);
        CoolingPadLightingWriteOutcome rawBrightness =
            await owner.SetBrightnessRawAsync(0x80, CancellationToken.None);

        Assert.Equal(CoolingPadLightingWriteOutcome.Success, frame);
        Assert.Equal(CoolingPadLightingWriteOutcome.Success, brightness);
        Assert.Equal(CoolingPadLightingWriteOutcome.Success, rawBrightness);
        Assert.Equal(CoolingPadLightingWriteOutcome.Success, queried.Outcome);
        Assert.Equal((byte)0xFF, queried.Raw);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(
            [
                "query-brightness",
                "frame:123456",
                "brightness:50",
                "brightness-raw:128",
            ],
            lighting.Operations);
    }

    [Fact]
    public async Task DormantLightingSink_IndeterminateWriteDropsSharedPortWithoutRetry()
    {
        var port = new FakePort(Success(0));
        var lighting = new FakeLightingSink
        {
            Failure = CoolingPadLightingWriteOutcome.Indeterminate,
        };
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            createLightingExecutor: _ => lighting);
        await provider.ObserveFreshAsync(CancellationToken.None);
        ICoolingPadLightingSessionOwner owner = provider.CreateLightingSessionOwner();

        CoolingPadLightingWriteOutcome result = await owner.SubmitFrameAsync(
            SolidLightingSlots(new RgbColor(1, 2, 3)),
            CancellationToken.None);

        Assert.Equal(CoolingPadLightingWriteOutcome.Indeterminate, result);
        Assert.Equal(["frame:010203"], lighting.Operations);
        Assert.Equal(1, port.DisposeCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task DormantLightingSink_LowPowerAllowsOnlyRawLifecycleRestoreOnOpenSession()
    {
        var lowPowerGate = new LowPowerGate();
        var port = new FakePort(Success(0));
        var lighting = new FakeLightingSink();
        var factory = new FakeFactory(port);
        await using var provider = new WindowsVerifiedCoolingPadProvider(
            factory,
            new FixedDiscovery(CoolingPadConnectionState.PresentExact),
            lowPowerGate: lowPowerGate,
            createLightingExecutor: _ => lighting);
        await provider.ObserveFreshAsync(CancellationToken.None);
        ICoolingPadLightingSessionOwner owner = provider.CreateLightingSessionOwner();
        Assert.True(lowPowerGate.Enter());

        CoolingPadLightingWriteOutcome blocked = await owner.SetBrightnessRawAsync(
            0x40,
            CancellationToken.None);
        CoolingPadLightingWriteOutcome restored =
            await owner.SetBrightnessRawForLifecycleAsync(
                0x80,
                CancellationToken.None);

        Assert.Equal(CoolingPadLightingWriteOutcome.KnownNotApplied, blocked);
        Assert.Equal(CoolingPadLightingWriteOutcome.Success, restored);
        Assert.Equal(["brightness-raw:128"], lighting.Operations);
        Assert.Equal(1, factory.OpenCount);
    }

    private static RgbColor[] SolidLightingSlots(RgbColor color) =>
        Enumerable.Repeat(color, 18).ToArray();

    private static HidSessionResult<CoolingPadFanObservation> Success(byte rawByte) =>
        new(
            new CoolingPadFanObservation(rawByte),
            null);

    private static HidSessionResult<CoolingPadFanObservation> Failure(
        HidSessionFailureKind failure) => new(default, failure);

    private sealed class FixedDiscovery(CoolingPadConnectionState state)
        : ICoolingPadDiscoverySource
    {
        internal int ObserveCount { get; private set; }

        public CoolingPadConnectionState Observe()
        {
            ObserveCount++;
            return state;
        }
    }

    private sealed class ScriptedDiscovery(
        params CoolingPadConnectionState[] states) : ICoolingPadDiscoverySource
    {
        internal int ObserveCount { get; private set; }

        public CoolingPadConnectionState Observe()
        {
            int index = Math.Min(ObserveCount++, states.Length - 1);
            return states[index];
        }
    }

    private sealed class FakeFactory : IProductionCoolingPadPortFactory
    {
        private readonly Queue<HidSessionResult<IProductionCoolingPadPort>> results;

        internal FakeFactory(params IProductionCoolingPadPort[] ports)
        {
            results = new Queue<HidSessionResult<IProductionCoolingPadPort>>(
                ports.Select(static port =>
                    new HidSessionResult<IProductionCoolingPadPort>(port, null)));
        }

        internal FakeFactory(HidSessionFailureKind failure)
        {
            results = new Queue<HidSessionResult<IProductionCoolingPadPort>>(
                [new HidSessionResult<IProductionCoolingPadPort>(null, failure)]);
        }

        internal int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionCoolingPadPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class BlockingPort(byte value) : IProductionCoolingPadPort
    {
        private readonly TaskCompletionSource observed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource complete = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ObserveCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public async Task<HidSessionResult<CoolingPadFanObservation>> ObserveFanAsync(
            CancellationToken cancellationToken)
        {
            ObserveCount++;
            observed.TrySetResult();
            await complete.Task.WaitAsync(cancellationToken);
            return Success(value);
        }

        internal Task WaitUntilObservedAsync() => observed.Task;

        internal void Complete() => complete.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePort(
        params HidSessionResult<CoolingPadFanObservation>[] results)
        : IProductionCoolingPadPort
    {
        private readonly Queue<HidSessionResult<CoolingPadFanObservation>> results =
            new(results);

        internal int ObserveCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public Task<HidSessionResult<CoolingPadFanObservation>> ObserveFanAsync(
            CancellationToken cancellationToken)
        {
            ObserveCount++;
            return Task.FromResult(results.Dequeue());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHostControlExecutor
        : ICoolingPadHostControlExecutorAdapter
    {
        internal List<string> Operations { get; } = [];

        internal Exception? Failure { get; init; }

        public Task AcquireAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
        {
            Operations.Add($"acquire:{target.Rpm}");
            return Complete();
        }

        public Task UpdateAsync(
            CoolingPadFanTarget target,
            CancellationToken cancellationToken)
        {
            Operations.Add($"update:{target.Rpm}");
            return Complete();
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            Operations.Add("release");
            return Complete();
        }

        private Task Complete() => Failure is null
            ? Task.CompletedTask
            : Task.FromException(Failure);
    }

    private sealed class FakeLightingSink : ICoolingPadLightingExecutorAdapter
    {
        internal List<string> Operations { get; } = [];

        internal CoolingPadLightingWriteOutcome Failure { get; init; }

        public Task<CoolingPadLightingBrightnessObservation> QueryBrightnessAsync(
            CancellationToken cancellationToken)
        {
            Operations.Add("query-brightness");
            return Task.FromResult(Failure == CoolingPadLightingWriteOutcome.Success
                ? new CoolingPadLightingBrightnessObservation(
                    Raw: 0xFF,
                    CoolingPadLightingWriteOutcome.Success)
                : new CoolingPadLightingBrightnessObservation(Raw: null, Failure));
        }

        public Task<CoolingPadLightingWriteOutcome> SubmitFrameAsync(
            IReadOnlyList<RgbColor> slots,
            CancellationToken cancellationToken)
        {
            RgbColor color = slots[0];
            Operations.Add($"frame:{color.Red:X2}{color.Green:X2}{color.Blue:X2}");
            return Task.FromResult(Failure);
        }

        public Task<CoolingPadLightingWriteOutcome> SetBrightnessAsync(
            byte percent,
            CancellationToken cancellationToken)
        {
            Operations.Add($"brightness:{percent}");
            return Task.FromResult(Failure);
        }

        public Task<CoolingPadLightingWriteOutcome> SetBrightnessRawAsync(
            byte raw,
            CancellationToken cancellationToken)
        {
            Operations.Add($"brightness-raw:{raw}");
            return Task.FromResult(Failure);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
