using System.Diagnostics;

namespace OpenBlade.SessionAgent;

internal enum SessionAgentPipeAcceptResult
{
    Accepted,
    Canceled,
    PipeClosed,
}

internal static class SessionAgentPipeAccept
{
    private const int PipeClosingHResult = unchecked((int)0x800700E8);

    internal static async Task<SessionAgentPipeAcceptResult> AcceptAsync(
        Func<CancellationToken, Task> accept,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accept);
        try
        {
            await accept(cancellationToken).ConfigureAwait(false);
            return SessionAgentPipeAcceptResult.Accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SessionAgentPipeAcceptResult.Canceled;
        }
        catch (IOException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Windows can report a canceled named-pipe accept as an I/O error.
            // Record the sanitized native result while completing shutdown.
            Trace.TraceInformation(
                "Session Agent pipe accept reported an I/O error during cancellation "
                + "(0x{0:X8}).",
                exception.HResult);
            return SessionAgentPipeAcceptResult.Canceled;
        }
        catch (IOException exception) when (exception.HResult == PipeClosingHResult)
        {
            // A client can connect and close before Windows completes the server-side
            // accept. Retire that unusable listener without terminating the agent.
            Trace.TraceInformation(
                "Session Agent pipe listener closed during accept (0x{0:X8}); "
                + "replacing it.",
                exception.HResult);
            return SessionAgentPipeAcceptResult.PipeClosed;
        }
    }
}
