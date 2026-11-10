using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

public interface ISetupServiceHealthProbe
{
    Task VerifyAsync(CancellationToken cancellationToken);

    internal Task VerifyWithTimingAsync(CancellationToken cancellationToken, SetupServiceHealthTiming timing)
        => VerifyAsync(cancellationToken);
}

public sealed class SetupServiceHealthProbe : ISetupServiceHealthProbe
{
    private readonly Func<CancellationToken, ValueTask<Stream>> connectAsync;
    private readonly Func<GetStatusRequest> createRequest;

    public SetupServiceHealthProbe()
        : this(token => ConnectAsync(TokenImpersonationLevel.None, token), CreateStatusRequest)
    {
    }

    public SetupServiceHealthProbe(
        Func<CancellationToken, ValueTask<Stream>> connectAsync,
        Func<GetStatusRequest> createRequest)
    {
        this.connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        this.createRequest = createRequest ?? throw new ArgumentNullException(nameof(createRequest));
    }

    internal static SetupServiceHealthProbe CreateCandidate()
        => new(token => ConnectAsync(TokenImpersonationLevel.Identification, token), CreateStatusRequest);

    private static GetStatusRequest CreateStatusRequest()
        => new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());

    public Task VerifyAsync(CancellationToken cancellationToken)
        => VerifyAsync(cancellationToken, timing: null);

    Task ISetupServiceHealthProbe.VerifyWithTimingAsync(
        CancellationToken cancellationToken,
        SetupServiceHealthTiming timing)
        => VerifyAsync(cancellationToken, timing);

    internal async Task VerifyAsync(
        CancellationToken cancellationToken,
        SetupServiceHealthTiming? timing,
        SetupCandidateServiceVerification? candidate = null)
    {
        if (candidate is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (timing is not null)
        {
            timing.StagesObserved = true;
        }
        GetStatusRequest request = createRequest();
        timing?.Connect.Begin(cancellationToken);
        try
        {
            await using Stream connection = await connectAsync(cancellationToken);
            timing?.Connect.End(cancellationToken, succeeded: true);
            try
            {
                if (candidate is not null)
                {
                    candidate.VerifyPeer(connection);
                    await candidate.VerifyLaunchAsync(cancellationToken);
                }

                await VerifyConnectionAsync(connection, request, cancellationToken, timing);

                // Keep the original client handle through the final checks.
                if (candidate is not null)
                {
                    candidate.VerifyPeer(connection);
                    await candidate.VerifyLaunchAsync(cancellationToken);
                }
            }
            finally
            {
                timing?.IdentifyPeer(connection);
            }
        }
        finally
        {
            timing?.Connect.FailIfPending(cancellationToken);
        }
    }

    private static async Task VerifyConnectionAsync(
        Stream connection,
        GetStatusRequest request,
        CancellationToken cancellationToken,
        SetupServiceHealthTiming? timing)
    {
        await IpcMessageCodec.WriteRequestAsync(connection, request, cancellationToken, timing?.RequestWrite);
        ServiceServerFrame frame;
        timing?.ResponseRead.Begin(cancellationToken);
        try
        {
            frame = await IpcMessageCodec.ReadServerFrameAsync(connection, cancellationToken);
            timing?.ResponseRead.End(cancellationToken, succeeded: true);
        }
        finally
        {
            timing?.ResponseRead.FailIfPending(cancellationToken);
        }

        if (frame is not ServiceResponse response)
        {
            throw new InvalidDataException("Service health check returned an unexpected frame.");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException("Service health response ID does not match the request.");
        }

        if (!response.Success || response.Status is null)
        {
            throw new InvalidDataException("Service health check did not return a status snapshot.");
        }
    }

    private static async ValueTask<Stream> ConnectAsync(
        TokenImpersonationLevel impersonationLevel, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            OpenBladeIpc.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            impersonationLevel);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
