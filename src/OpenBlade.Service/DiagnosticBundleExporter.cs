using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed record DiagnosticVersions(
    string OpenBladeVersion,
    string WindowsVersion,
    string BiosVersion,
    string EcVersion = "Unavailable",
    string McuVersion = "Unavailable",
    string VbiosVersion = "Unavailable",
    string VbiosGpuName = "Unavailable",
    string ThunderboltFirmwareVersion = "Unavailable",
    string TouchpadFirmwareVersion = "Unavailable",
    string? ThunderboltFirmwareAccess = null,
    string? TouchpadFirmwareAccess = null);

public sealed record DiagnosticDeviceIdentity(
    string Model,
    string VendorId,
    string ProductId,
    string? SerialNumber);

public sealed record DiagnosticCommandResult(
    DateTimeOffset Timestamp,
    string Subsystem,
    string Action,
    string Status,
    string? Detail)
{
    public Guid? TransitionId { get; init; }
}

public sealed record DiagnosticHyperBoostRestoreIntent(
    PerformancePreset Performance,
    CustomPerformanceSettings? CustomPerformance,
    GpuClockTuningSettings? GpuClockTuning,
    FanControlSettings FanControl);

public sealed record DiagnosticHyperBoostOwnershipJournal(
    bool Exists,
    bool Valid,
    string? State,
    PerformancePreset? BaseMode,
    bool? RequiresCoolingPad,
    DiagnosticHyperBoostRestoreIntent? RestoreIntent,
    string? Sha256)
{
    public string? RestoreRetryDisposition { get; init; }

    public string? RestoreRetryTarget { get; init; }

    public Guid? TransitionId { get; init; }

    public static DiagnosticHyperBoostOwnershipJournal Missing { get; } = new(
        Exists: false,
        Valid: true,
        State: null,
        BaseMode: null,
        RequiresCoolingPad: null,
        RestoreIntent: null,
        Sha256: null);
}

public sealed record DiagnosticBundle(
    DiagnosticVersions Versions,
    DiagnosticDeviceIdentity Device,
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyList<DiagnosticCommandResult> CommandResults,
    IReadOnlyList<string> LogLines,
    IReadOnlyCollection<string> SensitiveTokens,
    DiagnosticHyperBoostOwnershipJournal? HyperBoostOwnership = null);

public static class DiagnosticBundleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task ExportAsync(
        Stream destination,
        DiagnosticBundle bundle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(bundle);

        using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true);
        await WriteJsonEntryAsync(archive, "versions.json", bundle.Versions, bundle.SensitiveTokens, cancellationToken);
        await WriteJsonEntryAsync(archive, "device.json", bundle.Device, bundle.SensitiveTokens, cancellationToken);
        await WriteJsonEntryAsync(archive, "capabilities.json", bundle.Capabilities, bundle.SensitiveTokens, cancellationToken);
        await WriteJsonEntryAsync(archive, "configuration.json", bundle.Configuration, bundle.SensitiveTokens, cancellationToken);
        await WriteCommandResultsEntryAsync(
            archive,
            bundle.CommandResults,
            bundle.SensitiveTokens,
            cancellationToken);
        if (bundle.HyperBoostOwnership is { } ownership)
        {
            // This narrow typed record contains no user or device identifiers.
            // Free-text substring redaction would corrupt its property names or
            // journal hash and make the lifecycle evidence unverifiable.
            await WriteJsonEntryAsync(
                archive,
                "hyperboost-ownership.json",
                ownership,
                sensitiveTokens: [],
                cancellationToken);
        }
        await WriteLogEntryAsync(archive, bundle.LogLines, bundle.SensitiveTokens, cancellationToken);
    }

    private static Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        IReadOnlyCollection<string> sensitiveTokens,
        CancellationToken cancellationToken)
    {
        string json = SerializeRedactedJson(
            value,
            sensitiveTokens,
            writeIndented: true,
            cancellationToken);
        return WriteTextEntryAsync(archive, name, json, cancellationToken);
    }

    private static Task WriteCommandResultsEntryAsync(
        ZipArchive archive,
        IReadOnlyList<DiagnosticCommandResult> results,
        IReadOnlyCollection<string> sensitiveTokens,
        CancellationToken cancellationToken)
    {
        (string[] tokens, string replacement) =
            CreateRedactionPlan(sensitiveTokens);
        DiagnosticCommandResult[] redacted = results
            .Select(result => result with
            {
                Subsystem = RedactString(result.Subsystem, tokens, replacement),
                Action = RedactString(result.Action, tokens, replacement),
                Status = RedactString(result.Status, tokens, replacement),
                Detail = result.Detail is null
                    ? null
                    : RedactString(result.Detail, tokens, replacement),
            })
            .ToArray();

        // Timestamp and transition ID are typed, non-identifying evidence.
        // Redacting their serialized strings or fixed property names would make
        // the lifecycle record unreadable or break causal verification.
        return WriteJsonEntryAsync(
            archive,
            "command-results.json",
            redacted,
            sensitiveTokens: [],
            cancellationToken);
    }

    private static async Task WriteLogEntryAsync(
        ZipArchive archive,
        IReadOnlyList<string> lines,
        IReadOnlyCollection<string> sensitiveTokens,
        CancellationToken cancellationToken)
    {
        StringBuilder content = new();
        foreach (string line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            content.AppendLine(SerializeRedactedJson(
                new { line },
                sensitiveTokens,
                writeIndented: false,
                cancellationToken));
        }

        await WriteTextEntryAsync(
            archive,
            "logs.jsonl",
            content.ToString(),
            cancellationToken);
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await using StreamWriter writer = new(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    private static string SerializeRedactedJson<T>(
        T value,
        IReadOnlyCollection<string> sensitiveTokens,
        bool writeIndented,
        CancellationToken cancellationToken)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(serialized);
        (string[] tokens, string replacement) =
            CreateRedactionPlan(sensitiveTokens);

        using var destination = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            destination,
            new JsonWriterOptions { Indented = writeIndented }))
        {
            WriteRedactedElement(
                writer,
                document.RootElement,
                tokens,
                replacement,
                cancellationToken);
        }

        return Encoding.UTF8.GetString(destination.ToArray());
    }

    private static (string[] Tokens, string Replacement) CreateRedactionPlan(
        IReadOnlyCollection<string> sensitiveTokens)
    {
        string[] tokens = sensitiveTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .OrderByDescending(token => token.Length)
            .ToArray();
        const string standardReplacement = "[REDACTED]";
        string replacement = tokens.Any(token => standardReplacement.Contains(
            token,
            StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : standardReplacement;
        return (tokens, replacement);
    }

    private static void WriteRedactedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        IReadOnlyList<string> sensitiveTokens,
        string replacement,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(RedactString(
                        property.Name,
                        sensitiveTokens,
                        replacement));
                    WriteRedactedElement(
                        writer,
                        property.Value,
                        sensitiveTokens,
                        replacement,
                        cancellationToken);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteRedactedElement(
                        writer,
                        item,
                        sensitiveTokens,
                        replacement,
                        cancellationToken);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactString(
                    element.GetString() ?? string.Empty,
                    sensitiveTokens,
                    replacement));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string RedactString(
        string text,
        IReadOnlyList<string> sensitiveTokens,
        string replacement)
    {
        foreach (string token in sensitiveTokens)
        {
            text = text.Replace(
                token,
                replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }
}
