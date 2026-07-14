using System.Text.Json;
using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

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

        DeviceCommandCatalog catalog = await CapturedFixtureFile.LoadCatalogAsync(
            _path,
            CancellationToken.None);

        Assert.True(catalog.TryGet("performance.balanced", out _));
    }

    [Fact]
    public async Task LoadCatalogAsync_RejectsEmptyFixtureSet()
    {
        await File.WriteAllTextAsync(_path, "[]");

        await Assert.ThrowsAsync<UnvalidatedCommandException>(() =>
            CapturedFixtureFile.LoadCatalogAsync(_path, CancellationToken.None));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
