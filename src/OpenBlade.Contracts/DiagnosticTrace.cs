using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OpenBlade.Contracts;

/// <summary>Records failures at a catch boundary without changing its recovery or operation authority.</summary>
public static class DiagnosticTrace
{
    public static string FormatException(string component, string operation, Exception exception, string? context = null)
    {
        int nativeError = Marshal.GetLastPInvokeError();
        try
        {
            ArgumentNullException.ThrowIfNull(exception);
            DiagnosticFailure failure = DiagnosticFailure.FromException(
                component, operation, "Exception", "OperationFailed", exception);
            if (context is not null)
            {
                string sanitized = DiagnosticText.Redact(context);
                failure = failure with
                {
                    Context = sanitized.Length <= 8192 ? sanitized : sanitized[..8192],
                    DetailsTruncated = failure.DetailsTruncated || sanitized.Length > 8192,
                };
            }
            return JsonSerializer.Serialize(failure.LimitDetails());
        }
        finally
        {
            Marshal.SetLastPInvokeError(nativeError);
        }
    }

    public static void TraceFailure(DiagnosticFailure failure)
    {
        int nativeError = Marshal.GetLastPInvokeError();
        try
        {
            Trace.TraceWarning("Diagnostic failure: {0}", JsonSerializer.Serialize(failure.LimitDetails()));
        }
        catch
        {
            // Diagnostic listeners must not change product behavior.
        }
        finally
        {
            Marshal.SetLastPInvokeError(nativeError);
        }
    }

    public static void TraceException(string component, string operation, Exception exception, string? context = null)
    {
        int nativeError = Marshal.GetLastPInvokeError();
        try
        {
            Trace.TraceWarning("Diagnostic failure: {0}", FormatException(component, operation, exception, context));
        }
        catch
        {
            // An unavailable diagnostic sink must not change the operation's result.
        }
        finally
        {
            Marshal.SetLastPInvokeError(nativeError);
        }
    }
}
