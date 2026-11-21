using System.IO.Compression;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class DiagnosticArchiveProviderTests
{
    public static TheoryData<string, bool, bool, bool, bool, bool, bool> ObservedFeatureCapabilities() => new()
    {
        // localDimming, clear, intel, intelWrite, rangesKnown, gamingGpu
        { "none", false, false, false, false, false, false },
        { "clear", true, true, false, false, false, false },
        { "observed", true, false, true, false, false, true },
        { "known", false, false, true, false, true, false },
    };

    [Theory]
    [MemberData(nameof(ObservedFeatureCapabilities))]
    public async Task CreateAsync_ExportsObservedFeatureAndLocalDimmingKeys(
        string scenario,
        bool localDimming,
        bool clear,
        bool intel,
        bool intelWrite,
        bool rangesKnown,
        bool gamingGpu)
    {
        IntelCpuPowerLimitCapabilities knownRanges = new(
            new DeviceNumericRange(1_000, 190_000, 125),
            new DeviceNumericRange(1_000, 200_000, 125),
            [1, 28, 56, 128],
            120_000,
            160_000,
            56,
            DeviceFeatureAccess.ObservedOnly);
        IntelCpuPowerLimitCapabilities? intelLimit = !intel ? null
            : rangesKnown ? knownRanges
            : knownRanges with { LongTermPowerMilliwatts = null, ShortTermPowerMilliwatts = null, TimeWindowSeconds = null };
        var runtime = new SnapshotOnlyRuntime(CreateStatus() with
        {
            Capabilities = DeviceCapabilities.Unavailable() with
            {
                LocalDimming = scenario switch
                {
                    "clear" => new LocalDimmingCapabilities(DeviceFeatureAccess.ReadOnly, SupportsClear: true),
                    "observed" => new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, SupportsClear: false),
                    _ => null,
                },
                IntelCpuPowerLimit = intelLimit,
                FirmwareGamingGpuOverclockAccess = gamingGpu ? DeviceFeatureAccess.ObservedOnly : null,
            },
        });
        var provider = new DiagnosticArchiveProvider(
            runtime,
            new DiagnosticHistory(),
            new FixedMetadataSource(),
            new FirmwareQueryDiagnostics());

        DiagnosticArchive result = await provider.CreateAsync(liveReadingsEnabled: false, CancellationToken.None);

        Assert.Equal(0, runtime.HardwareOrMutationCalls);
        using var archive = new ZipArchive(new MemoryStream(result.ZipBytes), ZipArchiveMode.Read);
        using JsonDocument capabilities = JsonDocument.Parse(await ReadEntryAsync(archive, "capabilities.json"));
        JsonElement root = capabilities.RootElement;
        Assert.Equal(localDimming, root.GetProperty("display.localDimming.observed").GetBoolean());
        Assert.Equal(clear, root.GetProperty("display.localDimming.clear").GetBoolean());
        Assert.Equal(intel, root.GetProperty("performance.intelCpuPowerLimit.observed").GetBoolean());
        Assert.Equal(intelWrite, root.GetProperty("performance.intelCpuPowerLimit.write").GetBoolean());
        Assert.Equal(rangesKnown, root.GetProperty("performance.intelCpuPowerLimit.rangesKnown").GetBoolean());
        Assert.Equal(gamingGpu, root.GetProperty("performance.firmwareGamingGpuOverclock.observed").GetBoolean());
        Assert.False(root.GetProperty("performance.firmwareGamingGpuOverclock.write").GetBoolean());
    }
}
