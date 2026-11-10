using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed class WindowsRustModelEvidenceInventoryTests
{
    private const string FixturePath = "tests/OpenBlade.Protocol.Tests/Fixtures/windows-rust-model-evidence-inventory.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // These are pointers to the typed backend admission evidence for the two
    // physically validated catalog entries. The references do not promote a
    // feature on any other model or claim every control is validated.
    private static readonly IReadOnlyDictionary<string, BackendEvidencePointer[]> PhysicalBackendEvidence =
        new Dictionary<string, BackendEvidencePointer[]>(StringComparer.Ordinal)
        {
            ["RZ09-0581"] =
            [
                new("physical-capture-manifest", "evidence/legacy-captures/decoded/rz09-0581-pid-02e0-bios-3.01-evidence.json"),
                new("battery-command-admission", "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_BatteryProduction.json"),
                new("lighting-command-admission", "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingProduction.json"),
                new("lighting-semantic-admission", "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_3_01_LightingSemanticAdmission.json"),
                new("hyperboost-command-admission", "src/OpenBlade.Protocol/Fixtures/Rz09_0581_02e0_4_00_HyperBoostProduction.json"),
            ],
            ["RZ09-0528"] =
            [
                new("physical-capture-manifest", "evidence/legacy-captures/decoded/rz09-0528-pid-02c6-bios-2.02-evidence.json"),
                new("device-coverage-manifest", "evidence/legacy-captures/decoded/rz09-0528-pid-02c6-bios-2.02-device-coverage.json"),
                new("hyperboost-command-admission", "src/OpenBlade.Protocol/Fixtures/Rz09_0528_02c6_2_02_HyperBoostProduction.json"),
            ],
        };

    [Fact]
    public void ProductionModelAndEvidenceInventory_MatchesPortableFrozenBaseline()
    {
        string repository = FindRepositoryRoot();
        InventorySnapshot snapshot = ProjectInventory();
        ValidatePhysicalEvidencePointers(repository, snapshot);
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, FixturePath)));
        JsonElement actual = JsonSerializer.SerializeToElement(snapshot, JsonOptions);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual),
            "The frozen Windows model and feature evidence inventory differs from the production catalog.");
    }

    private static InventorySnapshot ProjectInventory()
    {
        Dictionary<string, ExperimentalBladeModel> experimental = ExperimentalBladeModels.All
            .ToDictionary(model => model.ModelNumber, StringComparer.Ordinal);

        string[] sourceReviewed = RazerBladeModelCatalog.All
            .Where(model => model.SupportBasis == BladeSupportBasis.SourceReviewed)
            .Select(model => model.ModelNumber).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(sourceReviewed, experimental.Keys.Order(StringComparer.Ordinal));
        return new InventorySnapshot(
            SchemaVersion: 2,
            SourceApis:
            [
                "OpenBlade.Contracts.RazerBladeModelCatalog.All",
                "OpenBlade.Protocol.ExperimentalBladeModels.All",
                "OpenBlade.Protocol.ExperimentalBladeModels.FeatureEvidenceFor",
                "OpenBlade.Protocol.WindowsHostIdentitySource",
                "OpenBlade.Protocol.BatteryValidationFactory.AdmittedServiceModels",
                "OpenBlade.Protocol.ExperimentalBladeFactory.IsCandidateDescriptor",
            ],
            Boundaries:
            [
                "Catalog support basis classifies model-level support; it is not a capability grant.",
                "FeatureEvidenceFor applies to ExperimentalBladeModels.All. Physically validated models have separate backend evidence pointers.",
                "An evidence pointer records provenance only; it does not extend a backend, feature, firmware rule, or physical validation result.",
                "This portable inventory includes catalog IDs and expected Windows registry product names. It omits SKU, BIOS, serial numbers, captures, and observed host data.",
                "The optional Windows control usage pair comes from the physically validated handle selectors. Source-reviewed control candidates have no fixed usage filter.",
            ],
            Models: RazerBladeModelCatalog.All.Select(model =>
            {
                experimental.TryGetValue(model.ModelNumber, out ExperimentalBladeModel? profile);
                string registryProductName = model.ModelNumber switch
                {
                    RazerBladeModelCatalog.OpenBladeTargetModelNumber => WindowsHostIdentitySource.ProductionIdentity.ProductName,
                    RazerBladeModelCatalog.Rz090528ModelNumber => WindowsHostIdentitySource.Rz090528Identity.ProductName,
                    _ => Assert.IsType<ExperimentalBladeModel>(profile).ProductName,
                };
                ushort[]? controlUsage = VerifyWindowsDiscoveryContract(model, profile, registryProductName);
                IReadOnlyList<ExperimentalFeatureEvidence> evidence = profile is null
                    ? []
                    : ExperimentalBladeModels.FeatureEvidenceFor(profile);
                return new ModelSnapshot(
                    model.ModelNumber,
                    model.ProductName,
                    model.ModelYear,
                    model.UsbVendorId,
                    model.UsbProductId,
                    model.SupportBasis.ToString(),
                    model.IsOfficiallySupported,
                    model.HasPhysicalValidation,
                    profile is null
                        ? null
                        : new ExperimentalProfileSnapshot(
                            profile.SupportsBatteryProtection,
                            profile.EvidenceSource,
                            profile.AdapterPowerEvidenceSource,
                            evidence.Select(feature => new FeatureSnapshot(
                                feature.Feature,
                                feature.Basis,
                                feature.Sources.ToArray())).ToArray()),
                    PhysicalBackendEvidence.TryGetValue(model.ModelNumber, out BackendEvidencePointer[]? pointers)
                        ? pointers
                        : [],
                    registryProductName,
                    controlUsage);
            }).ToArray());
    }

    private static ushort[]? VerifyWindowsDiscoveryContract(
        RazerBladeModelInfo model, ExperimentalBladeModel? profile, string registryProductName)
    {
        var registry = new SyntheticRegistry(registryProductName);
        var platform = new SyntheticPlatform();
        if (profile is not null)
        {
            Assert.Equal(model.UsbProductId, profile.ProductId.ToString("X4", CultureInfo.InvariantCulture));
            var candidate = new HidHandleIdentity(0x1532, profile.ProductId, 0xFF00, 0x0001, 91);
            Assert.True(ExperimentalBladeFactory.IsCandidateDescriptor(profile, candidate));
            Assert.True(ExperimentalBladeFactory.IsCandidateDescriptor(profile, candidate with { UsagePage = 1, Usage = 2 }));
            Assert.False(ExperimentalBladeFactory.IsCandidateDescriptor(profile, candidate with { ProductId = 0 }));
            Assert.False(ExperimentalBladeFactory.IsCandidateDescriptor(profile, candidate with { FeatureReportByteLength = 90 }));
            AssertExactHost(ExperimentalBladeModels.CreateIdentity(profile, registry, platform));
            return null;
        }

        AdmittedServiceModel physical = Assert.Single(BatteryValidationFactory.AdmittedServiceModels,
            row => row.HostIdentity.ProductName == registryProductName);
        HidHandleIdentity handle = physical.HandleIdentity;
        Assert.Equal(model.UsbVendorId, handle.VendorId.ToString("X4", CultureInfo.InvariantCulture));
        Assert.Equal(model.UsbProductId, handle.ProductId.ToString("X4", CultureInfo.InvariantCulture));
        AssertExactHost(new WindowsHostIdentitySource(registry, platform, physical.HostIdentity));
        return [handle.UsagePage, handle.Usage];

        static void AssertExactHost(IHostIdentitySource source)
        {
            HostIdentityReadResult result = source.ReadExactIdentity();
            Assert.Null(result.Failure);
            Assert.Equal("SYNTHETIC-VARIANT", result.Evidence?.Sku);
        }
    }

    private sealed class SyntheticRegistry(string productName) : IRegistryValueSource
    {
        public object? Read(RegistryHive hive, RegistryView view, string key, string valueName)
        {
            Assert.Equal(RegistryHive.LocalMachine, hive);
            Assert.Equal(RegistryView.Registry64, view);
            Assert.Contains(key, new[]
            {
                @"HARDWARE\DESCRIPTION\System\BIOS",
                @"SYSTEM\CurrentControlSet\Control\SystemInformation",
            });
            return valueName switch
            {
                "SystemManufacturer" => "Razer",
                "SystemProductName" => productName,
                "SystemSKU" => "SYNTHETIC-VARIANT",
                _ => null,
            };
        }
    }

    private sealed class SyntheticPlatform : IWindowsPlatformEvidenceSource
    {
        public WindowsPlatformEvidence Read() => new(true, true, new Version(10, 0, 22000), Architecture.X64);
    }

    private static void ValidatePhysicalEvidencePointers(string repository, InventorySnapshot inventory)
    {
        ModelSnapshot[] physicallyValidated = inventory.Models.Where(model => model.HasPhysicalValidation).ToArray();
        Assert.Equal(physicallyValidated.Select(model => model.ModelNumber).Order(StringComparer.Ordinal),
            PhysicalBackendEvidence.Keys.Order(StringComparer.Ordinal));

        foreach (ModelSnapshot model in physicallyValidated)
        {
            Assert.NotEmpty(model.PhysicalBackendEvidence);
            foreach (BackendEvidencePointer pointer in model.PhysicalBackendEvidence)
            {
                string path = Path.Combine(repository, pointer.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"Missing {model.ModelNumber} {pointer.Kind} evidence: {pointer.Path}");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "OpenBlade.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("Could not find OpenBlade.slnx.");
    }

    private sealed record InventorySnapshot(
        int SchemaVersion,
        IReadOnlyList<string> SourceApis,
        IReadOnlyList<string> Boundaries,
        IReadOnlyList<ModelSnapshot> Models);

    private sealed record ModelSnapshot(
        string ModelNumber,
        string ProductName,
        int ModelYear,
        string UsbVendorId,
        string UsbProductId,
        string SupportBasis,
        bool IsOfficiallySupported,
        bool HasPhysicalValidation,
        ExperimentalProfileSnapshot? ExperimentalProfile,
        IReadOnlyList<BackendEvidencePointer> PhysicalBackendEvidence,
        string WindowsRegistryProductName,
        ushort[]? WindowsControlUsage);

    private sealed record ExperimentalProfileSnapshot(
        bool SupportsBatteryProtection,
        string EvidenceSource,
        string? AdapterPowerEvidenceSource,
        IReadOnlyList<FeatureSnapshot> Features);

    private sealed record FeatureSnapshot(string Feature, string Basis, IReadOnlyList<string> Sources);

    private sealed record BackendEvidencePointer(string Kind, string Path);
}
