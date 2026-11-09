using System.Buffers.Binary;
using System.Text;
using OpenBlade.Deployment;

namespace OpenBlade.DeploymentClient;

internal static class DeploymentStatusCommand
{
    internal static async Task<int> RunAsync(TextWriter output, TextWriter error, CancellationToken token)
    {
        try
        {
            if (new WindowsClientIntegritySource().IsElevated()
                || !new WindowsClientSessionSource().IsActiveLocalConsoleSession())
            { throw new UnauthorizedAccessException("Deployment status requires the normal console user."); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            byte[] bytes = await ReadAsync(new WindowsBrokerPipeConnector(startService: false),
                new WindowsProtectedBrokerIdentityVerifier(), Guid.NewGuid(), deadline.Token).ConfigureAwait(false);
            await output.WriteLineAsync(Encoding.UTF8.GetString(bytes)).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            await error.WriteLineAsync("deployment-status-unavailable:" + exception.GetType().Name).ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<byte[]> ReadAsync(IBrokerPipeConnector connector,
        IBrokerServerIdentityVerifier verifier, Guid requestId, CancellationToken token)
    {
        await using IBrokerPipeConnection connection = await connector.ConnectAsync(token).ConfigureAwait(false);
        verifier.Verify(connection.ServerIdentity);
        byte[] request = DeploymentBrokerProtocol.SerializeRequest(new(1, requestId, DeploymentBrokerAction.ReadDeploymentStatus));
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, request.Length);
        await connection.Output.WriteAsync(header, token).ConfigureAwait(false);
        await connection.Output.WriteAsync(request, token).ConfigureAwait(false);
        await connection.Output.FlushAsync(token).ConfigureAwait(false);
        await connection.Input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > DeploymentStatusCodec.MaximumBytes)
        { throw new InvalidDataException("Deployment status frame exceeds its bound."); }
        byte[] bytes = new byte[length];
        await connection.Input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        _ = DeploymentStatusCodec.Parse(bytes, requestId);
        return bytes;
    }
}
