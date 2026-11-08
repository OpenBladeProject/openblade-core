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
    public string Identity { get; } = identity;

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
        CoolingPadProtocol.TryDecodeEffect(effect.Span, out var light);
        var activeEffect = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeEffect(activeEffect.Span, out var activeLight);
        var appMode = fan.Preset == CoolingPadFanPreset.Off ? CoolingPadFanMode.Off
            : fan.Ownership == CoolingPadFanOwnership.Synapse ? CoolingPadFanMode.Fixed : (CoolingPadFanMode?)null;
        var lightMode = DecodeLight(light!.Effect);
        int configuredTarget = fan.Ownership == CoolingPadFanOwnership.Synapse ? fan.ConfiguredRpm
            : fan.Preset == CoolingPadFanPreset.Off ? 0 : target.Span[2] * 50;
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

    public async Task SetFanAsync(CoolingPadFanMode mode, int? rpm, Func<bool> submissionAllowed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submissionAllowed);
        if (mode is CoolingPadFanMode.Fixed or CoolingPadFanMode.HostCurve)
        { CoolingPadProtocol.ValidateTarget(rpm ?? throw new ArgumentNullException(nameof(rpm))); }
        else if (mode is not (CoolingPadFanMode.Off or CoolingPadFanMode.Keep))
        { throw new ArgumentOutOfRangeException(nameof(mode)); }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireWritable();
            partialMutation = false;
            if (mode == CoolingPadFanMode.Keep) { return; }
            if (mode == CoolingPadFanMode.HostCurve)
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
                await Execute(CoolingPadProtocol.DemandSet(rpm!.Value), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                var target = await Execute(CoolingPadProtocol.TargetQuery(), false, null, cancellationToken).ConfigureAwait(false);
                if (target.Span[2] * 50 != rpm) { FailReadback("Cooling-pad target readback differs from the request."); }
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
        var confirmed = await ReadFan(token).ConfigureAwait(false);
        if (confirmed != state) { FailReadback("Cooling-pad fan-mode readback differs from the request."); }
    }

    private async Task<CoolingPadFanConfiguration> ReadFan(CancellationToken token)
    {
        var data = await Execute(CoolingPadProtocol.FanQuery(), false, null, token).ConfigureAwait(false);
        CoolingPadProtocol.TryDecodeFan(data.Span, out var fan);
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
            // Built-in effects persist in profile 1. Custom activation uses the reviewed volatile profile 0.
            await Execute(CoolingPadProtocol.EffectSet(effect), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
            await WriteBrightness(CoolingPadProtocol.PercentToRaw(settings.BrightnessPercent), submissionAllowed, cancellationToken).ConfigureAwait(false);
            mirrorActive = settings.LightingMode == CoolingPadLightingMode.KeyboardMirror;
            if (mirrorActive)
            {
                var active = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(active.Span, out var activeConfiguration);
                if (!SameEffect(effect, activeConfiguration!)) { FailReadback("Cooling-pad custom activation was not confirmed."); }
                customBufferOwned = true;
            }
            if (!mirrorActive)
            {
                var actual = await Execute(CoolingPadProtocol.EffectQuery(), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(actual.Span, out var confirmed);
                if (!SameEffect(effect, confirmed!)) { FailReadback("Cooling-pad effect readback differs from the request."); }
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
                    await Execute(CoolingPadProtocol.DemandSet(baseline.TargetRpm.Value), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                    var target = await Execute(CoolingPadProtocol.TargetQuery(), false, null, cancellationToken).ConfigureAwait(false);
                    if (target.Span[2] * 50 != baseline.TargetRpm) { FailReadback("Cooling-pad target restoration was not confirmed."); }
                }
            }
            if (restoreLighting)
            {
                var light = baseline.LightingConfiguration ?? throw new CoolingPadOperationException("The original lighting effect is unavailable.", false);
                await Execute(CoolingPadProtocol.EffectSet(light), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                if (baseline.BrightnessRaw is not { } raw) { throw new CoolingPadOperationException("The original brightness is unavailable.", partialMutation); }
                await WriteBrightness(raw, submissionAllowed, cancellationToken).ConfigureAwait(false);
                if (baseline.ActiveLightingConfiguration is { } activeBaseline)
                {
                    await Execute(CoolingPadProtocol.EffectSet(activeBaseline), true, submissionAllowed, cancellationToken).ConfigureAwait(false);
                    var active = await Execute(CoolingPadProtocol.EffectQuery(0), false, null, cancellationToken).ConfigureAwait(false);
                    CoolingPadProtocol.TryDecodeEffect(active.Span, out var confirmedActive);
                    if (!SameEffect(activeBaseline, confirmedActive!)) { FailReadback("Cooling-pad active effect restoration was not confirmed."); }
                }
                if (baseline.DeviceMode is { } mode)
                {
                    var currentMode = await Execute(CoolingPadProtocol.DeviceModeQuery(), false, null, cancellationToken).ConfigureAwait(false);
                    if (currentMode.Span[0] != (byte)mode)
                    { await WriteMode(mode, submissionAllowed, cancellationToken).ConfigureAwait(false); }
                }
                var actual = await Execute(CoolingPadProtocol.EffectQuery(), false, null, cancellationToken).ConfigureAwait(false);
                CoolingPadProtocol.TryDecodeEffect(actual.Span, out var confirmed);
                if (!SameEffect(light, confirmed!)) { FailReadback("Cooling-pad lighting restoration was not confirmed."); }
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
        }
        if (knownNotSubmitted && !partialMutation) { token.ThrowIfCancellationRequested(); }
        uncertain |= partialMutation || write && !knownNotSubmitted;
        throw new CoolingPadOperationException($"Cooling-pad operation failed: {result.Failure}.", uncertain)
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
    private static bool SameEffect(CoolingPadLightingConfiguration a, CoolingPadLightingConfiguration b) =>
        a.Profile == b.Profile && a.Region == b.Region && a.Effect == b.Effect && a.Flags == b.Flags
        && a.Rate == b.Rate && a.Colors.SequenceEqual(b.Colors);

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
