using System.Windows.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    private bool isClearLocalDimmingOperationInProgress;
    private string localDimmingStatusMessage = string.Empty;

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

    // The latest Clear result that changed nothing or failed, shown on the card
    // itself. A refusal names the setting to turn off in Synapse.
    public string LocalDimmingStatusMessage
    {
        get => localDimmingStatusMessage;
        private set
        {
            if (localDimmingStatusMessage == value)
            {
                return;
            }

            SetField(ref localDimmingStatusMessage, value);
            Notify(nameof(HasLocalDimmingStatusMessage));
        }
    }

    public bool HasLocalDimmingStatusMessage => !string.IsNullOrWhiteSpace(LocalDimmingStatusMessage);

    public ICommand ClearLocalDimmingCommand { get; }

    public async Task<bool> ClearLocalDimmingAsync(CancellationToken cancellationToken)
    {
        if (!CanClearLocalDimming)
        {
            return false;
        }

        IsClearLocalDimmingOperationInProgress = true;
        LocalDimmingStatusMessage = string.Empty;
        try
        {
            bool accepted = false;
            await RunOperationAsync(
                serviceClient.ClearLocalDimmingAsync,
                cancellationToken,
                onCompleted: result =>
                {
                    accepted = result.Status == ControlOperationStatus.Applied;
                    LocalDimmingStatusMessage = accepted ? string.Empty : result.Message;
                    // A Clear has no saved intent, so the shared "saved the change"
                    // Blocked text would be wrong; the card shows the reason instead.
                    if (result.Status == ControlOperationStatus.Blocked)
                    {
                        OperationFeedbackMessage = string.Empty;
                    }
                });
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
