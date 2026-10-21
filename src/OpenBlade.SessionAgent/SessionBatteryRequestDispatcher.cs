using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionBatteryRequestDispatcher
{
    private readonly Func<CancellationToken, Task<WindowsBatteryReport?>> readReport;

    public SessionBatteryRequestDispatcher(WindowsBatteryReportProvider provider)
        : this((provider ?? throw new ArgumentNullException(nameof(provider))).ReadAsync)
    {
    }

    internal SessionBatteryRequestDispatcher(
        Func<CancellationToken, Task<WindowsBatteryReport?>> readReport)
    {
        this.readReport = readReport ?? throw new ArgumentNullException(nameof(readReport));
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
        catch (Exception)
        {
            return Failure(
                request.RequestId,
                SessionBatteryOperationStatus.Failed,
                "The Session Agent could not read the Windows battery report.");
        }
    }

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
