using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private FanStopStatus CurrentFanStopStatus => latestStatus?.FanStopStatus ?? new();

    private bool FanStopOwnsControl => CurrentFanStopStatus.State is
        FanStopState.Stopped or FanStopState.Starting or FanStopState.ReturningToAutomatic
        or FanStopState.RecoveryRequired;

    public bool IsFanStopVisible => Capabilities.HasFanStopTrialEvidence
        || Capabilities.SupportsFanStop || FanStopOwnsControl;

    public bool FanStopRequested => CurrentFanStopStatus.RequestedMode == FanStopMode.Stop;

    public bool CanToggleFanStop => IsFanStopVisible && CanRequestHardwareControl
        && !IsFanControlOperationInProgress
        && CurrentFanStopStatus.State is not (FanStopState.RecoveryRequired or FanStopState.ReturningToAutomatic)
        && (FanStopRequested
            ? CurrentFanStopStatus.CanReturnToAutomatic
            : Capabilities.SupportsFanStop && CurrentFanStopStatus.CanStop);

    public string FanStopDescription => !IsConnected
        ? TrayText.Get("Fan stop is unavailable right now.")
        : CurrentFanStopStatus switch
        {
            { State: FanStopState.RecoveryRequired } =>
                TrayText.Get("Fan control needs recovery. Automatic cooling is not confirmed."),
            { State: FanStopState.Starting } =>
                TrayText.Get("Waiting for fan stop confirmation. This may take tens of seconds. Turn off to return to Auto."),
            { State: FanStopState.ReturningToAutomatic } =>
                TrayText.Get("Returning to Auto. Automatic cooling is not confirmed yet; this may take tens of seconds."),
            { State: FanStopState.Stopped } when CanToggleFanStop =>
                TrayText.Get("Fans are stopped. Turn off to return to Auto."),
            { State: FanStopState.Stopped } => TrayText.Get("Fans are stopped"),
            { UnavailableReason: FanStopUnavailableReason.GuardianUnavailable } =>
                TrayText.Get("Fan stop is unavailable because its safety guardian is not ready."),
            { UnavailableReason: FanStopUnavailableReason.RecoveryNotValidated } =>
                TrayText.Get("Fan stop is not available in this build."),
            { CanStop: true } when CanToggleFanStop => TrayText.Get("Turn on to stop the fans. Turn off to return to Auto."),
            _ => TrayText.Get("Fan stop is unavailable right now."),
        };

    public async Task SetFanStopAsync(bool enabled, CancellationToken token)
    {
        if (!CanToggleFanStop || enabled == FanStopRequested) { return; }
        IsFanControlOperationInProgress = true;
        var stamp = new ObservationRequestStamp(connectionGeneration, subscriptionConnected);
        try
        {
            await RunOperationAsync(ct => serviceClient.SetFanStopModeAsync(
                enabled ? FanStopMode.Stop : FanStopMode.Automatic, ct), token);
            ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(token);
            await dispatcher.InvokeAsync(() => ApplyObservedStatus(status, stamp, trayOnly: false), token);
        }
        finally
        {
            IsFanControlOperationInProgress = false;
            NotifyFanStopPresentation();
        }
    }

    private void NotifyFanStopPresentation()
    {
        NotifyComputed(nameof(IsFanStopVisible), IsFanStopVisible);
        NotifyComputed(nameof(FanStopRequested), FanStopRequested);
        NotifyComputed(nameof(CanToggleFanStop), CanToggleFanStop);
        NotifyComputed(nameof(FanStopDescription), FanStopDescription);
        NotifyComputed(nameof(ActiveFanModeLabel), ActiveFanModeLabel);
    }
}
