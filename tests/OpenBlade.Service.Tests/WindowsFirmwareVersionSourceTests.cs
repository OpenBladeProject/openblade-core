using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class WindowsFirmwareVersionSourceTests
{
    [Fact]
    public void Capture_ReadsSafeWindowsFirmwareMetadataAndNormalizesVersions()
    {
        var values = new Dictionary<string, object?>
        {
            ["BIOSVersion"] = " 3.01 ",
            ["ECFirmwareMajorRelease"] = 1,
            ["ECFirmwareMinorRelease"] = 4,
            ["SystemSKU"] = "RZ09-05819EN4",
        };
        var source = new WindowsFirmwareVersionSource(
            (_, name) => values.GetValueOrDefault(name),
            () =>
            [
                new VideoFirmwareCandidate("Intel Graphics", "Intel Video BIOS"),
                new VideoFirmwareCandidate(
                    "NVIDIA GeForce RTX 5090 Laptop GPU",
                    "Version98.3.5c.40.3d"),
            ]);

        FirmwareVersions result = source.Capture();

        Assert.Equal("3.01", result.BiosVersion);
        Assert.Equal("1.04", result.EcVersion);
        Assert.Equal("Unavailable", result.McuVersion);
        Assert.Equal("98.3.5c.40.3d", result.VbiosVersion);
        Assert.Equal("RZ09-0581", result.ModelNumber);
    }

    [Fact]
    public void Capture_DegradesEachMissingOrInaccessibleSourceToUnavailable()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, _) => throw new UnauthorizedAccessException("denied"),
            () => throw new IOException("unavailable"));

        Assert.Equal(FirmwareVersions.Unavailable(), source.Capture());
    }

    [Fact]
    public void Capture_DoesNotPresentMissingEcComponentsAsZeroVersion()
    {
        var source = new WindowsFirmwareVersionSource((_, _) => null, () => []);

        Assert.Equal("Unavailable", source.Capture().EcVersion);
    }

    [Fact]
    public void Capture_PreservesAnUnknownModelForCompatibilityReporting()
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name == "SystemSKU" ? "RZ09-9999ABC" : null,
            () => []);

        Assert.Equal("RZ09-9999ABC", source.Capture().ModelNumber);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData("not-a-number")]
    public void Capture_RejectsInvalidEcComponents(object invalidMinor)
    {
        var source = new WindowsFirmwareVersionSource(
            (_, name) => name switch
            {
                "BIOSVersion" => "3.01",
                "ECFirmwareMajorRelease" => 1,
                "ECFirmwareMinorRelease" => invalidMinor,
                _ => null,
            },
            () => []);

        Assert.Equal("Unavailable", source.Capture().EcVersion);
    }
}
