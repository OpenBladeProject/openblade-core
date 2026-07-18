using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBlade.Contracts;
using OpenBlade.Display;

namespace OpenBlade.Session;

public static class SessionDisplayIpcCodec
{
    private const int MaximumKeyLength = 1024;
    private const int MaximumTextLength = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Task WriteRequestAsync(
        Stream stream,
        SessionDisplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        return WriteFrameAsync(stream, request, cancellationToken);
    }

    public static async Task<SessionDisplayRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionDisplayRequest request = await ReadFrameAsync<SessionDisplayRequest>(
            stream,
            cancellationToken).ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static Task WriteResponseAsync(
        Stream stream,
        SessionDisplayResponse response,
        CancellationToken cancellationToken = default)
    {
        ValidateResponse(response);
        return WriteFrameAsync(stream, response, cancellationToken);
    }

    public static async Task<SessionDisplayResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionDisplayResponse response = await ReadFrameAsync<SessionDisplayResponse>(
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
            throw new InvalidDataException("Session display IPC body is invalid.", exception);
        }

        if (payload.Length is <= 0 or > SessionDisplayIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session display IPC message exceeds the 64 KiB limit.");
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
        if (length is <= 0 or > SessionDisplayIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session display IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("Session display IPC body is missing.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("Session display IPC body is invalid.", exception);
        }
    }

    private static void ValidateRequest(SessionDisplayRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("Session display IPC request is missing.");
        }

        ValidateEnvelope(request.Version, request.RequestId);
        bool valid = request switch
        {
            QueryDisplayRequest => true,
            SetRefreshRateRequest value =>
                IsConfirmed(value.ExpectedPowerSource) && ValidKey(value.RefreshRateKey),
            SetHdrRequest value => IsConfirmed(value.ExpectedPowerSource),
            SetColorProfileRequest value =>
                IsConfirmed(value.ExpectedPowerSource) && ValidKey(value.ColorProfileKey),
            ApplyDisplayProfileRequest value =>
                IsConfirmed(value.ExpectedPowerSource) && ValidSettings(value.Settings),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("Session display IPC request body is invalid.");
        }
    }

    private static void ValidateResponse(SessionDisplayResponse? response)
    {
        if (response is null)
        {
            throw new InvalidDataException("Session display IPC response is missing.");
        }

        ValidateEnvelope(response.Version, response.RequestId);
        bool validError = response.Status == SessionDisplayOperationStatus.Succeeded
            ? response.Error is null
            : !string.IsNullOrWhiteSpace(response.Error)
                && response.Error.Length <= MaximumTextLength
                && string.Equals(response.Error, response.Error.Trim(), StringComparison.Ordinal);
        bool valid = Enum.IsDefined(response.Status)
            && validError
            && ValidSnapshot(response.State);
        if (!valid)
        {
            throw new InvalidDataException("Session display IPC response body is invalid.");
        }
    }

    private static void ValidateEnvelope(int version, Guid requestId)
    {
        if (version != SessionDisplayIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Session display IPC protocol version is unsupported.");
        }
        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("Session display IPC request ID is missing.");
        }
    }

    private static bool ValidSettings(DisplayProfileSettings? settings) =>
        settings is not null
        && (settings.RefreshRateKey is null || ValidKey(settings.RefreshRateKey))
        && (settings.ColorProfileKey is null || ValidKey(settings.ColorProfileKey));

    private static bool ValidSnapshot(DisplaySettingsSnapshot? state) =>
        state is not null
        && state.Width >= 0
        && state.Height >= 0
        && state.RefreshOptions is not null
        && state.ColorProfiles is not null
        && state.RefreshOptions.Count <= 64
        && state.ColorProfiles.Count <= 64
        && (state.CurrentRefreshKey is null || ValidKey(state.CurrentRefreshKey))
        && (state.ActiveColorProfileId is null || ValidKey(state.ActiveColorProfileId))
        && (state.Message is null || state.Message.Length <= MaximumTextLength)
        && state.RefreshOptions.All(option =>
            option is not null
            && ValidKey(option.Key)
            && !string.IsNullOrWhiteSpace(option.Name)
            && option.Name.Length <= MaximumTextLength)
        && state.ColorProfiles.All(profile =>
            profile is not null
            && ValidKey(profile.Id)
            && !string.IsNullOrWhiteSpace(profile.Name)
            && profile.Name.Length <= MaximumTextLength);

    private static bool ValidKey(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumKeyLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !value.Any(char.IsControl);

    private static bool IsConfirmed(PowerSource source) => source is
        PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC;
}
