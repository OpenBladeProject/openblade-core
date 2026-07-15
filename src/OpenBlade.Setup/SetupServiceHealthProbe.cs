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
        : this(ConnectAsync, () => new GetStatusRequest(OpenSynapseIpc.ProtocolVersion, Guid.NewGuid()))
    {
    }

    public SetupServiceHealthProbe(
        Func<CancellationToken, ValueTask<Stream>> connectAsync,
        Func<GetStatusRequest> createRequest)
    {
        this.connectAsync = connectAsync ?? throw new ArgumentNullException(nameof(connectAsync));
        this.createRequest = createRequest ?? throw new ArgumentNullException(nameof(createRequest));
    }

    public async Task VerifyAsync(CancellationToken cancellationToken)
    {
        GetStatusRequest request = createRequest();
        await using Stream connection = await connectAsync(cancellationToken);
        await IpcMessageCodec.WriteRequestAsync(connection, request, cancellationToken);
        ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(connection, cancellationToken);
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
            OpenSynapseIpc.PipeName,
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
