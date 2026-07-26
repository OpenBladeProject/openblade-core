using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CapturedFixtureFileTests : IDisposable
{
    private readonly string fixturePath = Path.Combine(
        Path.GetTempPath(),
        $"openblade-fixture-{Guid.NewGuid():N}.json");

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
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(new[] { fixture }));
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        DeviceCommandCatalog catalog = await CapturedFixtureFile.LoadCatalogAsync(
            fixturePath,
            registry,
            CancellationToken.None);

        Assert.True(catalog.TryGet("performance.balanced", out _));
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsEmptyFixtureSet()
    {
        await File.WriteAllTextAsync(fixturePath, "[]");
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(fixturePath, registry, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateCapturedFixtureAsync_ProductionRegistryRejectsSelfAuthorizedBooleans()
    {
        await WriteValidatedFixtureAsync();

        UnvalidatedCommandException exception = await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.ValidateCapturedFixtureAsync(fixturePath, CancellationToken.None));

        Assert.Contains("SHA-256", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CurrentFileHash(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsByteModifiedFixtureWithSameJsonMeaning()
    {
        await WriteValidatedFixtureAsync();
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();
        await File.AppendAllTextAsync(fixturePath, " ");

        await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(fixturePath, registry, CancellationToken.None));
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsUnknownJsonMembers()
    {
        await WriteValidatedFixtureAsync();
        string json = await File.ReadAllTextAsync(fixturePath);
        json = json.Replace(
            "\"captured\":true",
            "\"unexpected\":true,\"captured\":true",
            StringComparison.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(fixturePath, json);
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<JsonException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(fixturePath, registry, CancellationToken.None));
    }

    [Theory]
    [InlineData("\"Action\":\"performance.balanced\"", "\"action\":\"other\",\"Action\":\"performance.balanced\"")]
    [InlineData("\"Captured\":true", "\"captured\":false,\"Captured\":true")]
    public async Task LoadCatalogAsync_RejectsDuplicateJsonMembers(
        string originalMember,
        string duplicateMembers)
    {
        await WriteValidatedFixtureAsync();
        string json = await File.ReadAllTextAsync(fixturePath);
        json = json.Replace(originalMember, duplicateMembers, StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixturePath, json);
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        await Assert.ThrowsAsync<JsonException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(fixturePath, registry, CancellationToken.None));
    }

    [Fact]
    public async Task ByteMemoryOverloadUsesTheSameHashAndDuplicateMemberGatesAsFileLoading()
    {
        await WriteValidatedFixtureAsync();
        byte[] bytes = await File.ReadAllBytesAsync(fixturePath);
        TrustedFixtureHashRegistry registry = RegistryForCurrentFile();

        DeviceCommandCatalog catalog = CapturedFixtureFile.LoadCatalog(bytes, registry);

        Assert.True(catalog.TryGet("performance.balanced", out _));

        string duplicate = (await File.ReadAllTextAsync(fixturePath)).Replace(
            "\"Captured\":true",
            "\"captured\":false,\"Captured\":true",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => CapturedFixtureFile.LoadCatalog(
            System.Text.Encoding.UTF8.GetBytes(duplicate),
            TrustedFixtureHashRegistry.FromSha256Hex(
                [Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(duplicate)))])));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[null,null]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[{\"device\":null,\"action\":\"battery-limit-80\",\"commandClass\":7,\"commandId\":18,\"payload\":[208],\"captured\":true,\"decoded\":true,\"applied\":true,\"readBack\":true,\"restored\":true}]")]
    public async Task TrustedMalformedFixtureFailsClosedWithStableValidationExceptionForFileAndMemory(
        string json)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(fixturePath, bytes);
        TrustedFixtureHashRegistry registry = TrustedFixtureHashRegistry.FromSha256Hex(
            [Convert.ToHexString(SHA256.HashData(bytes))]);

        Exception? fileFailure = await Record.ExceptionAsync(() =>
            CapturedFixtureFile.LoadCatalogAsync(fixturePath, registry, CancellationToken.None));
        Exception? memoryFailure = Record.Exception(() =>
            CapturedFixtureFile.LoadCatalog(bytes, registry));

        AssertStableValidationFailure(fileFailure);
        AssertStableValidationFailure(memoryFailure);
        Assert.Equal(fileFailure!.GetType(), memoryFailure!.GetType());
    }

    private static void AssertStableValidationFailure(Exception? failure)
    {
        Assert.NotNull(failure);
        Assert.True(
            failure is UnvalidatedCommandException or JsonException,
            $"Expected a stable fixture-validation exception, but received {failure.GetType().FullName}.");
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
        await File.WriteAllTextAsync(fixturePath, JsonSerializer.Serialize(new[] { fixture }));
    }

    private TrustedFixtureHashRegistry RegistryForCurrentFile() =>
        TrustedFixtureHashRegistry.FromSha256Hex([CurrentFileHash()]);

    private string CurrentFileHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath)));

    public void Dispose()
    {
        if (File.Exists(fixturePath))
        {
            File.Delete(fixturePath);
        }
    }
}
