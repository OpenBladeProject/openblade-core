namespace OpenBlade.Protocol;

internal static class BoundedValidationActorDisposal
{
    internal static async Task DisposeAsync(
        ValidationActor target,
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
            return;
        }

        if (!disposal.IsCompleted)
        {
            Task deadline = Task.Delay(timeout, timeProvider);
            await Task.WhenAny(disposal, deadline).ConfigureAwait(false);
        }

        if (disposal.IsCompleted)
        {
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
