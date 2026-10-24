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

    [Theory]
    [InlineData("RzCommon")]
    [InlineData("RzDev_02c6")]
    public void StructuralPackageRejectionRetainsPackageCheckMessageAndStack(string service)
    {
        RazerDriverApiPackageEvidence evidence = WindowsRazerDriverApiDiscovery.Capture(service, stage =>
        {
            stage("InfContract");
            WindowsRazerDriverApiDiscovery.ValidateInfContract(
                Inf.Replace("Razer Inc", "Other Vendor", StringComparison.Ordinal),
                "RzDev_02e0", @"HID\VID_1532&PID_02E0&MI_01&COL01");
            throw new InvalidOperationException("The malformed INF must have been rejected.");
        });

        Assert.True(evidence.Installed);
        Assert.False(evidence.Trusted);
        Assert.Null(evidence.Identity);
        Assert.Equal("The signed INF does not identify the Razer provider.", evidence.Failure);
        Assert.Equal(service, evidence.FailureDetail?.Operation);
        Assert.Equal("InfContract", evidence.FailureDetail?.Stage);
        Assert.Equal("ProviderMismatch", evidence.FailureDetail?.Reason);
        Assert.Equal(nameof(InvalidDataException), evidence.FailureDetail?.ExceptionType);
        Assert.Equal(new InvalidDataException().HResult, evidence.FailureDetail?.HResult);
        Assert.Contains(nameof(WindowsRazerDriverApiDiscovery.ValidateInfContract), evidence.FailureDetail?.StackTrace);
        Assert.True(evidence.FailureDetail?.IsValid());
    }

    [Fact]
    public void UnexpectedNativePackageFailurePreservesInnerChainAndRedactsItsMessage()
    {
        RazerDriverApiPackageEvidence evidence = WindowsRazerDriverApiDiscovery.Capture("RzCommon", stage =>
        {
            stage("PublishedInf");
            throw new IOException(@"Cannot open C:\Users\private-owner\package.inf",
                new System.ComponentModel.Win32Exception(5, "Access denied"));
        });
        Assert.False(evidence.Trusted);
        Assert.Equal(5, evidence.FailureDetail?.Cause?.NativeErrorCode);
        Assert.Equal("PublishedInf", evidence.FailureDetail?.Stage);
        Assert.DoesNotContain("private-owner",
            System.Text.Json.JsonSerializer.Serialize(evidence.FailureDetail), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CatalogFile=updated.cat", "CatalogFile=one.cat\nCatalogFile.NTamd64=two.cat", "CatalogMissingOrAmbiguous")]
    [InlineData("CatalogFile=updated.cat", "CatalogFile=wrong.dll", "CatalogExtensionMismatch")]
    [InlineData("CatalogFile=updated.cat", "CatalogFile=..\\bad.cat", "InvalidPackageFileName")]
    public void CatalogRejectionHasAnExactStableReason(string before, string after, string reason)
    {
        RazerDriverApiPackageEvidence evidence = WindowsRazerDriverApiDiscovery.Capture("RzCommon", stage =>
        {
            stage("InfContract");
            WindowsRazerDriverApiDiscovery.ValidateInfContract(
                Inf.Replace(before, after, StringComparison.Ordinal),
                "RzDev_02e0", @"HID\VID_1532&PID_02E0&MI_01&COL01");
            return new(true, true, "must-not-reach");
        });
        Assert.False(evidence.Trusted);
        Assert.Equal(reason, evidence.FailureDetail?.Reason);
    }

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
    [InlineData("02E0")]
    [InlineData("02C6")]
    [InlineData("029F")]
    [InlineData("029D")]
    [InlineData("02C5")]
    [InlineData("0255")]
    [InlineData("02A0")]
    public void ExactModelFilterDeclarationRequiresItsOwnServiceAndKeyboardCollection(string productId)
    {
        string service = $"RzDev_{productId.ToLowerInvariant()}";
        string hardwareId = $@"HID\VID_1532&PID_{productId}&MI_01&COL01";
        string modelInf = Inf.Replace("RzDev_02e0", service, StringComparison.Ordinal)
            .Replace("PID_02E0", $"PID_{productId}", StringComparison.Ordinal);

        Assert.Equal("updated.cat", WindowsRazerDriverApiDiscovery.ValidateInfContract(
            modelInf, service, hardwareId));
        Assert.Throws<InvalidDataException>(() => WindowsRazerDriverApiDiscovery.ValidateInfContract(
            modelInf, service, hardwareId.Replace("COL01", "COL03", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => WindowsRazerDriverApiDiscovery.ValidateInfContract(
            modelInf, "RzDev_other", hardwareId));
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
