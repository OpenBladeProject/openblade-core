using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalBladeIdentityTests
{
    [Fact]
    public void EveryExperimentalProfileHasACatalogIdentityForApplicabilityReview()
    {
        foreach (ExperimentalBladeModel profile in ExperimentalBladeModels.All)
        {
            RazerBladeModelInfo model = Assert.Single(RazerBladeModelCatalog.All,
                candidate => candidate.ModelNumber == profile.ModelNumber);
            Assert.Equal("1532", model.UsbVendorId);
            Assert.Equal(profile.ProductId.ToString("X4"), model.UsbProductId);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExactDualRegistryIdentity_IsModelScopedWithoutInventedFirmwareValidation(int index)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All[index];
        var registry = new Registry(model.ProductName);
        IHostIdentitySource identity = ExperimentalBladeModels.CreateIdentity(model, registry, new Platform());
        HostIdentityReadResult result = identity.ReadExactIdentity();
        Assert.NotNull(result.Evidence);
        Assert.Null(result.Failure);
        Assert.Equal(HostFirmwareCompatibility.ModelScoped, result.FirmwareCompatibility);
        Assert.Equal("unverified-local-firmware", result.Evidence.Value.BiosVersion);
    }

    [Theory]
    [InlineData("SystemManufacturer")]
    [InlineData("SystemProductName")]
    public void EitherRegistryMismatch_BlocksExperimentalIdentity(string field)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All[0];
        var registry = new Registry(model.ProductName) { MismatchedField = field };
        HostIdentityReadResult result = ExperimentalBladeModels.CreateIdentity(model, registry, new Platform()).ReadExactIdentity();
        Assert.Null(result.Evidence);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
    }

    [Fact]
    public void NeighborModelProductName_DoesNotMatchPrefix()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All[0];
        HostIdentityReadResult result = ExperimentalBladeModels.CreateIdentity(model,
            new Registry(model.ProductName + "-other"), new Platform()).ReadExactIdentity();
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData(0x1532, 0x029F, 91, true)]
    [InlineData(0x1532, 0x02C6, 91, false)]
    [InlineData(0x1532, 0x029D, 91, false)]
    [InlineData(0x1111, 0x029F, 91, false)]
    [InlineData(0x1532, 0x029F, 90, false)]
    public void NativeDescriptor_RequiresExactModelPidVendorAndReportLength(int vendor, int product,
        int length, bool matches)
    {
        Assert.Equal(matches, ExperimentalBladeFactory.IsCandidateDescriptor(ExperimentalBladeModels.All[0],
            new((ushort)vendor, (ushort)product, 0xFF00, 3, (ushort)length)));
    }

    private sealed class Registry(string productName) : IRegistryValueSource
    {
        internal string? MismatchedField { get; init; }

        public object? Read(RegistryHive hive, RegistryView view, string key, string valueName)
        {
            if (valueName == MismatchedField && key.Contains("SystemInformation", StringComparison.Ordinal))
            {
                return "wrong";
            }
            return valueName switch
            {
                "SystemManufacturer" => "Razer",
                "SystemProductName" => productName,
                "SystemSKU" => "regional-sku-diagnostic-only",
                "BIOSVersion" => "unverified-local-firmware",
                _ => null,
            };
        }
    }

    private sealed class Platform : IWindowsPlatformEvidenceSource
    {
        public WindowsPlatformEvidence Read() => new(true, true, new Version(10, 0, 26200), Architecture.X64);
    }
}
