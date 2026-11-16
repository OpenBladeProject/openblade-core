using System.Collections.Immutable;
using System.Numerics;

namespace OpenBlade.Protocol;

// Closed catalog of reviewed live CO contracts. Journal metadata selects an
// existing entry and never supplies a topology, wire codec, or backend pin.
internal sealed class CpuLiveCurveContract
{
    internal static readonly CpuLiveCurveContract Rz090528 = new(
        "rz09-0528-v31", 1, "RZ09-0528", 0x02C6, CpuLiveCurveSnapshot.SupportedMask,
        [(CpuLiveCurveSnapshot.SupportedPmVersion, CpuLiveCurveSnapshot.SupportedPmLength)],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DriverPath],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Platform.dll"],
        Rz090528InstalledCpuBackendInspector.ExpectedHashes[Rz090528InstalledCpuBackendInspector.DirectoryPath + @"\Device.dll"]);

    // Product 694's pinned AMD SDK 2.12 Phoenix implementation independently
    // defines E1 readback and per-core 53 writes; no 0528 topology is inherited.
    internal static readonly CpuLiveCurveContract Rz090508 = new(
        "rz09-0508-v22", 1, "RZ09-0508", 0x02B6, 0xFF,
        [(0x004C0008, 0xAF0)],
        "78A5C1387A3BA460316E415C07A7E895175DB136D2B99D30218D421B06819FE0",
        "5ABC017C207EDB94CAF69DB5022FC0C95DD8B50E574669FEB788127663881664",
        "57B4EA67B8DDF171B0084BD201C872811BE0E78130F3B9743F040454846FC683");

    // Product 669 names the identical AMD 1.0.1.0 package and exposes the
    // optimizer for the exact 7940HS model. Its own host/CPU admission remains separate.
    internal static readonly CpuLiveCurveContract Rz090482 = new(
        "rz09-0482-v22", 1, "RZ09-0482", 0x029D, 0xFF,
        [(0x004C0008, 0xAF0)],
        "78A5C1387A3BA460316E415C07A7E895175DB136D2B99D30218D421B06819FE0",
        "5ABC017C207EDB94CAF69DB5022FC0C95DD8B50E574669FEB788127663881664",
        "57B4EA67B8DDF171B0084BD201C872811BE0E78130F3B9743F040454846FC683");

    private static readonly ImmutableArray<CpuLiveCurveContract> Catalog = [Rz090528, Rz090508, Rz090482];
    internal static int MaximumCoreCount { get; } = Catalog.Max(contract => contract.CoreCount);

    private readonly string model;
    private readonly ushort productId;
    private readonly ImmutableArray<(uint Version, int Length)> pmTables;
    private readonly string driverSha256, platformSha256, deviceSha256;

    private CpuLiveCurveContract(string id, int version, string model, ushort productId, uint enabledCoreMask,
        ImmutableArray<(uint Version, int Length)> pmTables, string driverSha256, string platformSha256, string deviceSha256)
    {
        Id = id;
        Version = version;
        this.model = model;
        this.productId = productId;
        EnabledCoreMask = enabledCoreMask;
        CoreCount = BitOperations.PopCount(enabledCoreMask);
        this.pmTables = pmTables;
        this.driverSha256 = driverSha256;
        this.platformSha256 = platformSha256;
        this.deviceSha256 = deviceSha256;
    }

    internal string Id { get; }
    internal int Version { get; }
    internal uint EnabledCoreMask { get; }
    internal int CoreCount { get; }
    internal (uint Version, int Length) ExpectedPmTable => pmTables[0];
    internal bool UsesPhoenixProtocol => this == Rz090508 || this == Rz090482;
    internal static IReadOnlyList<CpuLiveCurveContract> All => Catalog;
    internal string ModelNumber => model;
    internal ushort ProductId => productId;
    internal string ProductName => this == Rz090528 ? "Blade 16 - RZ09-0528" : "Blade 14 - " + model;
    internal static CpuLiveCurveContract? ForIdentity(DeviceIdentity? identity) =>
        Catalog.FirstOrDefault(contract => identity is { VendorId: 0x1532 }
            && identity.Model == contract.model && identity.ProductId == contract.productId);


    internal static CpuLiveCurveContract? Resolve(string? id, int? version, DeviceIdentity? identity)
    {
        // Historical journals have no discriminator and always mean the 0528 V31 backend.
        var candidate = id is null && version is null ? Rz090528
            : Catalog.FirstOrDefault(contract => contract.Id == id && contract.Version == version);
        return candidate is not null && identity is { VendorId: 0x1532 }
            && identity.Model == candidate.model && identity.ProductId == candidate.productId ? candidate : null;
    }

    internal bool IsEnabled(int physicalCoreId) => physicalCoreId is >= 0 and < 32
        && (EnabledCoreMask & (1u << physicalCoreId)) != 0;

    internal bool AcceptsGeometry(uint mask, uint pmVersion, int pmLength) =>
        mask == EnabledCoreMask && pmTables.Contains((pmVersion, pmLength));

    internal bool MatchesBackend(string driver, string platform, string device) =>
        driver == driverSha256 && platform == platformSha256 && device == deviceSha256;
}
