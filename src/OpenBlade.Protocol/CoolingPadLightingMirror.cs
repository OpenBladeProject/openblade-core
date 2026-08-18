using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal interface IKeyboardLightingFrameObserver
{
    bool IsInterested { get; }

    bool IsBrightnessInterested { get; }

    void TryPublish(AmbientLightingFrame frame);

    void TryPublishBrightness(byte percent);
}

internal interface ICoolingPadLightingCoordinator
    : IKeyboardLightingFrameObserver
{
    event Action<CoolingPadLightingCoordinatorSnapshot>? SnapshotChanged;

    CoolingPadLightingCoordinatorSnapshot Snapshot { get; }

    DeviceFeatureAccess LightingSyncAccess { get; }

    CoolingPadLightingSyncState State { get; }

    CoolingPadLightingAdmission Admission { get; }

    string Reason { get; }

    Task SetEnabledAsync(bool value, CancellationToken cancellationToken);

    Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken);

    Task SetConnectedAsync(bool value, CancellationToken cancellationToken);

    Task SetSuppressedAsync(bool value, CancellationToken cancellationToken);

    Task SetDelegatedAsync(bool value, CancellationToken cancellationToken);

    Task InvalidateAsync(CancellationToken cancellationToken);
}

internal readonly record struct CoolingPadLightingCoordinatorSnapshot(
    DeviceFeatureAccess Access,
    bool Enabled,
    CoolingPadLightingSyncState State,
    string Reason);

internal readonly record struct CoolingPadLightingAdmission(
    CoolingPadLightingEvidenceGates Evidence,
    CoolingPadMirrorMapAvailability MapAvailability)
{
    internal bool WriteAdmitted => Evidence.WriteAdmitted;

    internal bool IsAdmitted =>
        WriteAdmitted && MapAvailability == CoolingPadMirrorMapAvailability.Resolved;
}

internal readonly record struct CoolingPadLightingEvidenceGates(
    bool ExactIdentityAndGeometryReviewed,
    bool WholeStripProjectionResolved,
    bool DisplayOffOracleObserved,
    bool OpenBladeReversibleFrameLifecycleValidated,
    bool FrameCadenceValidated,
    bool DisconnectReconnectValidated,
    bool SleepResumeValidated,
    bool ControllerHandoffValidated)
{
    internal bool WriteAdmitted =>
        ExactIdentityAndGeometryReviewed
        && WholeStripProjectionResolved
        && DisplayOffOracleObserved
        && OpenBladeReversibleFrameLifecycleValidated
        && FrameCadenceValidated
        && DisconnectReconnectValidated
        && SleepResumeValidated
        && ControllerHandoffValidated;

    internal static CoolingPadLightingEvidenceGates CurrentObservedWholeStrip =>
        CoolingPadAccessoryAdmissionSource.ReadLightingGates();

    internal static CoolingPadLightingEvidenceGates UniformForTest(bool admitted) => new(
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted,
        admitted);
}

internal sealed class UnavailableCoolingPadLightingCoordinator(
    CoolingPadLightingAdmission admission,
    string reason)
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

    public DeviceFeatureAccess LightingSyncAccess => DeviceFeatureAccess.ObservedOnly;

    public CoolingPadLightingSyncState State => CoolingPadLightingSyncState.Unavailable;

    public CoolingPadLightingAdmission Admission { get; } = admission;

    public string Reason { get; } = string.IsNullOrWhiteSpace(reason)
        ? throw new ArgumentException("A coordinator reason is required.", nameof(reason))
        : reason;

    public bool IsInterested => false;

    public bool IsBrightnessInterested => false;

    public void TryPublish(AmbientLightingFrame frame)
    {
    }

    public void TryPublishBrightness(byte percent)
    {
    }

    public Task SetEnabledAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value)
        {
            throw new CapabilityUnavailableException(Reason);
        }

        return Task.CompletedTask;
    }

    public Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SetConnectedAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SetSuppressedAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SetDelegatedAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal static class CoolingPadLightingCoordinatorFactory
{
    internal static ICoolingPadLightingCoordinator CreateWindows() => Create(
        CoolingPadLightingEvidenceGates.CurrentObservedWholeStrip,
        WholeStripCoolingPadKeyboardMirrorMapper.Instance);

    internal static ICoolingPadLightingCoordinator CreateWindows(
        ICoolingPadLightingSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        CoolingPadLightingEvidenceGates evidence =
            CoolingPadLightingEvidenceGates.CurrentObservedWholeStrip;
        ICoolingPadKeyboardMirrorMapper mapper =
            WholeStripCoolingPadKeyboardMirrorMapper.Instance;
        return Create(
            evidence,
            mapper,
            validatedMapper => new CoolingPadLightingLifecycleCoordinator(
                new CoolingPadLightingAdmission(
                    evidence,
                    validatedMapper.Availability),
                validatedMapper,
                sink));
    }

    internal static ICoolingPadLightingCoordinator Create(
        bool writeAdmitted,
        ICoolingPadKeyboardMirrorMapper mapper,
        Func<ICoolingPadKeyboardMirrorMapper, ICoolingPadLightingCoordinator>?
            createAdmitted = null) => Create(
                CoolingPadLightingEvidenceGates.UniformForTest(writeAdmitted),
                mapper,
                createAdmitted);

    internal static ICoolingPadLightingCoordinator Create(
        CoolingPadLightingEvidenceGates evidence,
        ICoolingPadKeyboardMirrorMapper mapper,
        Func<ICoolingPadKeyboardMirrorMapper, ICoolingPadLightingCoordinator>?
            createAdmitted = null)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        var admission = new CoolingPadLightingAdmission(
            evidence,
            mapper.Availability);
        if (!admission.IsAdmitted)
        {
            return new UnavailableCoolingPadLightingCoordinator(
                admission,
                admission.WriteAdmitted ? mapper.Reason : "LightingWriteUnadmitted");
        }

        if (createAdmitted is null)
        {
            throw new UnvalidatedCommandException(
                "Cooling-pad lighting admission has no reviewed production coordinator.");
        }

        ICoolingPadLightingCoordinator coordinator = createAdmitted(mapper)
            ?? throw new InvalidOperationException(
                "The admitted cooling-pad lighting coordinator factory returned null.");
        if (coordinator.Admission != admission)
        {
            throw new InvalidOperationException(
                "The admitted cooling-pad lighting coordinator does not match the reviewed admission gates.");
        }

        return coordinator;
    }
}

internal sealed class UnavailableKeyboardLightingFrameObserver
    : IKeyboardLightingFrameObserver
{
    internal static UnavailableKeyboardLightingFrameObserver Instance { get; } = new();

    private UnavailableKeyboardLightingFrameObserver()
    {
    }

    public bool IsInterested => false;

    public bool IsBrightnessInterested => false;

    public void TryPublish(AmbientLightingFrame frame)
    {
    }

    public void TryPublishBrightness(byte percent)
    {
    }
}

internal enum CoolingPadMirrorMapAvailability
{
    PhysicalSlotOrderUnvalidated,
    Resolved,
}

internal interface ICoolingPadKeyboardMirrorMapper
{
    CoolingPadMirrorMapAvailability Availability { get; }

    string Reason { get; }

    bool TryMap(AmbientLightingFrame keyboard, out CoolingPadLightingFrame frame);
}

internal sealed class UnresolvedCoolingPadKeyboardMirrorMapper
    : ICoolingPadKeyboardMirrorMapper
{
    internal static UnresolvedCoolingPadKeyboardMirrorMapper Instance { get; } = new();

    private UnresolvedCoolingPadKeyboardMirrorMapper()
    {
    }

    public CoolingPadMirrorMapAvailability Availability =>
        CoolingPadMirrorMapAvailability.PhysicalSlotOrderUnvalidated;

    public string Reason => "PhysicalSlotOrderUnvalidated";

    public bool TryMap(
        AmbientLightingFrame keyboard,
        out CoolingPadLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        frame = null!;
        return false;
    }
}

/// <summary>
/// Reduces the illuminated keyboard keys to one uniform whole-strip color.
/// Because all eighteen protocol slots receive the same color, this mapping
/// does not depend on the pad's unvalidated physical slot origin or direction.
/// </summary>
internal sealed class WholeStripCoolingPadKeyboardMirrorMapper
    : ICoolingPadKeyboardMirrorMapper
{
    internal static WholeStripCoolingPadKeyboardMirrorMapper Instance { get; } = new();

    private WholeStripCoolingPadKeyboardMirrorMapper()
    {
    }

    public CoolingPadMirrorMapAvailability Availability =>
        CoolingPadMirrorMapAvailability.Resolved;

    public string Reason => "WholeStripUniformAverage";

    public bool TryMap(
        AmbientLightingFrame keyboard,
        out CoolingPadLightingFrame frame)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        if (!keyboard.IsWellFormed())
        {
            frame = null!;
            return false;
        }

        int red = 0;
        int green = 0;
        int blue = 0;
        int keyCount = 0;
        for (int slot = 0; slot < AmbientLightingFrame.SlotCount; slot++)
        {
            if (AmbientLightingFrame.IsHole(slot))
            {
                continue;
            }

            RgbColor color = keyboard.Slots[slot];
            red += color.Red;
            green += color.Green;
            blue += color.Blue;
            keyCount++;
        }

        var wholeStripColor = new RgbColor(
            checked((byte)((red + (keyCount / 2)) / keyCount)),
            checked((byte)((green + (keyCount / 2)) / keyCount)),
            checked((byte)((blue + (keyCount / 2)) / keyCount)));
        frame = new CoolingPadLightingFrame(
            Enumerable.Repeat(
                wholeStripColor,
                CoolingPadLightingFrame.SlotCount).ToArray());
        return true;
    }
}
