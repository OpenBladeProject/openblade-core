using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

// Editor intent is deliberately independent of saved firmware and live readback.
public sealed class CpuTuningViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient client;
    private readonly Func<bool> safetyAvailable;
    private readonly Func<bool> connected;
    private CpuTuningStatus? status;
    private DeviceCapabilities? capabilities;
    private bool liveNow, managePower, manageCurve, curveEnabled, useInstalledBackend, busy;
    private int powerLimitWatts = 65, curveValue = -10;
    private string operationMessage = string.Empty;

    public CpuTuningViewModel(IServiceClient client, Func<bool> safetyAvailable,
        Func<bool> connected, CancellationToken lifetimeToken = default)
    {
        this.client = client;
        this.safetyAvailable = safetyAvailable;
        this.connected = connected;
        RefreshCommand = new((_, token) => RunAsync(() => client.ObserveCpuTuningAsync(UseInstalledBackend, token)),
            _ => CanRefresh, lifetimeToken);
        ApplyCommand = new((_, token) => RunAsync(() => client.SetCpuTuningAsync(Target, Settings, token)),
            _ => CanApply, lifetimeToken);
        RestoreCommand = new((_, token) => RunAsync(() => client.RestoreCpuTuningAsync(Target, token)),
            _ => CanRestore, lifetimeToken);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public bool IsVisible => capabilities?.CpuPowerLimit is not null
        || capabilities?.CpuVoltageOptimizer is not null;
    public bool LiveNow { get => liveNow; set { if (Set(ref liveNow, value)) { NotifyState(); } } }
    public CpuTuningTarget Target => LiveNow ? CpuTuningTarget.LiveNow : CpuTuningTarget.FirmwareNextBoot;
    public bool ManagePower { get => managePower; set { if (Set(ref managePower, value)) { NotifyState(); } } }
    public bool ManageCurve { get => manageCurve; set { if (Set(ref manageCurve, value)) { NotifyState(); } } }
    public bool CurveEnabled { get => curveEnabled; set { if (Set(ref curveEnabled, value)) { NotifyState(); } } }
    public int PowerLimitWatts { get => powerLimitWatts; set { if (Set(ref powerLimitWatts, value)) { NotifyState(); } } }
    public int CurveValue { get => curveValue; set { if (Set(ref curveValue, value)) { NotifyState(); } } }
    public bool UseInstalledBackend { get => useInstalledBackend; set => Set(ref useInstalledBackend, value); }
    public CpuTuningSettings Settings => new(ManagePower ? PowerLimitWatts : null,
        ManageCurve ? new CpuCurveOptimizerSettings(CurveEnabled, CurveValue) : null);
    public bool BackendReadWriteAvailable => status?.IsWellFormed == true
        && (LiveNow ? status.Live.CanWrite : status.Firmware.CanWrite);
    public bool CanEdit => IsVisible && BackendReadWriteAvailable && safetyAvailable() && !busy;
    private bool PowerFeatureAdmitted => capabilities?.CpuPowerLimit is { IsWellFormed: true } power
        && FeatureAccessAdmitted(power.Access);
    private bool CurveFeatureAdmitted => capabilities?.CpuVoltageOptimizer is { IsWellFormed: true } curve
        && FeatureAccessAdmitted(curve.Access);
    public bool CanEditPower => CanEdit && PowerFeatureAdmitted;
    public bool CanEditCurve => CanEdit && CurveFeatureAdmitted;
    public bool CanEditPowerValue => CanEditPower && ManagePower;
    public bool CanEditCurveValue => CanEditCurve && ManageCurve && CurveEnabled;
    public bool CanApply => CanEdit && Settings.IsConfigured && Settings.IsWellFormed
        && status?.FirmwareRecovery?.State != CpuTuningRecoveryState.Blocked
        && status?.LiveRecovery?.State != CpuTuningRecoveryState.Blocked
        && (!ManagePower || CanEditPower && capabilities!.CpuPowerLimit!.Watts.Supports(PowerLimitWatts))
        && (!ManageCurve || CanEditCurve && (CurveEnabled
            ? capabilities!.CpuVoltageOptimizer!.Value.Supports(CurveValue)
            : capabilities!.CpuVoltageOptimizer!.SupportsEnableDisable));
    public bool CanRestore => CanEdit && status?.RestorePending == true
        && (LiveNow ? status.LiveRecovery : status.FirmwareRecovery)
            is { IsWellFormed: true, State: CpuTuningRecoveryState.RestoreReady } recovery
        && (!recovery.Features.HasFlag(CpuTuningFeatures.PowerLimit)
            || PowerFeatureAdmitted)
        && (!recovery.Features.HasFlag(CpuTuningFeatures.CurveOptimizer)
            || CurveFeatureAdmitted);
    public bool CanRefresh => IsVisible && connected() && !busy;
    public string TargetLabel => TrayText.Get(LiveNow ? "Live now" : "Next boot");
    public string ApplyLabel => TrayText.Get(LiveNow ? "Apply now" : "Save for next boot");
    public string OperationMessage => operationMessage;
    public string PendingBootSummary => TrayText.Get(status?.PendingBoot switch
    {
        CpuTuningPendingBoot.Requested => "Next boot: change requested; running values are separate.",
        CpuTuningPendingBoot.None => "Next boot: no change pending.",
        _ => "Next boot: pending state unknown.",
    });
    public string AvailabilitySummary => BackendReadWriteAvailable
        ? TrayText.Get("CPU tuning backend available.")
        : TrayText.Get("CPU tuning is observed only. Writes require an admitted backend and verified readback.");
    public string SavedSummary => status?.SavedConfiguration is { IsWellFormed: true } saved
        ? TrayText.Format("Saved firmware: PPT {0} W ({1}); CO {2}.",
            saved.PowerLimitMilliwatts / 1000.0,
            TrayText.Get(saved.CustomPowerLimitEnabled switch { true => "enabled", false => "disabled", _ => "Unknown" }),
            DescribeCurve(saved.CurveOptimizer))
        : TrayText.Get("Saved firmware: unknown.");
    public string LiveSummary => status?.LiveState is { IsWellFormed: true } live
        ? TrayText.Format("Measured live: PPT {0}; CO {1}.",
            DescribePower(live.PowerLimits),
            live.CoreValues.Count == 0 ? TrayText.Get("Unknown")
                : live.UniformCurveOptimizerValue is { } curve
                    ? curve.ToString(System.Globalization.CultureInfo.CurrentCulture) : TrayText.Get("mixed per-core values"))
        : TrayText.Get("Measured live: unknown.");

    public void UpdateStatus(CpuTuningStatus? value, DeviceCapabilities? deviceCapabilities)
    {
        status = value;
        capabilities = deviceCapabilities;
        NotifyState();
    }

    public void NotifyState()
    {
        foreach (string property in new[] { nameof(IsVisible), nameof(Target), nameof(Settings), nameof(BackendReadWriteAvailable),
            nameof(CanEdit), nameof(CanEditPower), nameof(CanEditCurve), nameof(CanEditPowerValue), nameof(CanEditCurveValue),
            nameof(TargetLabel), nameof(CanApply), nameof(CanRestore), nameof(CanRefresh), nameof(ApplyLabel), nameof(SavedSummary), nameof(LiveSummary),
            nameof(PendingBootSummary), nameof(AvailabilitySummary) })
        {
            Notify(property);
        }
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    // Shared observed capability discovery does not admit a live backend. Saved
    // controls additionally require the selected firmware backend's CanWrite.
    private bool FeatureAccessAdmitted(DeviceFeatureAccess access) => access == DeviceFeatureAccess.ReadWrite
        || !LiveNow && access == DeviceFeatureAccess.ObservedOnly;

    private async Task RunAsync(Func<Task<ControlOperationResult>> operation)
    {
        busy = true;
        NotifyState();
        try
        {
            ControlOperationResult result = await operation();
            if (result.CpuTuning is { } observed) { status = observed; }
            operationMessage = result.Message;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { operationMessage = TrayText.Format("CPU tuning request failed: {0}", exception.Message); }
        finally { busy = false; Notify(nameof(OperationMessage)); NotifyState(); }
    }

    private static string DescribePower(CpuLivePowerLimits? power)
    {
        string effective = power?.EffectiveWatts is { } watts ? TrayText.Format("{0} W", watts) : TrayText.Get("Unknown");
        return power is { Message32Milliwatts: not null } or { Message33Milliwatts: not null }
            ? TrayText.Format("effective {0}, fast {1}, slow {2}", effective,
                DescribeMilliwatts(power.Message32Milliwatts), DescribeMilliwatts(power.Message33Milliwatts))
            : effective;
    }

    private static string DescribeMilliwatts(uint? milliwatts) => milliwatts is { } value
        ? TrayText.Format("{0:0.###} W", value / 1000m) : TrayText.Get("Unknown");

    private static string DescribeCurve(CpuCurveOptimizerState? curve) => curve is not { IsWellFormed: true }
        ? TrayText.Get("Unknown") : curve.Mode == CpuCurveOptimizerMode.AllCore
            ? curve.AllCoreValue!.Value.ToString(System.Globalization.CultureInfo.CurrentCulture)
            : curve.CoreValues.All(core => core.Value == curve.CoreValues[0].Value)
                ? curve.CoreValues[0].Value.ToString(System.Globalization.CultureInfo.CurrentCulture)
                : TrayText.Get("mixed per-core values");
    private bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) { return false; }
        field = value; Notify(property); return true;
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    public async ValueTask DisposeAsync()
    {
        await RefreshCommand.DisposeAsync();
        await ApplyCommand.DisposeAsync();
        await RestoreCommand.DisposeAsync();
    }
}
