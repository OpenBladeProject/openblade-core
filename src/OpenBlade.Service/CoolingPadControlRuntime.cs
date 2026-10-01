using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public enum CoolingPadPortFault { AdmissionClosed, Disconnected, ControllerConflict, HyperBoostActive, Rejected, SubmissionUnknown, AdmissionRevoked }
public enum CoolingPadRawControlState { Raw0000, Raw0300, Other }
public sealed record CoolingPadPortOpenResult(ICoolingPadControlPort? Port, CoolingPadPortFault? Fault = null);
public sealed record CoolingPadQueryResult(CoolingPadRawControlState State, CoolingPadPortFault? Fault = null);
public sealed record CoolingPadWriteResult(bool Attempted, bool Acknowledged, CoolingPadPortFault? Fault = null);
public sealed record CoolingPadEntryResult(CoolingPadWriteResult Entry, CoolingPadWriteResult Demand);

internal interface ICoolingPadProductionPortFactory
{
    bool Production { get; }
    bool SupportedHost { get; }
}

public interface ICoolingPadControlPortFactory
{
    Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token);
}

/// <summary>Exact-device, strictly guarded transport. Call admission after the controller guard before every SET.</summary>
public interface ICoolingPadControlPort : IAsyncDisposable
{
    Task<CoolingPadQueryResult> QueryControlStateAsync(CancellationToken token);
    Task<CoolingPadWriteResult> AcquireAsync(Func<bool> submissionAllowed, CancellationToken token);
    // Entry and demand are adjacent. Recheck admission before each SET; stop if entry is not exactly acknowledged.
    Task<CoolingPadEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> submissionAllowed, CancellationToken token);
    Task<CoolingPadWriteResult> SetDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> submissionAllowed, CancellationToken token);
}

internal interface ICoolingPadDeveloperRestorationPort
{
    Task<CoolingPadWriteResult> ReassertMediumAsync(Func<bool> allowed, CancellationToken token);
    Task<CoolingPadWriteResult> ReassertQuietAsync(Func<bool> allowed, CancellationToken token);
    // Success includes the saved raw0000 restore and its matching query.
    Task<CoolingPadWriteResult> RestoreRawAsync(Func<bool> allowed, CancellationToken token);
}

internal sealed record CoolingPadDeveloperRestorationResult
{
    public CoolingPadWriteResult? Medium { get; init; }
    public bool MediumPhysicalConfirmed { get; init; }
    public CoolingPadWriteResult? Quiet { get; init; }
    public bool QuietPhysicalConfirmed { get; init; }
    public CoolingPadWriteResult? RawRestore { get; init; }
    public bool RawRestorationConfirmed { get; init; }
    public bool FinalPhysicalConfirmed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    public bool SubmissionStateUnknown { get; init; }
    public bool Completed => Failure is null && RawRestorationConfirmed && FinalPhysicalConfirmed;
}

public sealed class UnavailableCoolingPadControlPortFactory : ICoolingPadControlPortFactory
{
    public Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new CoolingPadPortOpenResult(null, CoolingPadPortFault.AdmissionClosed));
    }
}

/// <summary>
/// Pad intent and acknowledgement history. Stop never sends firmware cleanup.
/// The caller owns the temperature source; a pending read must finish before its disposal.
/// </summary>
public sealed partial class CoolingPadControlRuntime
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PortTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ICoolingPadControlPortFactory factory;
    private readonly IFanTemperatureSource? temperatures;
    private readonly TimeProvider clock;
    private readonly Func<FanTemperatureReadings, bool> trustworthyInactive;
    private readonly Func<bool> writeAdmission;
    private CoolingPadControlStatus status = new(CoolingPadControlSettings.Stopped,
        CoolingPadControlState.Stopped, CoolingPadControlPauseReason.None);
    private ICoolingPadControlPort? port;
    private Task? portOperation;
    private Task? temperatureRead;
    private Task? pendingCleanup;
    private volatile bool revoked = true;
    private long revocationGeneration;
    private readonly object revocationLock = new();
    private long safeParkGeneration;
    private CoolingPadControlPauseReason safeParkReason;
    private long deniedAdmissionEpoch = -1;
    private bool freshnessRejected;
    private long unsafeRevocationGeneration;
    private long runningGeneration;
    private Func<bool> epochAdmission = () => true;
    private int retainedMutationEvidence;
    private readonly ICoolingPadMutationEvidenceStore evidenceStore;
    private CoolingPadMutationEvidenceSnapshot? armedEvidence;
    private int evidenceUnavailable;

    // Resource release and Stop do not undo a setter. This is session-local
    // evidence for cross-feature admission, not a fan-state or ownership claim.
    internal bool HasRetainedMutationEvidence => Volatile.Read(ref retainedMutationEvidence) != 0
        || Volatile.Read(ref evidenceUnavailable) != 0 || Volatile.Read(ref armedEvidence) is not null;

    public CoolingPadControlRuntime(ICoolingPadControlPortFactory? factory = null,
        IFanTemperatureSource? temperatures = null, TimeProvider? clock = null,
        Func<FanTemperatureReadings, bool>? isGpuInactivityTrustworthy = null,
        Func<bool>? writeAdmission = null)
        : this(new InMemoryCoolingPadMutationEvidenceStore(), factory, temperatures, clock, isGpuInactivityTrustworthy, writeAdmission) { }

    internal CoolingPadControlRuntime(ICoolingPadMutationEvidenceStore evidenceStore,
        ICoolingPadControlPortFactory? factory = null, IFanTemperatureSource? temperatures = null,
        TimeProvider? clock = null, Func<FanTemperatureReadings, bool>? isGpuInactivityTrustworthy = null,
        Func<bool>? writeAdmission = null, bool productionLifecycle = false)
    {
        this.evidenceStore = evidenceStore ?? throw new ArgumentNullException(nameof(evidenceStore));
        this.factory = factory ?? new UnavailableCoolingPadControlPortFactory();
        this.temperatures = temperatures;
        this.clock = clock ?? TimeProvider.System;
        trustworthyInactive = isGpuInactivityTrustworthy ?? (_ => false);
        this.writeAdmission = writeAdmission ?? (() => true);
        this.productionLifecycle = productionLifecycle || factory is ICoolingPadProductionPortFactory { Production: true };
        if (this.productionLifecycle && factory is not ICoolingPadProductionPortFactory { SupportedHost: false })
        { status = status with { Access = DeviceFeatureAccess.ReadWrite }; }
    }

    public CoolingPadControlStatus GetStatus() => Volatile.Read(ref status);

    // Fresh load also notices a separately attended developer process. This is
    // not cross-process exclusion between independent hardware controllers.
    internal async Task<bool> HasBlockingMutationEvidenceAsync(CancellationToken token)
    {
        try
        {
            var loaded = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
            return HasRetainedMutationEvidence || loaded.BlocksAdmission;
        }
        catch { return true; }
    }

    public async Task<CoolingPadControlStatus> ApplySettingsAsync(CoolingPadControlSettings settings, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode == CoolingPadControlMode.Stop) { Revoke(explicitStop: true); }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (productionLifecycle && restorationOwner is null && port is not null && !status.SubmissionStateUnknown
                && (parked || status.State == CoolingPadControlState.Running))
            {
                if (settings.Mode == CoolingPadControlMode.Stop)
                {
                    await CheckpointAsync(CoolingPadMutationPhase.Parked, null, token).ConfigureAwait(false);
                    parked = true;
                }
                status = status with
                {
                    Requested = settings,
                    PlannedDemand = null,
                    State = parked ? CoolingPadControlState.Stopped : status.State,
                    RetainedEffect = true,
                    RetainedSessionResumable = parked,
                    QuietRestorationAvailable = true
                };
                return status;
            }
            if (settings.Mode != CoolingPadControlMode.Stop && status.State == CoolingPadControlState.Running
                && port is not null && Allowed(token))
            {
                status = status with { Requested = settings, PlannedDemand = null };
                return status;
            }
            Revoke();
            await CloseAsync().ConfigureAwait(false);
            status = status with
            {
                Requested = settings,
                PlannedDemand = null,
                State = settings.Mode == CoolingPadControlMode.Stop ? CoolingPadControlState.Stopped : CoolingPadControlState.Paused,
                PauseReason = settings.Mode == CoolingPadControlMode.Stop ? CoolingPadControlPauseReason.None : CoolingPadControlPauseReason.ExplicitResumeRequired,
                ResumeRequired = settings.Mode != CoolingPadControlMode.Stop,
            };
            return status;
        }
        catch (Exception exception) { return await FailAsync(exception).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public Task<CoolingPadControlStatus> StopAsync(CancellationToken token) =>
        ApplySettingsAsync(CoolingPadControlSettings.Stopped, token);

    public async Task<CoolingPadControlStatus> PauseAsync(CoolingPadControlPauseReason reason, CancellationToken token)
    {
        Revoke();
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); return Pause(reason); }
        finally { gate.Release(); }
    }

    // Allows the source owner to defer disposal without blocking service shutdown.
    public async Task DrainAsync()
    {
        Revoke();
        await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseAsync().ConfigureAwait(false); status = status with { State = CoolingPadControlState.Stopped }; }
        finally { gate.Release(); }
        if (temperatureRead is { } reading) { try { await reading.ConfigureAwait(false); } catch { } }
        if (pendingCleanup is { } cleanup) { try { await cleanup.ConfigureAwait(false); } catch { } }
    }

    public async Task<CoolingPadControlStatus> ResumeAsync(CancellationToken token, Func<bool>? sessionAdmission = null)
    {
        long requestedGeneration = RevokeForRetainedEpoch(out bool retainedEpochSafe);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (productionLifecycle && port is not null && !retainedEpochSafe)
            {
                await CloseAsync().ConfigureAwait(false);
                return Pause(CoolingPadControlPauseReason.SessionChanged);
            }
            if (productionLifecycle && (parked || status.State == CoolingPadControlState.Running) && port is not null && status.Requested.Mode != CoolingPadControlMode.Stop)
            {
                var current = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
                if (current != armedEvidence || current.Evidence?.Phase is not (CoolingPadMutationPhase.Parked or CoolingPadMutationPhase.Acknowledged)
                    || requestedGeneration != Volatile.Read(ref revocationGeneration))
                { throw new ControlFailure(CoolingPadControlPauseReason.ExplicitResumeRequired); }
                revoked = false;
                runningGeneration = requestedGeneration;
                epochAdmission = sessionAdmission ?? (() => true);
                await RequireRawAsync(CoolingPadRawControlState.Raw0300, token).ConfigureAwait(false);
                var planned = await PlanAsync(token).ConfigureAwait(false);
                var updated = await PortAsync(() => port.SetDemandAsync(planned.Demand, () => Fresh(planned.Started, token), token), token, acknowledgedDemand: planned.Demand).ConfigureAwait(false);
                RequireWrite(updated);
                parked = false;
                status = status with
                {
                    State = CoolingPadControlState.Running,
                    PauseReason = CoolingPadControlPauseReason.None,
                    LastAcknowledgedDemand = planned.Demand,
                    ResumeRequired = false,
                    RetainedSessionResumable = false
                };
                return status;
            }
            await CloseAsync().ConfigureAwait(false);
            if (status.Requested.Mode == CoolingPadControlMode.Stop) { return status; }
            if (requestedGeneration != Volatile.Read(ref revocationGeneration))
            { return Pause(CoolingPadControlPauseReason.ExplicitResumeRequired); }
            if (pendingCleanup is { IsCompletedSuccessfully: false })
            { return Pause(CoolingPadControlPauseReason.DeviceUnavailable); }
            if (temperatureRead is { IsCompleted: false })
            { return Pause(CoolingPadControlPauseReason.TelemetryUnavailable); }
            var evidence = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
            if (evidence.BlocksAdmission || Volatile.Read(ref evidenceUnavailable) != 0)
            {
                if (productionLifecycle)
                {
                    status = status with
                    {
                        RetainedEffect = true,
                        SubmissionStateUnknown = evidence.Invalid
                    || Volatile.Read(ref evidenceUnavailable) != 0 || evidence.Evidence?.Phase is null or CoolingPadMutationPhase.Pending or CoolingPadMutationPhase.RestorationPending
                    };
                }
                return Pause(CoolingPadControlPauseReason.ExplicitResumeRequired);
            }
            armedEvidence = null;
            revoked = false;
            runningGeneration = requestedGeneration;
            epochAdmission = sessionAdmission ?? (() => true);
            status = status with { PlannedDemand = null };
            var opening = Task.Run(() => factory.OpenAsync(token), CancellationToken.None);
            CoolingPadPortOpenResult opened;
            try { opened = await opening.WaitAsync(PortTimeout, clock, token).ConfigureAwait(false); }
            catch
            {
                pendingCleanup = DisposeLateOpenAsync(opening);
                Retain(pendingCleanup);
                throw;
            }
            port = opened.Port;
            if (opened.Fault is { } fault) { throw new ControlFailure(Map(fault)); }
            if (port is null) { throw new ControlFailure(CoolingPadControlPauseReason.DeviceUnavailable); }
            status = status with { Connection = CoolingPadConnectionState.PresentExact };
            await PlanAsync(token).ConfigureAwait(false);
            await RequireRawAsync(CoolingPadRawControlState.Raw0000, token).ConfigureAwait(false);
            var acquired = await PortAsync(() => port.AcquireAsync(() => Allowed(token), token), token).ConfigureAwait(false);
            RequireWrite(acquired);
            await RequireRawAsync(CoolingPadRawControlState.Raw0300, token).ConfigureAwait(false);
            var first = await PlanAsync(token).ConfigureAwait(false);
            var entry = await PortAsync(() => port.EnterDemandAsync(first.Demand,
                () => Fresh(first.Started, token), token), token, acknowledgedDemand: first.Demand).ConfigureAwait(false);
            RequireWrite(entry.Entry);
            if (!entry.Demand.Attempted && !Fresh(first.Started, token))
            { throw new ControlFailure(Allowed(token) ? CoolingPadControlPauseReason.StaleTelemetry : CoolingPadControlPauseReason.SessionChanged); }
            RequireWrite(entry.Demand);
            status = status with
            {
                LastAcknowledgedDemand = first.Demand,
                State = CoolingPadControlState.Running,
                PauseReason = CoolingPadControlPauseReason.None,
                ResumeRequired = false,
                SubmissionStateUnknown = false,
                RetainedEffect = true,
                QuietRestorationAvailable = productionLifecycle,
            };
            return status;
        }
        catch (Exception exception) { return await FailAsync(exception).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<CoolingPadControlStatus> TickAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (status.State != CoolingPadControlState.Running) { return status; }
            if (revoked || runningGeneration != Volatile.Read(ref revocationGeneration))
            {
                if (await TryParkSafeRevocationAsync().ConfigureAwait(false)) { return status; }
                await CloseAsync().ConfigureAwait(false);
                return Pause(CoolingPadControlPauseReason.ExplicitResumeRequired);
            }
            if (port is null) { return Pause(CoolingPadControlPauseReason.DeviceUnavailable); }
            var sample = await PlanAsync(token).ConfigureAwait(false);
            if (sample.Demand == status.LastAcknowledgedDemand) { return status; }
            var write = await PortAsync(() => port.SetDemandAsync(sample.Demand,
                () => Fresh(sample.Started, token), token), token, acknowledgedDemand: sample.Demand).ConfigureAwait(false);
            if (!write.Attempted && !Fresh(sample.Started, token))
            { throw new ControlFailure(Allowed(token) ? CoolingPadControlPauseReason.StaleTelemetry : CoolingPadControlPauseReason.SessionChanged); }
            RequireWrite(write);
            status = status with { LastAcknowledgedDemand = sample.Demand };
            return status;
        }
        catch (Exception exception) { return await FailAsync(exception).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    // Only the finite developer harness can call this; no IPC or automatic cleanup path.
    // The coordinator owns the port throughout, and serialized ticks cannot interleave.
    internal async Task<CoolingPadDeveloperRestorationResult> RestoreForDeveloperValidationAsync(
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token,
        TimeSpan? operatorTimeout = null)
    {
        var result = new CoolingPadDeveloperRestorationResult();
        string phase = "restorationAdmission";
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (status.State != CoolingPadControlState.Running || !Allowed(token)
                || status.SubmissionStateUnknown || port is not ICoolingPadDeveloperRestorationPort restoration)
            { throw new ControlFailure(CoolingPadControlPauseReason.ExplicitResumeRequired); }

            async Task Observe(string stage)
            {
                phase = stage;
                if (!await CoolingPadDeveloperObservation.ConfirmAsync(confirm, stage, token,
                    operatorTimeout ?? TimeSpan.FromMinutes(10), clock).ConfigureAwait(false))
                { throw new InvalidOperationException("OperatorRejected"); }
                if (!Allowed(token)) { throw new OperationCanceledException(token); }
            }
            phase = "restoreMedium";
            var medium = await PortAsync(() => restoration.ReassertMediumAsync(() => Allowed(token), token), token).ConfigureAwait(false);
            result = result with { Medium = medium };
            RequireWrite(medium);
            await Observe("mediumPhysicalConfirmation").ConfigureAwait(false);
            result = result with { MediumPhysicalConfirmed = true };
            phase = "restoreQuiet";
            var quiet = await PortAsync(() => restoration.ReassertQuietAsync(() => Allowed(token), token), token).ConfigureAwait(false);
            result = result with { Quiet = quiet };
            RequireWrite(quiet);
            await Observe("quietPhysicalConfirmation").ConfigureAwait(false);
            result = result with { QuietPhysicalConfirmed = true };
            phase = "restoreRaw0000";
            var raw = await PortAsync(() => restoration.RestoreRawAsync(() => Allowed(token), token), token).ConfigureAwait(false);
            result = result with { RawRestore = raw };
            RequireWrite(raw);
            result = result with { RawRestorationConfirmed = true };
            await Observe("finalPhysicalConfirmation").ConfigureAwait(false);
            result = result with { FinalPhysicalConfirmed = true };
            phase = "persistAttendedResolution";
            var resolved = await EvidenceAsync(evidenceToken => evidenceStore.ResolveAsync(armedEvidence!, evidenceToken), token).ConfigureAwait(false);
            if (resolved.Invalid || resolved.Evidence?.MutationMayHaveOccurred != false)
            { throw new InvalidDataException("Pad evidence resolution was not confirmed."); }
            armedEvidence = null;
            Volatile.Write(ref retainedMutationEvidence, 0);
        }
        catch (Exception exception)
        {
            result = result with
            {
                FailurePhase = phase,
                Failure = exception is TimeoutException ? "OperatorTimeout" : exception is OperationCanceledException ? "Canceled"
                    : exception is ControlFailure failure ? failure.Reason.ToString()
                    : exception is InvalidOperationException && exception.Message == "OperatorRejected" ? "OperatorRejected" : "OperationFailed",
                SubmissionStateUnknown = status.SubmissionStateUnknown,
            };
        }
        finally
        {
            Revoke();
            await CloseAsync().ConfigureAwait(false);
            status = status with
            {
                State = result.Completed ? CoolingPadControlState.Stopped : CoolingPadControlState.Paused,
                PauseReason = result.Completed ? CoolingPadControlPauseReason.None : CoolingPadControlPauseReason.ExplicitResumeRequired,
                ResumeRequired = !result.Completed,
            };
            gate.Release();
        }
        return result;
    }

    private async Task<(CoolingPadConfiguredRpmDemand Demand, long? Started)> PlanAsync(CancellationToken token)
    {
        planningOnly = true;
        knownUnsubmitted = false;
        freshnessRejected = false;
        if (!Allowed(token)) { throw new OperationCanceledException(token); }
        if (status.Requested.Mode == CoolingPadControlMode.FixedDemand)
        {
            var demand = status.Requested.FixedDemand!.Value;
            status = status with { PlannedDemand = demand };
            return (demand, null);
        }
        if (temperatures is null) { throw new ControlFailure(CoolingPadControlPauseReason.TelemetryUnavailable); }
        long started = clock.GetTimestamp();
        var reading = Task.Run(async () => await temperatures.ReadAsync(token).ConfigureAwait(false), CancellationToken.None);
        temperatureRead = reading;
        Retain(reading);
        FanTemperatureReadings value;
        try { value = await reading.WaitAsync(MaximumAge, clock, token).ConfigureAwait(false); }
        catch (TimeoutException) { throw new ControlFailure(CoolingPadControlPauseReason.StaleTelemetry); }
        catch (OperationCanceledException) { throw; }
        catch { throw new ControlFailure(CoolingPadControlPauseReason.TelemetryUnavailable); }
        if (value.GpuInactive && !trustworthyInactive(value))
        { value = value with { GpuInactive = false, GpuCelsius = null }; }
        var plan = CoolingPadCurvePlanner.Plan(status.Requested.CpuCurve!, status.Requested.GpuCurve!,
            value, clock.GetElapsedTime(started), MaximumAge);
        status = status with { PlannedDemand = plan.Demand };
        if (plan.Demand is not { } planned)
        {
            throw new ControlFailure(plan.Reason == CoolingPadCurveDecisionReason.StaleSample
                ? CoolingPadControlPauseReason.StaleTelemetry : CoolingPadControlPauseReason.TelemetryUnavailable);
        }
        if (!Fresh(started, token)) { throw new ControlFailure(CoolingPadControlPauseReason.StaleTelemetry); }
        return (planned, started);
    }

    private long Revoke(bool explicitStop = false, bool hyperBoostEntry = false, bool healthyPause = false)
    {
        lock (revocationLock)
        {
            revoked = true;
            long generation = Interlocked.Increment(ref revocationGeneration);
            if (explicitStop || hyperBoostEntry || healthyPause)
            {
                safeParkGeneration = generation;
                safeParkReason = hyperBoostEntry ? CoolingPadControlPauseReason.HyperBoostActive : CoolingPadControlPauseReason.ExplicitResumeRequired;
            }
            else { unsafeRevocationGeneration = generation; }
            return generation;
        }
    }

    // Published synchronously before the outer HB epoch changes. This does not
    // infer HB intent from an arbitrary false callback.
    internal void RequestHyperBoostEntryPark() => Revoke(hyperBoostEntry: true);

    private long RevokeForRetainedEpoch(out bool safe)
    {
        lock (revocationLock)
        {
            safe = RetainedEpochHasNoUnsafeRevocation();
            return Revoke();
        }
    }

    private bool RetainedEpochHasNoUnsafeRevocation()
    {
        lock (revocationLock)
        {
            return unsafeRevocationGeneration <= runningGeneration
                && Volatile.Read(ref deniedAdmissionEpoch) != runningGeneration;
        }
    }

    private bool IsSafeParkRevoked()
    {
        lock (revocationLock)
        {
            return revoked && safeParkGeneration == revocationGeneration
                && unsafeRevocationGeneration <= runningGeneration
                && Volatile.Read(ref deniedAdmissionEpoch) != runningGeneration;
        }
    }

    private async Task<bool> TryParkSafeRevocationAsync()
    {
        // Stop/HB entry may revoke before its gate waiter runs. Preserve only a
        // healthy entered epoch; unsafe revocation or observed denial wins.
        return IsSafeParkRevoked()
            && await TryParkAsync(safeParkReason).ConfigureAwait(false)
            && IsSafeParkRevoked();
    }

    private bool Allowed(CancellationToken token)
    {
        long generation = runningGeneration;
        if (revoked || token.IsCancellationRequested || generation != Volatile.Read(ref revocationGeneration)
            || Volatile.Read(ref deniedAdmissionEpoch) == generation) { return false; }
        try
        {
            if (!epochAdmission() || !writeAdmission())
            {
                Interlocked.Exchange(ref deniedAdmissionEpoch, generation);
                return false;
            }
        }
        catch
        {
            Interlocked.Exchange(ref deniedAdmissionEpoch, generation);
            throw;
        }
        // A predicate may wait; revocation during it must deny the queued SET.
        return !revoked && !token.IsCancellationRequested
            && generation == Volatile.Read(ref revocationGeneration)
            && Volatile.Read(ref deniedAdmissionEpoch) != generation;
    }
    private bool Fresh(long? started, CancellationToken token)
    {
        if (!Allowed(token)) { return false; }
        bool fresh = started is null || clock.GetElapsedTime(started.Value) is var age && age >= TimeSpan.Zero && age < MaximumAge;
        if (!fresh) { freshnessRejected = true; }
        return fresh;
    }

    private bool CanParkUnrevokedEpoch()
    {
        try { return Allowed(CancellationToken.None); }
        catch { return false; }
    }

    private async Task<T> PortAsync<T>(Func<Task<T>> operation, CancellationToken token, bool mayWrite = true, CoolingPadConfiguredRpmDemand? acknowledgedDemand = null)
    {
        knownUnsubmitted = false;
        freshnessRejected = false;
        if (!Allowed(token)) { throw new OperationCanceledException(token); }
        if (mayWrite)
        {
            planningOnly = false;
            knownUnsubmitted = false;
            var current = await EvidenceAsync(evidenceStore.LoadAsync, token).ConfigureAwait(false);
            if (current.Invalid || Volatile.Read(ref evidenceUnavailable) != 0)
            { throw new ControlFailure(CoolingPadControlPauseReason.ExplicitResumeRequired); }
            if (armedEvidence is null)
            {
                armedEvidence = await EvidenceAsync(evidenceToken => evidenceStore.ArmAsync(current, evidenceToken), token).ConfigureAwait(false);
            }
            else if (current != armedEvidence)
            { throw new ControlFailure(CoolingPadControlPauseReason.ExplicitResumeRequired); }
            else if (productionLifecycle)
            {
                await CheckpointAsync(restorationOwner is null ? CoolingPadMutationPhase.Pending : CoolingPadMutationPhase.RestorationPending, null, token).ConfigureAwait(false);
            }
            if (armedEvidence.Invalid || armedEvidence.Evidence?.MutationMayHaveOccurred != true)
            { throw new ControlFailure(CoolingPadControlPauseReason.ExplicitResumeRequired); }
            // A completed or late journal operation never revives revoked admission.
            if (!Allowed(token)) { throw new OperationCanceledException(token); }
        }
        var pending = Task.Run(operation, CancellationToken.None);
        portOperation = pending;
        Retain(pending);
        try
        {
            var value = await pending.WaitAsync(PortTimeout, clock, token).ConfigureAwait(false);
            if (mayWrite && productionLifecycle)
            {
                bool acknowledged = value is CoolingPadWriteResult { Attempted: true, Acknowledged: true, Fault: null }
                    || value is CoolingPadEntryResult { Entry: { Attempted: true, Acknowledged: true, Fault: null }, Demand: { Attempted: true, Acknowledged: true, Fault: null } };
                knownUnsubmitted = value is CoolingPadWriteResult { Attempted: false, Acknowledged: false, Fault: CoolingPadPortFault.AdmissionRevoked };
                if (acknowledged || knownUnsubmitted)
                {
                    await CheckpointAsync(restorationOwner is null ? CoolingPadMutationPhase.Acknowledged : CoolingPadMutationPhase.RestorationAcknowledged,
                    restorationOwner is null && acknowledged ? acknowledgedDemand?.Rpm : null, token).ConfigureAwait(false);
                }
            }
            return value;
        }
        catch
        {
            if (mayWrite)
            {
                Volatile.Write(ref retainedMutationEvidence, 1);
                status = status with { SubmissionStateUnknown = true };
            }
            throw;
        }
    }

    private async Task<T> EvidenceAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cancellation = new CancellationTokenSource();
        Task cancellationCompleted = Task.CompletedTask;
        var pending = Task.Run(() => operation(cancellation.Token), CancellationToken.None);
        Retain(pending);
        try
        {
            var result = await pending.WaitAsync(PortTimeout, clock, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            Volatile.Write(ref evidenceUnavailable, 1);
            cancellationCompleted = cancellation.CancelAsync();
            Retain(cancellationCompleted);
            throw;
        }
        finally { Retain(DisposeEvidenceCancellationAsync(pending, cancellationCompleted, cancellation)); }
    }

    private static async Task DisposeEvidenceCancellationAsync(Task pending, Task cancellationCompleted, CancellationTokenSource cancellation)
    {
        try { await Task.WhenAll(pending, cancellationCompleted).ConfigureAwait(false); }
        catch { }
        finally { cancellation.Dispose(); }
    }

    private async Task RequireRawAsync(CoolingPadRawControlState expected, CancellationToken token)
    {
        var result = await PortAsync(() => port!.QueryControlStateAsync(token), token, mayWrite: false).ConfigureAwait(false);
        if (result.Fault is { } fault) { throw new ControlFailure(Map(fault)); }
        if (result.State != expected) { throw new ControlFailure(CoolingPadControlPauseReason.SessionChanged); }
    }

    private void RequireWrite(CoolingPadWriteResult result)
    {
        if (result.Attempted) { Volatile.Write(ref retainedMutationEvidence, 1); }
        if (result.Attempted && !result.Acknowledged) { status = status with { SubmissionStateUnknown = true }; }
        if (result.Fault is { } fault) { throw new ControlFailure(Map(fault)); }
        if (!result.Attempted || !result.Acknowledged)
        { throw new ControlFailure(CoolingPadControlPauseReason.SubmissionUnknown); }
    }

    private async Task<CoolingPadControlStatus> FailAsync(Exception exception)
    {
        if (productionLifecycle) { status = status with { RetainedEffect = HasRetainedMutationEvidence }; }
        var reason = exception is ControlFailure failure ? failure.Reason :
            status.SubmissionStateUnknown ? CoolingPadControlPauseReason.SubmissionUnknown :
            exception is OperationCanceledException ? CoolingPadControlPauseReason.ExplicitResumeRequired :
            CoolingPadControlPauseReason.DeviceUnavailable;
        if (productionLifecycle && (planningOnly || knownUnsubmitted))
        {
            if (await TryParkSafeRevocationAsync().ConfigureAwait(false)) { return status; }
            bool planningFailure = planningOnly && reason is CoolingPadControlPauseReason.TelemetryUnavailable or CoolingPadControlPauseReason.StaleTelemetry;
            if ((planningFailure || knownUnsubmitted && freshnessRejected)
                && CanParkUnrevokedEpoch() && await TryParkAsync(reason).ConfigureAwait(false)
                && CanParkUnrevokedEpoch())
            {
                Revoke(healthyPause: true);
                return status;
            }
        }
        Revoke();
        await CloseAsync().ConfigureAwait(false);
        return Pause(reason);
    }

    private CoolingPadControlStatus Pause(CoolingPadControlPauseReason reason)
    {
        Revoke();
        status = status with
        {
            State = reason == CoolingPadControlPauseReason.AdmissionClosed ? CoolingPadControlState.Unavailable : CoolingPadControlState.Paused,
            PauseReason = reason,
            ResumeRequired = true,
            Connection = reason == CoolingPadControlPauseReason.DeviceUnavailable ? CoolingPadConnectionState.Unavailable : status.Connection,
        };
        return status;
    }

    private async Task CloseAsync()
    {
        ICoolingPadControlPort? closing = port;
        port = null;
        parked = false;
        restorationOwner = null;
        status = status with { RetainedSessionResumable = false, QuietRestorationAvailable = false, Restoration = null };
        if (closing is null) { return; }
        Task? operation = portOperation;
        var cleanup = Task.Run(async () =>
        {
            if (operation is not null) { try { await operation.ConfigureAwait(false); } catch { } }
            await closing.DisposeAsync().ConfigureAwait(false);
        });
        pendingCleanup = cleanup;
        Retain(cleanup);
        try { await cleanup.WaitAsync(PortTimeout, clock).ConfigureAwait(false); }
        catch { }
    }

    private static async Task DisposeLateOpenAsync(Task<CoolingPadPortOpenResult> opening)
    {
        var opened = await opening.ConfigureAwait(false);
        if (opened.Port is not null) { await opened.Port.DisposeAsync().ConfigureAwait(false); }
    }

    private static void Retain(Task task) =>
        _ = task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static CoolingPadControlPauseReason Map(CoolingPadPortFault fault) => fault switch
    {
        CoolingPadPortFault.AdmissionRevoked => CoolingPadControlPauseReason.ExplicitResumeRequired,
        CoolingPadPortFault.AdmissionClosed => CoolingPadControlPauseReason.AdmissionClosed,
        CoolingPadPortFault.Disconnected => CoolingPadControlPauseReason.DeviceUnavailable,
        CoolingPadPortFault.ControllerConflict => CoolingPadControlPauseReason.ControllerConflict,
        CoolingPadPortFault.HyperBoostActive => CoolingPadControlPauseReason.HyperBoostActive,
        CoolingPadPortFault.SubmissionUnknown => CoolingPadControlPauseReason.SubmissionUnknown,
        _ => CoolingPadControlPauseReason.SessionChanged,
    };

    private sealed class ControlFailure(CoolingPadControlPauseReason reason) : Exception
    { internal CoolingPadControlPauseReason Reason { get; } = reason; }
}

// Same isolated observer pattern used by the reviewed attended Capture trials.
internal static class CoolingPadDeveloperObservation
{
    internal static async Task<bool> ConfirmAsync(Func<string, CancellationToken, Task<bool>> confirm,
        string stage, CancellationToken token, TimeSpan timeout, TimeProvider clock)
    {
        token.ThrowIfCancellationRequested();
        var observerCancellation = new CancellationTokenSource();
        Task<bool>? observation = null;
        long started = clock.GetTimestamp();
        try
        {
            CancellationToken observerToken = observerCancellation.Token;
            observation = Task.Run(() =>
            {
                observerToken.ThrowIfCancellationRequested();
                return confirm(stage, observerToken);
            }, CancellationToken.None);
            bool confirmed = await observation.WaitAsync(timeout, clock, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= timeout) { throw new TimeoutException("OperatorTimeout"); }
            return confirmed;
        }
        finally
        {
            PendingDisposalRegistry.Shared.Retain(CancelObserverAsync(observerCancellation));
            if (observation is not null) { PendingDisposalRegistry.Shared.Retain(observation); }
        }
    }
    private static async Task CancelObserverAsync(CancellationTokenSource observer)
    {
        try { await observer.CancelAsync().ConfigureAwait(false); }
        finally { observer.Dispose(); }
    }
}
