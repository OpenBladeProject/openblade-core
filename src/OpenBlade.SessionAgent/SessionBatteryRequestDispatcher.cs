using System.Diagnostics;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionBatteryRequestDispatcher
{
    private const string DiagnosticComponent = "session-agent";
    private const string DiagnosticOperation = "windows-battery-report";
    private readonly Func<CancellationToken, Task<WindowsBatteryReport?>> readReport;
    private readonly Action<DiagnosticFailure> diagnosticFailureSink;

    public SessionBatteryRequestDispatcher(WindowsBatteryReportProvider provider)
        : this(
            (provider ?? throw new ArgumentNullException(nameof(provider))).ReadAsync,
            TraceFailure)
    {
    }

    internal SessionBatteryRequestDispatcher(
        Func<CancellationToken, Task<WindowsBatteryReport?>> readReport,
        Action<DiagnosticFailure>? diagnosticFailureSink = null)
    {
        this.readReport = readReport ?? throw new ArgumentNullException(nameof(readReport));
        this.diagnosticFailureSink = diagnosticFailureSink ?? TraceFailure;
    }

    public async Task<SessionBatteryResponse> DispatchAsync(
        SessionBatteryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            WindowsBatteryReport? report = await readReport(cancellationToken)
                .ConfigureAwait(false);
            return report is null
                ? Failure(
                    request.RequestId,
                    SessionBatteryOperationStatus.Unavailable,
                    "Windows could not provide a battery report.")
                : new SessionBatteryResponse(
                    SessionBatteryIpc.ProtocolVersion,
                    request.RequestId,
                    SessionBatteryOperationStatus.Succeeded,
                    null,
                    report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            diagnosticFailureSink(DiagnosticFailure.FromException(
                DiagnosticComponent,
                DiagnosticOperation,
                "dispatcher",
                "exception",
                exception));
            return Failure(
                request.RequestId,
                SessionBatteryOperationStatus.Failed,
                "The Session Agent could not read the Windows battery report.");
        }
    }

    private static void TraceFailure(DiagnosticFailure failure) =>
        Trace.TraceWarning("Windows battery report dispatch failure: {0}", failure);

    private static SessionBatteryResponse Failure(
        Guid requestId,
        SessionBatteryOperationStatus status,
        string error) => new(
            SessionBatteryIpc.ProtocolVersion,
            requestId,
            status,
            error,
            null);
}
