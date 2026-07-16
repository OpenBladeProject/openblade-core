using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public static class OpenBladeIpc
{
    public const int ProtocolVersion = 4;
    public const int MaximumMessageBytes = 64 * 1024;
    public const int MaximumDiagnosticArchiveBytes = 32 * 1024;
    public const string PipeName = "OpenBlade.Service.v2";
}

public static class IpcMessageCodec
{
    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();

    public static Task WriteRequestAsync(
        Stream stream,
        ServiceRequest request,
        CancellationToken token)
    {
        ValidateRequest(request);
        return WriteFramedAsync<ServiceRequest>(stream, request, token);
    }

    public static async Task<ServiceRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken token)
    {
        ServiceRequest request = await ReadFramedAsync<ServiceRequest>(stream, token)
            .ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static Task WriteServerFrameAsync(
        Stream stream,
        ServiceServerFrame frame,
        CancellationToken token)
    {
        ValidateServerFrame(frame);
        return WriteFramedAsync<ServiceServerFrame>(stream, frame, token);
    }

    public static async Task<ServiceServerFrame> ReadServerFrameAsync(
        Stream stream,
        CancellationToken token)
    {
        ServiceServerFrame frame = await ReadFramedAsync<ServiceServerFrame>(stream, token)
            .ConfigureAwait(false);
        ValidateServerFrame(frame);
        return frame;
    }

    private static async Task WriteFramedAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw InvalidBody(exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("IPC message body is invalid.", exception);
        }

        if (payload.Length > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message exceeds the 64 KiB limit.");
        }

        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadFramedAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > OpenBladeIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("IPC message body is invalid.");
        }
        catch (JsonException exception)
        {
            throw InvalidBody(exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("IPC message body is invalid.", exception);
        }
    }

    private static void ValidateRequest(ServiceRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("IPC request is missing.");
        }

        ValidateVersion(request.Version);
        if (request.RequestId == Guid.Empty)
        {
            throw new InvalidDataException("IPC request ID is missing.");
        }

        bool validBody = request switch
        {
            GetStatusRequest => true,
            ObserveStateRequest => true,
            SubscribeStatusRequest => true,
            ExportDiagnosticsRequest => true,
            UpdateProfilesRequest update => update.Profiles is not null,
            SetPerformanceOverrideRequest set =>
                set.Value is null
                || Enum.IsDefined(set.Value.Value) && Enum.IsDefined(set.Value.Lifetime),
            SetFanOverrideRequest set =>
                set.Value is null
                || Enum.IsDefined(set.Value.Value) && Enum.IsDefined(set.Value.Lifetime),
            SetLightingOverrideRequest set =>
                set.Value is null
                || set.Value.Value is not null && Enum.IsDefined(set.Value.Lifetime),
            SetBatteryPolicyRequest set => set.Value is not null,
            SetBatteryProtectionRequest => true,
            SetFullChargeOverrideRequest => true,
            _ => false,
        };
        if (!validBody)
        {
            throw new InvalidDataException("IPC request body is invalid.");
        }
    }

    private static void ValidateServerFrame(ServiceServerFrame? frame)
    {
        if (frame is null)
        {
            throw new InvalidDataException("IPC server frame is missing.");
        }

        ValidateVersion(frame.Version);
        bool valid = frame switch
        {
            ServiceStatusEvent statusEvent => ValidateStatus(statusEvent.Status),
            ServiceResponse response => ValidateResponse(response),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("IPC server frame body is invalid.");
        }
    }

    private static bool ValidateResponse(ServiceResponse response)
    {
        if (response.RequestId == Guid.Empty)
        {
            return false;
        }

        int bodyCount = (response.Status is null ? 0 : 1)
            + (response.Result is null ? 0 : 1)
            + (response.Archive is null ? 0 : 1);
        bool valid = response.Success
            ? response.Error is null && bodyCount == 1
            : !string.IsNullOrWhiteSpace(response.Error) && bodyCount == 0;
        return valid
            && (response.Status is null || ValidateStatus(response.Status))
            && (response.Archive is null || ValidateArchive(response.Archive));
    }

    private static bool ValidateArchive(DiagnosticArchive archive)
    {
        string fileName = archive.SuggestedFileName;
        byte[] bytes = archive.ZipBytes;
        return !string.IsNullOrWhiteSpace(fileName)
            && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            && !fileName.Contains('/')
            && !fileName.Contains('\\')
            && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase)
            && bytes is { Length: >= 4 and <= OpenBladeIpc.MaximumDiagnosticArchiveBytes }
            && bytes[0] == 0x50
            && bytes[1] == 0x4b
            && bytes[2] == 0x03
            && bytes[3] == 0x04;
    }

    private static bool ValidateStatus(ServiceStatusSnapshot? status)
    {
        if (status is null
            || status.Revision < 0
            || status.PotentialControllers is null
            || status.ExternalChanges is null
            || status.Message is null
            || status.Profiles is null
            || status.DesiredState is null
            || status.Telemetry is null
            || status.Capabilities is { } capabilities
                && (capabilities.PerformancePresets is null
                    || capabilities.FanModes is null
                    || capabilities.LightingEffects is null
                    || capabilities.ChargeLimits is null
                    || !ValidChargeLimits(capabilities.ChargeLimits)))
        {
            return false;
        }

        return status.PotentialControllers.Count <= 16
            && status.PotentialControllers.Distinct(StringComparer.Ordinal).Count()
                == status.PotentialControllers.Count
            && status.PotentialControllers.SequenceEqual(
                status.PotentialControllers.OrderBy(
                    controller => controller,
                    StringComparer.Ordinal),
                StringComparer.Ordinal)
            && status.ExternalChanges.Count <= 4
            && status.ExternalChanges.All(Enum.IsDefined)
            && status.ExternalChanges.Distinct().Count() == status.ExternalChanges.Count
            && status.ExternalChanges.SequenceEqual(status.ExternalChanges.Order());
    }

    private static void ValidateVersion(int version)
    {
        if (version != OpenBladeIpc.ProtocolVersion)
        {
            throw new InvalidDataException("IPC protocol version is unsupported.");
        }
    }

    private static InvalidDataException InvalidBody(JsonException exception) =>
        new("IPC message body is invalid.", exception);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new BatteryPolicyJsonConverter());
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }

    private static bool ValidChargeLimits(IEnumerable<int> limits)
    {
        int[] values = limits.ToArray();
        return values.Length <= 7
            && values.Distinct().Count() == values.Length
            && values.All(value => value is >= 50 and <= 80 && value % 5 == 0);
    }

    private sealed class BatteryPolicyJsonConverter : JsonConverter<BatteryPolicy>
    {
        public override BatteryPolicy Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Battery policy must be an object.");
            }

            int? chargeLimitPercent = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(
                    property.Name,
                    "chargeLimitPercent",
                    StringComparison.Ordinal))
                {
                    throw new JsonException("Battery policy contains an unknown member.");
                }

                if (chargeLimitPercent.HasValue
                    || property.Value.ValueKind != JsonValueKind.Number
                    || !property.Value.TryGetInt32(out int value))
                {
                    throw new JsonException("Battery policy charge limit is invalid.");
                }

                chargeLimitPercent = value;
            }

            if (!chargeLimitPercent.HasValue)
            {
                throw new JsonException("Battery policy charge limit is missing.");
            }

            try
            {
                return BatteryPolicy.Create(chargeLimitPercent.Value);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new JsonException("Battery policy charge limit is invalid.", exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            BatteryPolicy value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("chargeLimitPercent", value.ChargeLimitPercent);
            writer.WriteEndObject();
        }
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType
            && typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            Type elementType = typeToConvert.GetGenericArguments()[0];
            if (elementType == typeof(int))
            {
                return new ChargeLimitSetJsonConverter();
            }

            return (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType))!;
        }
    }

    private sealed class ChargeLimitSetJsonConverter : JsonConverter<IReadOnlySet<int>>
    {
        public override IReadOnlySet<int> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Charge limits must be an array.");
            }

            List<int> values = [];
            int? previous = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number
                    || !reader.TryGetInt32(out int value)
                    || value is < 50 or > 80
                    || value % 5 != 0
                    || previous.HasValue && value <= previous.Value)
                {
                    throw new JsonException("Charge limits are invalid.");
                }

                values.Add(value);
                if (values.Count > 7)
                {
                    throw new JsonException("Too many charge limits were supplied.");
                }

                previous = value;
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("Charge limits array is incomplete.");
            }

            return values.ToFrozenSet();
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<int> value,
            JsonSerializerOptions options)
        {
            if (!ValidChargeLimits(value))
            {
                throw new JsonException("Charge limits are invalid.");
            }

            writer.WriteStartArray();
            foreach (int limit in value.Order())
            {
                writer.WriteNumberValue(limit);
            }
            writer.WriteEndArray();
        }
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
        where T : notnull
    {
        public override IReadOnlySet<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options)
            ?? throw new JsonException("Set value is missing.");

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
