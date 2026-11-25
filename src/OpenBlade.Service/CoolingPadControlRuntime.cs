using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

/// <summary>
/// One serial owner for exact-pad control. Saved intent is inert until an explicit Apply.
/// After a clean service-owned handback (service stop, low power or user change), the
/// service applies that user's saved settings again; an explicit Release ends this.
/// A possibly submitted operation stops all writes until an explicit successful read.
/// </summary>
internal sealed class CoolingPadControlRuntime(
    ICoolingPadDeviceProvider provider,
    CoolingPadStateStore store,
    IFanTemperatureSource temperatures,
    TimeProvider? timeProvider = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    // Shared with the laptop manual curve; pauses and validity checks stay on raw readings.
    private readonly FanCurveTemperatureFilter temperatureFilter = new(timeProvider ?? TimeProvider.System);
    private readonly EmergencyLimit cpuEmergency = new(FanCurveCalculator.EmergencyCpuCelsius);
    private readonly EmergencyLimit gpuEmergency = new(FanCurveCalculator.EmergencyGpuCelsius);
    private readonly Dictionary<Guid, DemandLease> leases = [];
    private CoolingPadStatusSnapshot status = new();
    private CoolingPadOwnershipRecord? ownership;
    private ICoolingPadDevice? device;
    private string? owner;
    private Func<bool> authority = static () => false;
    private long generation;
    private bool ownershipLoaded;
    private bool configurationFailure;
    private DateTimeOffset nextCurveRead;
    private long mirroredSequence = -1;
    private DateTimeOffset nextDiscovery;
    private long? lastTemperatureTimestamp;
    private bool resumeIntent;
    private DateTimeOffset nextResumeAttempt;
    // A closed admission (asleep, locked, another user) is checked again each second.
    // An attempt that reached the pad and was refused retries at the longer interval.
    internal static readonly TimeSpan ResumeAdmissionInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ResumeRetryInterval = TimeSpan.FromSeconds(5);
    // The configuration read after this session's last confirmed write, while it controls the pad.
    private CoolingPadDeviceSnapshot? controlled;
    private DateTimeOffset nextOutsideCheck;
    private readonly Queue<DateTimeOffset> outsideRestorations = new();
    // Each check is one full configuration read (about eight short HID queries).
    internal static readonly TimeSpan OutsideChangeCheckInterval = TimeSpan.FromSeconds(5);
    // Consecutive comparison reads that may fail before the session is handled as a lost connection.
    internal const int OutsideReadFailureLimit = 3;
    private int outsideReadFailures;
    // Set when the session used for the last confirmed write was replaced during control.
    private bool connectionLost;
    // Set when a lighting change made elsewhere was left in place while the fan stays controlled,
    // for example after the pad's fan button also reset its lighting. Automatic writes and
    // comparisons then leave lighting alone until the user applies settings or releases control.
    private bool lightingKept;
    // The message that explained the last time control stopped. A status read keeps it, and while
    // control runs a read keeps the latest control message, so the page's checks never hide them.
    private string? releaseReason;
    // Restoring a change made elsewhere is bounded so OpenBlade never fights another controller.
    internal const int OutsideRestorationLimit = 3;
    internal static readonly TimeSpan OutsideRestorationWindow = TimeSpan.FromMinutes(10);

    internal CoolingPadStatusSnapshot Status => Volatile.Read(ref status);

    private void UpdateStatus(Func<CoolingPadStatusSnapshot, CoolingPadStatusSnapshot> update)
    {
        CoolingPadStatusSnapshot previous;
        CoolingPadStatusSnapshot next;
        do
        {
            previous = Volatile.Read(ref status);
            next = update(previous);
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref status, next, previous), previous));
    }

    // Called at lifecycle ingress before waiting for outstanding native work.
    internal void Revoke()
    {
        Interlocked.Increment(ref generation);
        UpdateStatus(previous => previous with { Active = false, RecoveryRequired = previous.Active || previous.RecoveryRequired });
    }

    internal async Task SetUserAsync(string user, CancellationToken token)
    {
        if (owner == user && !configurationFailure) { return; }
        Revoke();
        await gate.WaitAsync(token).ConfigureAwait(false);
        lightingKept = false;
        try
        {
            try
            {
                await LoadOwnershipAsync(token).ConfigureAwait(false);
                CoolingPadSettings settings = await store.LoadSettingsAsync(user, token).ConfigureAwait(false);
                if (!CoolingPadSettings.IsWellFormed(settings)) { throw new InvalidDataException("Saved cooling pad settings are invalid."); }
                resumeIntent = await store.LoadResumeIntentAsync(user, token).ConfigureAwait(false);
                owner = user;
                configurationFailure = false;
                leases.Clear();
                authority = static () => false;
                UpdateStatus(previous => previous with
                {
                    Requested = Freeze(settings),
                    Active = false,
                    RecoveryRequired = ownership is not null,
                    WritesPaused = ownership is not null,
                    Message = ownership is null ? "Cooling pad control is stopped." : "Read the cooling pad before restoring retained settings.",
                });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Accessory storage must not abort ordinary laptop user activation.
                // Preserve any protected baseline; never substitute another user's saved intent.
                DiagnosticTrace.TraceException(nameof(CoolingPadControlRuntime), nameof(SetUserAsync), exception);
                owner = user;
                configurationFailure = true;
                resumeIntent = false;
                leases.Clear();
                authority = static () => false;
                UpdateStatus(previous => previous with
                {
                    Requested = CoolingPadSettings.Default,
                    Active = false,
                    WritesPaused = true,
                    RecoveryRequired = true,
                    Message = BoundMessage($"Cooling pad settings could not be loaded: {exception.Message}"),
                });
            }
        }
        finally { gate.Release(); }
    }

    internal async Task<ControlOperationResult> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadOwnershipAsync(token).ConfigureAwait(false);
            if (configurationFailure) { return Block("Cooling pad settings require recovery before control can continue."); }
            if (!status.Active && (status.WritesPaused || ownership is null))
            { await CloseDeviceAsync(token).ConfigureAwait(false); }
            CoolingPadDeviceSnapshot snapshot = await ReadDeviceAsync(token).ConfigureAwait(false);
            if (ownership is not null && snapshot.Identity != ownership.Baseline.Identity)
            { return Block("The connected cooling pad does not match the retained restoration state."); }
            Project(snapshot);
            bool explained = status.Active || status.Message == releaseReason;
            UpdateStatus(previous => previous with { WritesPaused = false, Message = explained ? previous.Message : "Cooling pad configuration was read." });
            if (ownership is null) { await CloseDeviceAsync(token).ConfigureAwait(false); }
            return Result(ControlOperationStatus.Stored);
        }
        catch (Exception exception) when (exception is TimeoutException
            || exception is CoolingPadOperationException { SubmissionUnknown: false } && device is { Usable: false })
        {
            // A read submits nothing, and a timed-out exchange or the session it retired is not a
            // change or a disconnect. Keep control and the last confirmed status, including the
            // message explaining it; maintenance reopens the retired session. A port that cannot
            // be opened or a refusal is still reported below.
            DiagnosticTrace.TraceException(nameof(CoolingPadControlRuntime), nameof(ReadAsync), exception);
            return new(ControlOperationStatus.Failed, BoundMessage(exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Fail(exception, false); }
        finally { gate.Release(); }
    }

    /// <param name="resumption">
    /// True when the service applies saved settings again after a handback. It keeps lighting
    /// that was left as changed elsewhere; the user's own Apply manages lighting again.
    /// </param>
    internal async Task<ControlOperationResult> ApplyAsync(CoolingPadSettings settings,
        Func<bool> admission, CancellationToken token, Func<bool>? continuedOwnership = null, bool resumption = false)
    {
        if (!CoolingPadSettings.IsWellFormed(settings)) { return Block("Cooling pad settings are invalid."); }
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool keepLighting = resumption && lightingKept;
            ControlOperationResult result = await ApplyCoreAsync(Freeze(settings), admission, token, continuedOwnership,
                keepLighting).ConfigureAwait(false);
            // An accepted setting starts a new allowance for restoring changes made elsewhere.
            if (result.Status is ControlOperationStatus.Applied or ControlOperationStatus.Stored)
            {
                outsideRestorations.Clear();
                lightingKept = keepLighting;
            }
            return result;
        }
        finally { gate.Release(); }
    }

    // Runs under the serial gate. A refusal before submission saves nothing, so maintenance and
    // resumption cannot later apply settings that were reported as not accepted.
    // keepLighting writes only the fan and leaves the pad's current lighting as it is.
    private async Task<ControlOperationResult> ApplyCoreAsync(CoolingPadSettings settings,
        Func<bool> admission, CancellationToken token, Func<bool>? continuedOwnership, bool keepLighting = false)
    {
        bool submitted = false;
        try
        {
            await LoadOwnershipAsync(token).ConfigureAwait(false);
            if (owner is null || !admission()) { return Block("The active Windows session cannot control the cooling pad."); }
            if (status.WritesPaused)
            { return Block("Read the retained cooling pad state before applying settings."); }
            if (ownership is not null && ownership.Owner != owner)
            { return Block("Cooling pad control belongs to another Windows user."); }

            CoolingPadDeviceSnapshot baseline = FreezeSnapshot(await ReadDeviceAsync(token).ConfigureAwait(false));
            if (ownership is not null && ownership.Baseline.Identity != baseline.Identity)
            { return Block("The connected cooling pad changed during control."); }
            bool fan = settings.FanMode != CoolingPadFanMode.Keep;
            bool lighting = settings.LightingMode != CoolingPadLightingMode.Keep && !keepLighting;
            // An effect the pad's own button selected but OpenBlade does not decode (7 or 9) could
            // not be put back on Release, so its lighting is not changed while it shows.
            if (lighting && baseline.BrightnessRaw is not null
                && (baseline.LightingConfiguration is null || baseline.ActiveLightingConfiguration is null))
            {
                return Block("The cooling pad is showing a lighting effect OpenBlade can't read. Choose another effect with the pad's lighting button, or keep the pad's lighting, then try again.");
            }
            if (fan && baseline.FanConfiguration is null
                || lighting && (baseline.LightingConfiguration is null || baseline.BrightnessRaw is null))
            { return Block("The affected cooling pad settings could not be read for restoration."); }

            // New settings start a new filter history from the first raw sample.
            ResetTemperatureHistory();
            int? target = IsTemperatureDriven(settings.FanMode)
                ? await CurveTargetAsync(settings, token).ConfigureAwait(false)
                : settings.FanMode == CoolingPadFanMode.Fixed ? settings.FixedRpm : null;
            long epoch = Interlocked.Read(ref generation);
            bool Allowed() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && admission();
            // Refuse before saving anything. Saving first let maintenance or resumption apply a
            // request this call had just reported as not accepted.
            if (!Allowed()) { return Block("Cooling pad authority changed before submission."); }
            await store.SaveSettingsAsync(owner, settings, token).ConfigureAwait(false);
            if (resumeIntent != (fan || lighting))
            {
                await store.SaveResumeIntentAsync(owner, fan || lighting, token).ConfigureAwait(false);
                resumeIntent = fan || lighting;
            }
            UpdateStatus(previous => previous with { Requested = settings });
            if (!settings.AllowDemandApi) { leases.Clear(); }
            authority = continuedOwnership ?? admission;
            bool OwnershipContinues() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && authority();
            if (fan || lighting)
            {
                CoolingPadDeviceSnapshot saved = ownership?.Baseline ?? baseline;
                if (fan && ownership?.FanChanged != true)
                { saved = saved with { FanConfiguration = baseline.FanConfiguration, FanMode = baseline.FanMode, TargetRpm = baseline.TargetRpm }; }
                if (lighting && ownership?.LightingChanged != true)
                {
                    saved = saved with
                    {
                        LightingConfiguration = baseline.LightingConfiguration,
                        ActiveLightingConfiguration = baseline.ActiveLightingConfiguration,
                        LightingSettings = baseline.LightingSettings,
                        BrightnessRaw = baseline.BrightnessRaw,
                        BrightnessPercent = baseline.BrightnessPercent,
                        LightingMode = baseline.LightingMode,
                        DeviceMode = baseline.DeviceMode
                    };
                }
                ownership = new(owner, saved, ownership?.FanChanged == true || fan, ownership?.LightingChanged == true || lighting);
                await store.SaveOwnershipAsync(ownership, token).ConfigureAwait(false);
            }
            if (fan)
            {
                submitted = true;
                await device!.SetFanAsync(settings.FanMode, target,
                    () => Allowed() && (!IsTemperatureDriven(settings.FanMode) || TemperatureIsFresh()), token,
                    SmartPreset(settings)).ConfigureAwait(false);
            }
            if (lighting)
            {
                if (settings.LightingMode == CoolingPadLightingMode.KeyboardMirror && ownership?.LightingChanged == true
                    && device is ICoolingPadRestorationProvenance provenance)
                { provenance.BindLightingBaseline(ownership.Baseline); }
                submitted = true;
                await device!.SetLightingAsync(settings, Allowed, token).ConfigureAwait(false);
            }
            CoolingPadDeviceSnapshot applied = FreezeSnapshot(await device!.ReadSnapshotAsync(token).ConfigureAwait(false));
            Project(applied);
            UpdateStatus(previous => previous with
            {
                Active = (fan || lighting) && OwnershipContinues(),
                RecoveryRequired = (!(fan || lighting) || !OwnershipContinues()) && ownership is not null,
                WritesPaused = false,
                Message = fan || lighting ? "Cooling pad settings applied." : "Cooling pad settings saved.",
            });
            // The confirmed configuration a later read compares to find a change made elsewhere.
            controlled = status.Active ? applied : null;
            nextOutsideCheck = clock.GetUtcNow() + OutsideChangeCheckInterval;
            outsideReadFailures = 0;
            connectionLost = false;
            nextCurveRead = clock.GetUtcNow().AddSeconds(1);
            mirroredSequence = -1;
            return Result(fan || lighting ? ControlOperationStatus.Applied : ControlOperationStatus.Stored);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (submitted) { Pause("Cooling pad submission was interrupted. Read its configuration before continuing."); }
            throw;
        }
        // The target is computed before any submission, so a missing reading is a refusal.
        catch (TemperatureUnavailableException exception) when (!submitted) { return Block(exception.Message); }
        catch (Exception exception) { return Fail(exception, submitted); }
    }

    private sealed class TemperatureUnavailableException() : InvalidOperationException(Text)
    {
        internal const string Text = "Cooling pad curve is waiting for a reading from its selected temperature source.";
    }

    internal async Task<ControlOperationResult> ReleaseAsync(Func<bool> admission, CancellationToken token,
        bool serviceOwnedRestoration = false, bool userRequested = false)
    {
        Revoke();
        await gate.WaitAsync(token).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            leases.Clear();
            // A service handback keeps lighting left as changed elsewhere for resumption.
            if (userRequested) { lightingKept = false; }
            await LoadOwnershipAsync(token).ConfigureAwait(false);
            if (userRequested && owner is not null && resumeIntent)
            {
                // The user stopped control. Later handbacks must not start it again.
                await store.SaveResumeIntentAsync(owner, false, token).ConfigureAwait(false);
                resumeIntent = false;
            }
            if (ownership is null)
            {
                UpdateStatus(previous => previous with { Active = false, Message = "Cooling pad control is stopped." });
                return Result(ControlOperationStatus.Stored);
            }
            if (!serviceOwnedRestoration && owner != ownership.Owner) { return Block("Cooling pad restoration belongs to another Windows user."); }
            if (status.WritesPaused) { return Block("Read the cooling pad before restoring retained settings."); }
            CoolingPadDeviceSnapshot current = await ReadDeviceAsync(token).ConfigureAwait(false);
            if (current.Identity != ownership.Baseline.Identity)
            { return Block("The connected cooling pad does not match the saved baseline."); }
            long epoch = Interlocked.Read(ref generation);
            bool Allowed() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && admission();
            if (!Allowed()) { return Block("Cooling pad restoration is not currently admitted."); }
            submitted = true;
            await device!.RestoreAsync(ownership.Baseline, ownership.FanChanged, ownership.LightingChanged, Allowed, token)
                .ConfigureAwait(false);
            Project(await device.ReadSnapshotAsync(token).ConfigureAwait(false));
            await store.ClearOwnershipAsync(token).ConfigureAwait(false);
            ownership = null;
            controlled = null;
            UpdateStatus(previous => previous with { Active = false, RecoveryRequired = false, WritesPaused = false, Message = "Saved cooling pad settings restored." });
            await CloseDeviceAsync(token).ConfigureAwait(false);
            return Result(ControlOperationStatus.Applied);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (submitted) { Pause("Cooling pad restoration was interrupted. Read its configuration before continuing."); }
            throw;
        }
        catch (Exception exception) { return Fail(exception, submitted); }
        finally { gate.Release(); }
    }

    /// <param name="restoreOutsideChanges">
    /// Follows "If settings change outside OpenBlade": true restores the saved settings when a
    /// read finds them changed elsewhere; false releases control and leaves that change in place.
    /// </param>
    /// <param name="restoreOutsideLighting">
    /// The lighting ownership opt-in. Without it a lighting change made elsewhere releases
    /// control, because only that policy may restore lighting automatically.
    /// </param>
    internal async Task MaintainAsync(Func<bool> admission, CancellationToken token, bool restoreOutsideChanges = true,
        bool restoreOutsideLighting = true)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        bool submitted = false;
        long epoch = Interlocked.Read(ref generation);
        try
        {
            // Re-evaluate activity after acquiring the serial gate. Apply may have
            // completed while an originally inactive maintenance call was queued.
            await ObserveConnectionCoreAsync(token).ConfigureAwait(false);
            if (!status.Active || status.WritesPaused) { return; }
            if (!authority())
            {
                Revoke();
                UpdateStatus(previous => previous with { Message = "Cooling pad control paused because its Windows session, power or controller changed." });
                leases.Clear();
                return;
            }
            // A fresh submission window can close during harmless classification work.
            // Defer that tick while durable user/session ownership remains valid.
            if (!admission()) { return; }
            bool Allowed() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested
                && Status.Active && authority() && admission();
            CoolingPadSettings settings = status.Requested;
            int? fanTarget = null;
            if (clock.GetUtcNow() >= nextCurveRead
                && (settings.FanMode == CoolingPadFanMode.Fixed || IsTemperatureDriven(settings.FanMode)))
            {
                nextCurveRead = clock.GetUtcNow().AddSeconds(1);
                ExpireLeases();
                int target = IsTemperatureDriven(settings.FanMode)
                    ? await CurveTargetAsync(settings, token).ConfigureAwait(false) : settings.FixedRpm;
                if (status.Message == TemperatureUnavailableException.Text)
                {
                    UpdateStatus(previous => previous with { Message = "Cooling pad curve is following its temperature source again." });
                }
                if (settings.AllowDemandApi && leases.Count != 0) { target = Math.Max(target, leases.Values.Max(value => value.Rpm)); }
                if (status.ConfirmedTargetRpm != target) { fanTarget = target; }
            }
            // A fan write reselects the mode's preset, which would hide a change made elsewhere,
            // so one runs only after a fresh comparison. Otherwise compare at a bounded interval.
            if (controlled is { } expected && (fanTarget is not null || clock.GetUtcNow() >= nextOutsideCheck))
            {
                nextOutsideCheck = clock.GetUtcNow() + OutsideChangeCheckInterval;
                if (connectionLost || device is { Usable: false })
                {
                    await RecoverLostConnectionAsync(expected, settings, restoreOutsideChanges, restoreOutsideLighting,
                        admission, token).ConfigureAwait(false);
                    return;
                }
                CoolingPadDeviceSnapshot current;
                try { current = FreezeSnapshot(await ReadDeviceAsync(token).ConfigureAwait(false)); }
                catch (Exception exception) when (exception is TimeoutException
                    or IOException and not CoolingPadOperationException
                    // The session retired itself without a submission; the next check reconnects.
                    || exception is CoolingPadOperationException { SubmissionUnknown: false } && device is { Usable: false })
                {
                    // A transient read failure is not a change and submitted nothing. Keep control
                    // and defer this tick's write. A refusal is not caught here and stops control.
                    DiagnosticTrace.TraceException(nameof(CoolingPadControlRuntime), nameof(MaintainAsync), exception);
                    if (++outsideReadFailures < OutsideReadFailureLimit) { return; }
                    // Repeated failures are handled as a lost connection: Restore them reopens the pad
                    // and applies the saved settings again within the restore allowance, and Notify me
                    // stops control with that reason. The reopen gets its own failure allowance.
                    outsideReadFailures = 0;
                    await CloseDeviceAsync(token).ConfigureAwait(false);
                    await RecoverLostConnectionAsync(expected, settings, restoreOutsideChanges, restoreOutsideLighting,
                        admission, token).ConfigureAwait(false);
                    return;
                }
                if (connectionLost)
                {
                    // The read reopened the session, so it cannot be compared with the old one's.
                    await RecoverLostConnectionAsync(expected, settings, restoreOutsideChanges, restoreOutsideLighting,
                        admission, token).ConfigureAwait(false);
                    return;
                }
                outsideReadFailures = 0;
                (bool fanOutside, bool lightingOutside) = OutsideChanges(expected, current, Managed(settings));
                if (fanOutside || lightingOutside)
                {
                    await HandleOutsideChangeAsync(current, fanOutside, lightingOutside,
                        restoreOutsideChanges, restoreOutsideLighting, admission, token).ConfigureAwait(false);
                    return;
                }
            }
            if (fanTarget is int demand)
            {
                submitted = true;
                await device!.SetFanAsync(settings.FanMode, demand,
                    () => Allowed() && (!IsTemperatureDriven(settings.FanMode) || TemperatureIsFresh()), token,
                    SmartPreset(settings)).ConfigureAwait(false);
                // Typed SetFan completes only after independent fan configuration readback.
                UpdateStatus(previous => previous with { ConfirmedTargetRpm = demand });
                if (settings.FanMode == CoolingPadFanMode.Fixed && controlled is not null)
                {
                    // A fixed target is stored in the fan record, which SetFan just confirmed by
                    // readback; compare later reads with the new value.
                    if (controlled.FanConfiguration is { } record)
                    { controlled = controlled with { FanConfiguration = record with { ConfiguredRpm = demand } }; }
                }
            }
            // A lost session waits for the next check to reconnect and reapply.
            if (Managed(settings).LightingMode == CoolingPadLightingMode.KeyboardMirror && !connectionLost && device is { Usable: true }
                && KeyboardLightingFrameStream.Latest is { } frame && frame.Sequence != mirroredSequence)
            {
                submitted = true;
                await device!.PushMirrorFrameAsync(frame.Colors, Allowed, token).ConfigureAwait(false);
                mirroredSequence = frame.Sequence;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (submitted) { Pause("Cooling pad submission was interrupted. Read its configuration before continuing."); }
            throw;
        }
        catch (CoolingPadOperationException exception) when (exception.LocalAdmissionDeferred && !exception.SubmissionUnknown
            && !token.IsCancellationRequested && epoch == Interlocked.Read(ref generation)
            && Status.Active && !Status.WritesPaused && authority())
        {
            // Proven local deferral submitted no native write and changed no firmware state.
            // Preserve the healthy owned epoch; the next scheduled tick gets fresh admission.
        }
        catch (TemperatureUnavailableException exception) when (!submitted)
        {
            // A missing reading pauses curve updates only. The last confirmed target keeps
            // running, and no write can use a stale reading because freshness was cleared.
            UpdateStatus(previous => previous with { Message = exception.Message });
        }
        catch (Exception exception) { Fail(exception, submitted); }
        finally { gate.Release(); }
    }

    // Runs under the serial gate from maintenance, after a read found the pad's controlled
    // settings changed by another controller, the pad itself or its buttons.
    private async Task HandleOutsideChangeAsync(CoolingPadDeviceSnapshot current, bool fanOutside, bool lightingOutside,
        bool restore, bool restoreLighting, Func<bool> admission, CancellationToken token)
    {
        DateTimeOffset now = clock.GetUtcNow();
        while (outsideRestorations.Count != 0 && now - outsideRestorations.Peek() >= OutsideRestorationWindow)
        { outsideRestorations.Dequeue(); }
        if (restore && lightingOutside && !restoreLighting)
        {
            // Only the lighting opt-in may rewrite lighting changed elsewhere, so it is left as it
            // is. A controlled fan stays controlled: the pad's fan button can also reset its lighting.
            if (!await KeepOutsideLightingAsync(token).ConfigureAwait(false)) { restore = false; }
            else if (!fanOutside)
            {
                controlled = current;
                UpdateStatus(previous => previous with
                {
                    Message = "The cooling pad lighting was changed outside OpenBlade and is left as it is. Apply lighting again to manage it.",
                });
                return;
            }
        }
        if (restore && outsideRestorations.Count < OutsideRestorationLimit)
        {
            // The same admitted, read-back apply as a user's change. A refused or failed
            // apply reports and stops as it would for the user; it is not retried here.
            outsideRestorations.Enqueue(now);
            ControlOperationResult result = await ApplyCoreAsync(status.Requested, admission, token, authority, lightingKept)
                .ConfigureAwait(false);
            if (result.Status == ControlOperationStatus.Applied)
            {
                string message = lightingOutside && lightingKept
                    ? "The cooling pad fan was changed outside OpenBlade and restored. Its lighting changed too and is left as it is."
                    : "Cooling pad settings changed outside OpenBlade and were restored.";
                UpdateStatus(previous => previous with { Message = message });
            }
            // A refused restoration would otherwise repeat at every check; stop instead.
            if (result.Status == ControlOperationStatus.Blocked && status.Active)
            {
                await ReleaseAfterOutsideChangeAsync(current, fanOutside, lightingOutside,
                    "Cooling pad settings changed outside OpenBlade and could not be restored. OpenBlade stopped controlling it.",
                    admission, token).ConfigureAwait(false);
            }
            return;
        }
        // Notify me, or another controller keeps changing the pad: stop instead of fighting it.
        await ReleaseAfterOutsideChangeAsync(current, fanOutside, lightingOutside, restore
            ? "Another app keeps changing the cooling pad. OpenBlade stopped controlling it."
            : "Cooling pad settings changed outside OpenBlade. OpenBlade stopped controlling it.",
            admission, token).ConfigureAwait(false);
    }

    // Runs under the serial gate from maintenance after the session used for the last confirmed
    // write retired itself. A new session cannot tell which fan owner or effect record the old one
    // confirmed, so the fan record and the showing effect cannot be compared. Restore them applies
    // the saved settings again within the restore allowance; Notify me stops control.
    private async Task RecoverLostConnectionAsync(CoolingPadDeviceSnapshot expected, CoolingPadSettings settings,
        bool restore, bool restoreLighting, Func<bool> admission, CancellationToken token)
    {
        connectionLost = true;
        if (device is { Usable: false }) { await CloseDeviceAsync(token).ConfigureAwait(false); }
        DateTimeOffset now = clock.GetUtcNow();
        while (outsideRestorations.Count != 0 && now - outsideRestorations.Peek() >= OutsideRestorationWindow)
        { outsideRestorations.Dequeue(); }
        if (!restore || outsideRestorations.Count >= OutsideRestorationLimit)
        {
            await CloseDeviceAsync(token).ConfigureAwait(false);
            Fail(new IOException(restore
                ? "The connection to the cooling pad keeps failing. OpenBlade stopped controlling it."
                : "The connection to the cooling pad was lost. OpenBlade stopped controlling it; apply settings again to continue."), false);
            releaseReason = status.Message;
            return;
        }
        device ??= await provider.OpenAsync(token).ConfigureAwait(false);
        if (device is null)
        {
            // Native retirement of the old session can hold the pad briefly; try at the next check.
            if (++outsideReadFailures < OutsideReadFailureLimit) { return; }
            Fail(new IOException("The cooling pad connection could not be reopened. OpenBlade stopped controlling it."), false);
            releaseReason = status.Message;
            return;
        }
        if (Managed(settings).LightingMode != CoolingPadLightingMode.Keep && !restoreLighting)
        {
            // The stored effect, brightness and device mode read the same through any session.
            // Without the lighting opt-in, a change there is left as it is instead of being
            // rewritten; a controlled fan is still applied again, otherwise control is released.
            CoolingPadDeviceSnapshot current = FreezeSnapshot(await ReadDeviceAsync(token).ConfigureAwait(false));
            if ((!SameEffect(expected.LightingConfiguration, current.LightingConfiguration)
                || expected.BrightnessRaw != current.BrightnessRaw || expected.DeviceMode != current.DeviceMode)
                && !await KeepOutsideLightingAsync(token).ConfigureAwait(false))
            {
                await HandleOutsideChangeAsync(current, false, true, false, false, admission, token).ConfigureAwait(false);
                return;
            }
        }
        ControlOperationResult result = await ApplyCoreAsync(status.Requested, admission, token, authority, lightingKept)
            .ConfigureAwait(false);
        if (result.Status == ControlOperationStatus.Applied)
        {
            outsideRestorations.Enqueue(now);
            UpdateStatus(previous => previous with { Message = "The cooling pad connection was reopened and its settings were applied again." });
        }
        // A curve waiting for a temperature reading submitted nothing; try again at the next check.
        else if (status.Active && status.Message == TemperatureUnavailableException.Text) { }
        else if (status.Active)
        {
            // A refused reapply would otherwise repeat at every check.
            Revoke();
            UpdateStatus(previous => previous with { Message = "The cooling pad connection was reopened, but its settings could not be applied again. OpenBlade stopped controlling it." });
            releaseReason = status.Message;
        }
    }

    // Release control without overwriting the change: restore the original settings only for
    // the parts this session changed and nothing else has changed since, as Release would.
    private async Task ReleaseAfterOutsideChangeAsync(CoolingPadDeviceSnapshot current, bool fanOutside,
        bool lightingOutside, string message, Func<bool> admission, CancellationToken token)
    {
        Revoke();
        leases.Clear();
        lightingKept = false;
        authority = static () => false;
        controlled = null;
        bool submitted = false;
        try
        {
            if (owner is not null && resumeIntent)
            {
                // As with Release, later handbacks must not start control again.
                await store.SaveResumeIntentAsync(owner, false, token).ConfigureAwait(false);
                resumeIntent = false;
            }
            if (ownership is { } owned)
            {
                bool restoreFan = owned.FanChanged && !fanOutside;
                bool restoreLighting = owned.LightingChanged && !lightingOutside;
                if (restoreFan || restoreLighting)
                {
                    long epoch = Interlocked.Read(ref generation);
                    bool Allowed() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && admission();
                    submitted = true;
                    await device!.RestoreAsync(owned.Baseline, restoreFan, restoreLighting, Allowed, token).ConfigureAwait(false);
                    current = await device.ReadSnapshotAsync(token).ConfigureAwait(false);
                }
                await store.ClearOwnershipAsync(token).ConfigureAwait(false);
                ownership = null;
            }
            Project(current);
            UpdateStatus(previous => previous with { Active = false, RecoveryRequired = false, WritesPaused = false, Message = message });
            releaseReason = message;
            await CloseDeviceAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (submitted) { Pause("Cooling pad restoration was interrupted. Read its configuration before continuing."); }
            throw;
        }
        catch (Exception exception) { Fail(exception, submitted); }
    }

    // The parts of the saved settings OpenBlade currently writes and compares.
    private CoolingPadSettings Managed(CoolingPadSettings settings) =>
        lightingKept ? settings with { LightingMode = CoolingPadLightingMode.Keep } : settings;

    // Leaves lighting changed elsewhere in place while a controlled fan stays controlled. Release and
    // service handback then restore only the fan, because the new lighting is no longer OpenBlade's
    // change. False when the fan is not controlled, so the caller releases control instead.
    private async Task<bool> KeepOutsideLightingAsync(CancellationToken token)
    {
        if (status.Requested.FanMode == CoolingPadFanMode.Keep || ownership is not { FanChanged: true } owned) { return false; }
        lightingKept = true;
        if (owned.LightingChanged)
        {
            ownership = owned with { LightingChanged = false };
            await store.SaveOwnershipAsync(ownership, token).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Which controlled parts a read shows changed since this session confirmed them.</summary>
    internal static (bool Fan, bool Lighting) OutsideChanges(CoolingPadDeviceSnapshot expected,
        CoolingPadDeviceSnapshot current, CoolingPadSettings settings)
    {
        // The fan demand and ramping speed vary on their own; the selected fan record does not.
        bool fan = settings.FanMode != CoolingPadFanMode.Keep && current.FanConfiguration != expected.FanConfiguration;
        bool lighting = settings.LightingMode != CoolingPadLightingMode.Keep
            && (!SameEffect(expected.LightingConfiguration, current.LightingConfiguration)
                || !SameEffect(expected.ActiveLightingConfiguration, current.ActiveLightingConfiguration)
                || expected.BrightnessRaw != current.BrightnessRaw
                || expected.DeviceMode != current.DeviceMode);
        return (fan, lighting);
    }

    private static bool SameEffect(CoolingPadLightingConfiguration? expected, CoolingPadLightingConfiguration? current) =>
        expected is null ? current is null
            : current is not null && expected with { Colors = current.Colors } == current && expected.Colors.SequenceEqual(current.Colors);

    private async Task ObserveConnectionCoreAsync(CancellationToken token)
    {
        if (clock.GetUtcNow() < nextDiscovery) { return; }
        nextDiscovery = clock.GetUtcNow().AddSeconds(2);
        CoolingPadConnectionState connection;
        try { connection = provider.ObserveConnection(); }
        catch { connection = CoolingPadConnectionState.ObservationFailed; }
        switch (connection)
        {
            case CoolingPadConnectionState.PresentExact:
                UpdateStatus(previous => previous with { Connected = true, Connection = connection });
                break;
            case CoolingPadConnectionState.Absent:
                Revoke();
                leases.Clear();
                authority = static () => false;
                UpdateStatus(previous => previous with
                {
                    Active = false,
                    Connected = false,
                    Connection = connection,
                    RecoveryRequired = ownership is not null || configurationFailure,
                    Message = "Cooling pad disconnected. Saved settings are retained.",
                });
                // Confirmed unplug retires software ownership without sending any cleanup write.
                await CloseDeviceAsync(token).ConfigureAwait(false);
                break;
            case CoolingPadConnectionState.Ambiguous:
            case CoolingPadConnectionState.ObservationFailed:
            case CoolingPadConnectionState.Unavailable:
                // Unknown discovery is not absence. Retain the last confirmed presentation.
                UpdateStatus(previous => previous with { Connection = connection });
                break;
        }
    }

    internal Task<ControlOperationResult> HandBackForUserTransitionAsync(Func<bool> admission, CancellationToken token) =>
        ReleaseAsync(admission, token, serviceOwnedRestoration: true);

    /// <summary>
    /// Applies the user's saved settings again after a clean service-owned handback, so a
    /// service restart, sleep or user change does not leave the pad on its own settings.
    /// Retained restoration state, paused writes, a failed apply or an explicit Release
    /// wait for the user instead. A blocked attempt retries at a bounded interval.
    /// </summary>
    internal async Task ResumeSavedControlAsync(string user, Func<bool> admission, Func<bool> continuedOwnership,
        CancellationToken token)
    {
        if (clock.GetUtcNow() < nextResumeAttempt || !ResumeEligible()) { return; }
        if (!admission())
        {
            nextResumeAttempt = clock.GetUtcNow() + ResumeAdmissionInterval;
            return;
        }
        nextResumeAttempt = clock.GetUtcNow() + ResumeRetryInterval;
        await SetUserAsync(user, token).ConfigureAwait(false);
        if (!resumeIntent || !ResumeEligible()) { return; }
        await ApplyAsync(Status.Requested, admission, token, continuedOwnership, resumption: true).ConfigureAwait(false);
    }

    private bool ResumeEligible() => !configurationFailure
        && Status is { Active: false, WritesPaused: false, RecoveryRequired: false, Connection: CoolingPadConnectionState.PresentExact };

    internal async Task<ControlOperationResult> SetLeaseAsync(Guid id, string client, int rpm, int seconds,
        Func<bool> clientAlive, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ExpireLeases();
            if (!status.Requested.AllowDemandApi)
            { return Block("Enable app cooling requests before using the cooling pad demand API."); }
            if (!status.Active || status.Requested.FanMode is not (CoolingPadFanMode.Fixed or CoolingPadFanMode.HostCurve or CoolingPadFanMode.Auto))
            { return Block("Start cooling pad fixed, curve or automatic control before requesting additional cooling."); }
            if (id == Guid.Empty || !CoolingPadSettings.ValidRpm(rpm) || seconds is < 1 or > 30 || !clientAlive())
            { return Block("The cooling pad demand lease is invalid."); }
            if (leases.TryGetValue(id, out DemandLease? existing) && existing.Client != client)
            { return Block("This cooling pad demand lease belongs to another client."); }
            if (!leases.ContainsKey(id) && leases.Count >= 8) { return Block("The cooling pad demand lease limit was reached."); }
            leases[id] = new(client, rpm, clock.GetUtcNow().AddSeconds(seconds), clientAlive);
            nextCurveRead = DateTimeOffset.MinValue;
            UpdateStatus(previous => previous with { Message = "Cooling pad demand lease stored." });
            return Result(ControlOperationStatus.Stored);
        }
        finally { gate.Release(); }
    }

    internal async Task<ControlOperationResult> RemoveLeaseAsync(Guid id, string client, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (leases.TryGetValue(id, out DemandLease? lease) && lease.Client != client)
            { return Block("This cooling pad demand lease belongs to another client."); }
            leases.Remove(id);
            nextCurveRead = DateTimeOffset.MinValue;
            return new(ControlOperationStatus.Stored, "Cooling pad demand lease removed.");
        }
        finally { gate.Release(); }
    }

    internal async Task DrainAsync()
    {
        Revoke();
        await gate.WaitAsync().ConfigureAwait(false);
        try { await CloseDeviceAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task LoadOwnershipAsync(CancellationToken token)
    {
        if (ownershipLoaded) { return; }
        ownership = await store.LoadOwnershipAsync(token).ConfigureAwait(false);
        if (ownership is not null)
        {
            if (ownership.SchemaVersion != 1 || ownership.Baseline is null || string.IsNullOrWhiteSpace(ownership.Owner)
                || string.IsNullOrWhiteSpace(ownership.Baseline.Identity)
                || !ownership.FanChanged && !ownership.LightingChanged)
            { throw new InvalidDataException("Cooling pad restoration state is invalid."); }
            UpdateStatus(previous => previous with { RecoveryRequired = true, WritesPaused = true });
        }
        ownershipLoaded = true;
    }

    private async Task<CoolingPadDeviceSnapshot> ReadDeviceAsync(CancellationToken token)
    {
        if (device is { Usable: false })
        {
            // A timed-out exchange retires the session, and its later commands fail as Disposed
            // without reaching the pad. Reads, Apply and Release open a fresh exact-device session.
            connectionLost |= controlled is not null && status.Active;
            await CloseDeviceAsync(token).ConfigureAwait(false);
        }
        device ??= await provider.OpenAsync(token).ConfigureAwait(false);
        if (device is null)
        {
            switch (provider.ObserveConnection())
            {
                case CoolingPadConnectionState.Absent:
                    Revoke();
                    UpdateStatus(previous => previous with { Active = false, Connected = false, Connection = CoolingPadConnectionState.Absent });
                    break;
                case CoolingPadConnectionState.PresentExact:
                    UpdateStatus(previous => previous with { Connected = true, Connection = CoolingPadConnectionState.PresentExact });
                    break;
            }
            throw new IOException("Cooling pad control is temporarily unavailable.");
        }
        CoolingPadDeviceSnapshot snapshot = await device.ReadSnapshotAsync(token).ConfigureAwait(false);
        if (snapshot.Identity != device.Identity) { throw new InvalidDataException("Cooling pad identity changed."); }
        return snapshot;
    }

    private void Project(CoolingPadDeviceSnapshot snapshot) => UpdateStatus(previous => previous with
    {
        Connected = true,
        Connection = CoolingPadConnectionState.PresentExact,
        Identity = snapshot.Identity,
        ConfirmedFanMode = snapshot.FanMode,
        ConfirmedTargetRpm = snapshot.TargetRpm,
        ConfirmedBrightnessPercent = snapshot.BrightnessPercent,
        ConfirmedLightingMode = snapshot.LightingMode,
        MeasuredRpm = null,
    });

    private ControlOperationResult Fail(Exception exception, bool submitted)
    {
        bool uncertain = exception is CoolingPadOperationException operation
            ? operation.SubmissionUnknown : submitted;
        if (uncertain) { Pause("Cooling pad submission is uncertain. Read its configuration before continuing."); }
        else { Revoke(); UpdateStatus(previous => previous with { RecoveryRequired = ownership is not null || configurationFailure, Message = BoundMessage(exception.Message) }); }
        return Result(ControlOperationStatus.Failed);
    }

    private void Pause(string message)
    {
        Revoke();
        UpdateStatus(previous => previous with { WritesPaused = true, RecoveryRequired = ownership is not null || configurationFailure, Message = BoundMessage(message) });
        leases.Clear();
    }

    private ControlOperationResult Block(string message)
    {
        UpdateStatus(previous => previous with { Message = message });
        return Result(ControlOperationStatus.Blocked);
    }
    private static string BoundMessage(string message) => message.Length <= 2048 ? message : message[..2048];

    private ControlOperationResult Result(ControlOperationStatus result) => new(result, status.Message);
    private async Task CloseDeviceAsync(CancellationToken token = default)
    {
        if (device is not { } previous) { return; }
        device = null;
        Task retiring = previous.DisposeAsync().AsTask();
        try { await retiring.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            // The protocol retains its cooperative lease until actual native retirement.
            // Respect the caller's lifecycle budget without abandoning exception observation.
            _ = retiring.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }
    private void ExpireLeases()
    {
        foreach (Guid id in leases.Where(pair => pair.Value.Expires <= clock.GetUtcNow() || !pair.Value.Alive())
            .Select(pair => pair.Key).ToArray()) { leases.Remove(id); }
    }

    private async Task<int> CurveTargetAsync(CoolingPadSettings settings, CancellationToken token)
    {
        lastTemperatureTimestamp = null;
        bool cpu = settings.TemperatureSource is CoolingPadTemperatureSource.Cpu or CoolingPadTemperatureSource.Both;
        bool gpu = settings.TemperatureSource is CoolingPadTemperatureSource.Gpu or CoolingPadTemperatureSource.Both;
        FanTemperatureReadings reading = await temperatures.ReadForFanCurveAsync(cpu, gpu, token).ConfigureAwait(false);
        long acquired = clock.GetTimestamp();
        // A GPU confirmed powered down (Windows D3) produces no heat and has no reading.
        // As in the laptop curve, it is ignored rather than treated as missing; workload
        // absence alone does not establish that.
        bool gpuOff = gpu && reading.GpuCelsius is null && reading.GpuPoweredDown;
        // Availability is judged on the raw sample; only the curve input is conditioned.
        if (cpu && reading.CpuCelsius is not (>= 0 and <= 115) || gpu && !gpuOff && reading.GpuCelsius is not (>= 0 and <= 115))
        {
            ResetTemperatureHistory();
            throw new TemperatureUnavailableException();
        }
        FanTemperatureReadings filtered = temperatureFilter.Filter(reading);
        int target;
        // The laptop curve's emergency limits, held on the selected sources' raw readings, so a
        // quiet table or curve cannot hold the pad back. Smoothing would take 12-20 s to reach
        // a steady limit. Synapse has none. Both are updated so each keeps its own streak.
        bool emergency = cpuEmergency.Update(cpu ? reading.CpuCelsius : null, acquired, clock)
            | gpuEmergency.Update(gpu && !gpuOff ? reading.GpuCelsius : null, acquired, clock);
        if (emergency)
        {
            target = CoolingPadSettings.MaximumRpm;
        }
        else if (settings.FanMode == CoolingPadFanMode.Auto)
        {
            target = CoolingPadAutoCurves.Target(settings.AutoProfile, settings.TemperatureSource,
                filtered.CpuCelsius, gpuOff ? null : filtered.GpuCelsius);
        }
        else
        {
            target = cpu ? Evaluate(settings.CpuCurve, filtered.CpuCelsius!.Value) : CoolingPadSettings.MinimumRpm;
            if (gpu && !gpuOff) { target = Math.Max(target, Evaluate(settings.GpuCurve, filtered.GpuCelsius!.Value)); }
            // A GPU-only curve with the GPU off runs at its coolest point.
            else if (gpu && !cpu) { target = settings.GpuCurve[0].Rpm; }
        }
        lastTemperatureTimestamp = acquired;
        return target;
    }

    private void ResetTemperatureHistory()
    {
        temperatureFilter.Reset();
        cpuEmergency.Reset();
        gpuEmergency.Reset();
    }

    /// <summary>
    /// Full pad speed once a raw reading has stayed at or above the limit for
    /// <see cref="Confirmation"/>, so a transient package spike does not trip it. It holds
    /// until the reading falls <see cref="ReleaseMarginCelsius"/> below the limit, so a
    /// reading at the limit does not switch the speed back and forth.
    /// </summary>
    private sealed class EmergencyLimit(int limitCelsius)
    {
        internal static readonly TimeSpan Confirmation = TimeSpan.FromSeconds(2);
        internal const int ReleaseMarginCelsius = 3;
        private long? hotSince;
        private bool active;

        internal bool Update(int? celsius, long timestamp, TimeProvider clock)
        {
            if (celsius is not int value)
            {
                Reset();
                return false;
            }
            if (value >= limitCelsius)
            {
                hotSince ??= timestamp;
                if (clock.GetElapsedTime(hotSince.Value, timestamp) >= Confirmation) { active = true; }
            }
            else
            {
                hotSince = null;
                if (value < limitCelsius - ReleaseMarginCelsius) { active = false; }
            }
            return active;
        }

        internal void Reset()
        {
            hotSince = null;
            active = false;
        }
    }

    // Auto follows Synapse's Smart tables; HostCurve follows the user's curves. Both send 0D/01
    // demands from fresh host temperatures under the same freshness and admission gates.
    private static bool IsTemperatureDriven(CoolingPadFanMode mode) =>
        mode is CoolingPadFanMode.HostCurve or CoolingPadFanMode.Auto;

    private static CoolingPadFanPreset? SmartPreset(CoolingPadSettings settings) =>
        settings.FanMode == CoolingPadFanMode.Auto ? CoolingPadAutoCurves.Preset(settings.AutoProfile) : null;

    private bool TemperatureIsFresh() => lastTemperatureTimestamp is { } acquired
        && clock.GetElapsedTime(acquired, clock.GetTimestamp()) <= TimeSpan.FromSeconds(5);

    internal static int Evaluate(IReadOnlyList<CoolingPadCurvePoint> curve, int temperature)
    {
        if (temperature <= curve[0].TemperatureCelsius) { return curve[0].Rpm; }
        for (int i = 1; i < curve.Count; i++)
        {
            CoolingPadCurvePoint left = curve[i - 1], right = curve[i];
            if (temperature > right.TemperatureCelsius) { continue; }
            double fraction = (double)(temperature - left.TemperatureCelsius)
                / (right.TemperatureCelsius - left.TemperatureCelsius);
            return Math.Clamp((int)Math.Round((left.Rpm + fraction * (right.Rpm - left.Rpm)) / 50.0,
                MidpointRounding.AwayFromZero) * 50, CoolingPadSettings.MinimumRpm, CoolingPadSettings.MaximumRpm);
        }
        return curve[^1].Rpm;
    }

    private static CoolingPadDeviceSnapshot FreezeSnapshot(CoolingPadDeviceSnapshot snapshot) => snapshot with
    {
        LightingConfiguration = snapshot.LightingConfiguration is { } persistent ? persistent with { Colors = persistent.Colors.ToArray() } : null,
        ActiveLightingConfiguration = snapshot.ActiveLightingConfiguration is { } active ? active with { Colors = active.Colors.ToArray() } : null,
        LightingSettings = snapshot.LightingSettings is { } settings ? Freeze(settings) : null,
    };

    private static CoolingPadSettings Freeze(CoolingPadSettings settings) => settings with
    { CpuCurve = settings.CpuCurve.ToArray(), GpuCurve = settings.GpuCurve.ToArray() };
    private sealed record DemandLease(string Client, int Rpm, DateTimeOffset Expires, Func<bool> Alive);
}
