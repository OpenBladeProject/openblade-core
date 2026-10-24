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
        int? processExitCode = null) => new(
            component,
            operation,
            stage,
            reason,
            mayHaveSubmitted || cause?.MayHaveSubmitted == true,
            Cause: Trim(cause, 3),
            ProcessExitCode: processExitCode);

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
        // Retain the new exception's native error as well as the earlier failure
        // when cleanup needs the bounded Cause slot for the original operation.
        return cause is null ? failure : failure with
        {
            NativeErrorCode = failure.NativeErrorCode ?? FindNativeError(failure.Cause),
            Cause = Trim(cause, 3),
        };
    }

    private static int? FindNativeError(DiagnosticFailure? failure)
    {
        for (int depth = 0; failure is not null && depth < 4; depth++, failure = failure.Cause)
        {
            if (failure.NativeErrorCode is { } code)
            {
                return code;
            }
        }
        return null;
    }

    private static DiagnosticFailure? Trim(DiagnosticFailure? failure, int remaining)
    {
        if (failure is null || remaining <= 0)
        {
            return null;
        }
        return failure with { Cause = Trim(failure.Cause, remaining - 1) };
    }
}
