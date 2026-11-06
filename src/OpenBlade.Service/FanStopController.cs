using OpenBlade.Contracts;

namespace OpenBlade.Service;

// Stopped is positively identified direct-control Stop, never inferred from
// idle zero RPM. Unknown is a resolved observation with unusable state.
internal enum FanStopObservedState { Unknown, Ordinary, Stopped, Automatic }
internal enum FanStopSubmission { NotSubmitted, Submitted, Uncertain }
internal enum FanStopStartupDisposition { Unknown, Clear, Unresolved }

/// <summary>Fresh backend evidence, never inferred from the selected UI mode.
/// Stop scope currently means the reviewed exact SO6120 USB-C Balanced path.
/// Temperature admission must cover the complete reviewed thermal inputs.</summary>
internal sealed record FanStopAdmission(
    bool ExactDeviceVerified,
    bool ControllersAbsent,
    bool PowerKnown,
    bool SessionAdmitted,
    bool SystemActive,
    bool ProfileInScope,
    bool PowerInScope,
    bool TemperatureSafe,
    bool OwnershipConfirmed,
    bool TransactionResolved)
{
    public bool CanWrite => ExactDeviceVerified && ControllersAbsent && PowerKnown
        && SessionAdmitted && TransactionResolved;
    public bool CanStop => CanWrite && SystemActive && ProfileInScope
        && PowerInScope && TemperatureSafe;
    // Auto cleanup is deliberately independent of Silent/Balanced, temperature,
    // and active-power entry conditions. Backend per-write admission still applies.
    public bool CanReturnToAutomatic => CanWrite && OwnershipConfirmed;
}

/// <summary>The future sole-owner backend must serialize its native mailbox,
/// durably checkpoint ownership before submission, revalidate every native write,
/// and independently observe actual PWM and tach or exact Auto control state.
/// It must not infer Stop from HID RPM, load the Capture runner, or recover by
/// replaying an unresolved journal. No production implementation is admitted.</summary>
internal interface IFanStopBackend
{
    FanStopUnavailableReason UnavailableReason { get; }
    // Command-free, independently established before runtime construction. A
    // future backend must derive this from durable ownership/guardian evidence.
    FanStopStartupDisposition StartupDisposition => FanStopStartupDisposition.Unknown;
    // Collect basic host/controller/session/transaction admission without issuing
    // a mapped mailbox command. ReadState may itself publish A0 and therefore
    // requires this admission just as a mutation does; its backend must recheck
    // the same guard immediately before each native command.
    Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token);
    // Unknown is a completed, resolved command with unusable state. Throwing or
    // cancellation after invocation does not prove that no command was published.
    Task<FanStopObservedState> ReadStateAsync(CancellationToken token);
    Task<FanStopSubmission> SubmitAsync(FanStopMode mode, CancellationToken token);
}

internal sealed class UnavailableFanStopBackend : IFanStopBackend
{
    public static UnavailableFanStopBackend Instance { get; } = new();
    public FanStopUnavailableReason UnavailableReason => FanStopUnavailableReason.RecoveryNotValidated;
    public FanStopStartupDisposition StartupDisposition => FanStopStartupDisposition.Clear;
    public Task<FanStopAdmission> ReadAdmissionAsync(CancellationToken token) =>
        throw new InvalidOperationException("The live fan-stop backend is not admitted.");
    public Task<FanStopObservedState> ReadStateAsync(CancellationToken token) =>
        throw new InvalidOperationException("The live fan-stop backend is not admitted.");
    public Task<FanStopSubmission> SubmitAsync(FanStopMode mode, CancellationToken token) =>
        throw new InvalidOperationException("The live fan-stop backend is not admitted.");
}

/// <summary>Called only while the service operation gate is held. Intent is
/// activation-local: restart must never resurrect Stop from a saved profile.</summary>
internal sealed class FanStopController(IFanStopBackend backend)
{
    internal static readonly TimeSpan ConfirmationBudget = TimeSpan.FromSeconds(5);
    // Same planning horizon as the reviewed Capture Auto phase (AutoLimit),
    // not evidence that a future service backend completes within this duration.
    internal static readonly TimeSpan AutomaticCleanupBudget = TimeSpan.FromSeconds(60);
    internal const string UnavailableMessage =
        "Fan stop is unavailable: the service backend and controller-loss recovery are not validated.";
    private FanStopStatus status = backend.StartupDisposition == FanStopStartupDisposition.Clear
        ? new(State: FanStopState.Inactive, UnavailableReason: FanStopUnavailableReason.None)
        : new(State: FanStopState.RecoveryRequired, UnavailableReason: FanStopUnavailableReason.None,
            Message: "Fan-stop startup ownership is unresolved; commands remain fenced.");
    public bool BackendAvailable => backend.UnavailableReason == FanStopUnavailableReason.None;
    public long ConfirmedStopGeneration { get; private set; }
    public bool OwnsStop => status.State == FanStopState.Stopped;
    public bool RequiresRecovery => status.State == FanStopState.RecoveryRequired;
    public bool BlocksOrdinaryThermalWrites => OwnsStop || RequiresRecovery;
    public bool HoldsAutomatic => status.State == FanStopState.Automatic;

    public void RetireAutomaticObservation()
    {
        if (HoldsAutomatic)
        {
            status = new(State: FanStopState.Inactive, UnavailableReason: FanStopUnavailableReason.None);
        }
    }

    public async Task RefreshAsync(bool hasTrialEvidence, Func<bool> stillAdmitted, CancellationToken token)
    {
        if (!hasTrialEvidence || backend.UnavailableReason != FanStopUnavailableReason.None
            || BlocksOrdinaryThermalWrites)
        {
            return;
        }

        bool readStateStarted = false;
        try
        {
            FanStopAdmission admission = await backend.ReadAdmissionAsync(token).ConfigureAwait(false);
            if (!admission.TransactionResolved)
            {
                Fence("A previous fan-stop transaction is unresolved.");
                return;
            }
            if (!admission.CanWrite || !stillAdmitted())
            {
                status = status with { CanStop = false, CanReturnToAutomatic = false };
                return;
            }
            token.ThrowIfCancellationRequested();
            readStateStarted = true;
            FanStopObservedState observation = await backend.ReadStateAsync(token).ConfigureAwait(false);
            readStateStarted = false;
            if (observation == FanStopObservedState.Stopped)
            {
                Fence("Unowned direct fan Stop was observed; ownership cannot be adopted and commands remain fenced.");
                return;
            }
            if (observation != FanStopObservedState.Automatic)
            {
                RetireAutomaticObservation();
            }
            status = status with
            {
                // A matching baseline never establishes ownership or confirms an
                // earlier Auto operation. It only allows a fresh Stop request.
                CanStop = admission.CanStop && stillAdmitted()
                    && observation is FanStopObservedState.Ordinary or FanStopObservedState.Automatic,
                CanReturnToAutomatic = false,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (readStateStarted)
            {
                Fence("Fan-stop observation was canceled after its command may have started.");
            }
            throw;
        }
        catch (Exception)
        {
            if (readStateStarted)
            {
                Fence("Fan-stop observation failed after its command may have started.");
            }
            else
            {
                status = status with { CanStop = false, CanReturnToAutomatic = false };
            }
        }
    }

    public FanStopStatus GetStatus(bool hasTrialEvidence)
    {
        FanStopStatus current = Volatile.Read(ref status);
        if (current.State == FanStopState.RecoveryRequired || current.State == FanStopState.Stopped)
        {
            return current;
        }

        if (!hasTrialEvidence)
        {
            return new(Message: "Fan stop has no reviewed evidence for this device.");
        }

        return backend.UnavailableReason == FanStopUnavailableReason.None ? current
            : new(UnavailableReason: backend.UnavailableReason, Message: UnavailableMessage);
    }

    public async Task<ControlOperationResult> SetModeAsync(
        FanStopMode mode, bool hasTrialEvidence, FanControlSettings normalFanControl,
        Func<bool> stillAdmitted, CancellationToken token, Func<bool>? stopEntryAdmitted = null,
        Func<bool>? requestAdmitted = null)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        token.ThrowIfCancellationRequested();
        FanStopStatus current = GetStatus(hasTrialEvidence);
        if (current.State == FanStopState.Unavailable)
        {
            return Blocked(current.Message!);
        }

        if (RequiresRecovery)
        {
            return Blocked(status.Message!);
        }

        if (!stillAdmitted() || requestAdmitted?.Invoke() == false)
        {
            return Blocked("The active service control context changed.");
        }
        if (mode == FanStopMode.Stop && stopEntryAdmitted?.Invoke() == false)
        {
            return Blocked("Fan-stop entry is closed by the current service lifecycle or configuration.");
        }

        if (mode == FanStopMode.Automatic && !OwnsStop)
        {
            return new(ControlOperationStatus.Stored, "No fan-stop ownership needs Auto handback.");
        }

        FanStopAdmission admission;
        FanStopObservedState observation;
        bool readStateStarted = false;
        try
        {
            admission = await backend.ReadAdmissionAsync(token).ConfigureAwait(false);
            if (!admission.TransactionResolved)
            {
                return Fence("A previous fan-stop transaction is unresolved.");
            }
            if (!admission.CanWrite || !stillAdmitted() || requestAdmitted?.Invoke() == false
                || mode == FanStopMode.Stop && stopEntryAdmitted?.Invoke() == false)
            {
                status = status with { CanStop = false, CanReturnToAutomatic = false };
                return Blocked("Fresh device, controller, power and session admission is required before fan-stop observation.");
            }

            token.ThrowIfCancellationRequested();
            readStateStarted = true;
            observation = await backend.ReadStateAsync(token).ConfigureAwait(false);
            readStateStarted = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (readStateStarted)
            {
                Fence("Fan-stop observation was canceled after its command may have started.");
            }
            throw;
        }
        catch (Exception)
        {
            return readStateStarted || OwnsStop ? Fence("Fan-stop state could not be independently observed; its command may be unresolved.")
                : Blocked("Fan-stop admission could not be observed.");
        }
        if (OwnsStop && (observation != FanStopObservedState.Stopped || !admission.OwnershipConfirmed))
        {
            return Fence("Fan-stop ownership no longer matches independent readback.");
        }

        if (!OwnsStop && observation == FanStopObservedState.Stopped)
        {
            return Fence("Unowned direct fan Stop was observed; ownership cannot be adopted and commands remain fenced.", normalFanControl);
        }
        if (!OwnsStop && observation == FanStopObservedState.Unknown)
        {
            return Blocked("A known unowned baseline is required; the resolved observation is unusable.");
        }

        // An explicit caller still needs authority before dispatch. Mandatory
        // confirmation and autonomous owned cleanup below use service authority.
        bool admitted = mode == FanStopMode.Stop ? admission.CanStop : admission.CanReturnToAutomatic;
        if (!admitted || !stillAdmitted() || requestAdmitted?.Invoke() == false
            || mode == FanStopMode.Stop && stopEntryAdmitted?.Invoke() == false)
        {
            status = status with { CanStop = false, CanReturnToAutomatic = false };
            return Blocked("Fresh fan-stop device, controller, power, session or thermal admission failed.");
        }
        if (mode == FanStopMode.Stop && OwnsStop)
        {
            status = status with { CanReturnToAutomatic = admission.CanReturnToAutomatic };
            return new(ControlOperationStatus.Applied, "Owned fan stop remains independently confirmed.");
        }

        // From this point any exception can follow submission. Never issue Auto
        // or an alternate write to recover an ambiguous result.
        bool submitted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            submitted = true;
            FanStopSubmission result = await backend.SubmitAsync(mode, token).ConfigureAwait(false);
            if (result == FanStopSubmission.NotSubmitted)
            {
                return Blocked("The backend refused before fan-stop submission.");
            }

            if (result != FanStopSubmission.Submitted)
            {
                return Fence("Fan-stop submission is unresolved; further writes are paused.", normalFanControl);
            }
            // Once submission has resolved successfully, finish its mandatory
            // observation in a separate bounded phase. Caller cancellation cannot
            // skip confirmation; this is no guarantee against a stalled native call.
            using var confirmation = new CancellationTokenSource(ConfirmationBudget);
            FanStopAdmission confirmationAdmission = await backend.ReadAdmissionAsync(confirmation.Token).ConfigureAwait(false);
            if (!confirmationAdmission.CanWrite || !stillAdmitted())
            {
                return Fence("Fan-stop submission resolved, but fresh admission prevents its mandatory observation.", normalFanControl);
            }
            if (mode == FanStopMode.Stop && !confirmationAdmission.OwnershipConfirmed)
            {
                return Fence("Stop submission resolved without independently confirmed backend ownership.", normalFanControl);
            }
            FanStopObservedState confirmed = await backend.ReadStateAsync(confirmation.Token).ConfigureAwait(false);
            FanStopObservedState expected = mode == FanStopMode.Stop
                ? FanStopObservedState.Stopped : FanStopObservedState.Automatic;
            if (confirmed != expected)
            {
                return Fence("Fan-stop submission lacks independent confirmation.", normalFanControl);
            }

            status = new(mode, mode == FanStopMode.Stop ? FanStopState.Stopped : FanStopState.Automatic,
                FanStopUnavailableReason.None, CanStop: false,
                CanReturnToAutomatic: mode == FanStopMode.Stop,
                NormalFanControl: mode == FanStopMode.Stop ? normalFanControl : null,
                Message: mode == FanStopMode.Stop ? "Fan stop independently confirmed." : "Firmware Auto independently confirmed.");
            if (mode == FanStopMode.Stop)
            {
                ConfirmedStopGeneration++;
            }
            submitted = false;
            if (mode == FanStopMode.Stop
                && (token.IsCancellationRequested || stopEntryAdmitted?.Invoke() == false))
            {
                using var cleanup = new CancellationTokenSource(AutomaticCleanupBudget);
                ControlOperationResult? cleanupResult;
                try
                {
                    cleanupResult = await ReconcileAsync(true, stillAdmitted, cleanup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    cleanupResult = Blocked("The independent Auto cleanup budget expired; retained ownership or recovery state requires attention.");
                }
                if (!token.IsCancellationRequested)
                {
                    return cleanupResult ?? Blocked("Fan-stop entry closed after confirmation.");
                }
            }
            token.ThrowIfCancellationRequested();
            return new(ControlOperationStatus.Applied, status.Message!);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (submitted)
            {
                Fence("Fan-stop submission was canceled after it may have started.", normalFanControl);
            }

            throw;
        }
        catch (Exception)
        {
            return Fence("Fan-stop submission failed after it may have started.", normalFanControl);
        }
    }

    public async Task<ControlOperationResult?> ReconcileAsync(
        bool forceAutomatic, Func<bool> stillAdmitted, CancellationToken token)
    {
        if (RequiresRecovery)
        {
            return Blocked(status.Message!);
        }

        if (!OwnsStop)
        {
            return null;
        }

        FanStopAdmission admission;
        try { admission = await backend.ReadAdmissionAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Fence("Owned fan-stop safety admission could not be observed."); }
        if (!admission.TransactionResolved || !admission.OwnershipConfirmed)
        {
            return Fence("Owned fan-stop transaction or ownership is unresolved.");
        }

        if (forceAutomatic || !admission.CanStop)
        {
            return await SetModeAsync(FanStopMode.Automatic, true,
                status.NormalFanControl!, stillAdmitted, token).ConfigureAwait(false);
        }
        // A running controller never treats an old Stop result as current evidence.
        return await SetModeAsync(FanStopMode.Stop, true,
            status.NormalFanControl!, stillAdmitted, token).ConfigureAwait(false);
    }

    private ControlOperationResult Fence(string message, FanControlSettings? normal = null)
    {
        status = status with
        {
            State = FanStopState.RecoveryRequired,
            RequestedMode = status.RequestedMode,
            CanStop = false,
            CanReturnToAutomatic = false,
            NormalFanControl = status.NormalFanControl ?? normal,
            Message = message
        };
        return new(ControlOperationStatus.Blocked, message);
    }

    private static ControlOperationResult Blocked(string message) => new(ControlOperationStatus.Blocked, message);
}
