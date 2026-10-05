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
    private bool liveNow, useInstalledBackend, busy;
    private readonly CurveEditor savedEditor = new(), liveEditor = new();
    private CurveEditor Editor => LiveNow ? liveEditor : savedEditor;
    private sealed class CurveEditor
    {
        public bool Initialized, Dirty, Enabled;
        public int Value = -1;
    }
    private string operationMessage = string.Empty;
    private sealed record ApplyRequest(CpuTuningTarget Target, CpuTuningSettings Settings);

    public CpuTuningViewModel(IServiceClient client, Func<bool> safetyAvailable,
        Func<bool> connected, CancellationToken lifetimeToken = default)
    {
        this.client = client;
        this.safetyAvailable = safetyAvailable;
        this.connected = connected;
        RefreshCommand = new((_, token) => RunAsync(() => client.ObserveCpuTuningAsync(UseInstalledBackend, token)),
            _ => CanRefresh, lifetimeToken);
        ApplyCommand = new((parameter, token) => ApplyAsync((ApplyRequest)parameter!, token),
            _ => CanApply, lifetimeToken, _ => new ApplyRequest(Target, Settings));
        RestoreCommand = new((parameter, token) => RestoreAsync((CpuTuningTarget)parameter!, token),
            _ => CanRestore, lifetimeToken, _ => Target);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public bool IsVisible => capabilities?.CpuVoltageOptimizer is not null;
    public bool LiveNow { get => liveNow; set { if (Set(ref liveNow, value)) { InitializeEditor(); NotifyState(); } } }
    public CpuTuningTarget Target => LiveNow ? CpuTuningTarget.LiveNow : CpuTuningTarget.FirmwareNextBoot;
    public bool CurveEnabled { get => Editor.Enabled; set { if (Editor.Enabled != value) { Editor.Enabled = value; Editor.Dirty = true; NotifyState(); } } }
    public int CurveValue { get => Editor.Value; set { if (Editor.Value != value) { Editor.Value = value; Editor.Dirty = true; NotifyState(); } } }
    public bool UseInstalledBackend { get => useInstalledBackend; set => Set(ref useInstalledBackend, value); }
    public CpuTuningSettings Settings => new(null, new CpuCurveOptimizerSettings(CurveEnabled, CurveValue));
    public bool BackendReadWriteAvailable => BackendAvailable(Target);
    private bool BackendAvailable(CpuTuningTarget target) => status?.IsWellFormed == true
        && (target == CpuTuningTarget.LiveNow ? status.Live.CanWrite : status.Firmware.CanWrite);
    private bool OptimizerConfirmed(CpuTuningTarget target) => status?.IsWellFormed == true && (target == CpuTuningTarget.LiveNow
        ? status.LiveState is { IsWellFormed: true, CoreValues.Count: > 0 }
        : status.SavedConfiguration?.CurveOptimizer is { IsWellFormed: true });
    private bool CanEditTarget(CpuTuningTarget target) => IsVisible && BackendAvailable(target)
        && OptimizerConfirmed(target) && safetyAvailable() && !busy;
    public bool CanEdit => CanEditTarget(Target);
    private bool CurveAdmitted(CpuTuningTarget target) => capabilities?.CpuVoltageOptimizer is { IsWellFormed: true } curve
        && FeatureAccessAdmitted(curve.Access, target);
    public bool CanEditCurve => CanEdit && CurveAdmitted(Target);
    public bool CanEditCurveValue => CanEditCurve && CurveEnabled;
    public bool CanApply => CanApplyRequest(Target, Settings);
    private bool CanApplyRequest(CpuTuningTarget target, CpuTuningSettings settings) => CanEditTarget(target)
        && (target == CpuTuningTarget.LiveNow ? liveEditor : savedEditor) is { Initialized: true } or { Dirty: true }
        && settings.IsConfigured && settings.IsWellFormed
        && status?.FirmwareRecovery?.State != CpuTuningRecoveryState.Blocked
        && status?.LiveRecovery?.State != CpuTuningRecoveryState.Blocked
        && CurveAdmitted(target) && (settings.CurveOptimizer!.Enabled
            ? capabilities!.CpuVoltageOptimizer!.Value.Supports(settings.CurveOptimizer.Value)
            : capabilities!.CpuVoltageOptimizer!.SupportsEnableDisable);
    public bool CanRestore => CanRestoreTarget(Target);
    private bool CanRestoreTarget(CpuTuningTarget target) => CanEditTarget(target) && CurveAdmitted(target) && status?.RestorePending == true
        && (target == CpuTuningTarget.LiveNow ? status.LiveRecovery : status.FirmwareRecovery)
            is { IsWellFormed: true, State: CpuTuningRecoveryState.RestoreReady } recovery
        && recovery.Features == CpuTuningFeatures.CurveOptimizer;
    public bool CanRefresh => IsVisible && connected() && !busy;
    public string TargetLabel => TrayText.Get(LiveNow ? "Live now" : "Next boot");
    public string ApplyLabel => TrayText.Get(LiveNow ? "Apply now" : "Save for next boot");
    public string OperationMessage => operationMessage;
    public bool ShowPendingBootSummary => status?.PendingBoot == CpuTuningPendingBoot.Requested;
    public bool ShowAvailabilitySummary => !(BackendReadWriteAvailable && CurveAdmitted(Target) && OptimizerConfirmed(Target));
    public string PendingBootSummary => TrayText.Get(status?.PendingBoot switch
    {
        CpuTuningPendingBoot.Requested => "Saved change takes effect next boot.",
        CpuTuningPendingBoot.None => "Next boot: no change pending.",
        _ => "Next boot: pending state unknown.",
    });
    public string AvailabilitySummary => BackendReadWriteAvailable && CurveAdmitted(Target) && OptimizerConfirmed(Target)
        ? TrayText.Get(LiveNow ? "Running optimizer changes are available." : "Saving the optimizer for next boot is available.")
        : TrayText.Get("Optimizer changes are currently unavailable for this target.");
    public string SavedSummary => status?.SavedConfiguration is { IsWellFormed: true } saved
        ? TrayText.Format("Saved for next boot: optimizer {0}.", DescribeCurve(saved.CurveOptimizer))
        : TrayText.Get("Saved for next boot: optimizer unknown.");
    public string LiveSummary => status?.LiveState is { IsWellFormed: true } live
        ? TrayText.Format("Measured running optimizer: {0}.",
            live.CoreValues.Count == 0 ? TrayText.Get("Unknown")
                : live.UniformCurveOptimizerValue is { } curve
                    ? DescribeValue(curve) : TrayText.Get("mixed per-core values"))
        : TrayText.Get("Measured running optimizer: unknown.");

    public void UpdateStatus(CpuTuningStatus? value, DeviceCapabilities? deviceCapabilities)
    {
        status = value;
        capabilities = deviceCapabilities;
        InitializeEditor();
        NotifyState();
    }

    public void NotifyState()
    {
        foreach (string property in new[] { nameof(IsVisible), nameof(Target), nameof(Settings), nameof(BackendReadWriteAvailable),
            nameof(CanEdit), nameof(CanEditCurve), nameof(CanEditCurveValue), nameof(CurveEnabled), nameof(CurveValue),
            nameof(TargetLabel), nameof(CanApply), nameof(CanRestore), nameof(CanRefresh), nameof(ApplyLabel), nameof(SavedSummary), nameof(LiveSummary),
            nameof(PendingBootSummary), nameof(AvailabilitySummary), nameof(ShowPendingBootSummary), nameof(ShowAvailabilitySummary) })
        {
            Notify(property);
        }
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    // Shared observed capability discovery does not admit a live backend. Saved
    // controls additionally require the selected firmware backend's CanWrite.
    private static bool FeatureAccessAdmitted(DeviceFeatureAccess access, CpuTuningTarget target) => access == DeviceFeatureAccess.ReadWrite
        || target == CpuTuningTarget.FirmwareNextBoot && access == DeviceFeatureAccess.ObservedOnly;

    private Task ApplyAsync(ApplyRequest request, CancellationToken token) => CanApplyRequest(request.Target, request.Settings)
        ? RunAsync(() => client.SetCpuTuningAsync(request.Target, request.Settings, token)) : Task.CompletedTask;
    private Task RestoreAsync(CpuTuningTarget target, CancellationToken token) => CanRestoreTarget(target)
        ? RunAsync(() => client.RestoreCpuTuningAsync(target, token)) : Task.CompletedTask;

    private async Task RunAsync(Func<Task<ControlOperationResult>> operation)
    {
        busy = true;
        NotifyState();
        try
        {
            ControlOperationResult result = await operation();
            if (result.CpuTuning is { } observed) { status = observed; InitializeEditor(); }
            operationMessage = result.Message;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { operationMessage = TrayText.Format("CPU tuning request failed: {0}", exception.Message); }
        finally { busy = false; Notify(nameof(OperationMessage)); NotifyState(); }
    }

    private void InitializeEditor()
    {
        if (Editor.Initialized || Editor.Dirty || status?.IsWellFormed != true) { return; }
        int? value = LiveNow ? status.LiveState?.UniformCurveOptimizerValue
            : UniformValue(status.SavedConfiguration?.CurveOptimizer);
        if (value is not (0 or >= -30 and <= -1)) { return; }
        Editor.Enabled = value != 0;
        if (value < 0) { Editor.Value = value.Value; }
        Editor.Initialized = true;
    }
    private static int? UniformValue(CpuCurveOptimizerState? curve) => curve is not { IsWellFormed: true }
        ? null : curve.Mode == CpuCurveOptimizerMode.AllCore ? curve.AllCoreValue
            : curve.CoreValues.All(core => core.Value == curve.CoreValues[0].Value) ? curve.CoreValues[0].Value : null;
    private static string DescribeValue(int value) => value == 0 ? TrayText.Get("Off")
        : value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private static string DescribeCurve(CpuCurveOptimizerState? curve) => curve is not { IsWellFormed: true }
        ? TrayText.Get("Unknown") : curve.Mode == CpuCurveOptimizerMode.AllCore
            ? DescribeValue(curve.AllCoreValue!.Value)
            : curve.CoreValues.All(core => core.Value == curve.CoreValues[0].Value)
                ? DescribeValue(curve.CoreValues[0].Value)
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
