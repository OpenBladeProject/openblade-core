using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed record CoolingPadModeOption(CoolingPadControlMode Mode, string Label);

public sealed class CoolingPadCurveEditorPoint : INotifyPropertyChanged
{
    private int temperature;
    private int demand;
    public CoolingPadCurveEditorPoint(int temperature, int demand)
    { this.temperature = temperature; this.demand = demand; }
    public int Temperature { get => temperature; set { temperature = value; Changed(); } }
    public int Demand { get => demand; set { demand = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Pad intent and acknowledgements; no inferred physical fan speed.</summary>
public sealed class CoolingPadControlViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IServiceClient client;
    private CoolingPadControlStatus? status;
    private bool connected;
    private int fixedDemand = 2200;
    private CoolingPadModeOption selectedMode;
    private string message = string.Empty;
    private long connectionGeneration;
    private long latestRevision = -1;
    private bool notifyingCommands;
    private bool physicalConfirmation;

    public CoolingPadControlViewModel(IServiceClient client, CancellationToken lifetimeToken)
    {
        this.client = client;
        ModeOptions = [
            new(CoolingPadControlMode.Stop, TrayText.Get("Stop updates")),
            new(CoolingPadControlMode.FixedDemand, TrayText.Get("Fixed demand")),
            new(CoolingPadControlMode.HostCurve, TrayText.Get("Host curve")),
        ];
        selectedMode = ModeOptions[0];
        CpuPoints = [new(40, 1000), new(90, 2200)];
        GpuPoints = [new(30, 1000), new(80, 2200)];
        foreach (var point in CpuPoints.Concat(GpuPoints))
        {
            point.PropertyChanged += (_, _) => NotifyEditor();
        }
        ApplyCommand = new(async (_, token) =>
        {
            long generation = connectionGeneration;
            try
            {
                var result = await client.SetCoolingPadControlAsync(CreateSettings(), token);
                Message = result.Message;
                var refreshed = await client.GetStatusAsync(token);
                if (generation == connectionGeneration) { Update(refreshed, connected); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { Message = TrayText.Get("The cooling-pad request could not be completed. Refresh its status before trying again."); }
        }, _ => connected && IsEditorValid && ResumeCommand?.IsRunning != true && RestoreQuietCommand?.IsRunning != true && status?.Restoration is null, lifetimeToken);
        ResumeCommand = new(async (_, token) =>
        {
            long generation = connectionGeneration;
            try
            {
                var result = await client.ResumeCoolingPadControlAsync(token);
                Message = result.Message;
                var refreshed = await client.GetStatusAsync(token);
                if (generation == connectionGeneration) { Update(refreshed, connected); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception) { Message = TrayText.Get("The cooling-pad request could not be completed. Refresh its status before trying again."); }
        }, _ => CanResume && !ApplyCommand.IsRunning && RestoreQuietCommand?.IsRunning != true, lifetimeToken);
        RestoreQuietCommand = new(async (_, token) =>
        {
            var prompt = status?.Restoration;
            bool confirmation = PhysicalConfirmation;
            PhysicalConfirmation = false;
            long generation = connectionGeneration;
            try
            {
                var result = await client.RestoreCoolingPadQuietAsync(prompt?.OperationId ?? Guid.Empty,
                    prompt?.Stage ?? CoolingPadRestorationStage.Begin, confirmation, token);
                Message = result.Message;
                var refreshed = await client.GetStatusAsync(token);
                if (generation == connectionGeneration) { Update(refreshed, connected); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { Message = TrayText.Get("Restoration could not be confirmed. Refresh status; do not repeat a stale confirmation."); }
        }, _ => CanConfirmPhysical && PhysicalConfirmation
            && !ApplyCommand.IsRunning && !ResumeCommand.IsRunning, lifetimeToken);
        CancelRestorationCommand = new(async (_, token) =>
        {
            if (status?.Restoration is not { } prompt) { return; }
            PhysicalConfirmation = false;
            try
            {
                var result = await client.RestoreCoolingPadQuietAsync(prompt.OperationId, prompt.Stage, false, token);
                Message = result.Message;
                Update(await client.GetStatusAsync(token), connected);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { Message = TrayText.Get("Restoration cancellation could not be confirmed. Further stages are not confirmed."); }
        }, _ => connected && status?.Restoration is not null && !RestoreQuietCommand.IsRunning, lifetimeToken);
        RestoreQuietCommand.CanExecuteChanged += (_, _) => NotifyCommands();
        CancelRestorationCommand.CanExecuteChanged += (_, _) => NotifyCommands();
        ApplyCommand.CanExecuteChanged += (_, _) => NotifyCommands();
        ResumeCommand.CanExecuteChanged += (_, _) => NotifyCommands();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<CoolingPadModeOption> ModeOptions { get; }
    public ObservableCollection<CoolingPadCurveEditorPoint> CpuPoints { get; }
    public ObservableCollection<CoolingPadCurveEditorPoint> GpuPoints { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand RestoreQuietCommand { get; }
    public AsyncRelayCommand CancelRestorationCommand { get; }
    public bool PhysicalConfirmation
    {
        get => physicalConfirmation;
        set { physicalConfirmation = value; Notify(); RestoreQuietCommand?.NotifyCanExecuteChanged(); }
    }
    public bool CanConfirmPhysical => connected && status?.SubmissionStateUnknown == false
        && (status.QuietRestorationAvailable || status.Restoration is not null);
    public string PhysicalConfirmationText => TrayText.Get("I have checked the pad and confirm the requested physical condition");
    public string RestoreButtonText => TrayText.Get("Restore quiet / confirm stage");
    public string CancelRestorationText => TrayText.Get("Cancel restoration");
    public string RestorationText => status?.Restoration?.Stage switch
    {
        CoolingPadRestorationStage.Medium => TrayText.Get("Check normal Medium fan behavior, unchanged lighting, no red/sleep indication and HyperBoost inactive. Confirm to request preferred quiet."),
        CoolingPadRestorationStage.Quiet => TrayText.Get("Check preferred quiet fan behavior, unchanged lighting, no red/sleep indication and HyperBoost inactive. Confirm to finish the restoration commands."),
        CoolingPadRestorationStage.Final => TrayText.Get("Check that preferred quiet and unchanged lighting remain normal, without red/sleep and with HyperBoost inactive. Confirm to finish restoration."),
        _ when status?.QuietRestorationAvailable == true => TrayText.Get("Check normal current fan behavior, unchanged lighting, no red/sleep and HyperBoost inactive. Restore preferred quiet first requests Medium. Each physical confirmation expires after ten minutes."),
        _ when status?.RetainedEffect == true => TrayText.Get("Retained pad effects require attended resolution. This session cannot resume or restore automatically."),
        _ => TrayText.Get("Stop retains the last demand. Preferred quiet restoration is available after this session acquires control."),
    };
    public bool IsVisible => status is not null;
    public bool IsConnected => connected;
    public bool IsFixed => selectedMode.Mode == CoolingPadControlMode.FixedDemand;
    public bool IsCurve => selectedMode.Mode == CoolingPadControlMode.HostCurve;
    public CoolingPadModeOption SelectedMode
    {
        get => selectedMode;
        set { if (value is null || !ModeOptions.Contains(value)) { return; } selectedMode = value; NotifyEditor(); }
    }
    public int FixedDemand { get => fixedDemand; set { fixedDemand = value; NotifyEditor(); } }
    public string Message { get => message; private set { message = value; Notify(); } }
    public bool IsEditorValid
    {
        get { try { _ = CreateSettings(); return true; } catch (ArgumentException) { return false; } }
    }
    public bool CanResume => connected && status is
    {
        Access: DeviceFeatureAccess.ReadWrite,
        Requested.Mode: not CoolingPadControlMode.Stop, SubmissionStateUnknown: false
    } && status.Restoration is null && (!status.RetainedEffect || status.RetainedSessionResumable || status.State == CoolingPadControlState.Running);
    public string StatusText => status?.PauseReason switch
    {
        CoolingPadControlPauseReason.AdmissionClosed or CoolingPadControlPauseReason.Unsupported =>
            TrayText.Get("Cooling-pad control is being validated. Hardware control is not enabled in this build."),
        CoolingPadControlPauseReason.TelemetryUnavailable or CoolingPadControlPauseReason.StaleTelemetry =>
            TrayText.Get("Updates paused because temperature readings are unavailable or too old. The last demand may remain active."),
        CoolingPadControlPauseReason.SubmissionUnknown =>
            TrayText.Get("The command outcome is unknown. Further updates are paused."),
        _ when status?.Access == DeviceFeatureAccess.ObservedOnly =>
            TrayText.Get("Cooling-pad control is being validated. Hardware control is not enabled in this build."),
        _ when status?.State == CoolingPadControlState.Running => TrayText.Get("Host demand updates are running."),
        _ when status?.State == CoolingPadControlState.Stopped => TrayText.Get("Host updates are stopped. The pad may retain its last demand."),
        _ => TrayText.Get("Host updates are paused. Check the pad before resuming."),
    };
    public string RequestedText => status?.Requested.Mode switch
    {
        CoolingPadControlMode.HostCurve => TrayText.Get("Host curve"),
        CoolingPadControlMode.Stop => TrayText.Get("Stop updates"),
        _ => FormatDemand(status?.Requested.FixedDemand),
    };
    public string PlannedText => FormatDemand(status?.PlannedDemand);
    public string AcknowledgedText => FormatDemand(status?.LastAcknowledgedDemand);
    private static string FormatDemand(CoolingPadConfiguredRpmDemand? demand) => demand is { } d
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} RPM", d.Rpm)
        : TrayText.Get("Unavailable", "Unavailable");

    public CoolingPadControlSettings CreateSettings() => selectedMode.Mode switch
    {
        CoolingPadControlMode.Stop => CoolingPadControlSettings.Stopped,
        CoolingPadControlMode.FixedDemand => new(selectedMode.Mode, new CoolingPadConfiguredRpmDemand(fixedDemand)),
        CoolingPadControlMode.HostCurve => new(selectedMode.Mode, cpuCurve: Curve(CpuPoints), gpuCurve: Curve(GpuPoints)),
        _ => throw new ArgumentException("Unknown cooling-pad mode."),
    };
    private static CoolingPadDemandCurve Curve(IEnumerable<CoolingPadCurveEditorPoint> points) =>
        new(points.Select(p => new CoolingPadCurvePoint(p.Temperature, new(p.Demand))));

    public void Update(ServiceStatusSnapshot snapshot, bool isConnected)
    {
        if (snapshot.Revision < latestRevision) { return; }
        latestRevision = snapshot.Revision;
        if (connected != isConnected
            || status?.Restoration != snapshot.CoolingPadControl?.Restoration
            || status?.QuietRestorationAvailable != snapshot.CoolingPadControl?.QuietRestorationAvailable
            || status?.SubmissionStateUnknown != snapshot.CoolingPadControl?.SubmissionStateUnknown
            || status?.LastAcknowledgedDemand != snapshot.CoolingPadControl?.LastAcknowledgedDemand
            || status?.RetainedEffect != snapshot.CoolingPadControl?.RetainedEffect)
        { PhysicalConfirmation = false; }
        status = snapshot.CoolingPadControl;
        connected = isConnected;
        Notify(nameof(IsVisible)); Notify(nameof(IsConnected)); Notify(nameof(StatusText));
        Notify(nameof(RequestedText)); Notify(nameof(PlannedText)); Notify(nameof(AcknowledgedText)); Notify(nameof(CanResume)); Notify(nameof(RestorationText)); Notify(nameof(CanConfirmPhysical));
        ApplyCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
        RestoreQuietCommand.NotifyCanExecuteChanged(); CancelRestorationCommand.NotifyCanExecuteChanged();
    }
    public void Disconnect()
    {
        connectionGeneration++;
        latestRevision = -1;
        PhysicalConfirmation = false;
        connected = false; Notify(nameof(IsConnected)); Notify(nameof(CanResume)); Notify(nameof(RestorationText)); Notify(nameof(CanConfirmPhysical));
        ApplyCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
        RestoreQuietCommand.NotifyCanExecuteChanged(); CancelRestorationCommand.NotifyCanExecuteChanged();
    }
    private void NotifyEditor()
    {
        Notify(nameof(SelectedMode)); Notify(nameof(FixedDemand)); Notify(nameof(IsFixed));
        Notify(nameof(IsCurve)); Notify(nameof(IsEditorValid)); ApplyCommand?.NotifyCanExecuteChanged();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyCommands()
    {
        if (notifyingCommands) { return; }
        notifyingCommands = true;
        try
        {
            ApplyCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
            RestoreQuietCommand.NotifyCanExecuteChanged(); CancelRestorationCommand.NotifyCanExecuteChanged();
        }
        finally { notifyingCommands = false; }
    }
    public async ValueTask DisposeAsync()
    { await ApplyCommand.DisposeAsync(); await ResumeCommand.DisposeAsync(); await RestoreQuietCommand.DisposeAsync(); await CancelRestorationCommand.DisposeAsync(); }
}
