using System.ComponentModel;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

// editor intent is deliberately independent of measured running values.
public sealed class CpuTuningViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient client;
    private readonly Func<bool> safetyAvailable;
    private readonly Func<bool> connected;
    private CpuTuningStatus? status;
    private DeviceCapabilities? capabilities;
    private bool busy;
    private readonly Dictionary<string, object?> presentationValues = new(StringComparer.Ordinal);
    private readonly CurveEditor editor = new();
    private sealed class CurveEditor
    {
        public bool Initialized, Dirty, Enabled;
        public int Value = -1;
    }
    private string operationMessage = string.Empty;
    private sealed record ApplyRequest(CpuTuningSettings Settings);

    public CpuTuningViewModel(IServiceClient client, Func<bool> safetyAvailable,
        Func<bool> connected, CancellationToken lifetimeToken = default)
    {
        this.client = client;
        this.safetyAvailable = safetyAvailable;
        this.connected = connected;
        RefreshCommand = new((_, token) => RunAsync(() => client.ObserveCpuTuningAsync(true, token)),
            _ => CanRefresh, lifetimeToken);
        ApplyCommand = new((parameter, token) => ApplyAsync((ApplyRequest)parameter!, token),
            _ => CanApply, lifetimeToken, _ => new ApplyRequest(Settings));
        RestoreCommand = new((_, token) => RestoreAsync(token),
            _ => CanRestore, lifetimeToken);
        NotifyState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public bool IsVisible => capabilities?.CpuVoltageOptimizer is not null;
    public bool CurveEnabled { get => editor.Enabled; set { if (editor.Enabled != value) { editor.Enabled = value; editor.Dirty = true; NotifyState(); } } }
    public int CurveValue { get => editor.Value; set { if (editor.Value != value) { editor.Value = value; editor.Dirty = true; NotifyState(); } } }
    public CpuTuningSettings Settings => new(null, new CpuCurveOptimizerSettings(CurveEnabled, CurveValue));
    public bool BackendReadWriteAvailable => BackendAvailable();
    private bool BackendAvailable() => status?.IsWellFormed == true && status.Live.CanWrite;
    private bool OptimizerConfirmed() => status?.IsWellFormed == true
        && status.LiveState is { IsWellFormed: true, CoreValues.Count: > 0 };
    public bool CanEdit => IsVisible && BackendAvailable()
        && OptimizerConfirmed() && safetyAvailable() && !busy;
    private bool CurveAdmitted() => capabilities?.CpuVoltageOptimizer is { IsWellFormed: true } curve
        && curve.Access == DeviceFeatureAccess.ReadWrite;
    public bool CanEditCurve => CanEdit && CurveAdmitted();
    public bool CanEditCurveValue => CanEditCurve && CurveEnabled;
    public bool CanApply => CanApplyRequest(Settings);
    private bool CanApplyRequest(CpuTuningSettings settings) => CanEdit
        && editor is { Initialized: true } or { Dirty: true }
        && settings.IsConfigured && settings.IsWellFormed
        && status?.FirmwareRecovery?.State != CpuTuningRecoveryState.Blocked
        && status?.LiveRecovery?.State != CpuTuningRecoveryState.Blocked
        && !(status?.RestorePending == true && status.LiveRecovery is { Features: var features }
            && (features & CpuTuningFeatures.PowerLimit) != 0)
        && CurveAdmitted() && (settings.CurveOptimizer!.Enabled
            ? capabilities!.CpuVoltageOptimizer!.Value.Supports(settings.CurveOptimizer.Value)
            : capabilities!.CpuVoltageOptimizer!.SupportsEnableDisable);
    public bool CanRestore => CanEdit && CurveAdmitted() && status?.RestorePending == true
        && status.LiveRecovery is { IsWellFormed: true, State: CpuTuningRecoveryState.RestoreReady, Features: CpuTuningFeatures.CurveOptimizer };
    public bool CanRefresh => IsVisible && connected() && !busy;
    public string ApplyLabel => TrayText.Get("Apply now");
    public string OperationMessage => operationMessage;
    public bool ShowAvailabilitySummary => !(BackendReadWriteAvailable && CurveAdmitted() && OptimizerConfirmed())
        || status?.LiveRecovery is { State: CpuTuningRecoveryState.Blocked, Features: CpuTuningFeatures.None }
        || status?.FirmwareRecovery is { State: CpuTuningRecoveryState.Blocked, Features: CpuTuningFeatures.None };
    public string AvailabilitySummary => ShowAvailabilitySummary
        ? TrayText.Get("Running optimizer changes are unavailable.") : string.Empty;
    public bool ShowRecoverySummary => !string.IsNullOrEmpty(RecoverySummary);
    public string RecoverySummary
    {
        get
        {
            if (status?.FirmwareRecovery is { State: CpuTuningRecoveryState.Blocked, Features: not CpuTuningFeatures.None })
            {
                return TrayText.Get("Recovery of earlier saved CPU settings is blocked.");
            }
            if (status?.RestorePending != true) { return string.Empty; }
            return status.LiveRecovery switch
            {
                { State: CpuTuningRecoveryState.Blocked, Features: not CpuTuningFeatures.None }
                    => TrayText.Get("Optimizer recovery is blocked. Refresh to check its status."),
                { State: CpuTuningRecoveryState.RestoreReady, Features: not (CpuTuningFeatures.None or CpuTuningFeatures.CurveOptimizer) }
                    => TrayText.Get("CPU recovery includes power settings and cannot be restored here."),
                _ => string.Empty,
            };
        }
    }
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
        NotifyComputed(nameof(IsVisible), IsVisible);
        NotifyComputed(nameof(Settings), Settings);
        NotifyComputed(nameof(BackendReadWriteAvailable), BackendReadWriteAvailable);
        NotifyComputed(nameof(CanEdit), CanEdit);
        NotifyComputed(nameof(CanEditCurve), CanEditCurve);
        NotifyComputed(nameof(CanEditCurveValue), CanEditCurveValue);
        NotifyComputed(nameof(CurveEnabled), CurveEnabled);
        NotifyComputed(nameof(CurveValue), CurveValue);
        bool applyChanged = NotifyComputed(nameof(CanApply), CanApply);
        bool restoreChanged = NotifyComputed(nameof(CanRestore), CanRestore);
        bool refreshChanged = NotifyComputed(nameof(CanRefresh), CanRefresh);
        NotifyComputed(nameof(ApplyLabel), ApplyLabel);
        NotifyComputed(nameof(LiveSummary), LiveSummary);
        NotifyComputed(nameof(RecoverySummary), RecoverySummary);
        NotifyComputed(nameof(AvailabilitySummary), AvailabilitySummary);
        NotifyComputed(nameof(ShowRecoverySummary), ShowRecoverySummary);
        NotifyComputed(nameof(ShowAvailabilitySummary), ShowAvailabilitySummary);
        if (refreshChanged) { RefreshCommand.NotifyCanExecuteChanged(); }
        if (applyChanged) { ApplyCommand.NotifyCanExecuteChanged(); }
        if (restoreChanged) { RestoreCommand.NotifyCanExecuteChanged(); }
    }

    private bool NotifyComputed<T>(string property, T value)
    {
        if (presentationValues.TryGetValue(property, out object? previous) && Equals(previous, value)) { return false; }
        presentationValues[property] = value;
        Notify(property);
        return true;
    }

    private Task ApplyAsync(ApplyRequest request, CancellationToken token) => CanApplyRequest(request.Settings)
        ? RunAsync(() => client.SetCpuTuningAsync(CpuTuningTarget.LiveNow, request.Settings, token)) : Task.CompletedTask;
    private Task RestoreAsync(CancellationToken token) => CanRestore
        ? RunAsync(() => client.RestoreCpuTuningAsync(CpuTuningTarget.LiveNow, token)) : Task.CompletedTask;

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
        if (editor.Dirty) { return; }
        int? value = status?.IsWellFormed == true ? status.LiveState?.UniformCurveOptimizerValue : null;
        editor.Initialized = value is 0 or >= -30 and <= -1;
        if (!editor.Initialized) { editor.Enabled = false; return; }
        editor.Enabled = value != 0;
        if (value < 0) { editor.Value = value.Value; }
        editor.Initialized = true;
    }
    private static string DescribeValue(int value) => value == 0 ? TrayText.Get("Off")
        : value.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private void Notify(string property) => PropertyChanged?.Invoke(this, new(property));
    public async ValueTask DisposeAsync()
    {
        await RefreshCommand.DisposeAsync();
        await ApplyCommand.DisposeAsync();
        await RestoreCommand.DisposeAsync();
    }
}
