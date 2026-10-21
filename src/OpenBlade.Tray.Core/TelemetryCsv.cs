using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenBlade.Tray;

public sealed record TelemetryCsvLimits(
    long MaximumBytes,
    int MaximumSamples,
    int MaximumEvents,
    int MaximumFieldCharacters)
{
    public static TelemetryCsvLimits Default { get; } = new(
        32L * 1024 * 1024,
        86_400,
        8_192,
        4_096);

    internal void Validate()
    {
        if (MaximumBytes <= 0 || MaximumSamples <= 0 || MaximumEvents < 0
            || MaximumFieldCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(TelemetryCsvLimits));
        }
    }
}

public static class TelemetryCsv
{
    public const int SchemaVersion = 1;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Header =
    [
        "recordType", "timestampUtc", "serviceRevision", "source", "powerSource",
        "profileIdentity", "batteryPowerFlowMilliwatts", "fan1Rpm", "fan2Rpm",
        "cpuTemperatureCelsius", "gpuTemperatureCelsius", "eventKind",
        "previousValue", "currentValue",
    ];

    public static async Task WriteAsync(
        string path,
        TelemetrySessionDocument document,
        TelemetryCsvLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null)
        {
            throw new ArgumentException("A telemetry destination directory is required.", nameof(path));
        }
        Directory.CreateDirectory(directory);
        await using FileStream stream = new(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using TelemetryCsvWriter writer = await CreateWriterAsync(
            stream,
            document.Metadata with { EndedAtUtc = null },
            limits ?? TelemetryCsvLimits.Default,
            leaveOpen: true,
            cancellationToken).ConfigureAwait(false);
        foreach (TelemetrySample sample in document.Samples)
        {
            await writer.WriteSampleAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        foreach (TelemetryEvent marker in document.Events)
        {
            await writer.WriteEventAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        DateTimeOffset ended = document.Metadata.EndedAtUtc
            ?? document.Samples.LastOrDefault()?.TimestampUtc
            ?? document.Metadata.StartedAtUtc;
        await writer.CompleteAsync(ended, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TelemetrySessionDocument> ReadAsync(
        string path,
        TelemetryCsvLimits? limits = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        TelemetryCsvLimits effectiveLimits = limits ?? TelemetryCsvLimits.Default;
        effectiveLimits.Validate();
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length is <= 0 || info.Length > effectiveLimits.MaximumBytes)
        {
            throw new InvalidDataException("The telemetry CSV size is invalid.");
        }

        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        int rowNumber = 0;
        TelemetrySessionMetadata? metadata = null;
        var samples = new List<TelemetrySample>();
        var events = new List<TelemetryEvent>();
        DateTimeOffset? endedAt = null;
        bool sawEnd = false;

        await foreach (string[] row in ReadRowsAsync(
            stream,
            effectiveLimits.MaximumFieldCharacters,
            cancellationToken).ConfigureAwait(false))
        {
            rowNumber++;
            if (rowNumber == 1)
            {
                RequireRow(row, "OpenBladeTelemetry", SchemaVersion.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            if (rowNumber == 2)
            {
                if (row.Length != 4 || row[0] != "Metadata"
                    || !Guid.TryParseExact(row[1], "D", out Guid sessionId)
                    || sessionId == Guid.Empty
                    || !TryParseUtc(row[2], out DateTimeOffset startedAt)
                    || !Enum.TryParse(row[3], ignoreCase: false, out TelemetrySource recordedSource)
                    || recordedSource != TelemetrySource.LiveService)
                {
                    throw InvalidRow(rowNumber);
                }
                metadata = new TelemetrySessionMetadata(
                    sessionId,
                    startedAt,
                    null,
                    TelemetrySource.LocalCsvReplay)
                {
                    RecordedSource = recordedSource,
                    ImportedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow(),
                    OriginPath = fullPath,
                };
                continue;
            }
            if (rowNumber == 3)
            {
                if (!row.SequenceEqual(Header, StringComparer.Ordinal))
                {
                    throw new InvalidDataException("The telemetry CSV header is unsupported.");
                }
                continue;
            }
            if (sawEnd)
            {
                throw new InvalidDataException("The telemetry CSV contains records after its end marker.");
            }
            if (row.Length == 2 && row[0] == "End")
            {
                if (!TryParseUtc(row[1], out DateTimeOffset parsedEnd))
                {
                    throw InvalidRow(rowNumber);
                }
                endedAt = parsedEnd;
                sawEnd = true;
                continue;
            }
            if (row.Length != Header.Length)
            {
                throw InvalidRow(rowNumber);
            }
            if (row[0] == "Sample")
            {
                if (samples.Count >= effectiveLimits.MaximumSamples)
                {
                    throw new InvalidDataException("The telemetry CSV sample limit was exceeded.");
                }
                TelemetrySample sample = ParseSample(row, rowNumber);
                if (samples.Count > 0 && sample.TimestampUtc <= samples[^1].TimestampUtc)
                {
                    throw new InvalidDataException("Telemetry CSV sample timestamps must increase.");
                }
                samples.Add(sample);
            }
            else if (row[0] == "Event")
            {
                if (events.Count >= effectiveLimits.MaximumEvents)
                {
                    throw new InvalidDataException("The telemetry CSV event limit was exceeded.");
                }
                events.Add(ParseEvent(row, rowNumber));
            }
            else
            {
                throw InvalidRow(rowNumber);
            }
        }

        if (rowNumber < 4 || metadata is null || !sawEnd || endedAt is null)
        {
            throw new InvalidDataException("The telemetry CSV is incomplete.");
        }
        TelemetrySessionMetadata completed = metadata with { EndedAtUtc = endedAt };
        TelemetryValidation.ValidateMetadata(completed);
        if (samples.Any(sample => sample.TimestampUtc < completed.StartedAtUtc
            || sample.TimestampUtc > completed.EndedAtUtc))
        {
            throw new InvalidDataException("A telemetry sample falls outside its session bounds.");
        }
        if (events.Any(value => value.TimestampUtc < completed.StartedAtUtc
            || value.TimestampUtc > completed.EndedAtUtc))
        {
            throw new InvalidDataException("A telemetry event falls outside its session bounds.");
        }
        return new TelemetrySessionDocument(completed, samples, events);
    }

    internal static async Task<TelemetryCsvWriter> CreateWriterAsync(
        Stream stream,
        TelemetrySessionMetadata metadata,
        TelemetryCsvLimits limits,
        bool leaveOpen,
        CancellationToken cancellationToken)
    {
        TelemetryValidation.ValidateMetadata(metadata);
        limits.Validate();
        var writer = new TelemetryCsvWriter(stream, metadata, limits, leaveOpen);
        try
        {
            await writer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return writer;
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static string FormatRow(params string?[] fields) => string.Join(
        ',',
        fields.Select(Escape)) + "\r\n";

    private static TelemetrySample ParseSample(string[] row, int rowNumber)
    {
        if (!TryParseUtc(row[1], out DateTimeOffset timestamp)
            || !long.TryParse(row[2], NumberStyles.None, CultureInfo.InvariantCulture, out long revision)
            || !Enum.TryParse(row[3], ignoreCase: false, out TelemetrySource recordedSource)
            || recordedSource != TelemetrySource.LiveService
            || !Enum.TryParse(row[4], ignoreCase: false, out OpenBlade.Contracts.PowerSource powerSource)
            || !TryParseOptionalInt(row[6], out int? battery)
            || !TryParseOptionalInt(row[7], out int? fan1)
            || !TryParseOptionalInt(row[8], out int? fan2)
            || !TryParseOptionalInt(row[9], out int? cpu)
            || !TryParseOptionalInt(row[10], out int? gpu)
            || row[11].Length != 0 || row[12].Length != 0 || row[13].Length != 0)
        {
            throw InvalidRow(rowNumber);
        }
        var sample = new TelemetrySample(
            timestamp,
            revision,
            TelemetrySource.LocalCsvReplay,
            powerSource,
            row[5],
            battery,
            fan1,
            fan2,
            cpu,
            gpu)
        {
            RecordedSource = recordedSource,
        };
        TelemetryValidation.ValidateSample(sample);
        return sample;
    }

    private static TelemetryEvent ParseEvent(string[] row, int rowNumber)
    {
        if (!TryParseUtc(row[1], out DateTimeOffset timestamp)
            || row.Skip(2).Take(9).Any(value => value.Length != 0)
            || !Enum.TryParse(row[11], ignoreCase: false, out TelemetryEventKind kind))
        {
            throw InvalidRow(rowNumber);
        }
        var marker = new TelemetryEvent(timestamp, kind, row[12], row[13]);
        TelemetryValidation.ValidateEvent(marker);
        return marker;
    }

    private static async IAsyncEnumerable<string[]> ReadRowsAsync(
        Stream stream,
        int maximumFieldCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Utf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16_384,
            leaveOpen: true);
        char[] buffer = new char[4_096];
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool afterQuote = false;
        bool pendingCarriageReturn = false;
        bool rowHasContent = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            for (int index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                char value = buffer[index];
                if (pendingCarriageReturn)
                {
                    if (value != '\n')
                    {
                        throw new InvalidDataException("Telemetry CSV contains a bare carriage return.");
                    }
                    pendingCarriageReturn = false;
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    rowHasContent = false;
                    afterQuote = false;
                    continue;
                }
                if (quoted)
                {
                    if (value == '"')
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                    else
                    {
                        field.Append(value);
                    }
                }
                else if (afterQuote)
                {
                    if (value == '"')
                    {
                        field.Append('"');
                        quoted = true;
                        afterQuote = false;
                    }
                    else if (value == ',')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                        afterQuote = false;
                        rowHasContent = true;
                    }
                    else if (value == '\r')
                    {
                        pendingCarriageReturn = true;
                    }
                    else if (value == '\n')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                        yield return row.ToArray();
                        row.Clear();
                        rowHasContent = false;
                        afterQuote = false;
                    }
                    else
                    {
                        throw new InvalidDataException("Telemetry CSV has characters after a closing quote.");
                    }
                }
                else if (value == '"')
                {
                    if (field.Length != 0)
                    {
                        throw new InvalidDataException("Telemetry CSV has a quote inside an unquoted field.");
                    }
                    quoted = true;
                    rowHasContent = true;
                }
                else if (value == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    rowHasContent = true;
                }
                else if (value == '\r')
                {
                    pendingCarriageReturn = true;
                }
                else if (value == '\n')
                {
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    rowHasContent = false;
                }
                else
                {
                    field.Append(value);
                    rowHasContent = true;
                }
                if (field.Length > maximumFieldCharacters)
                {
                    throw new InvalidDataException("A telemetry CSV field exceeds its limit.");
                }
            }
        }
        if (quoted || pendingCarriageReturn)
        {
            throw new InvalidDataException("Telemetry CSV ends inside a quoted field or line ending.");
        }
        if (rowHasContent || row.Count > 0 || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static void RequireRow(string[] row, params string[] expected)
    {
        if (!row.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The telemetry CSV schema is unsupported.");
        }
    }

    private static bool TryParseUtc(string value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out parsed)
        && parsed.Offset == TimeSpan.Zero;

    private static bool TryParseOptionalInt(string value, out int? parsed)
    {
        if (value.Length == 0)
        {
            parsed = null;
            return true;
        }
        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
        {
            parsed = number;
            return true;
        }
        parsed = null;
        return false;
    }

    private static InvalidDataException InvalidRow(int rowNumber) =>
        new($"Telemetry CSV row {rowNumber} is invalid.");

    internal static UTF8Encoding Encoding => Utf8;

    internal static IReadOnlyList<string> ColumnHeader => Header;
}

internal sealed class TelemetryCsvWriter : IAsyncDisposable
{
    private static readonly int ReservedEndBytes = TelemetryCsv.Encoding.GetByteCount(
        TelemetryCsv.FormatRow(
            "End",
            DateTimeOffset.MaxValue.ToString("O", CultureInfo.InvariantCulture)));

    private readonly Stream stream;
    private readonly TelemetrySessionMetadata metadata;
    private readonly TelemetryCsvLimits limits;
    private readonly bool leaveOpen;
    private long bytesWritten;
    private int sampleCount;
    private int eventCount;
    private DateTimeOffset? lastSampleAt;
    private bool initialized;
    private bool completed;

    internal TelemetryCsvWriter(
        Stream stream,
        TelemetrySessionMetadata metadata,
        TelemetryCsvLimits limits,
        bool leaveOpen)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The telemetry CSV stream must be writable.", nameof(stream));
        }
        this.metadata = metadata;
        this.limits = limits;
        this.leaveOpen = leaveOpen;
    }

    internal async Task InitializeAsync(CancellationToken token)
    {
        if (initialized)
        {
            throw new InvalidOperationException("The telemetry CSV writer is already initialized.");
        }
        initialized = true;
        await WriteRowAsync(TelemetryCsv.FormatRow(
            "OpenBladeTelemetry",
            TelemetryCsv.SchemaVersion.ToString(CultureInfo.InvariantCulture)), token).ConfigureAwait(false);
        await WriteRowAsync(TelemetryCsv.FormatRow(
            "Metadata",
            metadata.SessionId.ToString("D"),
            metadata.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            TelemetrySource.LiveService.ToString()), token).ConfigureAwait(false);
        await WriteRowAsync(TelemetryCsv.FormatRow(TelemetryCsv.ColumnHeader.ToArray()), token)
            .ConfigureAwait(false);
    }

    internal async Task WriteSampleAsync(TelemetrySample sample, CancellationToken token)
    {
        RequireWritable();
        TelemetryValidation.ValidateSample(sample);
        if (sample.Source != TelemetrySource.LiveService
            || sample.TimestampUtc < metadata.StartedAtUtc
            || lastSampleAt is { } previous && sample.TimestampUtc <= previous)
        {
            throw new InvalidDataException("The live telemetry sample is outside the recording sequence.");
        }
        if (sampleCount >= limits.MaximumSamples)
        {
            throw new TelemetryCapacityReachedException(
                "The telemetry recording sample limit was reached.");
        }
        await WriteRowAsync(TelemetryCsv.FormatRow(
            "Sample",
            sample.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            sample.ServiceRevision.ToString(CultureInfo.InvariantCulture),
            sample.Source.ToString(),
            sample.PowerSource.ToString(),
            sample.ProfileIdentity,
            Format(sample.BatteryPowerFlowMilliwatts),
            Format(sample.Fan1Rpm),
            Format(sample.Fan2Rpm),
            Format(sample.CpuTemperatureCelsius),
            Format(sample.GpuTemperatureCelsius),
            null,
            null,
            null), token).ConfigureAwait(false);
        lastSampleAt = sample.TimestampUtc;
        sampleCount++;
    }

    internal async Task WriteEventAsync(TelemetryEvent marker, CancellationToken token)
    {
        RequireWritable();
        TelemetryValidation.ValidateEvent(marker);
        if (marker.TimestampUtc < metadata.StartedAtUtc)
        {
            throw new InvalidDataException("The telemetry event precedes the recording.");
        }
        if (eventCount >= limits.MaximumEvents)
        {
            throw new TelemetryCapacityReachedException(
                "The telemetry recording event limit was reached.");
        }
        await WriteRowAsync(TelemetryCsv.FormatRow(
            "Event",
            marker.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            null, null, null, null, null, null, null, null, null,
            marker.Kind.ToString(),
            marker.PreviousValue,
            marker.CurrentValue), token).ConfigureAwait(false);
        eventCount++;
    }

    internal async Task CompleteAsync(DateTimeOffset endedAtUtc, CancellationToken token)
    {
        RequireWritable();
        TelemetryValidation.ValidateUtc(endedAtUtc, nameof(endedAtUtc));
        if (endedAtUtc < metadata.StartedAtUtc || lastSampleAt > endedAtUtc)
        {
            throw new InvalidDataException("The telemetry recording end time is invalid.");
        }
        await WriteRowAsync(TelemetryCsv.FormatRow(
            "End",
            endedAtUtc.ToString("O", CultureInfo.InvariantCulture)), token, reserveEndSpace: false)
            .ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        if (stream is FileStream file)
        {
            file.Flush(flushToDisk: true);
        }
        completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteRowAsync(
        string row,
        CancellationToken token,
        bool reserveEndSpace = true)
    {
        byte[] bytes = TelemetryCsv.Encoding.GetBytes(row);
        long requiredBytes = bytes.Length + (reserveEndSpace ? ReservedEndBytes : 0L);
        if (requiredBytes > limits.MaximumBytes
            || bytesWritten > limits.MaximumBytes - requiredBytes)
        {
            throw new TelemetryCapacityReachedException(
                "The telemetry recording byte limit was reached.");
        }
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        bytesWritten += bytes.Length;
    }

    private void RequireWritable()
    {
        if (!initialized || completed)
        {
            throw new InvalidOperationException("The telemetry CSV writer is not writable.");
        }
    }

    private static string? Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture);
}

internal sealed class TelemetryCapacityReachedException(string message)
    : IOException(message);
