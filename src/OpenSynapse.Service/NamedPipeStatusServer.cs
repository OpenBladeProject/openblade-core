using System.IO.Pipes;
using System.Security.Principal;
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
        ServiceRequest request = await IpcMessageCodec.ReadAsync<ServiceRequest>(
            pipe,
            cancellationToken);
        ServiceResponse response = handler.Handle(request);
        await IpcMessageCodec.WriteAsync(pipe, response, cancellationToken);
    }
}
