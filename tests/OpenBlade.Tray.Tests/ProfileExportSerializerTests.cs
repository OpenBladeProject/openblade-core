using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ProfileExportSerializerTests
{
    [Fact]
    public void Serialize_WritesReadableVersionedJsonWithEveryProfile()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DateTimeOffset exportedAt = new(2026, 7, 20, 9, 30, 0, TimeSpan.Zero);

        string json = ProfileExportSerializer.Serialize(
            profiles,
            "0.1.0+abcdef0",
            exportedAt);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("0.1.0+abcdef0", root.GetProperty("applicationVersion").GetString());
        Assert.Equal(exportedAt, root.GetProperty("exportedAtUtc").GetDateTimeOffset());
        JsonElement exportedProfiles = root.GetProperty("profiles");
        Assert.Equal(
            "balanced",
            exportedProfiles.GetProperty("pluggedIn").GetProperty("performance").GetString());
        Assert.Equal(
            "batterySaver",
            exportedProfiles.GetProperty("battery").GetProperty("performance").GetString());
        Assert.Equal(
            "balanced",
            exportedProfiles.GetProperty("usbC").GetProperty("performance").GetString());
        Assert.Equal(
            80,
            exportedProfiles.GetProperty("batteryPolicy")
                .GetProperty("chargeLimitPercent")
                .GetInt32());
    }

    [Fact]
    public void Serialize_RejectsMissingVersion()
    {
        Assert.Throws<ArgumentException>(() => ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            " ",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Deserialize_RoundTripsEveryProfile()
    {
        ProfileSet expected = ProfileDefaults.CreateSafeDefaults();
        string json = ProfileExportSerializer.Serialize(
            expected,
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow);

        ProfileSet actual = ProfileExportSerializer.Deserialize(json);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedSchema()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 2",
                StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProfileExportSerializer.Deserialize(json));

        Assert.Contains("schema 2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("newer version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsObsoleteSchemaThroughVersionDispatcher()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 0",
                StringComparison.Ordinal);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProfileExportSerializer.Deserialize(json));

        Assert.Contains("schema 0", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_RejectsNumericEnumValues()
    {
        string json = ProfileExportSerializer.Serialize(
            ProfileDefaults.CreateSafeDefaults(),
            "0.1.0+abcdef0",
            DateTimeOffset.UtcNow).Replace(
                "\"performance\": \"balanced\"",
                "\"performance\": 999",
                StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"profiles\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"unexpected\":true}")]
    public void Deserialize_RejectsInvalidDocuments(string json)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProfileExportSerializer.Deserialize(json));
    }
}
