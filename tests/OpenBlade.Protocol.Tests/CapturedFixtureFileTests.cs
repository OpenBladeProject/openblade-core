using System.Text.Json;
using System.Security.Cryptography;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CapturedFixtureFileTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"opensynapse-fixture-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task LoadCatalogAsync_AdmitsValidatedFixtureFile()
    {
        DeviceIdentity target = CapturedFixtureFile.TargetDevice;
        CapturedCommandFixture fixture = new(
            target,
            "performance.balanced",
            0x0D,
            0x02,
            [1, 1, 0, 0],
            true,
            true,
            true,
            true,
            true);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new[] { fixture }));
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        DeviceCommandCatalog catalog = await CapturedFixtureFile.LoadCatalogAsync(
            _path,
            registry,
            CancellationToken.None);

        Assert.True(catalog.TryGet("performance.balanced", out _));
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsEmptyFixtureSet()
    {
        await File.WriteAllTextAsync(_path, "[]");
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(_path, registry, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCapturedFixtureAsync_ProductionRegistryRejectsSelfAuthorizedBooleans()
    {
        await WriteValidatedFixtureAsync();

        UnvalidatedCommandException exception = await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.ValidateCapturedFixtureAsync(_path, CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CurrentFileHash(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsByteModifiedFixtureWithSameJsonMeaning()
    {
        await WriteValidatedFixtureAsync();
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();
        await File.AppendAllTextAsync(_path, " ");

        await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(_path, registry, CancellationToken.None));
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsUnknownJsonMembers()
    {
        await WriteValidatedFixtureAsync();
        string json = await File.ReadAllTextAsync(_path);
        json = json.Replace(
            "\"captured\":true",
            "\"unexpected\":true,\"captured\":true",
            StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(_path, json);
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<JsonException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(_path, registry, CancellationToken.None));
    }

    [Theory]
    [InlineData("\"Action\":\"performance.balanced\"", "\"action\":\"other\",\"Action\":\"performance.balanced\"")]
    [InlineData("\"Captured\":true", "\"captured\":false,\"Captured\":true")]
    public async Task LoadCatalogAsync_RejectsDuplicateJsonMembers(
        string originalMember,
        string duplicateMembers)
    {
        await WriteValidatedFixtureAsync();
        string json = await File.ReadAllTextAsync(_path);
        json = json.Replace(originalMember, duplicateMembers, StringComparison.Ordinal);
        await File.WriteAllTextAsync(_path, json);
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<JsonException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(_path, registry, CancellationToken.None));
    }

    private async Task WriteValidatedFixtureAsync()
    {
        CapturedCommandFixture fixture = new(
            CapturedFixtureFile.TargetDevice,
            "performance.balanced",
            0x0D,
            0x02,
            [1, 1, 0, 0],
            true,
            true,
            true,
            true,
            true);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(new[] { fixture }));
    }

    private TrustedFixtureHashRegistry RegistryForCurrentFile() =>
        TrustedFixtureHashRegistry.FromSha256Hex([CurrentFileHash()]);

    private string CurrentFileHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_path)));

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
