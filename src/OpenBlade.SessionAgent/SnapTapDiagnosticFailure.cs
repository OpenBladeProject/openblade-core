using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

internal static class SnapTapDiagnosticFailure
{
    internal const string Coordinator = "SnapTapCoordinator";
    internal const string Supervisor = "SnapTapSupervisor";
    internal const string Worker = "SnapTapWorker";

    internal static DiagnosticFailure Create(
        string component,
        string operation,
        string stage,
        string reason,
        bool mayHaveSubmitted = false,
        DiagnosticFailure? cause = null,
        int? processExitCode = null)
    {
        return new DiagnosticFailure(
            component,
            operation,
            stage,
            reason,
            mayHaveSubmitted || cause?.MayHaveSubmitted == true,
            Cause: cause,
            ProcessExitCode: processExitCode)
            .LimitDetails();
    }

    internal static DiagnosticFailure FromException(
        string component,
        string operation,
        string stage,
        string reason,
        Exception exception,
        bool mayHaveSubmitted = false,
        DiagnosticFailure? cause = null)
    {
        DiagnosticFailure failure = DiagnosticFailure.FromException(
            component,
            operation,
            stage,
            reason,
            exception,
            mayHaveSubmitted || cause?.MayHaveSubmitted == true);
        if (cause is null)
        {
            return failure;
        }

        // Keep the earlier operation as the causal chain while retaining the
        // caught exception's own inner/aggregate branches as related details.
        var related = new List<DiagnosticFailure>();
        if (failure.Cause is not null)
        {
            related.Add(failure.Cause);
        }
        if (failure.Related is not null)
        {
            related.AddRange(failure.Related);
        }
        DiagnosticFailure combined = failure with
        {
            NativeErrorCode = failure.NativeErrorCode
                ?? FindNativeError(failure)
                ?? FindNativeError(cause),
            Cause = cause,
            Related = related.Count == 0 ? null : related,
        };
        return combined.LimitDetails();
    }

    private static int? FindNativeError(DiagnosticFailure? failure)
    {
        var pending = new Stack<DiagnosticFailure>();
        if (failure is not null)
        {
            pending.Push(failure);
        }
        int visited = 0;
        while (pending.Count > 0 && visited++ < DiagnosticFailure.MaximumNodes)
        {
            failure = pending.Pop();
            if (failure.NativeErrorCode is { } code)
            {
                return code;
            }
            if (failure.Related is not null)
            {
                for (int index = failure.Related.Count - 1; index >= 0; index--)
                {
                    pending.Push(failure.Related[index]);
                }
            }
            if (failure.Cause is not null)
            {
                pending.Push(failure.Cause);
            }
        }
        return null;
    }

}
