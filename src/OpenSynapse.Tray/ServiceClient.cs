using System.IO;
using System.IO.Pipes;
using OpenSynapse.Contracts;

namespace OpenSynapse.Tray;

public sealed class ServiceClient(string pipeName)
{
    public ServiceClient()
        : this(OpenSynapseIpc.PipeName)
    {
    }

    public async Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream pipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);
        ServiceRequest request = new GetStatusRequest(
            OpenSynapseIpc.ProtocolVersion,
            Guid.NewGuid());
        await IpcMessageCodec.WriteRequestAsync(pipe, request, cancellationToken);
        ServiceServerFrame frame = await IpcMessageCodec.ReadServerFrameAsync(
            pipe,
            cancellationToken);
        if (frame is not ServiceResponse response)
        {
            throw new InvalidDataException("Service returned an unexpected server frame.");
        }

        if (response.Version != OpenSynapseIpc.ProtocolVersion
            || response.RequestId != request.RequestId
            || !response.Success
            || response.Status is null)
        {
            throw new InvalidDataException(response.Error ?? "Service returned an invalid status response.");
        }

        return response.Status;
    }
}
