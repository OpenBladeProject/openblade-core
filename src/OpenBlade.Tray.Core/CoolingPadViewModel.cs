using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record CoolingPadChoice<T>(T Value, string Label);

/// <summary>
/// Accessory controls. Loading or observing the editor never writes; a user change
/// applies the complete selection after a short debounce, like the laptop controls.
/// </summary>
public sealed class CoolingPadViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan DefaultApplyDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(3);
    // A refused change is tried again this many times before the page reports it.
    internal const int MaxRefusedRetries = 5;
    private readonly IServiceClient client;
    private readonly IUiDispatcher dispatcher;
    private readonly CancellationToken lifetime;
    private readonly TimeSpan applyDelay;
    private readonly TimeSpan refreshInterval;
    private readonly TimeSpan retryDelay;
    private int refusedRetries;
    private bool retryPending;
    private CoolingPadStatusSnapshot status = new();
    private bool busy;
    private bool applying;
    private bool uncertainSubmission;
    private bool editorInitialized;
    private bool suppressApply;
    private bool applyPending;
    private bool changePending;
    private long applyTicket;
    // The service request and control state the editor currently shows.
    private CoolingPadSettings? editorSource;
    private bool editorShowsModes;
    // True when the editor shows no lighting because the pad keeps lighting changed on it.
    private bool editorKeepsLighting;
    private bool serviceAvailable;
    private bool lastConfirmedConnected;
    private bool connectionKnown;
    private bool allowDemandApi;
    private bool pageVisible;
    private CancellationTokenSource? refreshLoop;
    private string? feedback;
    private CoolingPadChoice<CoolingPadFanMode>? fanMode;
    private CoolingPadChoice<CoolingPadAutoProfile>? autoProfile;
    private CoolingPadChoice<CoolingPadLightingMode>? lightingMode;
    private CoolingPadChoice<CoolingPadWaveDirection>? waveDirection;
    private CoolingPadChoice<CoolingPadTemperatureSource>? temperatureSource;
    private int fixedRpm = 1500;
    private double brightness = 100;
    private RgbColor primaryColor = new(0, 255, 0);
    private RgbColor secondaryColor = new(0, 0, 255);

    public CoolingPadViewModel(IServiceClient client, IUiDispatcher dispatcher, CancellationToken lifetime = default,
        TimeSpan? applyDelay = null, TimeSpan? refreshInterval = null, TimeSpan? retryDelay = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.lifetime = lifetime;
        this.applyDelay = applyDelay ?? DefaultApplyDelay;
        this.refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        this.retryDelay = retryDelay ?? DefaultRetryDelay;
        FanModes = [new(CoolingPadFanMode.Auto, TrayText.Get("Automatic")),
            new(CoolingPadFanMode.Fixed, TrayText.Get("Fixed speed")),
            new(CoolingPadFanMode.HostCurve, TrayText.Get("Manual fan curve")),
            new(CoolingPadFanMode.Off, TrayText.Get("Off"))];
        AutoProfiles = [new(CoolingPadAutoProfile.Quiet, TrayText.Get("Quiet")),
            new(CoolingPadAutoProfile.Balanced, TrayText.Get("Balanced")),
            new(CoolingPadAutoProfile.Performance, TrayText.Get("Performance"))];
        LightingModes = [new(CoolingPadLightingMode.Off, TrayText.Get("Off")),
            new(CoolingPadLightingMode.Static, TrayText.Get("Static")),
            new(CoolingPadLightingMode.Breathing, TrayText.Get("Breathing")),
            new(CoolingPadLightingMode.Spectrum, TrayText.Get("Spectrum")),
            new(CoolingPadLightingMode.Wave, TrayText.Get("Wave")),
            new(CoolingPadLightingMode.KeyboardMirror, TrayText.Get("Mirror keyboard"))];
        WaveDirections = [new(CoolingPadWaveDirection.Left, TrayText.Get("Leftward")),
            new(CoolingPadWaveDirection.Right, TrayText.Get("Rightward"))];
        TemperatureSources = [new(CoolingPadTemperatureSource.Cpu, TrayText.Get("CPU")),
            new(CoolingPadTemperatureSource.Gpu, TrayText.Get("GPU")),
            new(CoolingPadTemperatureSource.Both, TrayText.Get("Both"))];
        autoProfile = AutoProfiles[1]; waveDirection = WaveDirections[1]; temperatureSource = TemperatureSources[2];
        CpuCurve = CreateCurve(CoolingPadSettings.Default.CpuCurve);
        GpuCurve = CreateCurve(CoolingPadSettings.Default.GpuCurve);
        ReleaseCommand = new((_, token) => ExecuteAsync(Operation.Release, token), _ => CanRelease, lifetime);
        RefreshCommand = new((_, token) => ExecuteAsync(Operation.ExplicitRead, token), _ => CanRefresh, lifetime);
    }

    private enum Operation { Apply, Release, ExplicitRead, AutomaticRead }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<CoolingPadChoice<CoolingPadFanMode>> FanModes { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadAutoProfile>> AutoProfiles { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadLightingMode>> LightingModes { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadWaveDirection>> WaveDirections { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadTemperatureSource>> TemperatureSources { get; }
    public IReadOnlyList<FanCurveEditorPoint> CpuCurve { get; private set; }
    public IReadOnlyList<FanCurveEditorPoint> GpuCurve { get; private set; }
    public AsyncRelayCommand ReleaseCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public bool IsVisible => lastConfirmedConnected;
    public bool CanEdit => serviceAvailable && lastConfirmedConnected && connectionKnown;
    public bool CanApply => CanEdit && !status.WritesPaused && !uncertainSubmission;
    public bool CanRelease => CanApply && !busy && (status.Active || status.RecoveryRequired);
    public bool CanRefresh => serviceAvailable && !busy;
    public bool IsBusy => busy;
    /// <summary>A user change or release is in flight; background reads do not show progress.</summary>
    public bool IsApplying => applying;
    public bool ShowRelease => !NeedsCheck;
    /// <summary>A change may have reached the pad unconfirmed; writes wait for a confirmed read.</summary>
    public bool NeedsCheck => uncertainSubmission || status.WritesPaused;
    public bool ShowDemandApiOption => FanMode?.Value is CoolingPadFanMode.Fixed or CoolingPadFanMode.HostCurve or CoolingPadFanMode.Auto;
    public bool AllowDemandApi { get => allowDemandApi; set { if (SetField(ref allowDemandApi, value)) { ScheduleApply(); } } }
    public bool ShowFixedSpeed => FanMode?.Value == CoolingPadFanMode.Fixed;
    public bool ShowAutoProfile => FanMode?.Value == CoolingPadFanMode.Auto;
    public bool ShowCurves => FanMode?.Value == CoolingPadFanMode.HostCurve;
    public bool ShowTemperatureSource => FanMode?.Value is CoolingPadFanMode.HostCurve or CoolingPadFanMode.Auto;
    public bool ShowCpuCurve => TemperatureSource?.Value != CoolingPadTemperatureSource.Gpu;
    public bool ShowGpuCurve => TemperatureSource?.Value != CoolingPadTemperatureSource.Cpu;
    public bool ShowLightingOptions => LightingMode is not null;
    public bool ShowPrimaryColor => LightingMode?.Value is CoolingPadLightingMode.Static or CoolingPadLightingMode.Breathing;
    public bool ShowSecondaryColor => LightingMode?.Value == CoolingPadLightingMode.Breathing;
    public bool ShowWaveDirection => LightingMode?.Value == CoolingPadLightingMode.Wave;

    /// <summary>Null means OpenBlade leaves the pad's own fan setting in place.</summary>
    public CoolingPadChoice<CoolingPadFanMode>? FanMode
    {
        get => fanMode;
        set
        {
            if (!SetField(ref fanMode, value)) { return; }
            foreach (string name in new[] { nameof(ShowFixedSpeed), nameof(ShowAutoProfile), nameof(ShowCurves), nameof(ShowTemperatureSource), nameof(ShowDemandApiOption) })
            { Notify(name); }
            ScheduleApply();
        }
    }

    public CoolingPadChoice<CoolingPadAutoProfile>? AutoProfile { get => autoProfile; set { if (SetField(ref autoProfile, value)) { ScheduleApply(); } } }

    /// <summary>Null means OpenBlade leaves the pad's own lighting in place.</summary>
    public CoolingPadChoice<CoolingPadLightingMode>? LightingMode
    {
        get => lightingMode;
        set
        {
            if (!SetField(ref lightingMode, value)) { return; }
            foreach (string name in new[] { nameof(ShowLightingOptions), nameof(ShowPrimaryColor), nameof(ShowSecondaryColor), nameof(ShowWaveDirection) })
            { Notify(name); }
            ScheduleApply();
        }
    }

    public CoolingPadChoice<CoolingPadWaveDirection>? WaveDirection { get => waveDirection; set { if (SetField(ref waveDirection, value)) { ScheduleApply(); } } }

    public CoolingPadChoice<CoolingPadTemperatureSource>? TemperatureSource
    {
        get => temperatureSource;
        set
        {
            if (!SetField(ref temperatureSource, value)) { return; }
            Notify(nameof(ShowCpuCurve));
            Notify(nameof(ShowGpuCurve));
            ScheduleApply();
        }
    }

    public int FixedRpm
    {
        get => fixedRpm;
        set
        {
            int normalized = Math.Clamp((int)Math.Round(value / (double)CoolingPadSettings.RpmStep, MidpointRounding.AwayFromZero)
                * CoolingPadSettings.RpmStep, CoolingPadSettings.MinimumRpm, CoolingPadSettings.MaximumRpm);
            if (SetField(ref fixedRpm, normalized)) { Notify(nameof(FixedRpmText)); ScheduleApply(); }
        }
    }

    public double Brightness
    {
        get => brightness;
        set
        {
            double normalized = double.IsFinite(value) ? Math.Clamp(Math.Round(value), 0, 100) : brightness;
            if (SetField(ref brightness, normalized)) { Notify(nameof(BrightnessText)); ScheduleApply(); }
        }
    }

    public string FixedRpmText => TrayText.Format("{0:N0} RPM", fixedRpm);
    public string BrightnessText => TrayText.Format("{0}%", brightness);
    public RgbColor PrimaryColor { get => primaryColor; set { if (SetField(ref primaryColor, value)) { ScheduleApply(); } } }
    public RgbColor SecondaryColor { get => secondaryColor; set { if (SetField(ref secondaryColor, value)) { ScheduleApply(); } } }

    /// <summary>The page shows status only while it is visible, refreshing it on a short interval.</summary>
    public bool IsPageVisible
    {
        get => pageVisible;
        set
        {
            if (!SetField(ref pageVisible, value)) { return; }
            refreshLoop?.Cancel();
            refreshLoop?.Dispose();
            refreshLoop = null;
            if (value && !lifetime.IsCancellationRequested)
            {
                refreshLoop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _ = RefreshWhileVisibleAsync(refreshLoop.Token);
            }
        }
    }

    public string StatusText => feedback ?? (NeedsCheck
        ? TrayText.Get("A change to the cooling pad could not be confirmed. OpenBlade is checking the pad.")
        : !serviceAvailable ? TrayText.Get("Reconnecting. Showing the last confirmed cooling-pad state.")
        : !connectionKnown ? TrayText.Get("Cooling-pad status is unavailable. Showing the last confirmed state.")
        : status.RecoveryRequired ? TrayText.Get("Choose a setting, or release control to restore the pad's own settings.")
        : status.Active ? TrayText.Get("OpenBlade is controlling the cooling pad.")
        : TrayText.Get("Connected. The pad is using its own settings."));
    public string ConfiguredTargetText => status.ConfirmedTargetRpm is int rpm
        ? TrayText.Format("{0:N0} RPM", rpm)
        : TrayText.Get("Unavailable");
    public string ConfiguredLightingText => status.ConfirmedBrightnessPercent is int percent
        ? TrayText.Format("{0}%", percent)
        : TrayText.Get("Unavailable");

    public bool OwnsCurvePoint(FanCurveEditorPoint point) => CpuCurve.Contains(point) || GpuCurve.Contains(point);

    /// <summary>Accepts a committed curve edit, keeping the curve non-decreasing, then applies it.</summary>
    public void UpdateCurvePoint(FanCurveEditorPoint point, int speedRpm)
    {
        ArgumentNullException.ThrowIfNull(point);
        bool cpu = CpuCurve.Contains(point);
        IReadOnlyList<FanCurveEditorPoint> source = cpu ? CpuCurve
            : GpuCurve.Contains(point) ? GpuCurve
            : throw new ArgumentException("The fan-curve point does not belong to the cooling-pad editor.", nameof(point));
        int index = source.ToList().IndexOf(point);
        int minimum = index == 0 ? point.MinimumRpm : source[index - 1].SpeedRpm;
        int maximum = index == source.Count - 1 ? point.MaximumRpm : source[index + 1].SpeedRpm;
        point.SpeedRpm = Math.Clamp(speedRpm, minimum, maximum);
        // Republish so a clamped slider shows the accepted value.
        FanCurveEditorPoint[] updated = source.Select(value => FanCurveEditorPoint.ForCoolingPad(value.ToCoolingPadValue())).ToArray();
        if (cpu) { CpuCurve = updated; Notify(nameof(CpuCurve)); }
        else { GpuCurve = updated; Notify(nameof(GpuCurve)); }
        ScheduleApply();
    }

    public void ApplyStatus(CoolingPadStatusSnapshot snapshot, bool available = true)
    {
        serviceAvailable = available;
        bool confirmedPresent = snapshot.Connected || snapshot.Connection == CoolingPadConnectionState.PresentExact;
        connectionKnown = confirmedPresent || snapshot.Connection == CoolingPadConnectionState.Absent;
        if (connectionKnown)
        {
            lastConfirmedConnected = confirmedPresent;
            status = snapshot with { Connected = confirmedPresent };
        }
        else
        {
            // A restarted service has no discovery result yet. Retain the last
            // confirmed presence and target display until a typed result arrives.
            status = status with
            {
                WritesPaused = status.WritesPaused || snapshot.WritesPaused,
                RecoveryRequired = status.RecoveryRequired || snapshot.RecoveryRequired,
            };
        }
        bool showModes = snapshot.Active || snapshot.RecoveryRequired;
        if (!editorInitialized && confirmedPresent)
        {
            editorInitialized = true;
            LoadEditor(snapshot.Requested, showModes, snapshot.LightingKept);
        }
        else if (editorInitialized && confirmedPresent && !applying && !changePending
            && (showModes != editorShowsModes || snapshot.LightingKept != editorKeepsLighting
                || !SameSettings(editorSource, snapshot.Requested)))
        {
            // Another client, a restarted service or an install changed what the service
            // runs. Show that request; a local change still waiting to apply wins.
            LoadEditor(snapshot.Requested, showModes, snapshot.LightingKept);
        }
        NotifyState();
    }

    public void SetServiceAvailable(bool available)
    {
        serviceAvailable = available;
        NotifyState();
    }

    private void LoadEditor(CoolingPadSettings requested, bool showModes, bool keepsLighting)
    {
        suppressApply = true;
        try
        {
            // Saved intent is inert until a change; while the pad runs its own
            // settings, the selectors show no OpenBlade mode.
            FanMode = showModes ? FanModes.FirstOrDefault(x => x.Value == requested.FanMode) : null;
            // While the pad keeps lighting changed on it, no lighting is selected, so choosing
            // the saved one again is a change that applies it.
            LightingMode = showModes && !keepsLighting
                ? LightingModes.FirstOrDefault(x => x.Value == requested.LightingMode) : null;
            AutoProfile = AutoProfiles.FirstOrDefault(x => x.Value == requested.AutoProfile) ?? AutoProfiles[1];
            WaveDirection = WaveDirections.FirstOrDefault(x => x.Value == requested.WaveDirection) ?? WaveDirections[1];
            TemperatureSource = TemperatureSources.FirstOrDefault(x => x.Value == requested.TemperatureSource) ?? TemperatureSources[2];
            FixedRpm = requested.FixedRpm; Brightness = requested.BrightnessPercent;
            AllowDemandApi = requested.AllowDemandApi;
            PrimaryColor = ToRgb(requested.Color);
            SecondaryColor = ToRgb(requested.SecondaryColor ?? new CoolingPadColor(0, 0, 255));
            CpuCurve = CreateCurve(requested.CpuCurve);
            GpuCurve = CreateCurve(requested.GpuCurve);
            Notify(nameof(CpuCurve)); Notify(nameof(GpuCurve));
            editorSource = requested;
            editorShowsModes = showModes;
            editorKeepsLighting = keepsLighting;
        }
        finally { suppressApply = false; }
    }

    private static bool SameSettings(CoolingPadSettings? shown, CoolingPadSettings requested) =>
        shown is not null
        && shown with { CpuCurve = requested.CpuCurve, GpuCurve = requested.GpuCurve } == requested
        && shown.CpuCurve.SequenceEqual(requested.CpuCurve) && shown.GpuCurve.SequenceEqual(requested.GpuCurve);

    private void ScheduleApply()
    {
        if (suppressApply || !editorInitialized || lifetime.IsCancellationRequested) { return; }
        // A new user change starts its own retry allowance.
        refusedRetries = 0;
        changePending = true;
        long ticket = ++applyTicket;
        _ = ApplyAfterDelayAsync(ticket, applyDelay, retry: false);
    }

    private void ScheduleRetry()
    {
        if (lifetime.IsCancellationRequested) { return; }
        changePending = true;
        long ticket = ++applyTicket;
        _ = ApplyAfterDelayAsync(ticket, retryDelay, retry: true);
    }

    private static string RetryFeedback =>
        TrayText.Get("The cooling pad has not accepted the change yet. OpenBlade will try again.");

    private async Task ApplyAfterDelayAsync(long ticket, TimeSpan delay, bool retry)
    {
        try
        {
            // Coalesce slider drags and color-picker movement into one apply.
            if (delay > TimeSpan.Zero) { await Task.Delay(delay, lifetime); }
            if (ticket != applyTicket) { return; }
            // A retry that meets another operation stays a retry, so it keeps its allowance.
            if (busy) { if (retry) { retryPending = true; } else { applyPending = true; } return; }
            await ExecuteAsync(Operation.Apply, lifetime);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task RefreshWhileVisibleAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // An unconfirmed change keeps writes stopped; this read-only check is how the
                // page recovers by itself once the service confirms the pad's state.
                if (serviceAvailable && lastConfirmedConnected && !busy)
                { await ExecuteAsync(Operation.AutomaticRead, token); }
                await Task.Delay(refreshInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(Operation operation, CancellationToken token)
    {
        bool apply = operation == Operation.Apply;
        bool release = operation == Operation.Release;
        // An apply that starts, or cannot run, settles the pending change either way.
        if (apply && !busy) { changePending = false; }
        if (busy || (apply && !CanApply) || (release && !CanRelease)
            || (operation is Operation.ExplicitRead or Operation.AutomaticRead && !CanRefresh))
        {
            if (apply && !busy && feedback == RetryFeedback)
            {
                // The retry cannot run while writes are stopped; do not promise one.
                feedback = null;
                NotifyState();
            }
            return;
        }
        if (release)
        {
            // Releasing control replaces any refused change still waiting to be tried again.
            applyTicket++;
            retryPending = false;
            changePending = false;
            refusedRetries = 0;
        }

        CoolingPadSettings? settings = null;
        if (apply && !TryBuildSettings(out settings))
        {
            feedback = TrayText.Get("Check the cooling-pad settings and try again.");
            NotifyState(); return;
        }
        if (apply && settings!.FanMode == CoolingPadFanMode.Keep && settings.LightingMode == CoolingPadLightingMode.Keep)
        {
            return;
        }
        busy = true;
        applying = operation is Operation.Apply or Operation.Release;
        if (operation != Operation.AutomaticRead) { feedback = null; }
        NotifyState();
        bool mayHaveSubmitted = false;
        try
        {
            ControlOperationResult? result = null;
            if (apply)
            {
                mayHaveSubmitted = true;
                result = await client.SetCoolingPadSettingsAsync(settings!, token);
            }
            else if (release)
            {
                mayHaveSubmitted = true;
                result = await client.ReleaseCoolingPadControlAsync(token);
            }
            if (result?.Status is ControlOperationStatus.Failed or ControlOperationStatus.Blocked)
            {
                // Blocked is a known pre-submission rejection. It must not turn
                // an ordinary retry after conditions change into a recovery gate.
                mayHaveSubmitted = result.Status != ControlOperationStatus.Blocked;
                await dispatcher.InvokeAsync(() =>
                {
                    uncertainSubmission = result.Status == ControlOperationStatus.Failed;
                    if (apply && result.Status == ControlOperationStatus.Blocked && refusedRetries < MaxRefusedRetries)
                    {
                        // Nothing reached the pad. Keep the change and try it again shortly,
                        // with fresh admission; conditions such as power or session settle.
                        refusedRetries++;
                        retryPending = true;
                        // Hold the draft so a status update cannot replace it before the retry.
                        changePending = true;
                        feedback = RetryFeedback;
                        return;
                    }
                    // The editor no longer matches the service request; the next status reloads it.
                    if (apply) { editorSource = null; }
                    feedback = result.Status == ControlOperationStatus.Failed
                        ? null
                        : TrayText.Get("The cooling pad did not accept the change. Check Troubleshooting for the reason.");
                }, token);
                return;
            }
            ServiceStatusSnapshot readback = await client.ReadCoolingPadStatusAsync(token);
            await dispatcher.InvokeAsync(() =>
            {
                if (apply)
                {
                    // The service stores the accepted request as sent.
                    editorSource = settings;
                    editorShowsModes = readback.CoolingPad.Active || readback.CoolingPad.RecoveryRequired;
                    editorKeepsLighting = readback.CoolingPad.LightingKept;
                }
                ApplyStatus(readback.CoolingPad);
                // Failed submissions return above without a recovery read. A separate
                // confirmed read, explicit or automatic, clears their uncertainty.
                if (operation is Operation.ExplicitRead or Operation.AutomaticRead && connectionKnown)
                {
                    uncertainSubmission = false;
                }
                if (apply) { refusedRetries = 0; }
                if (release && !readback.CoolingPad.Active)
                {
                    // The pad runs its own settings again; keep the editor values for next time.
                    suppressApply = true;
                    try { FanMode = null; LightingMode = null; }
                    finally { suppressApply = false; }
                    editorShowsModes = false;
                    editorKeepsLighting = false;
                }
                if (operation != Operation.AutomaticRead) { feedback = null; }
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (mayHaveSubmitted)
            {
                uncertainSubmission = true;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (mayHaveSubmitted)
                {
                    uncertainSubmission = true;
                }

                feedback = mayHaveSubmitted
                    ? null
                    : TrayText.Get("Cooling-pad status is unavailable. Showing the last confirmed state.");
            }, lifetime);
            DiagnosticTrace.TraceException("OpenBlade.Tray.CoolingPadViewModel", "Control", exception);
        }
        finally
        {
            busy = false;
            applying = false;
            if (!lifetime.IsCancellationRequested)
            {
                await dispatcher.InvokeAsync(NotifyState, lifetime);
                if (applyPending)
                {
                    // A user change made during this operation supersedes any retry.
                    applyPending = false;
                    retryPending = false;
                    ScheduleApply();
                }
                else if (retryPending)
                {
                    retryPending = false;
                    ScheduleRetry();
                }
            }
        }
    }

    private bool TryBuildSettings(out CoolingPadSettings? settings)
    {
        settings = new CoolingPadSettings
        {
            FanMode = FanMode?.Value ?? CoolingPadFanMode.Keep,
            AutoProfile = AutoProfile?.Value ?? CoolingPadAutoProfile.Balanced,
            AllowDemandApi = AllowDemandApi,
            FixedRpm = FixedRpm,
            TemperatureSource = TemperatureSource?.Value ?? CoolingPadTemperatureSource.Both,
            CpuCurve = CpuCurve.Select(p => p.ToCoolingPadValue()).ToArray(),
            GpuCurve = GpuCurve.Select(p => p.ToCoolingPadValue()).ToArray(),
            LightingMode = LightingMode?.Value ?? CoolingPadLightingMode.Keep,
            BrightnessPercent = (int)Brightness,
            Color = new(PrimaryColor.Red, PrimaryColor.Green, PrimaryColor.Blue),
            SecondaryColor = new(SecondaryColor.Red, SecondaryColor.Green, SecondaryColor.Blue),
            WaveDirection = WaveDirection?.Value ?? CoolingPadWaveDirection.Right,
        };
        if (CoolingPadSettings.IsWellFormed(settings)) { return true; }
        settings = null;
        return false;
    }

    private static FanCurveEditorPoint[] CreateCurve(IReadOnlyList<CoolingPadCurvePoint> points) =>
        points.Select(FanCurveEditorPoint.ForCoolingPad).ToArray();
    private static RgbColor ToRgb(CoolingPadColor color) => new(color.R, color.G, color.B);
    private bool SetField<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }
        storage = value;
        Notify(name);
        return true;
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void NotifyState()
    {
        foreach (string name in new[] { nameof(IsVisible), nameof(CanEdit), nameof(CanApply), nameof(CanRelease), nameof(CanRefresh), nameof(IsBusy), nameof(IsApplying), nameof(NeedsCheck), nameof(ShowRelease), nameof(StatusText), nameof(ConfiguredTargetText), nameof(ConfiguredLightingText) })
        {
            Notify(name);
        }

        ReleaseCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        refreshLoop?.Cancel();
        refreshLoop?.Dispose();
        refreshLoop = null;
        await ReleaseCommand.DisposeAsync(); await RefreshCommand.DisposeAsync();
    }
}
