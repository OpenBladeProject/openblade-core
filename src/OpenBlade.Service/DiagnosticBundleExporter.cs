using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Service;

public sealed record DiagnosticVersions(
    string OpenSynapseVersion,
    string WindowsVersion,
    string BiosVersion);

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
    string? Detail);

public sealed record DiagnosticBundle(
    DiagnosticVersions Versions,
    DiagnosticDeviceIdentity Device,
    IReadOnlyDictionary<string, bool> Capabilities,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyList<DiagnosticCommandResult> CommandResults,
    IReadOnlyList<string> LogLines,
    IReadOnlyCollection<string> SensitiveTokens);

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
        await WriteJsonEntryAsync(archive, "command-results.json", bundle.CommandResults, bundle.SensitiveTokens, cancellationToken);
        await WriteLogEntryAsync(archive, bundle.LogLines, bundle.SensitiveTokens, cancellationToken);
    }

    private static Task WriteJsonEntryAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        IReadOnlyCollection<string> sensitiveTokens,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        return WriteTextEntryAsync(archive, name, Redact(json, sensitiveTokens), cancellationToken);
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
            content.AppendLine(JsonSerializer.Serialize(new { line }));
        }

        await WriteTextEntryAsync(
            archive,
            "logs.jsonl",
            Redact(content.ToString(), sensitiveTokens),
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

    private static string Redact(string text, IReadOnlyCollection<string> sensitiveTokens)
    {
        foreach (string token in sensitiveTokens.Where(token => !string.IsNullOrEmpty(token)).OrderByDescending(token => token.Length))
        {
            text = text.Replace(token, "[REDACTED]", StringComparison.Ordinal);

            string encodedToken = JsonSerializer.Serialize(token, JsonOptions)[1..^1];
            text = text.Replace(encodedToken, "[REDACTED]", StringComparison.Ordinal);
        }

        return text;
    }
}
