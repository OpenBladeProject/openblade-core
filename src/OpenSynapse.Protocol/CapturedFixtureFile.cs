using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSynapse.Protocol;

public static class CapturedFixtureFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static DeviceIdentity TargetDevice { get; } = new(
        Model: TargetBladeDevice.Model,
        VendorId: TargetBladeDevice.VendorId,
        ProductId: TargetBladeDevice.ProductId,
        BiosVersion: TargetBladeDevice.BiosVersion);

    public static async Task ValidateCapturedFixtureAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        _ = await LoadCatalogAsync(path, cancellationToken);
    }

    internal static async Task<DeviceCommandCatalog> LoadCatalogAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await LoadCatalogAsync(
            path,
            TrustedFixtureHashRegistry.Production,
            cancellationToken);
    }

    internal static async Task<DeviceCommandCatalog> LoadCatalogAsync(
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

        RejectDuplicateJsonMembers(fixtureBytes);

        CapturedCommandFixture[]? fixtures =
            JsonSerializer.Deserialize<CapturedCommandFixture[]>(fixtureBytes, SerializerOptions);

        if (fixtures is null || fixtures.Length == 0)
        {
            throw new UnvalidatedCommandException("The fixture file contains no validated commands.");
        }

        return DeviceCommandCatalog.Create(TargetDevice, fixtures);
    }

    private static void RejectDuplicateJsonMembers(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        RejectDuplicateJsonMembers(document.RootElement);
    }

    private static void RejectDuplicateJsonMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> propertyNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new JsonException($"Duplicate JSON member '{property.Name}'.");
                }

                RejectDuplicateJsonMembers(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateJsonMembers(item);
            }
        }
    }
}
