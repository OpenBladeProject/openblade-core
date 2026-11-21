using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

/// <summary>
/// One serial owner for exact-pad control. Saved intent is inert until an explicit Apply.
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
        try
        {
            try
            {
                await LoadOwnershipAsync(token).ConfigureAwait(false);
                CoolingPadSettings settings = await store.LoadSettingsAsync(user, token).ConfigureAwait(false);
                if (!CoolingPadSettings.IsWellFormed(settings)) { throw new InvalidDataException("Saved cooling pad settings are invalid."); }
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
            UpdateStatus(previous => previous with { WritesPaused = false, Message = "Cooling pad configuration was read." });
            if (ownership is null) { await CloseDeviceAsync(token).ConfigureAwait(false); }
            return Result(ControlOperationStatus.Stored);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { return Fail(exception, false); }
        finally { gate.Release(); }
    }

    internal async Task<ControlOperationResult> ApplyAsync(CoolingPadSettings settings,
        Func<bool> admission, CancellationToken token, Func<bool>? continuedOwnership = null)
    {
        if (!CoolingPadSettings.IsWellFormed(settings)) { return Block("Cooling pad settings are invalid."); }
        settings = Freeze(settings);
        await gate.WaitAsync(token).ConfigureAwait(false);
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
            bool lighting = settings.LightingMode != CoolingPadLightingMode.Keep;
            if (fan && baseline.FanConfiguration is null
                || lighting && (baseline.LightingConfiguration is null || baseline.BrightnessRaw is null))
            { return Block("The affected cooling pad settings could not be read for restoration."); }

            // New settings start a new filter history from the first raw sample.
            temperatureFilter.Reset();
            int? target = IsTemperatureDriven(settings.FanMode)
                ? await CurveTargetAsync(settings, token).ConfigureAwait(false)
                : settings.FanMode == CoolingPadFanMode.Fixed ? settings.FixedRpm : null;
            await store.SaveSettingsAsync(owner, settings, token).ConfigureAwait(false);
            UpdateStatus(previous => previous with { Requested = settings });
            if (!settings.AllowDemandApi) { leases.Clear(); }
            authority = continuedOwnership ?? admission;
            long epoch = Interlocked.Read(ref generation);
            bool Allowed() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && admission();
            bool OwnershipContinues() => epoch == Interlocked.Read(ref generation) && !token.IsCancellationRequested && authority();
            if (!Allowed()) { return Block("Cooling pad authority changed before submission."); }
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
            Project(await device!.ReadSnapshotAsync(token).ConfigureAwait(false));
            UpdateStatus(previous => previous with
            {
                Active = (fan || lighting) && OwnershipContinues(),
                RecoveryRequired = (!(fan || lighting) || !OwnershipContinues()) && ownership is not null,
                WritesPaused = false,
                Message = fan || lighting ? "Cooling pad settings applied." : "Cooling pad settings saved.",
            });
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
        finally { gate.Release(); }
    }

    private sealed class TemperatureUnavailableException()
        : InvalidOperationException("Cooling pad curve is waiting for a reading from its selected temperature source.");

    internal async Task<ControlOperationResult> ReleaseAsync(Func<bool> admission, CancellationToken token,
        bool serviceOwnedRestoration = false)
    {
        Revoke();
        await gate.WaitAsync(token).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            leases.Clear();
            await LoadOwnershipAsync(token).ConfigureAwait(false);
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

    internal async Task MaintainAsync(Func<bool> admission, CancellationToken token)
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
            if (clock.GetUtcNow() >= nextCurveRead
                && (settings.FanMode == CoolingPadFanMode.Fixed || IsTemperatureDriven(settings.FanMode)))
            {
                nextCurveRead = clock.GetUtcNow().AddSeconds(1);
                ExpireLeases();
                int target = IsTemperatureDriven(settings.FanMode)
                    ? await CurveTargetAsync(settings, token).ConfigureAwait(false) : settings.FixedRpm;
                if (settings.AllowDemandApi && leases.Count != 0) { target = Math.Max(target, leases.Values.Max(value => value.Rpm)); }
                if (status.ConfirmedTargetRpm != target)
                {
                    submitted = true;
                    await device!.SetFanAsync(settings.FanMode, target,
                        () => Allowed() && (!IsTemperatureDriven(settings.FanMode) || TemperatureIsFresh()), token,
                        SmartPreset(settings)).ConfigureAwait(false);
                    // Typed SetFan completes only after independent fan configuration readback.
                    UpdateStatus(previous => previous with { ConfirmedTargetRpm = target });
                }
            }
            if (settings.LightingMode == CoolingPadLightingMode.KeyboardMirror
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
            temperatureFilter.Reset();
            throw new TemperatureUnavailableException();
        }
        FanTemperatureReadings filtered = temperatureFilter.Filter(reading);
        int target;
        if (settings.FanMode == CoolingPadFanMode.Auto)
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
