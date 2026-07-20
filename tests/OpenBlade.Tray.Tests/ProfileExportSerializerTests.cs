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
}
