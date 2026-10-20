namespace OpenBlade.Platform.Tests;

public sealed class WindowsRazerDriverApiDiscoveryTests
{
    // Same explicit model/services shape as the Razer control and keyboard-filter
    // INFs. Version, catalog name and certificate rotation are not API admission pins.
    private const string Inf = """
        [Version]
        Provider=%ManufacturerName%
        CatalogFile=updated.cat
        DriverVer=10/20/2026,99.0.0.1
        [Manufacturer]
        %ManufacturerName%=Standard,NTamd64
        [Standard.NTamd64]
        %Description%=Device, HID\VID_1532&PID_02E0&MI_01&COL01
        [Device.NT]
        Include=keyboard.inf
        Needs=HID_Keyboard_Inst.NT
        [Device.NT.Services]
        AddService=RzDev_02e0,,DriverService
        Include=keyboard.inf
        Needs=HID_Keyboard_Inst.NT.Services
        [DriverService]
        ServiceType=1
        ServiceBinary=%12%\RzDev_02e0.sys
        [Strings]
        ManufacturerName="Razer Inc"
        Description="Razer keyboard"
        """;

    [Fact]
    public void UpdatedPackageKeepsExactFilterContractWithoutVersionPin()
    {
        Assert.Equal("updated.cat", WindowsRazerDriverApiDiscovery.ValidateInfContract(
            Inf, "RzDev_02e0", @"HID\VID_1532&PID_02E0&MI_01&COL01"));
        Assert.Equal("updated.cat", WindowsRazerDriverApiDiscovery.ValidateInfContract(
            Inf.Replace("99.0.0.1", "200.1.9.7", StringComparison.Ordinal),
            "RzDev_02e0", @"HID\VID_1532&PID_02E0&MI_01&COL01"));
    }

    [Fact]
    public void SharedControlPackageMustDeclareItsExactHardwareAndService()
    {
        string control = Inf.Replace("RzDev_02e0", "RzCommon", StringComparison.Ordinal)
            .Replace(@"HID\VID_1532&PID_02E0&MI_01&COL01", @"RAZER\DeviceContol", StringComparison.Ordinal);
        Assert.Equal("updated.cat", WindowsRazerDriverApiDiscovery.ValidateInfContract(
            control, "RzCommon", @"RAZER\DeviceContol"));
    }

    [Theory]
    [InlineData("PID_02E0", "PID_02C6")]
    [InlineData("COL01", "COL03")]
    [InlineData("AddService=RzDev_02e0", "AddService=Other")]
    [InlineData(@"ServiceBinary=%12%\RzDev_02e0.sys", @"ServiceBinary=%12%\Other.sys")]
    [InlineData("ServiceType=1", "ServiceType=2")]
    [InlineData("NTamd64", "NTarm64")]
    [InlineData("CatalogFile=updated.cat", @"CatalogFile=..\updated.cat")]
    [InlineData("ManufacturerName=\"Razer Inc\"", "ManufacturerName=\"Other Vendor\"")]
    public void SignedUnrelatedDeclarationsCannotSupplyTheRequiredContract(string before, string after)
    {
        Assert.Throws<InvalidDataException>(() => WindowsRazerDriverApiDiscovery.ValidateInfContract(
            Inf.Replace(before, after, StringComparison.Ordinal),
            "RzDev_02e0", @"HID\VID_1532&PID_02E0&MI_01&COL01"));
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\RzDev_02e0.sys")]
    [InlineData(@"%SystemRoot%\System32\drivers\RzDev_02e0.sys")]
    [InlineData(@"System32\drivers\RzDev_02e0.sys")]
    [InlineData(@"C:\Windows\System32\drivers\RzDev_02e0.sys")]
    public void ResolvesOnlyProtectedServiceImage(string image)
    {
        Assert.Equal(@"C:\Windows\System32\drivers\RzDev_02e0.sys",
            WindowsRazerDriverApiDiscovery.ResolveServiceImage(image, @"C:\Windows", "RzDev_02e0"));
    }

    [Theory]
    [InlineData(@"C:\Temp\RzDev_02e0.sys")]
    [InlineData(@"C:\Windows\System32\drivers\Other.sys")]
    [InlineData(@"C:\Windows\System32\drivers\..\RzDev_02e0.sys")]
    public void RejectsRenamedOrUnprotectedServiceImage(string image)
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsRazerDriverApiDiscovery.ResolveServiceImage(image, @"C:\Windows", "RzDev_02e0"));
    }
}
