using System.Buffers.Binary;
using System.Text.Json;

namespace OpenSynapse.Contracts;

public static class OpenSynapseIpc
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 64 * 1024;
    public const string PipeName = "OpenSynapse.Service.v1";
}

public static class IpcMessageCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        if (payload.Length > OpenSynapseIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the 64 KiB limit.");
        }

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > OpenSynapseIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
            ?? throw new InvalidDataException("IPC message body is empty.");
    }
}
