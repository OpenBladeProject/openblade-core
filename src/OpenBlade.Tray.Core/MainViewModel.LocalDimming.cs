using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private bool isClearLocalDimmingOperationInProgress;

    // Local dimming has a one-time Clear only. The row appears when the service
    // admits Clear for this exact laptop and its last confirmed read was on.
    public bool ShowLocalDimmingClear =>
        latestStatus?.Capabilities?.LocalDimming is { SupportsClear: true }
        && latestStatus.ConfirmedState?.LocalDimmingActive == true;

    public bool IsClearLocalDimmingOperationInProgress
    {
        get => isClearLocalDimmingOperationInProgress;
        private set
        {
            if (isClearLocalDimmingOperationInProgress == value)
            {
                return;
            }

            SetField(ref isClearLocalDimmingOperationInProgress, value);
            Notify(nameof(CanClearLocalDimming));
            NotifyCommands();
        }
    }

    public bool CanClearLocalDimming => ShowLocalDimmingClear
        && CanMutate
        && !IsClearLocalDimmingOperationInProgress;

    public ICommand ClearLocalDimmingCommand { get; }

    public async Task<bool> ClearLocalDimmingAsync(CancellationToken cancellationToken)
    {
        if (!CanClearLocalDimming)
        {
            return false;
        }

        IsClearLocalDimmingOperationInProgress = true;
        try
        {
            bool accepted = false;
            await RunOperationAsync(
                serviceClient.ClearLocalDimmingAsync,
                cancellationToken,
                onCompleted: result => accepted = result.Status == ControlOperationStatus.Applied);
            return accepted;
        }
        finally
        {
            IsClearLocalDimmingOperationInProgress = false;
        }
    }

    private void NotifyLocalDimmingPresentation()
    {
        NotifyComputed(nameof(ShowLocalDimmingClear), ShowLocalDimmingClear);
        NotifyComputed(nameof(CanClearLocalDimming), CanClearLocalDimming);
    }
}
