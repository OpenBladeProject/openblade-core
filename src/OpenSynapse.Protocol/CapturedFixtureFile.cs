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
        string json = await File.ReadAllTextAsync(path, cancellationToken);
        CapturedCommandFixture[]? fixtures =
            JsonSerializer.Deserialize<CapturedCommandFixture[]>(json, SerializerOptions);

        if (fixtures is null || fixtures.Length == 0)
        {
            throw new UnvalidatedCommandException("The fixture file contains no validated commands.");
        }

        return DeviceCommandCatalog.Create(TargetDevice, fixtures);
    }
}
