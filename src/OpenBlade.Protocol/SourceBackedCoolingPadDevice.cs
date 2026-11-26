using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class SourceBackedCoolingPadDevice(
    ValidationActor actor, string identity, Func<Task> released) : ICoolingPadDevice, ICoolingPadRestorationProvenance
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool uncertain;
    private volatile bool disposed;
    private bool mirrorActive;
    private bool customBufferOwned;
    private CoolingPadDeviceSnapshot? restorationBaseline;
    private byte? brightnessRegion;
    private CoolingPadDeviceMode? originalMirrorMode;
    private bool partialMutation;
    // The firmware runs the preset set last by either owner and cannot report which one that was.
    // Until this session confirms its own fan write, the firmware-owned selection is reported.
    private CoolingPadFanOwnership runningOwner = CoolingPadFanOwnership.Firmware;
    // Set once this session has written and independently confirmed a fan selection.
    private bool fanWriteConfirmed;
    // The effect this session wrote last and confirmed by readback. The strip shows the last
    // effect written, so it identifies the showing record while that record still holds it.
    private CoolingPadLightingConfiguration? lastConfirmedEffect;
    public string Identity { get; } = identity;
    public bool Usable => !disposed && actor.Session.Disposition == RazerCommandSessionDisposition.Healthy;

    public void BindLightingBaseline(CoolingPadDeviceSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!gate.Wait(0)) { throw new CoolingPadOperationException("The cooling-pad session is busy.", false); }
        try
        {
            RequireWritable();
            if (baseline.Identity != Identity || baseline.BrightnessRaw is null
                || baseline.DeviceMode is not { } originalMode || !Enum.IsDefined(originalMode)
                || baseline.LightingConfiguration is not { Profile: 1, Region: 0, Effect: not 8 } persistent
                || baseline.ActiveLightingConfiguration is not { Profile: 0, Region: 0, Effect: not 8 } active)
            { throw new CoolingPadOperationException("The original lighting restoration state is incomplete.", false); }
            // Revalidate and freeze the protected journal's values; current custom pixels are not
            // read or claimed. A later explicit new destination still has independent admission.
            var saved = baseline with
            {
                LightingConfiguration = new(persistent.Profile, persistent.Region, persistent.Effect, persistent.Flags, persistent.Rate, persistent.Colors),
                ActiveLightingConfiguration = new(active.Profile, active.Region, active.Effect, active.Flags, active.Rate, active.Colors),
            };
            restorationBaseline = saved;
        }
        finally { gate.Release(); }
    }
    public async Task<CoolingPadDeviceSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCore(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<CoolingPadDeviceSnapshot> ReadCore(CancellationToken token)
    {
        var fan = await ReadFan(token).ConfigureAwait(false);
        var target = await Execute(CoolingPadProtocol.TargetQuery(), false, null, token).ConfigureAwait(false);
        var mode = await Execute(CoolingPadProtocol.DeviceModeQuery(), false, null, token).ConfigureAwait(false);
        var region = await Execute(CoolingPadProtocol.RegionQuery(), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeRegion(region.Span, out byte regionId);
        brightnessRegion = regionId;
        var brightness = await Execute(CoolingPadProtocol.BrightnessQuery(regionId), false, null, token).ConfigureAwait(false);
        var effect = await Execute(CoolingPadProtocol.EffectQuery(), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeEffect(effect.Span, 1, out var light);
        var activeEffect = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeEffect(activeEffect.Span, 0, out var activeLight);
        var appMode = fan.Preset == CoolingPadFanPreset.Off ? CoolingPadFanMode.Off
            : fan.Ownership == CoolingPadFanOwnership.Synapse ? CoolingPadFanMode.Fixed : (CoolingPadFanMode?)null;
        // Profile 1 is the stored effect and profile 0 the unsaved one; the strip shows the last
        // one written. Two different effects say which is showing only when this session wrote
        // the last one and its record still holds it; otherwise neither is confirmed. A record holding
        // an effect that is not decoded (7 or 9) reads as null, and the showing effect is then unknown.
        var lightMode = light is null || activeLight is null ? null
            : light.Effect == activeLight.Effect ? DecodeLight(light.Effect)
            : lastConfirmedEffect is { } last && CoolingPadProtocol.ConfirmsEffect(last, last.Profile == 1 ? light : activeLight)
                ? DecodeLight(last.Effect)
                : null;
        // Low/Medium/High report their configured speed; Smart presets only the ramping speed from 0D/81.
        int? configuredTarget = fan.Preset is CoolingPadFanPreset.Low or CoolingPadFanPreset.Medium or CoolingPadFanPreset.High
            ? fan.ConfiguredRpm
            : fan.Preset == CoolingPadFanPreset.Off ? 0 : CoolingPadProtocol.DecodeTarget(target.Span[2]);
        return new(Identity, appMode, configuredTarget,
            CoolingPadProtocol.RawToPercent(brightness.Span[2]), lightMode)
        {
            FanConfiguration = fan,
            LightingConfiguration = light,
            ActiveLightingConfiguration = activeLight,
            BrightnessRaw = brightness.Span[2],
            DeviceMode = (CoolingPadDeviceMode)mode.Span[0],
        };
    }

    public async Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> submissionAllowed, CancellationToken cancellationToken,
        CoolingPadFanPreset? smartPreset = null)
    {
        ArgumentNullException.ThrowIfNull(submissionAllowed);
        if (mode is CoolingPadFanMode.Fixed or CoolingPadFanMode.HostCurve or CoolingPadFanMode.Auto)
        { CoolingPadProtocol.ValidateTarget(rpm ?? throw new ArgumentNullException(nameof(rpm))); }
        else if (mode is not (CoolingPadFanMode.Off or CoolingPadFanMode.Keep))
        { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (mode == CoolingPadFanMode.Auto
            && smartPreset is not (CoolingPadFanPreset.Quiet or CoolingPadFanPreset.Balanced or CoolingPadFanPreset.Performance))
        { throw new ArgumentOutOfRangeException(nameof(smartPreset)); }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWritable();
            partialMutation = false;
            if (mode == CoolingPadFanMode.Keep) { return; }
            if (mode == CoolingPadFanMode.Auto)
            {
                // Synapse Smart: setThermalFanCoolingMode(smart.active, 0, 0) selects the profile's
                // preset in the firmware-owned record, then 0D/01 carries each demand. Go there
                // directly. Skip the selection only when this session's own confirmed write left
                // exactly that preset running: the firmware cannot report which owner wrote last.
                var smart = new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, smartPreset!.Value, 0);
                var current = await ReadFan(cancellationToken).ConfigureAwait(false);
                if (!fanWriteConfirmed || runningOwner != CoolingPadFanOwnership.Firmware || current != smart)
                {
                    await WriteFan(smart, submissionAllowed, cancellationToken).ConfigureAwait(false);
                }
                // The verified echo confirms the demand; 0D/81 reports the ramping speed, not this target.
                await Execute(CoolingPadProtocol.DemandSet(rpm!.Value), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
            }
            else if (mode == CoolingPadFanMode.HostCurve)
            {
                var current = await ReadFan(cancellationToken).ConfigureAwait(false);
                // Preserve an already selected vendor Smart preset. Entry from another mode goes
                // directly to Quiet, the source-defined Smart destination; no Fixed/Medium reset.
                if (current.Ownership != CoolingPadFanOwnership.Firmware
                    || current.Preset is not (CoolingPadFanPreset.Quiet or CoolingPadFanPreset.Balanced or CoolingPadFanPreset.Performance))
                {
                    var smart = new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Quiet, 0);
                    await WriteFan(smart, submissionAllowed, cancellationToken).ConfigureAwait(false);
                }
                // The verified echo confirms the demand; 0D/81 reports the ramping speed, not this target.
                await Execute(CoolingPadProtocol.DemandSet(rpm!.Value), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var state = mode == CoolingPadFanMode.Off
                    ? new CoolingPadFanConfiguration(CoolingPadFanOwnership.Firmware, CoolingPadFanPreset.Off, 0)
                    : new CoolingPadFanConfiguration(CoolingPadFanOwnership.Synapse, CoolingPadFanPreset.Medium, rpm!.Value);
                await WriteFan(state, submissionAllowed, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !partialMutation) { throw; }
        catch (Exception exception) { throw MapFailure(exception); }
        finally { partialMutation = false; gate.Release(); }
    }

    private async Task WriteFan(CoolingPadFanConfiguration state, Func<bool> allowed, CancellationToken token)
    {
        await Execute(CoolingPadProtocol.FanSet(state), true, allowed, token).ConfigureAwait(false);
        // The firmware records the selection under the writing owner; confirm that owner's record.
        var confirmed = await ReadFan(state.Ownership, token).ConfigureAwait(false);
        if (confirmed != state) { FailReadback("Cooling-pad fan-mode readback differs from the request."); }
        runningOwner = state.Ownership;
        fanWriteConfirmed = true;
    }

    private Task<CoolingPadFanConfiguration> ReadFan(CancellationToken token) => ReadFan(runningOwner, token);

    private async Task<CoolingPadFanConfiguration> ReadFan(CoolingPadFanOwnership owner, CancellationToken token)
    {
        var data = await Execute(CoolingPadProtocol.FanQuery(owner), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeFan(data.Span, owner, out var fan);
        return fan!;
    }

    public async Task SetLightingAsync(CoolingPadSettings settings, Func<bool> submissionAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(submissionAllowed);
        if (!CoolingPadSettings.IsWellFormed(settings)) { throw new ArgumentException("Invalid cooling-pad settings.", nameof(settings)); }
        if (settings.LightingMode == CoolingPadLightingMode.Keep) { return; }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWritable();
            partialMutation = false;
            var baseline = await ReadCore(cancellationToken).ConfigureAwait(false);
            // Effect GET cannot recover another renderer's custom pixel buffer. Refuse replacing it.
            if (settings.LightingMode == CoolingPadLightingMode.KeyboardMirror
                && (baseline.LightingConfiguration?.Effect == 8 || baseline.ActiveLightingConfiguration?.Effect == 8)
                && !customBufferOwned && restorationBaseline is null)
            { throw new CoolingPadOperationException("Stop the existing custom lighting renderer before starting keyboard sync.", false); }
            if (settings.LightingMode == CoolingPadLightingMode.KeyboardMirror)
            {
                originalMirrorMode ??= restorationBaseline?.DeviceMode ?? baseline.DeviceMode;
                if (baseline.DeviceMode != CoolingPadDeviceMode.Driver)
                {
                    await WriteMode(CoolingPadDeviceMode.Driver, submissionAllowed, cancellationToken).ConfigureAwait(false);
                }
            }
            var effect = CoolingPadProtocol.EffectFor(settings);
            lastConfirmedEffect = null;
            // Built-in effects persist in profile 1. Custom activation uses the reviewed volatile profile 0.
            await Execute(CoolingPadProtocol.EffectSet(effect), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
            await WriteBrightness(CoolingPadProtocol.PercentToRaw(settings.BrightnessPercent), submissionAllowed, cancellationToken).ConfigureAwait(false);
            mirrorActive = settings.LightingMode == CoolingPadLightingMode.KeyboardMirror;
            if (mirrorActive)
            {
                var active = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(active.Span, 0, out var activeConfiguration);
                if (activeConfiguration is null || !CoolingPadProtocol.ConfirmsEffect(effect, activeConfiguration)) { FailReadback("Cooling-pad custom activation was not confirmed."); }
                customBufferOwned = true;
                lastConfirmedEffect = effect;
            }
            if (!mirrorActive)
            {
                var actual = await Execute(CoolingPadProtocol.EffectQuery(), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(actual.Span, 1, out var confirmed);
                if (confirmed is null || !CoolingPadProtocol.ConfirmsEffect(effect, confirmed)) { FailReadback("Cooling-pad effect readback differs from the request."); }
                lastConfirmedEffect = effect;
                if (originalMirrorMode is { } saved)
                {
                    await WriteMode(saved, submissionAllowed, cancellationToken).ConfigureAwait(false);
                    originalMirrorMode = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !partialMutation) { throw; }
        catch (Exception exception) { throw MapFailure(exception); }
        finally { partialMutation = false; gate.Release(); }
    }

    public async Task PushMirrorFrameAsync(IReadOnlyList<CoolingPadColor> colors, Func<bool> submissionAllowed, CancellationToken cancellationToken)
    {
        // Copy before the native queue; callers cannot change a queued payload.
        var command = CoolingPadProtocol.FrameSet(colors);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWritable();
            partialMutation = false;
            if (!mirrorActive) { throw new CoolingPadOperationException("Keyboard lighting sync is inactive.", false); }
            // The exact source streams frames with SetFeature only. Native completion confirms
            // submission, not pixel readback; no GET/retry is invented for these host frames.
            await Execute(command, true, submissionAllowed, cancellationToken, setOnly: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !partialMutation) { throw; }
        catch (Exception exception) { throw MapFailure(exception); }
        finally { partialMutation = false; gate.Release(); }
    }

    public async Task RestoreAsync(CoolingPadDeviceSnapshot baseline, bool restoreFan, bool restoreLighting,
        Func<bool> submissionAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(submissionAllowed);
        if (baseline.Identity != Identity) { throw new CoolingPadOperationException("Cooling-pad baseline belongs to another device.", false); }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWritable();
            partialMutation = false;
            if (restoreFan)
            {
                var fan = baseline.FanConfiguration ?? throw new CoolingPadOperationException("The original fan configuration is unavailable.", false);
                await WriteFan(fan, submissionAllowed, cancellationToken).ConfigureAwait(false);
                if (fan.Ownership == CoolingPadFanOwnership.Firmware
                    && fan.Preset is CoolingPadFanPreset.Quiet or CoolingPadFanPreset.Balanced or CoolingPadFanPreset.Performance
                    && baseline.TargetRpm is >= 500 and <= 3200)
                {
                    // The verified echo confirms the demand; 0D/81 reports the ramping speed, not this target.
                    await Execute(CoolingPadProtocol.DemandSet(baseline.TargetRpm.Value), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                }
            }
            if (restoreLighting)
            {
                var light = baseline.LightingConfiguration ?? throw new CoolingPadOperationException("The original lighting effect is unavailable.", false);
                lastConfirmedEffect = null;
                await Execute(CoolingPadProtocol.EffectSet(light), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                if (baseline.BrightnessRaw is not { } raw) { throw new CoolingPadOperationException("The original brightness is unavailable.", partialMutation); }
                await WriteBrightness(raw, submissionAllowed, cancellationToken).ConfigureAwait(false);
                if (baseline.ActiveLightingConfiguration is { } activeBaseline)
                {
                    await Execute(CoolingPadProtocol.EffectSet(activeBaseline), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                    var active = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, cancellationToken).ConfigureAwait(false);
                    CoolingPadProtocol.TryDecodeEffect(active.Span, 0, out var confirmedActive);
                    if (confirmedActive is null || !CoolingPadProtocol.ConfirmsEffect(activeBaseline, confirmedActive)) { FailReadback("Cooling-pad active effect restoration was not confirmed."); }
                }
                if (baseline.DeviceMode is { } mode)
                {
                    var currentMode = await Execute(CoolingPadProtocol.DeviceModeQuery(), false, null, cancellationToken).ConfigureAwait(false);
                    if (currentMode.Span[0] != (byte)mode)
                    { await WriteMode(mode, submissionAllowed, cancellationToken).ConfigureAwait(false); }
                }
                var actual = await Execute(CoolingPadProtocol.EffectQuery(), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(actual.Span, 1, out var confirmed);
                if (confirmed is null || !CoolingPadProtocol.ConfirmsEffect(light, confirmed)) { FailReadback("Cooling-pad lighting restoration was not confirmed."); }
                // The active record is written after the stored one when the baseline has it.
                lastConfirmedEffect = baseline.ActiveLightingConfiguration ?? light;
                mirrorActive = false;
                customBufferOwned = false;
                originalMirrorMode = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !partialMutation) { throw; }
        catch (Exception exception) { throw MapFailure(exception); }
        finally { partialMutation = false; gate.Release(); }
    }

    private async Task WriteMode(CoolingPadDeviceMode mode, Func<bool> allowed, CancellationToken token)
    {
        await Execute(CoolingPadProtocol.DeviceModeSet(mode), true, allowed, token).ConfigureAwait(false);
        var result = await Execute(CoolingPadProtocol.DeviceModeQuery(), false, null, token).ConfigureAwait(false);
        if (result.Span[0] != (byte)mode) { FailReadback("Cooling-pad device-mode readback differs from the request."); }
    }

    private async Task WriteBrightness(byte raw, Func<bool> allowed, CancellationToken token)
    {
        if (brightnessRegion is not { } region) { throw new CoolingPadOperationException("Cooling-pad lighting geometry has not been read.", false); }
        await Execute(CoolingPadProtocol.BrightnessSet(raw), true, allowed, token).ConfigureAwait(false);
        var actual = await Execute(CoolingPadProtocol.BrightnessQuery(region), false, null, token).ConfigureAwait(false);
        if (actual.Span[2] != raw) { FailReadback("Cooling-pad brightness readback differs from the request."); }
    }

    // Names the failing typed command and the session's own classification so an exported
    // status identifies which read or write stopped. No response payload bytes are included.
    internal static string DescribeFailure(RazerCommandSpec command, HidSessionResult<ReadOnlyMemory<byte>> result)
    {
        var detail = new List<string>(4) { $"{command.ClassId:X2}/{command.CommandId:X2}" };
        if (result.Diagnostic is { } diagnostic)
        {
            detail.Add(diagnostic.Phase.ToString());
            if (diagnostic.ProtocolValidationFailure is { } validation) { detail.Add(validation.ToString()); }
            // A correlated reply whose content the decoder rejected keeps its bounded bytes, so a
            // report the reviewed decoder does not cover can be read from the diagnostics log.
            if (diagnostic.RejectedPayloadHex is { Length: > 0 } payload) { detail.Add($"payload {payload}"); }
            if (diagnostic.NativeWin32Error is { } error) { detail.Add($"Win32 {error}"); }
        }
        if (result.QueryResponseStatus is { } status) { detail.Add($"status 0x{status:X2}"); }
        return $"Cooling-pad operation failed: {result.Failure} ({string.Join(", ", detail)}).";
    }

    private async Task<ReadOnlyMemory<byte>> Execute(RazerCommandSpec command, bool write, Func<bool>? allowed,
        CancellationToken token, bool setOnly = false)
    {
        if (disposed || uncertain) { throw new CoolingPadOperationException("Cooling-pad writes require explicit recovery after an uncertain operation.", uncertain); }
        bool admissionRevoked = false;
        if (write)
        {
            command = command with
            {
                SubmissionAllowed = () =>
                {
                    bool admitted = !disposed && !uncertain && !token.IsCancellationRequested && allowed!() && !token.IsCancellationRequested;
                    admissionRevoked = !admitted;
                    return admitted;
                },
            };
        }
        byte tid = command.ClassId == 0x0F ? (byte)0x1F : (byte)0x02;
        Task<HidSessionResult<ReadOnlyMemory<byte>>> pending;
        if (setOnly && actor.Session is IRazerSetFeatureCommandSession submitter)
        { pending = submitter.SubmitAsync(command, () => TransactionIdAssignment.Valid(tid), TimeSpan.FromSeconds(3), token); }
        else
        { pending = actor.Session.ExecuteAsync(command, () => TransactionIdAssignment.Valid(tid), TimeSpan.FromSeconds(3), token); }
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            // A synchronous Windows guard inside the actor can stall beyond its own deadline.
            // Returning here must revoke any queued setter; the retained actor owns native retirement.
            result = await pending.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            uncertain |= write || partialMutation;
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            if (uncertain) { throw new CoolingPadOperationException("Cooling-pad completion is uncertain; further writes are paused.", true); }
            throw;
        }
        if (result.Failure is null)
        {
            partialMutation |= write;
            return result.Value;
        }
        bool knownNotSubmitted = result.Failure is HidSessionFailureKind.IdentityBlocked or HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed or HidSessionFailureKind.QueuedCanceled
            or HidSessionFailureKind.Exhausted or HidSessionFailureKind.Disposed or HidSessionFailureKind.Poisoned;
        if (!admissionRevoked && result.Failure is (HidSessionFailureKind.ConflictBlocked or HidSessionFailureKind.ConflictDetectionFailed or HidSessionFailureKind.IdentityBlocked))
        {
            // A real controller/identity boundary invalidates pixel ownership. A proven late
            // local admission revocation preserves the healthy retained renderer epoch.
            customBufferOwned = false;
            restorationBaseline = null;
            mirrorActive = false;
            // Another controller may have written since; the last confirmed write is unknown.
            lastConfirmedEffect = null;
        }
        if (knownNotSubmitted && !partialMutation) { token.ThrowIfCancellationRequested(); }
        uncertain |= partialMutation || write && !knownNotSubmitted;
        throw new CoolingPadOperationException(DescribeFailure(command, result), uncertain)
        {
            LocalAdmissionDeferred = write && admissionRevoked && knownNotSubmitted && !partialMutation && !uncertain,
        };
    }

    private void RequireWritable()
    {
        if (disposed || uncertain) { throw new CoolingPadOperationException("Read and reconcile cooling-pad state before continuing.", uncertain); }
    }
    private void FailReadback(string message) { uncertain = true; throw new CoolingPadOperationException(message, true); }
    private Exception MapFailure(Exception exception)
    {
        if (partialMutation) { uncertain = true; }
        return exception is CoolingPadOperationException operation
            ? operation.SubmissionUnknown || !uncertain ? operation : new CoolingPadOperationException(operation.Message, true)
            : new CoolingPadOperationException(exception.Message, uncertain);
    }
    private static CoolingPadLightingMode? DecodeLight(byte effect) => effect switch
    {
        0 => CoolingPadLightingMode.Off,
        1 => CoolingPadLightingMode.Static,
        2 => CoolingPadLightingMode.Breathing,
        3 => CoolingPadLightingMode.Spectrum,
        4 => CoolingPadLightingMode.Wave,
        8 => CoolingPadLightingMode.KeyboardMirror,
        _ => null
    };

    private readonly object disposalGate = new();
    private Task? disposal;

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            disposed = true;
            return new(disposal ??= DisposeCore());
        }
    }

    private async Task DisposeCore()
    {
        Task retiring = actor.DisposeAsync().AsTask();
        Task release = retiring.ContinueWith(async task =>
        {
            _ = task.Exception;
            if (task.IsCompletedSuccessfully && actor.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed)
            { await released().ConfigureAwait(false); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        try { await release.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Keep the provider slot and cooperative lease until actual native disposal completes.
            // The continuation releases them later; no replacement handle can bypass this boundary.
            _ = release.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }
}
