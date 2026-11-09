using System.Diagnostics;

namespace OpenBlade.Contracts;

// An optional, per-probe recorder. The codec never emits logs or invokes
// external code while a framed write is in progress.
internal sealed class IpcRequestWriteTiming
{
    internal IpcStageTiming Header;
    internal IpcStageTiming Body;
    internal IpcStageTiming Flush;

    internal void FailPending(CancellationToken token)
    {
        Header.FailIfPending(token);
        Body.FailIfPending(token);
        Flush.FailIfPending(token);
    }
}

internal struct IpcStageTiming
{
    internal long? BeginQpc { get; private set; }
    internal long? EndQpc { get; private set; }
    internal bool CanceledAtEntry { get; private set; }
    internal bool CanceledAtExit { get; private set; }
    internal bool Succeeded { get; private set; }

    internal void Begin(CancellationToken token)
    {
        BeginQpc = Stopwatch.GetTimestamp();
        CanceledAtEntry = token.IsCancellationRequested;
    }

    internal void End(CancellationToken token, bool succeeded)
    {
        EndQpc = Stopwatch.GetTimestamp();
        CanceledAtExit = token.IsCancellationRequested;
        Succeeded = succeeded;
    }

    internal void FailIfPending(CancellationToken token)
    {
        if (BeginQpc.HasValue && !EndQpc.HasValue)
        {
            End(token, succeeded: false);
        }
    }
}
