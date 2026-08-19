using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadLightingMirrorTests
{
    [Fact]
    public void UnresolvedMapper_NeverInventsPhysicalSlotOrder()
    {
        var keyboard = new AmbientLightingFrame(
            new RgbColor[AmbientLightingFrame.SlotCount]);

        bool mapped = UnresolvedCoolingPadKeyboardMirrorMapper.Instance.TryMap(
            keyboard,
            out CoolingPadLightingFrame frame);

        Assert.False(mapped);
        Assert.Null(frame);
        Assert.Equal(
            CoolingPadMirrorMapAvailability.PhysicalSlotOrderUnvalidated,
            UnresolvedCoolingPadKeyboardMirrorMapper.Instance.Availability);
        Assert.Equal(
            "PhysicalSlotOrderUnvalidated",
            UnresolvedCoolingPadKeyboardMirrorMapper.Instance.Reason);
    }

    [Fact]
    public void UnavailableObserver_HasNoActiveSinkOrSideEffect()
    {
        var frame = new AmbientLightingFrame(
            new RgbColor[AmbientLightingFrame.SlotCount]);

        UnavailableKeyboardLightingFrameObserver.Instance.TryPublish(frame);

        Assert.False(UnavailableKeyboardLightingFrameObserver.Instance.IsInterested);
    }

    [Fact]
    public void CreateWindows_ReturnsInertUnavailableCoordinator()
    {
        ICoolingPadLightingCoordinator coordinator =
            CoolingPadLightingCoordinatorFactory.CreateWindows();
        var frame = new AmbientLightingFrame(
            new RgbColor[AmbientLightingFrame.SlotCount]);

        coordinator.TryPublish(frame);

        Assert.Equal(DeviceFeatureAccess.ObservedOnly, coordinator.LightingSyncAccess);
        Assert.Equal(CoolingPadLightingSyncState.Unavailable, coordinator.State);
        Assert.False(coordinator.Admission.WriteAdmitted);
        Assert.True(coordinator.Admission.Evidence.ExactIdentityAndGeometryReviewed);
        Assert.True(coordinator.Admission.Evidence.WholeStripProjectionResolved);
        Assert.True(coordinator.Admission.Evidence.DisplayOffOracleObserved);
        Assert.False(
            coordinator.Admission.Evidence.OpenBladeReversibleFrameLifecycleValidated);
        Assert.False(coordinator.Admission.Evidence.FrameCadenceValidated);
        Assert.False(coordinator.Admission.Evidence.DisconnectReconnectValidated);
        Assert.False(coordinator.Admission.Evidence.SleepResumeValidated);
        Assert.False(coordinator.Admission.Evidence.ControllerHandoffValidated);
        Assert.Equal(
            CoolingPadMirrorMapAvailability.Resolved,
            coordinator.Admission.MapAvailability);
        Assert.False(coordinator.Admission.IsAdmitted);
        Assert.Equal("LightingWriteUnadmitted", coordinator.Reason);
        Assert.False(coordinator.IsInterested);
        Assert.Equal(
            new CoolingPadLightingCoordinatorSnapshot(
                DeviceFeatureAccess.ObservedOnly,
                Enabled: false,
                CoolingPadLightingSyncState.Unavailable,
                coordinator.Reason),
            coordinator.Snapshot);
    }

    [Fact]
    public void CreateWindows_WithSharedSinkStillReturnsInertCoordinator()
    {
        var sink = new ThrowingSink();

        ICoolingPadLightingCoordinator coordinator =
            CoolingPadLightingCoordinatorFactory.CreateWindows(sink);
        coordinator.TryPublish(new AmbientLightingFrame(
            new RgbColor[AmbientLightingFrame.SlotCount]));

        Assert.IsType<UnavailableCoolingPadLightingCoordinator>(coordinator);
        Assert.False(coordinator.IsInterested);
        Assert.False(coordinator.Admission.IsAdmitted);
        Assert.Equal(0, sink.CallCount);
    }

    [Fact]
    public void WholeStripMapper_AveragesOnlyPhysicalKeyboardKeys()
    {
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        int physicalKey = 0;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (AmbientLightingFrame.IsHole(slot))
            {
                continue;
            }

            slots[slot] = physicalKey++ % 2 == 0
                ? new RgbColor(10, 20, 30)
                : new RgbColor(20, 40, 60);
        }

        bool mapped = WholeStripCoolingPadKeyboardMirrorMapper.Instance.TryMap(
            new AmbientLightingFrame(slots),
            out CoolingPadLightingFrame frame);

        Assert.True(mapped);
        Assert.Equal(CoolingPadMirrorMapAvailability.Resolved,
            WholeStripCoolingPadKeyboardMirrorMapper.Instance.Availability);
        Assert.Equal("WholeStripUniformAverage",
            WholeStripCoolingPadKeyboardMirrorMapper.Instance.Reason);
        Assert.All(frame.ToArray(), color => Assert.Equal(new RgbColor(15, 30, 45), color));
    }

    [Fact]
    public void WholeStripMapper_RejectsMalformedKeyboardFrame()
    {
        var malformed = new AmbientLightingFrame(
            new RgbColor[AmbientLightingFrame.SlotCount - 1]);

        bool mapped = WholeStripCoolingPadKeyboardMirrorMapper.Instance.TryMap(
            malformed,
            out CoolingPadLightingFrame frame);

        Assert.False(mapped);
        Assert.Null(frame);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Factory_RequiresWriteAndPhysicalMapAdmission(
        bool writeAdmitted,
        bool mapResolved,
        bool expectedFactoryCall)
    {
        int calls = 0;
        var admitted = new StubCoordinator();
        var mapper = new StubMapper(mapResolved);

        ICoolingPadLightingCoordinator selected =
            CoolingPadLightingCoordinatorFactory.Create(
                writeAdmitted,
                mapper,
                validatedMapper =>
                {
                    Assert.Same(mapper, validatedMapper);
                    calls++;
                    return admitted;
                });

        Assert.Equal(expectedFactoryCall ? 1 : 0, calls);
        if (expectedFactoryCall)
        {
            Assert.Same(admitted, selected);
        }
        else
        {
            Assert.IsType<UnavailableCoolingPadLightingCoordinator>(selected);
        }
        Assert.Equal(expectedFactoryCall, selected.Admission.IsAdmitted);
    }

    [Fact]
    public void Factory_BothGatesWithoutReviewedCoordinator_FailsClosed()
    {
        Assert.Throws<UnvalidatedCommandException>(() =>
            CoolingPadLightingCoordinatorFactory.Create(
                writeAdmitted: true,
                new StubMapper(resolved: true)));
    }

    [Fact]
    public void Factory_RejectsCoordinatorThatDoesNotMatchReviewedAdmission()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CoolingPadLightingCoordinatorFactory.Create(
                writeAdmitted: true,
                new StubMapper(resolved: true),
                _ => new StubCoordinator(admitted: false)));
    }

    private sealed class StubMapper(bool resolved)
        : ICoolingPadKeyboardMirrorMapper
    {
        public CoolingPadMirrorMapAvailability Availability => resolved
            ? CoolingPadMirrorMapAvailability.Resolved
            : CoolingPadMirrorMapAvailability.PhysicalSlotOrderUnvalidated;

        public string Reason => resolved ? "Resolved" : "PhysicalSlotOrderUnvalidated";

        public bool TryMap(
            AmbientLightingFrame keyboard,
            out CoolingPadLightingFrame frame)
        {
            frame = null!;
            return false;
        }
    }

    private sealed class ThrowingSink : ICoolingPadLightingSink
    {
        internal int CallCount { get; private set; }

        public Task<HidSessionResult<CoolingPadBrightnessReading>>
            QueryBrightnessAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The dormant sink was invoked.");
        }

        public Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
            CoolingPadLightingFrame frame,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The dormant sink was invoked.");
        }

        public Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
            byte percent,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The dormant sink was invoked.");
        }

        public Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
            byte raw,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("The dormant sink was invoked.");
        }
    }

    private sealed class StubCoordinator(bool admitted = true)
        : ICoolingPadLightingCoordinator
    {
        public event Action<CoolingPadLightingCoordinatorSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public CoolingPadLightingCoordinatorSnapshot Snapshot => new(
            LightingSyncAccess,
            Enabled: false,
            State,
            Reason);

        public DeviceFeatureAccess LightingSyncAccess => DeviceFeatureAccess.ReadWrite;

        public CoolingPadLightingSyncState State => CoolingPadLightingSyncState.Disabled;

        public CoolingPadLightingAdmission Admission => admitted
            ? new(
                CoolingPadLightingEvidenceGates.UniformForTest(admitted: true),
                CoolingPadMirrorMapAvailability.Resolved)
            : new(
                CoolingPadLightingEvidenceGates.UniformForTest(admitted: false),
                CoolingPadMirrorMapAvailability.PhysicalSlotOrderUnvalidated);

        public string Reason => "Admitted";

        public bool IsInterested => true;

        public bool IsBrightnessInterested => true;

        public void TryPublish(AmbientLightingFrame frame)
        {
        }

        public void TryPublishBrightness(byte percent)
        {
        }

        public Task SetEnabledAsync(bool value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetConnectedAsync(bool value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetSuppressedAsync(bool value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetDelegatedAsync(bool value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task InvalidateAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
