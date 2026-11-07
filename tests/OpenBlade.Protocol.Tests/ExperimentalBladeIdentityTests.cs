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

    public static TheoryData<int> ModelIndices => new(Enumerable.Range(0, ExperimentalBladeModels.All.Count));

    [Theory]
    [MemberData(nameof(ModelIndices))]
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

    public static TheoryData<int, string> IdentityMismatchCases
    {
        get
        {
            var cases = new TheoryData<int, string>();
            foreach (int index in Enumerable.Range(0, ExperimentalBladeModels.All.Count))
            {
                cases.Add(index, "SystemManufacturer");
                cases.Add(index, "SystemProductName");
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(IdentityMismatchCases))]
    public void EitherRegistryMismatch_BlocksExperimentalIdentity(int index, string field)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All[index];
        var registry = new Registry(model.ProductName) { MismatchedField = field };
        HostIdentityReadResult result = ExperimentalBladeModels.CreateIdentity(model, registry, new Platform()).ReadExactIdentity();
        Assert.Null(result.Evidence);
        Assert.Equal(HidSessionFailureKind.IdentityBlocked, result.Failure);
    }

    [Theory]
    [MemberData(nameof(ModelIndices))]
    public void NeighborModelProductName_DoesNotMatchPrefix(int index)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All[index];
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

    [Theory]
    [InlineData(0x0270, "Blade 14 - RZ09-0370")]
    [InlineData(0x028C, "Blade 14 (2022) - RZ09-0427")]
    [InlineData(0x028A, "Blade 15 (2022) - RZ09-0421")]
    [InlineData(0x028B, "Blade 17 (2022) - RZ09-0423")]
    public void EarlyProfiles_UseDmiExactNameAndRejectSiblingDescriptors(int product, string expectedName)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == product);
        Assert.Equal(expectedName, model.ProductName);
        Assert.True(ExperimentalBladeFactory.IsCandidateDescriptor(model, new(0x1532, (ushort)product, 0xFF00, 3, 91)));
        foreach (ExperimentalBladeModel other in ExperimentalBladeModels.All.Where(item => item != model))
        {
            Assert.False(ExperimentalBladeFactory.IsCandidateDescriptor(model, new(0x1532, other.ProductId, 0xFF00, 3, 91)));
        }
        Assert.False(ExperimentalBladeFactory.IsCandidateDescriptor(model, new(0x1111, (ushort)product, 0xFF00, 3, 91)));
        Assert.False(ExperimentalBladeFactory.IsCandidateDescriptor(model, new(0x1532, (ushort)product, 0xFF00, 3, 90)));
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
