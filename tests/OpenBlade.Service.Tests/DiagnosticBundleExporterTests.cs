using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class DiagnosticBundleExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesStructuredEntriesAndRedactsSensitiveTokensEverywhere()
    {
        const string secret = "device-secret-123";
        DiagnosticBundle bundle = new(
            new DiagnosticVersions(
                "1.2.3",
                "Windows 11",
                $"3.01-{secret}",
                "1.04",
                "1.5.0.0",
                "98.3.5c.40.3d",
                "NVIDIA GeForce RTX 5090 Laptop GPU",
                "Unavailable",
                "Unavailable",
                "ObservedOnly",
                "ObservedOnly"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", secret),
            new Dictionary<string, bool> { [$"fan-{secret}"] = true },
            new Dictionary<string, string> { ["profile"] = $"Balanced {secret}" },
            [new DiagnosticCommandResult(
                DateTimeOffset.Parse("2026-07-13T12:00:00Z"),
                "Performance",
                "SetBalanced",
                "Succeeded",
                secret)],
            [$"Service started with token {secret.ToUpperInvariant()}"],
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
        Assert.Equal("1.04", versions.RootElement.GetProperty("ecVersion").GetString());
        Assert.Equal("1.5.0.0", versions.RootElement.GetProperty("mcuVersion").GetString());
        Assert.Equal("98.3.5c.40.3d", versions.RootElement.GetProperty("vbiosVersion").GetString());
        Assert.Equal(
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            versions.RootElement.GetProperty("vbiosGpuName").GetString());
        Assert.Equal(
            "ObservedOnly",
            versions.RootElement.GetProperty("thunderboltFirmwareAccess").GetString());
        Assert.Equal(
            "ObservedOnly",
            versions.RootElement.GetProperty("touchpadFirmwareAccess").GetString());
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

    [Fact]
    public async Task ExportAsync_PreservesTypedJournalEvidenceAcrossTokenCollisions()
    {
        DiagnosticBundle bundle = new(
            new DiagnosticVersions("1.2.3", "Windows 11", "3.01"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", null),
            new Dictionary<string, bool>(),
            new Dictionary<string, string>(),
            [],
            [],
            ["fan", "A"],
            new DiagnosticHyperBoostOwnershipJournal(
                Exists: true,
                Valid: true,
                State: "OwnedConfirmed",
                BaseMode: PerformancePreset.Balanced,
                RequiresCoolingPad: false,
                RestoreIntent: new DiagnosticHyperBoostRestoreIntent(
                    PerformancePreset.Silent,
                    CustomPerformance: null,
                    GpuClockTuning: null,
                    FanControlSettings.Default),
                Sha256: new string('A', 64)));

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(
            destination,
            bundle,
            CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        using JsonDocument ownership = JsonDocument.Parse(
            await ReadEntryAsync(archive, "hyperboost-ownership.json"));
        Assert.Equal(
            "OwnedConfirmed",
            ownership.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            new string('A', 64),
            ownership.RootElement.GetProperty("sha256").GetString());
        Assert.True(
            ownership.RootElement.GetProperty("restoreIntent")
                .TryGetProperty("fanControl", out _));
    }

    [Fact]
    public async Task ExportAsync_PreservesTypedCommandEvidenceAcrossTokenCollisions()
    {
        Guid transitionId = Guid.Parse("a18b65e1-99ae-4d7a-a859-13d87be64aac");
        DiagnosticBundle bundle = new(
            new DiagnosticVersions("1.2.3", "Windows 11", "3.01"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", null),
            new Dictionary<string, bool>(),
            new Dictionary<string, string>(),
            [new DiagnosticCommandResult(
                DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
                "Fans",
                "Maintain",
                "Applied",
                "manual fan restored")
            {
                TransitionId = transitionId,
            }],
            [],
            ["a"]);

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(
            destination,
            bundle,
            CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        string json = await ReadEntryAsync(archive, "command-results.json");
        DiagnosticCommandResult result = Assert.Single(
            JsonSerializer.Deserialize<DiagnosticCommandResult[]>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                })!);
        Assert.Equal(transitionId, result.TransitionId);
        Assert.DoesNotContain("a", result.Subsystem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a", result.Action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a", result.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("xy")]
    public async Task ExportAsync_RedactsOneOrTwoCharacterSensitiveToken(string token)
    {
        DiagnosticBundle bundle = new(
            new DiagnosticVersions("1.2.3", "Windows 11", "3.01"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", null),
            new Dictionary<string, bool>(),
            new Dictionary<string, string>(),
            [],
            [$"User {token}."],
            [token]);

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(destination, bundle, CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        string logs = await ReadEntryAsync(archive, "logs.jsonl");
        Assert.DoesNotContain(token, logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", logs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("t")]
    [InlineData("tr")]
    [InlineData("n")]
    [InlineData("nu")]
    public async Task ExportAsync_SyntaxCollidingShortTokenPreservesValidJson(
        string token)
    {
        DiagnosticBundle bundle = new(
            new DiagnosticVersions($"version-{token}", "Windows 11", "3.01"),
            new DiagnosticDeviceIdentity("RZ09-0581", "1532", "02E0", null),
            new Dictionary<string, bool> { [$"feature-{token}"] = true },
            new Dictionary<string, string> { [$"setting-{token}"] = $"value-{token}" },
            [new DiagnosticCommandResult(
                DateTimeOffset.Parse("2026-07-15T12:00:00Z"),
                $"subsystem-{token}",
                "Reconcile",
                "Blocked",
                $"detail-{token}")],
            [$"log-{token}"],
            [token]);

        await using MemoryStream destination = new();
        await DiagnosticBundleExporter.ExportAsync(destination, bundle, CancellationToken.None);

        destination.Position = 0;
        using ZipArchive archive = new(destination, ZipArchiveMode.Read, leaveOpen: true);
        foreach (string entryName in new[]
        {
            "versions.json",
            "device.json",
            "capabilities.json",
            "configuration.json",
        })
        {
            string content = await ReadEntryAsync(archive, entryName);
            using JsonDocument document = JsonDocument.Parse(content);
            AssertNoSensitiveJsonString(document.RootElement, token);
        }

        using (JsonDocument commands = JsonDocument.Parse(
            await ReadEntryAsync(archive, "command-results.json")))
        {
            JsonElement command = commands.RootElement[0];
            Assert.True(command.TryGetProperty("timestamp", out _));
            Assert.True(command.TryGetProperty("transitionId", out _));
            foreach (string propertyName in new[]
            {
                "subsystem",
                "action",
                "status",
                "detail",
            })
            {
                Assert.DoesNotContain(
                    token,
                    command.GetProperty(propertyName).GetString(),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        string[] logRecords = (await ReadEntryAsync(archive, "logs.jsonl"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(logRecords);
        foreach (string record in logRecords)
        {
            using JsonDocument document = JsonDocument.Parse(record);
            AssertNoSensitiveJsonString(document.RootElement, token);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, candidate => candidate.FullName == name);
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    private static void AssertNoSensitiveJsonString(JsonElement element, string token)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Assert.DoesNotContain(token, property.Name, StringComparison.OrdinalIgnoreCase);
                    AssertNoSensitiveJsonString(property.Value, token);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertNoSensitiveJsonString(item, token);
                }

                break;
            case JsonValueKind.String:
                Assert.DoesNotContain(
                    token,
                    element.GetString()!,
                    StringComparison.OrdinalIgnoreCase);
                break;
        }
    }
}
