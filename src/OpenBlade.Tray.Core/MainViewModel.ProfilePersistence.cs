using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainViewModel
{
    // Every profile writer replaces the complete service document. Own candidate
    // construction through confirmation so independent editors cannot lose edits.
    private readonly SemaphoreSlim profilePersistenceGate = new(1, 1);
    private bool profilePersistenceRequiresRefresh;

    private async Task<bool> BeginProfilePersistenceAsync(CancellationToken token)
    {
        await profilePersistenceGate.WaitAsync(token);
        bool admitted = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!profilePersistenceRequiresRefresh)
            {
                admitted = true;
                return true;
            }

            ObservationRequestStamp stamp = new(connectionGeneration, subscriptionConnected);
            ServiceStatusSnapshot status = await serviceClient.GetStatusAsync(token);
            bool accepted = false;
            await dispatcher.InvokeAsync(() =>
            {
                if (stamp.BeganWithConnectedSubscription
                    && stamp.Generation == connectionGeneration)
                {
                    // A newer subscription is already authoritative; an older
                    // read must not regress it merely to reconcile a lost reply.
                    accepted = status.Revision < latestAcceptedRevision
                        || ApplyObservedStatus(status, stamp, trayOnly: false);
                }
            }, token);
            if (accepted)
            {
                profilePersistenceRequiresRefresh = false;
                admitted = true;
                return true;
            }

            await ReportProfilePersistenceBlockedAsync(TrayText.Get("Reconnecting to service"), token);
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ReportOperationFailureAsync(exception, token);
            return false;
        }
        finally
        {
            if (!admitted)
            {
                profilePersistenceGate.Release();
            }
        }
    }

    private Task ReportProfilePersistenceBlockedAsync(string message, CancellationToken token) =>
        dispatcher.InvokeAsync(() =>
        {
            OperationStatusMessage = message;
            OperationFeedbackMessage = FormatOperationResult(new(ControlOperationStatus.Blocked, message));
        }, token);

    private Task ReportOperationFailureAsync(Exception exception, CancellationToken token)
    {
        DiagnosticTrace.TraceException("OpenBlade.Tray.MainViewModel", "RunOperation", exception);
        string message = TrayText.Format("Operation failed: {0}", exception.Message);
        return dispatcher.InvokeAsync(() =>
        {
            OperationStatusMessage = message;
            OperationFeedbackMessage = FormatOperationResult(new(ControlOperationStatus.Failed, message));
            TroubleshootingRecentErrorStatus = FormatTroubleshootingValue(message);
        }, token);
    }

    private async Task<bool> PersistProfileSetAsync(ProfileSet candidate, CancellationToken token)
    {
        bool persisted = false;
        await RunOperationAsync(async operationToken =>
        {
            // A lost or canceled reply may follow a committed save. The next
            // edit must read authoritative state instead of replaying this one.
            profilePersistenceRequiresRefresh = true;
            return await serviceClient.UpdateProfilesAsync(candidate, operationToken);
        }, token, result =>
        {
            persisted = result.Status is ControlOperationStatus.Stored or ControlOperationStatus.Applied;
            if (persisted)
            {
                Profiles = candidate;
            }
            // The reply carries no status revision. Even a successful save can
            // be followed by an already-buffered pre-save subscription snapshot.
            // Read the service revision before deriving the next whole document.
        });
        return persisted;
    }
}
