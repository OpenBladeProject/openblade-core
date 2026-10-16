using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionAudioIpcCodec
{
    private const int MaximumTextLength = 4096;
    private const int MaximumIdLength = 256;

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Task WriteRequestAsync(
        Stream stream,
        SessionAudioRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        return WriteFrameAsync(stream, request, cancellationToken);
    }

    public static async Task<SessionAudioRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionAudioRequest request = await ReadFrameAsync<SessionAudioRequest>(
            stream,
            cancellationToken).ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static Task WriteResponseAsync(
        Stream stream,
        SessionAudioResponse response,
        CancellationToken cancellationToken = default)
    {
        ValidateResponse(response);
        return WriteFrameAsync(stream, response, cancellationToken);
    }

    public static async Task<SessionAudioResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionAudioResponse response = await ReadFrameAsync<SessionAudioResponse>(
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
            throw new InvalidDataException("Session audio IPC body is invalid.", exception);
        }

        if (payload.Length is <= 0 or > SessionAudioIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session audio IPC message exceeds the 64 KiB limit.");
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
        if (length is <= 0 or > SessionAudioIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session audio IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("Session audio IPC body is missing.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("Session audio IPC body is invalid.", exception);
        }
    }

    private static void ValidateRequest(SessionAudioRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("Session audio IPC request is missing.");
        }

        ValidateEnvelope(request.Version, request.RequestId);
        bool valid = request switch
        {
            DiscoverThxAudioRequest => true,
            ReadThxAudioRequest value => Enum.IsDefined(value.Endpoint),
            SetThxSpatialRequest value => Enum.IsDefined(value.Endpoint),
            SetThxEqualizerRequest value => Enum.IsDefined(value.Endpoint),
            SelectThxPresetRequest value =>
                Enum.IsDefined(value.Endpoint) && ValidId(value.PresetId),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("Session audio IPC request body is invalid.");
        }
    }

    private static void ValidateResponse(SessionAudioResponse? response)
    {
        if (response is null)
        {
            throw new InvalidDataException("Session audio IPC response is missing.");
        }

        ValidateEnvelope(response.Version, response.RequestId);
        bool validError = response.Status == ThxAudioOperationStatus.Succeeded
            ? response.Error is null
            : ValidText(response.Error);
        bool valid = Enum.IsDefined(response.Status)
            && Enum.IsDefined(response.Availability)
            && validError
            && (response.State is null || ValidState(response.State))
            && (response.Status == ThxAudioOperationStatus.Succeeded
                ? response.Availability == ThxAudioAvailability.Available
                    || response is { State: null, Availability: not ThxAudioAvailability.Available }
                : response.State is null);
        if (!valid)
        {
            throw new InvalidDataException("Session audio IPC response body is invalid.");
        }
    }

    private static void ValidateEnvelope(int version, Guid requestId)
    {
        if (version != SessionAudioIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Session audio IPC protocol version is unsupported.");
        }
        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("Session audio IPC request ID is missing.");
        }
    }

    private static bool ValidState(ThxAudioState state) =>
        Enum.IsDefined(state.Endpoint)
        && state.Presets is not null
        && state.Presets.Count <= SessionAudioIpc.MaximumPresets
        && (state.SelectedPresetId is null || ValidId(state.SelectedPresetId))
        && state.Presets.All(preset =>
            preset is not null
            && ValidId(preset.Id)
            && ValidText(preset.Name));

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumIdLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static bool ValidText(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumTextLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);
}
