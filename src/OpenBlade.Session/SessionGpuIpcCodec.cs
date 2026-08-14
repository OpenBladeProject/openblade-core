using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionGpuIpcCodec
{
    private const int MaximumApps = 128;
    private const int MaximumTextLength = 4096;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Task WriteRequestAsync(
        Stream stream,
        SessionGpuRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        return WriteFrameAsync(stream, request, cancellationToken);
    }

    public static async Task<SessionGpuRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionGpuRequest request = await ReadFrameAsync<SessionGpuRequest>(
            stream,
            cancellationToken).ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static Task WriteResponseAsync(
        Stream stream,
        SessionGpuResponse response,
        CancellationToken cancellationToken = default)
    {
        ValidateResponse(response);
        return WriteFrameAsync(stream, response, cancellationToken);
    }

    public static async Task<SessionGpuResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionGpuResponse response = await ReadFrameAsync<SessionGpuResponse>(
            stream,
            cancellationToken).ConfigureAwait(false);
        ValidateResponse(response);
        return response;
    }

    private static async Task WriteFrameAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Session GPU IPC body is invalid.", exception);
        }

        if (payload.Length is <= 0 or > SessionGpuIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session GPU IPC message exceeds the 64 KiB limit.");
        }

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadFrameAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > SessionGpuIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session GPU IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("Session GPU IPC body is missing.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("Session GPU IPC body is invalid.", exception);
        }
    }

    private static void ValidateRequest(SessionGpuRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("Session GPU IPC request is missing.");
        }
        ValidateEnvelope(request.Version, request.RequestId);
        bool valid = request switch
        {
            QueryGpuAppsRequest => true,
            CloseGpuAppsRequest value => value.Targets is not null
                && value.Targets.Count is > 0 and <= MaximumApps
                && value.Targets.All(ValidIdentity)
                && value.Targets.Select(target => target.ProcessId).Distinct().Count()
                    == value.Targets.Count,
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("Session GPU IPC request body is invalid.");
        }
    }

    private static void ValidateResponse(SessionGpuResponse? response)
    {
        if (response is null)
        {
            throw new InvalidDataException("Session GPU IPC response is missing.");
        }
        ValidateEnvelope(response.Version, response.RequestId);
        bool validError = response.Status == SessionGpuOperationStatus.Succeeded
            ? response.Error is null
            : response.Error is not null && ValidText(response.Error);
        bool valid = Enum.IsDefined(response.Status)
            && validError
            && response.Apps is not null
            && response.Results is not null
            && response.Apps.Count <= MaximumApps
            && response.Results.Count <= MaximumApps
            && response.Apps.All(ValidApp)
            && response.Results.All(ValidResult);
        if (!valid)
        {
            throw new InvalidDataException("Session GPU IPC response body is invalid.");
        }
    }

    private static void ValidateEnvelope(int version, Guid requestId)
    {
        if (version != SessionGpuIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Session GPU IPC protocol version is unsupported.");
        }
        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("Session GPU IPC request ID is missing.");
        }
    }

    private static bool ValidIdentity(SessionGpuAppIdentity value) =>
        value is not null
        && value.ProcessId > 0
        && value.StartTimeUtcTicks > 0
        && ValidName(value.Name);

    private static bool ValidApp(SessionGpuApp value) =>
        value is not null
        && value.ProcessId > 0
        && value.StartTimeUtcTicks > 0
        && ValidName(value.Name)
        && double.IsFinite(value.UtilizationPercent)
        && value.UtilizationPercent >= 0
        && (value.ExecutablePath is null || ValidText(value.ExecutablePath))
        && (value.BlockReason is null || ValidText(value.BlockReason))
        && (value.CanClose ? value.BlockReason is null : value.BlockReason is not null);

    private static bool ValidResult(SessionGpuActionResult value) =>
        value is not null
        && value.ProcessId > 0
        && value.StartTimeUtcTicks > 0
        && (value.Succeeded ? value.Error is null : value.Error is not null && ValidText(value.Error));

    private static bool ValidName(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 260
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static bool ValidText(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumTextLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(character => char.IsControl(character) && character is not '\r' and not '\n');
}
