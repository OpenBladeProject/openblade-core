using System.IO.Pipes;
using OpenBlade.Contracts;

namespace OpenBlade.Setup;

public interface ISetupServiceHealthProbe
{
    Task VerifyAsync(CancellationToken cancellationToken);
}

public sealed class SetupServiceHealthProbe : ISetupServiceHealthProbe
{
    private readonly Func<CancellationToken, ValueTask<Stream>> connectAsync;
    private readonly Func<GetStatusRequest> createRequest;

    public SetupServiceHealthProbe()
        : this(ConnectAsync, () => new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()))
    {
    }

    public SetupServiceHealthProbe(
        Func<CancellationToken, ValueTask<Stream>> connectAsync,
        Func<GetStatusRequest> createRequest)
    {
        this.connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        this.createRequest = createRequest ?? throw new ArgumentNullException(nameof(createRequest));
    }

    public Task VerifyAsync(CancellationToken cancellationToken)
        => VerifyAsync(cancellationToken, timing: null);

    internal async Task VerifyAsync(
        CancellationToken cancellationToken,
        SetupServiceHealthTiming? timing)
    {
        GetStatusRequest request = createRequest();
        timing?.Connect.Begin(cancellationToken);
        try
        {
            await using Stream connection = await connectAsync(cancellationToken);
            timing?.Connect.End(cancellationToken, succeeded: true);
            try
            {
                await VerifyConnectionAsync(connection, request, cancellationToken, timing);
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

    private static async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            OpenBladeIpc.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
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
