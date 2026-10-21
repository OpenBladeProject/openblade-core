using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Session;

namespace OpenBlade.Tray;

internal sealed class SessionBatteryReportAdapter : IWindowsBatteryReportAdapter
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(22);
    private readonly string pipeName;

    internal SessionBatteryReportAdapter()
    {
        SecurityIdentifier userSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        pipeName = SessionBatteryPipeEndpoint.Create(userSid, process.SessionId).PipeName;
    }

    internal SessionBatteryReportAdapter(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        this.pipeName = pipeName;
    }

    public async Task<WindowsBatteryReport> ReadAsync(CancellationToken cancellationToken)
    {
        Guid requestId = Guid.NewGuid();
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            using (var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
            {
                connectionDeadline.CancelAfter(ConnectionTimeout);
                await pipe.ConnectAsync(connectionDeadline.Token).ConfigureAwait(false);
            }

            using var queryDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            queryDeadline.CancelAfter(QueryTimeout);
            await SessionBatteryIpcCodec.WriteRequestAsync(
                pipe,
                new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, requestId),
                queryDeadline.Token).ConfigureAwait(false);
            SessionBatteryResponse response = await SessionBatteryIpcCodec.ReadResponseAsync(
                pipe,
                queryDeadline.Token).ConfigureAwait(false);
            if (response.RequestId != requestId)
            {
                throw new InvalidDataException(
                    "Session-agent response ID does not match the request.");
            }

            if (response.Status != SessionBatteryOperationStatus.Succeeded
                || response.Report is null)
            {
                throw new InvalidOperationException(
                    response.Error ?? "Battery history is unavailable.");
            }

            return response.Report;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("The battery session agent is not responding.");
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new IOException("Battery history is unavailable.", exception);
        }
    }
}
