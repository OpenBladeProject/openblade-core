using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record CoolingPadChoice<T>(T Value, string Label);

public sealed class CoolingPadCurvePointEditor : INotifyPropertyChanged
{
    private double temperature;
    private double rpm;
    public CoolingPadCurvePointEditor(int temperature, int rpm)
    {
        this.temperature = temperature;
        this.rpm = rpm;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public double Temperature { get => temperature; set { if (!temperature.Equals(value)) { temperature = value; Notify(); } } }
    public double Rpm { get => rpm; set { if (!rpm.Equals(value)) { rpm = value; Notify(); } } }
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Explicit accessory controls; constructing or observing this editor never writes.</summary>
public sealed class CoolingPadViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient client;
    private readonly IUiDispatcher dispatcher;
    private readonly CancellationToken lifetime;
    private CoolingPadStatusSnapshot status = new();
    private bool busy;
    private bool uncertainSubmission;
    private bool editorInitialized;
    private bool serviceAvailable;
    private bool lastConfirmedConnected;
    private bool connectionKnown;
    private bool allowDemandApi;
    private string? feedback;
    private CoolingPadChoice<CoolingPadFanMode>? fanMode;
    private CoolingPadChoice<CoolingPadLightingMode>? lightingMode;
    private CoolingPadChoice<CoolingPadWaveDirection>? waveDirection;
    private CoolingPadChoice<CoolingPadTemperatureSource>? temperatureSource;
    private double fixedRpm = 1500;
    private double brightness = 100;
    private string primaryColor = "00FF00";
    private string secondaryColor = "0000FF";

    public CoolingPadViewModel(IServiceClient client, IUiDispatcher dispatcher, CancellationToken lifetime = default)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.lifetime = lifetime;
        FanModes = [new(CoolingPadFanMode.Keep, TrayText.Get("Keep current")),
            new(CoolingPadFanMode.Off, TrayText.Get("Off")),
            new(CoolingPadFanMode.Fixed, TrayText.Get("Fixed speed")),
            new(CoolingPadFanMode.HostCurve, TrayText.Get("CPU/GPU curves"))];
        LightingModes = [new(CoolingPadLightingMode.Keep, TrayText.Get("Keep current")),
            new(CoolingPadLightingMode.Off, TrayText.Get("Off")),
            new(CoolingPadLightingMode.Static, TrayText.Get("Static")),
            new(CoolingPadLightingMode.Breathing, TrayText.Get("Breathing")),
            new(CoolingPadLightingMode.Spectrum, TrayText.Get("Spectrum")),
            new(CoolingPadLightingMode.Wave, TrayText.Get("Wave")),
            new(CoolingPadLightingMode.KeyboardMirror, TrayText.Get("Mirror keyboard"))];
        WaveDirections = [new(CoolingPadWaveDirection.Left, TrayText.Get("Leftward")),
            new(CoolingPadWaveDirection.Right, TrayText.Get("Rightward"))];
        fanMode = FanModes[0]; lightingMode = LightingModes[0]; waveDirection = WaveDirections[1];
        TemperatureSources = [new(CoolingPadTemperatureSource.Cpu, TrayText.Get("CPU")),
            new(CoolingPadTemperatureSource.Gpu, TrayText.Get("GPU")),
            new(CoolingPadTemperatureSource.Both, TrayText.Get("Both"))];
        temperatureSource = TemperatureSources[2];
        CpuCurve = DefaultCurve(); GpuCurve = DefaultCurve();
        ApplyCommand = new((_, token) => ExecuteAsync(apply: true, release: false, token), _ => CanApply, lifetime);
        ReleaseCommand = new((_, token) => ExecuteAsync(apply: false, release: true, token), _ => CanRelease, lifetime);
        RefreshCommand = new((_, token) => ExecuteAsync(apply: false, release: false, token), _ => CanRefresh, lifetime);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<CoolingPadChoice<CoolingPadFanMode>> FanModes { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadLightingMode>> LightingModes { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadWaveDirection>> WaveDirections { get; }
    public IReadOnlyList<CoolingPadChoice<CoolingPadTemperatureSource>> TemperatureSources { get; }
    public IReadOnlyList<CoolingPadCurvePointEditor> CpuCurve { get; private set; }
    public IReadOnlyList<CoolingPadCurvePointEditor> GpuCurve { get; private set; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand ReleaseCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public bool IsVisible => lastConfirmedConnected;
    public bool CanEdit => serviceAvailable && lastConfirmedConnected && connectionKnown && !busy;
    public bool CanApply => CanEdit && !status.WritesPaused && !uncertainSubmission;
    public bool CanRelease => CanEdit && (status.Active || status.RecoveryRequired) && !status.WritesPaused && !uncertainSubmission;
    public bool CanRefresh => serviceAvailable && !busy;
    public bool IsBusy => busy;
    public bool ShowDemandApiOption => FanMode?.Value is CoolingPadFanMode.Fixed or CoolingPadFanMode.HostCurve;
    public bool AllowDemandApi { get => allowDemandApi; set => SetField(ref allowDemandApi, value); }
    public bool ShowFixedSpeed => FanMode?.Value == CoolingPadFanMode.Fixed;
    public bool ShowCurves => FanMode?.Value == CoolingPadFanMode.HostCurve;
    public bool ShowCpuCurve => TemperatureSource?.Value != CoolingPadTemperatureSource.Gpu;
    public bool ShowGpuCurve => TemperatureSource?.Value != CoolingPadTemperatureSource.Cpu;
    public bool ShowPrimaryColor => LightingMode?.Value is CoolingPadLightingMode.Static or CoolingPadLightingMode.Breathing;
    public bool ShowSecondaryColor => LightingMode?.Value == CoolingPadLightingMode.Breathing;
    public bool ShowWaveDirection => LightingMode?.Value == CoolingPadLightingMode.Wave;
    public CoolingPadChoice<CoolingPadFanMode>? FanMode { get => fanMode; set { if (SetField(ref fanMode, value)) { Notify(nameof(ShowFixedSpeed)); Notify(nameof(ShowCurves)); Notify(nameof(ShowDemandApiOption)); } } }
    public CoolingPadChoice<CoolingPadLightingMode>? LightingMode { get => lightingMode; set { if (SetField(ref lightingMode, value)) { Notify(nameof(ShowPrimaryColor)); Notify(nameof(ShowSecondaryColor)); Notify(nameof(ShowWaveDirection)); } } }
    public CoolingPadChoice<CoolingPadWaveDirection>? WaveDirection { get => waveDirection; set => SetField(ref waveDirection, value); }
    public CoolingPadChoice<CoolingPadTemperatureSource>? TemperatureSource
    {
        get => temperatureSource;
        set
        {
            if (SetField(ref temperatureSource, value))
            {
                Notify(nameof(ShowCpuCurve));
                Notify(nameof(ShowGpuCurve));
            }
        }
    }
    public double FixedRpm { get => fixedRpm; set => SetField(ref fixedRpm, value); }
    public double Brightness { get => brightness; set => SetField(ref brightness, value); }
    public string PrimaryColor { get => primaryColor; set => SetField(ref primaryColor, value); }
    public string SecondaryColor { get => secondaryColor; set => SetField(ref secondaryColor, value); }
    public string StatusText => feedback ?? (uncertainSubmission || status.WritesPaused
        ? TrayText.Get("Cooling-pad control is paused. Refresh status before making another change.")
        : !serviceAvailable ? TrayText.Get("Reconnecting. Showing the last confirmed cooling-pad state.")
        : !connectionKnown ? TrayText.Get("Cooling-pad status is unavailable. Showing the last confirmed state.")
        : status.RecoveryRequired ? TrayText.Get("Apply new settings, or release control to restore the captured cooling-pad settings.")
        : status.Active ? TrayText.Get("OpenBlade is controlling the cooling pad.")
        : TrayText.Get("Connected. Choose settings and apply them to take control."));
    public string ConfiguredTargetText => status.ConfirmedTargetRpm is int rpm
        ? TrayText.Format("Configured fan target: {0} RPM", rpm)
        : TrayText.Get("Configured fan target unavailable");
    public string ConfiguredLightingText => status.ConfirmedBrightnessPercent is int percent
        ? TrayText.Format("Configured brightness: {0}%", percent)
        : TrayText.Get("Configured brightness unavailable");

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
        if (!editorInitialized && confirmedPresent)
        {
            editorInitialized = true;
            CoolingPadSettings requested = snapshot.Requested;
            FanMode = FanModes.FirstOrDefault(x => x.Value == requested.FanMode) ?? FanModes[0];
            LightingMode = LightingModes.FirstOrDefault(x => x.Value == requested.LightingMode) ?? LightingModes[0];
            WaveDirection = WaveDirections.FirstOrDefault(x => x.Value == requested.WaveDirection) ?? WaveDirections[1];
            TemperatureSource = TemperatureSources.FirstOrDefault(x => x.Value == requested.TemperatureSource) ?? TemperatureSources[2];
            FixedRpm = requested.FixedRpm; Brightness = requested.BrightnessPercent;
            AllowDemandApi = requested.AllowDemandApi;
            PrimaryColor = ToHex(requested.Color);
            SecondaryColor = ToHex(requested.SecondaryColor ?? new CoolingPadColor(0, 0, 255));
            CpuCurve = requested.CpuCurve.Select(p => new CoolingPadCurvePointEditor(p.TemperatureCelsius, p.Rpm)).ToArray();
            GpuCurve = requested.GpuCurve.Select(p => new CoolingPadCurvePointEditor(p.TemperatureCelsius, p.Rpm)).ToArray();
            Notify(nameof(CpuCurve)); Notify(nameof(GpuCurve));
        }
        NotifyState();
    }

    public void SetServiceAvailable(bool available)
    {
        serviceAvailable = available;
        NotifyState();
    }

    private async Task ExecuteAsync(bool apply, bool release, CancellationToken token)
    {
        if (busy || (apply && !CanApply) || (release && !CanRelease) || (!apply && !release && !CanRefresh))
        {
            return;
        }

        CoolingPadSettings? settings = null;
        if (apply && !TryBuildSettings(out settings))
        {
            feedback = TrayText.Get("Check fan speeds, increasing curve temperatures, and six-digit RGB colors.");
            NotifyState(); return;
        }
        busy = true; feedback = null; NotifyState();
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
                    feedback = result.Status == ControlOperationStatus.Failed
                        ? TrayText.Get("The cooling-pad change could not be confirmed. Refresh status before continuing.")
                        : TrayText.Get("The cooling-pad change was blocked. Refresh status and check Troubleshooting.");
                }, token);
                return;
            }
            ServiceStatusSnapshot readback = await client.ReadCoolingPadStatusAsync(token);
            await dispatcher.InvokeAsync(() =>
            {
                ApplyStatus(readback.CoolingPad);
                // Failed submissions return above without a recovery read. Only
                // a separate explicit confirmed refresh clears their uncertainty.
                if (!apply && !release && connectionKnown)
                {
                    uncertainSubmission = false;
                }
                feedback = null;
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
                    ? TrayText.Get("The cooling-pad change could not be confirmed. Refresh status before continuing.")
                    : TrayText.Get("Cooling-pad status is unavailable. Showing the last confirmed state.");
            }, lifetime);
            DiagnosticTrace.TraceException("OpenBlade.Tray.CoolingPadViewModel", "ExplicitControl", exception);
        }
        finally
        {
            busy = false;
            if (!lifetime.IsCancellationRequested)
            {
                await dispatcher.InvokeAsync(NotifyState, lifetime);
            }
        }
    }

    private bool TryBuildSettings(out CoolingPadSettings? settings)
    {
        settings = null;
        if (!Whole(FixedRpm, CoolingPadSettings.MinimumRpm, CoolingPadSettings.MaximumRpm) || !Whole(Brightness, 0, 100)
            || !TryColor(PrimaryColor, out CoolingPadColor color)
            || !TryColor(SecondaryColor, out CoolingPadColor secondary))
        {
            return false;
        }

        if (ShowCurves && (!ValidCurve(CpuCurve) || !ValidCurve(GpuCurve)))
        {
            return false;
        }

        settings = new CoolingPadSettings
        {
            FanMode = FanMode?.Value ?? CoolingPadFanMode.Keep,
            AllowDemandApi = AllowDemandApi,
            FixedRpm = (int)FixedRpm,
            TemperatureSource = TemperatureSource?.Value ?? CoolingPadTemperatureSource.Both,
            CpuCurve = CpuCurve.Select(p => new CoolingPadCurvePoint((int)p.Temperature, (int)p.Rpm)).ToArray(),
            GpuCurve = GpuCurve.Select(p => new CoolingPadCurvePoint((int)p.Temperature, (int)p.Rpm)).ToArray(),
            LightingMode = LightingMode?.Value ?? CoolingPadLightingMode.Keep,
            BrightnessPercent = (int)Brightness,
            Color = color,
            SecondaryColor = secondary,
            WaveDirection = WaveDirection?.Value ?? CoolingPadWaveDirection.Right,
        };
        return CoolingPadSettings.IsWellFormed(settings);
    }
    private static bool ValidCurve(IReadOnlyList<CoolingPadCurvePointEditor> points) =>
        points.All(p => Whole(p.Temperature, 0, 110) && Whole(p.Rpm, CoolingPadSettings.MinimumRpm, CoolingPadSettings.MaximumRpm))
        && points.Zip(points.Skip(1)).All(pair => pair.First.Temperature < pair.Second.Temperature);
    private static bool Whole(double value, int min, int max) => double.IsFinite(value) && value >= min && value <= max && value == Math.Truncate(value);
    private static bool TryColor(string value, out CoolingPadColor color)
    {
        color = new(0, 0, 0);
        string hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }

        color = new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); return true;
    }
    private static string ToHex(CoolingPadColor color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";
    private static IReadOnlyList<CoolingPadCurvePointEditor> DefaultCurve() => CoolingPadSettings.Default.CpuCurve.Select(p => new CoolingPadCurvePointEditor(p.TemperatureCelsius, p.Rpm)).ToArray();
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
        foreach (string name in new[] { nameof(IsVisible), nameof(CanEdit), nameof(CanApply), nameof(CanRelease), nameof(CanRefresh), nameof(IsBusy), nameof(StatusText), nameof(ConfiguredTargetText), nameof(ConfiguredLightingText) })
        {
            Notify(name);
        }

        ApplyCommand.NotifyCanExecuteChanged(); ReleaseCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        await ApplyCommand.DisposeAsync(); await ReleaseCommand.DisposeAsync(); await RefreshCommand.DisposeAsync();
    }
}
