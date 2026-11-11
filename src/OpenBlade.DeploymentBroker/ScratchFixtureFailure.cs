#if OPENBLADE_CANDIDATE_RECEIPT_FIXTURE
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentBroker;

internal static class ScratchFixtureFailure
{
    internal static bool IsRefusal(Exception error) => error is RetainedDataException retained
        ? IsRefusal(retained.InnerException!)
        : error is AggregateException aggregate
        ? aggregate.InnerExceptions.Count != 0 && aggregate.InnerExceptions.All(IsRefusal)
        : error is IOException or InvalidDataException or UnauthorizedAccessException
            or OpenBladeReleaseVerificationException or OperationCanceledException or Win32Exception;

    // Only this diagnostic wrapper delegates classification to its cause. Ordinary
    // typed I/O/input wrappers retain their existing refusal contract.
    internal sealed class RetainedDataException(string message, Exception cause) : IOException(message, cause);

    // A scope must not let a Dispose exception replace the operation's cause.
    internal static void Run(Action operation, params Action[] cleanup)
    {
        Exception? failure = null;
        try { operation(); }
        catch (Exception error) { failure = error; }
        Cleanup(failure, cleanup);
    }

    // Attempt every cleanup once; an unconfirmed stop is a failure, never success.
    internal static void Cleanup(Exception? primary, params Action[] cleanup)
    {
        List<Exception> failures = [];
        foreach (var action in cleanup)
        {
            try { action(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0)
        {
            if (primary is not null) { failures.Insert(0, primary); }
            throw new AggregateException("Scratch refusal and/or cleanup failure.", failures);
        }
        if (primary is not null) { ExceptionDispatchInfo.Capture(primary).Throw(); }
    }
}
#endif
