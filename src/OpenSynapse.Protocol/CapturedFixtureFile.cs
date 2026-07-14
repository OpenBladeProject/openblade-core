using System.Security.Cryptography;
using System.Text.Json;

namespace OpenSynapse.Protocol;

public static class CapturedFixtureFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static DeviceIdentity TargetDevice { get; } = new(
        Model: "RZ09-0581",
        VendorId: 0x1532,
        ProductId: 0x02E0,
        BiosVersion: "3.01");

    public static async Task<DeviceCommandCatalog> LoadCatalogAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await LoadCatalogAsync(
            path,
            TrustedFixtureHashRegistry.Production,
            cancellationToken);
    }

    public static async Task<DeviceCommandCatalog> LoadCatalogAsync(
        string path,
        TrustedFixtureHashRegistry trustedHashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trustedHashes);
        byte[] fixtureBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] hash = SHA256.HashData(fixtureBytes);
        if (!trustedHashes.Contains(hash))
        {
            throw new UnvalidatedCommandException(
                $"Fixture SHA-256 {Convert.ToHexString(hash)} is not present in the code-controlled trusted hash registry.");
        }

        CapturedCommandFixture[]? fixtures =
            JsonSerializer.Deserialize<CapturedCommandFixture[]>(fixtureBytes, SerializerOptions);

        if (fixtures is null || fixtures.Length == 0)
        {
            throw new UnvalidatedCommandException("The fixture file contains no validated commands.");
        }

        return DeviceCommandCatalog.Create(TargetDevice, fixtures);
    }
}
