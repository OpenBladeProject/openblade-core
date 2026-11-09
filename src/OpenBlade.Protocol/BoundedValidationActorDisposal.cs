namespace OpenBlade.Protocol;

internal static class BoundedValidationActorDisposal
{
    internal static async Task DisposeAsync(
        IAsyncDisposable target,
        TimeProvider timeProvider,
        TimeSpan timeout,
        PendingDisposalRegistry pendingDisposals)
    {
        Task disposal;
        try
        {
            disposal = target.DisposeAsync().AsTask();
        }
        catch
        {
            pendingDisposals.RecordUnconfirmedDisposal();
            return;
        }

        if (!disposal.IsCompleted)
        {
            Task deadline = Task.Delay(timeout, timeProvider);
            await Task.WhenAny(disposal, deadline).ConfigureAwait(false);
        }

        if (disposal.IsCompleted)
        {
            if (!disposal.IsCompletedSuccessfully)
            {
                pendingDisposals.RecordUnconfirmedDisposal();
            }
            if (disposal.IsFaulted)
            {
                _ = disposal.Exception;
            }
        }
        else
        {
            pendingDisposals.Retain(disposal);
        }
    }
}
