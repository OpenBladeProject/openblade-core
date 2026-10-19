using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionAudioIpcCodec
{
    private const int MaximumTextLength = 4096;
    private const int MaximumIdLength = 256;
    private const int MaximumExecutableNameLength = 260;
    private const double MinimumEqualizerGainDb = -12;
    private const double MaximumEqualizerGainDb = 12;

    private static readonly HashSet<string> EqualizerPresetIds =
        new(StringComparer.Ordinal)
        {
            "game",
            "movie",
            "music",
            "voice",
            "custom",
            "default",
        };

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
            ConnectThxAudioRequest value => Enum.IsDefined(value.Endpoint),
            SetThxSpatialRequest value => Enum.IsDefined(value.Endpoint),
            SetThxEqualizerRequest value => Enum.IsDefined(value.Endpoint),
            SelectThxPresetRequest value =>
                Enum.IsDefined(value.Endpoint)
                && EqualizerPresetIds.Contains(value.PresetId)
                && ValidEqualizerGains(value.Endpoint, value.GainsDb),
            SetThxEnhancementRequest value =>
                Enum.IsDefined(value.Endpoint)
                && Enum.IsDefined(value.Kind)
                && (value.LevelPercent is null or >= 0 and <= 100)
                && (value.Enabled || value.LevelPercent is null)
                && (value.Kind != ThxAudioEnhancementKind.SoundNormalization
                    || value.Endpoint == ThxAudioEndpoint.Speakers),
            ApplyThxApplicationProfileRequest value =>
                Enum.IsDefined(value.Endpoint)
                && Enum.IsDefined(value.Mode)
                && ValidExecutableName(value.ExecutableName),
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
                ? response.Availability is ThxAudioAvailability.Available or ThxAudioAvailability.AvailableReadOnly
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
        && Enum.IsDefined(state.ApplicationProfiles)
        && Enum.IsDefined(state.Equalizer)
        && state.Presets is not null
        && state.Presets.Count <= SessionAudioIpc.MaximumPresets
        && (state.SelectedPresetId is null
            || EqualizerPresetIds.Contains(state.SelectedPresetId))
        && (state.SelectedNativeProfileId is null
            || ValidId(state.SelectedNativeProfileId))
        && state.Presets.All(preset =>
            preset is not null
            && EqualizerPresetIds.Contains(preset.Id)
            && ValidText(preset.Name))
        && state.Presets.Select(preset => preset.Id)
            .Distinct(StringComparer.Ordinal).Count() == state.Presets.Count
        && (state.SelectedPresetId is null
            || state.Presets.Any(preset => string.Equals(
                preset.Id,
                state.SelectedPresetId,
                StringComparison.Ordinal)))
        && state.EqualizerBands is not null
        && (state.EqualizerBands.Count == 0
            || ValidEqualizerBands(state.Endpoint, state.EqualizerBands))
        && state.FactoryPresetCurves is not null
        && (state.FactoryPresetCurves.Count == 0
            || ValidFactoryCurves(state.Endpoint, state.FactoryPresetCurves))
        && ValidEnhancement(state.SoundNormalization)
        && ValidEnhancement(state.VoiceClarity)
        && (state.Endpoint == ThxAudioEndpoint.Speakers
            || state.SoundNormalization is null)
        && state.ApplicationProfileCatalog is not null
        && state.ApplicationProfileCatalog.Count <= SessionAudioIpc.MaximumApplicationProfiles
        && state.ApplicationProfileCatalog.All(ValidApplicationProfile)
        && state.ApplicationProfileCatalog.Select(profile => profile.ExecutableName)
            .Distinct(StringComparer.Ordinal).Count() == state.ApplicationProfileCatalog.Count;

    private static bool ValidFactoryCurves(
        ThxAudioEndpoint endpoint,
        IReadOnlyList<ThxAudioPresetCurve> curves)
    {
        string[] expected = ["game", "movie", "music", "voice", "custom"];
        return curves.Count == expected.Length
            && curves.Select(curve => curve.PresetId).SequenceEqual(expected)
            && curves.All(curve => ValidEqualizerGains(endpoint, curve.GainsDb));
    }

    private static bool ValidApplicationProfile(ThxAudioApplicationProfile profile) =>
        profile is not null
        && ValidExecutableName(profile.ExecutableName)
        && (profile.SupportsImmersive || profile.SupportsCompetitive);

    private static bool ValidEqualizerGains(
        ThxAudioEndpoint endpoint,
        IReadOnlyList<double>? gains) =>
        gains is not null
        && (gains.Count == 10 || endpoint == ThxAudioEndpoint.Speakers && gains.Count == 7)
        && gains.All(ValidGain);

    private static bool ValidEqualizerBands(
        ThxAudioEndpoint endpoint,
        IReadOnlyList<ThxAudioEqualizerBand> bands)
    {
        int[] expected = endpoint == ThxAudioEndpoint.Speakers && bands.Count == 7
            ? [250, 500, 1000, 2000, 4000, 8000, 16000]
            : [31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
        return bands.Count == expected.Length
            && bands.Select(band => band.FrequencyHz).SequenceEqual(expected)
            && bands.All(band => ValidGain(band.GainDb));
    }

    private static bool ValidGain(double value) =>
        double.IsFinite(value)
        && value is >= MinimumEqualizerGainDb and <= MaximumEqualizerGainDb;

    private static bool ValidEnhancement(ThxAudioEnhancement? value) =>
        value is null || value.LevelPercent is >= 0 and <= 100;

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

    private static bool ValidExecutableName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumExecutableNameLength
        && value.EndsWith(".exe", StringComparison.Ordinal)
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal)
        && value.IndexOfAny(['/', '\\']) < 0
        && !value.Any(char.IsControl);
}
