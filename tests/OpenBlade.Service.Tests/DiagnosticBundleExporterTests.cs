using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesStructuredEntriesAndRedactsSensitiveTokensEverywhere()
    {
        const string secret = "device-secret-123";
        DiagnosticBundle bundle = new(
            new DiagnosticVersions("1.2.3", "Windows 11", $"3.01-{secret}"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", secret),
            new Dictionary<string, bool> { [$"fan-{secret}"] = true },
            new Dictionary<string, string> { ["profile"] = $"Balanced {secret}" },
            [new DiagnosticCommandResult(
                DateTimeOffset.Parse("2026-07-13T12:00:00Z"),
                "Performance",
                "SetBalanced",
                "Succeeded",
                secret)],
            [$"Service started with token {secret}"],
            [secret]);

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(destination, bundle, CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(
            ["versions.json", "device.json", "capabilities.json", "configuration.json", "command-results.json", "logs.jsonl"],
            archive.Entries.Select(entry => entry.FullName));

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            string text = await reader.ReadToEndAsync(CancellationToken.None);
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }

        string deviceJson = await ReadEntryAsync(archive, "device.json");
        Assert.Contains("[REDACTED]", deviceJson, StringComparison.Ordinal);
        using JsonDocument device = JsonDocument.Parse(deviceJson);
        Assert.Equal("RZ09-0581", device.RootElement.GetProperty("model").GetString());

        string versionsJson = await ReadEntryAsync(archive, "versions.json");
        using JsonDocument versions = JsonDocument.Parse(versionsJson);
        Assert.Equal("1.2.3", versions.RootElement.GetProperty("openBladeVersion").GetString());
        string formerVersionProperty = "open" + "SynapseVersion";
        Assert.False(versions.RootElement.TryGetProperty(formerVersionProperty, out _));

        string commandResultsJson = await ReadEntryAsync(archive, "command-results.json");
        using JsonDocument results = JsonDocument.Parse(commandResultsJson);
        Assert.Equal("Succeeded", results.RootElement[0].GetProperty("status").GetString());

        string logs = await ReadEntryAsync(archive, "logs.jsonl");
        using JsonDocument log = JsonDocument.Parse(logs.Trim());
        Assert.Equal("Service started with token [REDACTED]", log.RootElement.GetProperty("line").GetString());
    }

    [Fact]
    public async Task ExportAsync_WritesOneJsonObjectPerLogLine()
    {
        DiagnosticBundle bundle = new(
            new DiagnosticVersions("1.2.3", "Windows 11", "3.01"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", null),
            new Dictionary<string, bool>(),
            new Dictionary<string, string>(),
            [],
            ["first", "second"],
            []);

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(destination, bundle, CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        string[] records = (await ReadEntryAsync(archive, "logs.jsonl"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(2, records.Length);
        Assert.All(records, record => JsonDocument.Parse(record).Dispose());
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, candidate => candidate.FullName == name);
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }
}
