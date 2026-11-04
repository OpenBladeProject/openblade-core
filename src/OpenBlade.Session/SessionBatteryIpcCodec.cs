using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionBatteryIpcCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static Task WriteRequestAsync(
        Stream stream,
        SessionBatteryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        return WriteFrameAsync(stream, request, cancellationToken);
    }

    public static async Task<SessionBatteryRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionBatteryRequest request = await ReadFrameAsync<SessionBatteryRequest>(
            stream,
            cancellationToken).ConfigureAwait(false);
        ValidateRequest(request);
        return request;
    }

    public static Task WriteResponseAsync(
        Stream stream,
        SessionBatteryResponse response,
        CancellationToken cancellationToken = default)
    {
        ValidateResponse(response);
        return WriteFrameAsync(stream, response, cancellationToken);
    }

    public static async Task<SessionBatteryResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        SessionBatteryResponse response = await ReadFrameAsync<SessionBatteryResponse>(
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
            throw new InvalidDataException("Session battery IPC body is invalid.", exception);
        }
        if (payload.Length is <= 0 or > SessionBatteryIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session battery IPC message exceeds its limit.");
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
        if (length is <= 0 or > SessionBatteryIpc.MaximumMessageBytes)
        {
            throw new InvalidDataException("Session battery IPC message length is invalid.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateNoDuplicateObjectMembers(payload);
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new InvalidDataException("Session battery IPC body is missing.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or NotSupportedException or ArgumentException)
        {
            throw new InvalidDataException("Session battery IPC body is invalid.", exception);
        }
    }

    private static void ValidateRequest(SessionBatteryRequest? request)
    {
        if (request is null)
        {
            throw new InvalidDataException("Session battery IPC request is missing.");
        }
        ValidateEnvelope(request.Version, request.RequestId);
    }

    private static void ValidateResponse(SessionBatteryResponse? response)
    {
        if (response is null)
        {
            throw new InvalidDataException("Session battery IPC response is missing.");
        }
        ValidateEnvelope(response.Version, response.RequestId);

        bool valid = Enum.IsDefined(response.Status)
            && (response.Status == SessionBatteryOperationStatus.Succeeded
                ? response.Error is null && ValidReport(response.Report)
                : response.Report is null && ValidError(response.Error));
        if (!valid)
        {
            throw new InvalidDataException("Session battery IPC response body is invalid.");
        }
    }

    private static void ValidateEnvelope(int version, Guid requestId)
    {
        if (version != SessionBatteryIpc.ProtocolVersion)
        {
            throw new InvalidDataException("Session battery IPC protocol version is unsupported.");
        }
        if (requestId == Guid.Empty)
        {
            throw new InvalidDataException("Session battery IPC request ID is missing.");
        }
    }

    private static bool ValidReport(WindowsBatteryReport? report)
    {
        if (report is null
            || !Enum.IsDefined(report.Source)
            || report.GeneratedAtUtc.Offset != TimeSpan.Zero
            || report.WindowStartUtc.Offset != TimeSpan.Zero
            || report.WindowEndUtc.Offset != TimeSpan.Zero
            || report.WindowStartUtc > report.GeneratedAtUtc
            || report.GeneratedAtUtc > report.WindowEndUtc
            || report.WindowEndUtc - report.WindowStartUtc > TimeSpan.FromHours(24)
            || !ValidStatus(report.Status)
            || report.Intervals is null
            || report.Intervals.Count > WindowsBatteryReport.MaximumIntervals)
        {
            return false;
        }

        DateTimeOffset? previousEnd = null;
        foreach (WindowsBatteryReportInterval? interval in report.Intervals)
        {
            if (interval is null
                || !Enum.IsDefined(interval.PowerSource)
                || !Enum.IsDefined(interval.Activity)
                || interval.StartUtc.Offset != TimeSpan.Zero
                || interval.EndUtc.Offset != TimeSpan.Zero
                || interval.StartUtc < report.WindowStartUtc
                || interval.EndUtc > report.WindowEndUtc
                || interval.StartUtc >= interval.EndUtc
                || previousEnd > interval.StartUtc
                || !ValidCapacity(interval.StartChargeMilliwattHours)
                || !ValidCapacity(interval.FullChargeCapacityMilliwattHours)
                || !ValidSignedCapacity(interval.DischargeMilliwattHours)
                || !ValidPercent(interval.StartChargePercent))
            {
                return false;
            }
            previousEnd = interval.EndUtc;
        }
        return true;
    }

    private static bool ValidStatus(WindowsBatteryStatus? status) =>
        status is not null
        && ValidCapacity(status.DesignCapacityMilliwattHours)
        && ValidCapacity(status.FullChargeCapacityMilliwattHours)
        && ValidPercent(status.HealthPercent)
        && status.CycleCount is null or >= 0 and <= 10_000_000;

    private static bool ValidCapacity(long? value) =>
        value is null or >= 0 and <= 1_000_000_000;

    private static bool ValidSignedCapacity(long? value) =>
        value is null or >= -1_000_000_000 and <= 1_000_000_000;

    private static bool ValidPercent(double? value) =>
        value is null || (double.IsFinite(value.Value) && value.Value is >= 0 and <= 1000);

    private static bool ValidError(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && error.Length <= SessionBatteryIpc.MaximumErrorCharacters
        && string.Equals(error, error.Trim(), StringComparison.Ordinal)
        && !error.Any(char.IsControl);

    private static void ValidateNoDuplicateObjectMembers(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        ValidateNoDuplicateObjectMembers(document.RootElement);
    }

    private static void ValidateNoDuplicateObjectMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Session battery IPC contains a duplicate member.");
                }
                ValidateNoDuplicateObjectMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateNoDuplicateObjectMembers(item);
            }
        }
    }
}
