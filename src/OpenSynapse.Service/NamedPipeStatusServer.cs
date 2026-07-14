using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using OpenSynapse.Contracts;

namespace OpenSynapse.Service;

public sealed class NamedPipeStatusServer(
    string pipeName,
    SecurityIdentifier authorizedUser,
    StatusRequestHandler handler)
{
    public async Task ServeOnceAsync(CancellationToken cancellationToken)
    {
        await using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            PipeSecurityFactory.Create(authorizedUser));

        await pipe.WaitForConnectionAsync(cancellationToken);
        ServiceResponse response;
        try
        {
            ServiceRequest request = await IpcMessageCodec.ReadAsync<ServiceRequest>(
                pipe,
                cancellationToken);
            response = handler.Handle(request);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            response = new ServiceResponse(
                OpenSynapseIpc.ProtocolVersion,
                Guid.Empty,
                Success: false,
                Error: "IPC request is malformed.",
                Status: null);
        }

        await IpcMessageCodec.WriteAsync(pipe, response, cancellationToken);
    }
}
