using System.Diagnostics;

namespace OpenBlade.SessionAgent;

internal static class SessionAgentPipeAccept
{
    internal static async Task<bool> TryAcceptAsync(
        Func<CancellationToken, Task> accept,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accept);
        try
        {
            await accept(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Windows can report a canceled named-pipe accept as an I/O error.
            // Record the sanitized native result while completing shutdown.
            Trace.TraceInformation(
                "Session Agent pipe accept reported an I/O error during cancellation "
                + "(0x{0:X8}).",
                exception.HResult);
            return false;
        }
    }
}
